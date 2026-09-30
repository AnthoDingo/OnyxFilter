using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.Filtering;

// Implémente ICustomFilterRulesService : une règle par ligne, saisie directement par l'utilisateur (pas
// d'abonnement à une URL, contrairement aux listes de blocage/autorisation) :
//   - "||domaine.tld^" : bloque le domaine et tous ses sous-domaines ;
//   - "@@||domaine.tld^" : exception, ne bloque jamais le domaine ni ses sous-domaines — prend le dessus
//     aussi bien sur les autres règles personnalisées que sur les listes de blocage abonnées
//     (IDnsFilterService), exactement comme chez AdGuard Home ;
//   - "adresse_ip domaine.tld" (syntaxe fichier hosts) : répond directement avec cette adresse pour ce
//     domaine exact, sans couvrir ses sous-domaines ;
//   - "! commentaire" ou "# commentaire" : ignoré ;
//   - "/expression régulière/" : bloque tout domaine dont le nom correspond à l'expression.
// Un domaine peut correspondre à plusieurs types de règle à la fois (ex. un "hosts" exact et un
// "||domaine^" plus général) : la correspondance la plus spécifique l'emporte, dans cet ordre :
// exception, puis réponse "hosts" exacte, puis expression régulière, puis blocage par domaine/parent.
//
// Vérifiée juste après les Réécritures DNS (IDnsRewriteService) dans le pipeline de résolution
// (DnsQueryPipeline), avant les listes de blocage/autorisation abonnées : des règles saisies
// explicitement par l'utilisateur priment toujours sur un abonnement.
public sealed class CustomFilterRulesService : ICustomFilterRulesService, IDisposable
{
    private sealed class HostsAnswer
    {
        public HostsAnswer(IPAddress? ipv4, IPAddress? ipv6)
        {
            Ipv4 = ipv4;
            Ipv6 = ipv6;
        }

        public IPAddress? Ipv4 { get; }

        public IPAddress? Ipv6 { get; }
    }

    private static readonly char[] AdblockTerminators = { '^', '/', '$', '*' };
    private static readonly char[] WhitespaceChars = { ' ', '\t' };

    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<CustomFilterRulesService> logger;
    private readonly object syncRoot = new object();

    private bool enabled;
    private DnsBlockingMode blockingMode = DnsBlockingMode.Default;
    private string customBlockingIpv4 = string.Empty;
    private string customBlockingIpv6 = string.Empty;

    private HashSet<string> blockDomains = new HashSet<string>(StringComparer.Ordinal);
    private HashSet<string> exceptionDomains = new HashSet<string>(StringComparer.Ordinal);
    private Dictionary<string, HostsAnswer> hostsAnswers = new Dictionary<string, HostsAnswer>(StringComparer.Ordinal);
    private List<Regex> blockRegexes = new List<Regex>();

    public CustomFilterRulesService(ILocalSettingsStore settingsStore, ILogger<CustomFilterRulesService> logger)
    {
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        ApplySettingsSnapshot(settings);
    }

    public bool IsExcepted(string domain)
    {
        bool isEnabled;
        HashSet<string> exceptions;

        lock (syncRoot)
        {
            isEnabled = enabled;
            exceptions = exceptionDomains;
        }

        return isEnabled && exceptions.Count != 0 && DomainListRepository.ContainsDomainOrParent(domain, exceptions);
    }

    public bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedRule)
    {
        response = null;
        matchedRule = null;

        bool isEnabled;
        DnsBlockingMode mode;
        string ipv4;
        string ipv6;
        Dictionary<string, HostsAnswer> answers;
        List<Regex> regexes;
        HashSet<string> domains;

        lock (syncRoot)
        {
            isEnabled = enabled;
            mode = blockingMode;
            ipv4 = customBlockingIpv4;
            ipv6 = customBlockingIpv6;
            answers = hostsAnswers;
            regexes = blockRegexes;
            domains = blockDomains;
        }

        if (!isEnabled)
        {
            return false;
        }

        if (!DnsMessageParser.TryReadQuestionName(query, out string name) || name.Length == 0)
        {
            return false;
        }

        if (!DnsMessageParser.TryReadQuestionType(query, out ushort queryType))
        {
            return false;
        }

        // Règle "hosts" exacte (pas de couverture des sous-domaines) : répond directement avec l'adresse
        // configurée, quel que soit le "Mode de blocage" habituel.
        if (answers.TryGetValue(name, out HostsAnswer? hostsAnswer))
        {
            string answerIpv4 = hostsAnswer.Ipv4?.ToString() ?? string.Empty;
            string answerIpv6 = hostsAnswer.Ipv6?.ToString() ?? string.Empty;
            matchedRule = (answerIpv4.Length > 0 ? answerIpv4 : answerIpv6) + " " + name;
            response = DnsBlockResponseBuilder.Build(query, queryType, DnsBlockingMode.CustomIp, answerIpv4, answerIpv6);
            return true;
        }

        Regex? matchedRegex = null;

        foreach (Regex regex in regexes)
        {
            if (regex.IsMatch(name))
            {
                matchedRegex = regex;
                break;
            }
        }

        bool matchesDomainRule = domains.Count != 0 && DomainListRepository.ContainsDomainOrParent(name, domains);

        if (matchedRegex is null && !matchesDomainRule)
        {
            return false;
        }

        if (matchedRegex is not null)
        {
            matchedRule = "/" + matchedRegex.ToString() + "/";
        }
        else
        {
            // Remonte la hiérarchie pour trouver le domaine exact de la règle.
            ReadOnlySpan<char> current = name;

            while (true)
            {
                if (domains.Contains(current.ToString()))
                {
                    matchedRule = "||" + current.ToString() + "^";
                    break;
                }

                int dot = current.IndexOf('.');

                if (dot < 0)
                {
                    break;
                }

                current = current.Slice(dot + 1);
            }
        }

        response = DnsBlockResponseBuilder.Build(query, queryType, mode, ipv4, ipv6);
        return true;
    }

    private void OnSettingsChanged()
    {
        _ = ReloadSettingsAsync();
    }

    private async Task ReloadSettingsAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            ApplySettingsSnapshot(settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de recharger les règles de filtrage personnalisées.");
        }
    }

    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        HashSet<string> newBlockDomains = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> newExceptionDomains = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, HostsAnswer> newHostsAnswers = new Dictionary<string, HostsAnswer>(StringComparer.Ordinal);
        List<Regex> newBlockRegexes = new List<Regex>();

        foreach (string rawLine in settings.CustomFilterRules.RulesText.Split('\n'))
        {
            ParseLine(rawLine.Trim('\r').Trim(), newBlockDomains, newExceptionDomains, newHostsAnswers, newBlockRegexes);
        }

        lock (syncRoot)
        {
            enabled = settings.General.BlockDomainsWithFilters;
            blockingMode = settings.Dns.BlockingMode;
            customBlockingIpv4 = settings.Dns.CustomBlockingIpv4;
            customBlockingIpv6 = settings.Dns.CustomBlockingIpv6;
            blockDomains = newBlockDomains;
            exceptionDomains = newExceptionDomains;
            hostsAnswers = newHostsAnswers;
            blockRegexes = newBlockRegexes;
        }
    }

    private static void ParseLine(
        string line,
        HashSet<string> blockDomains,
        HashSet<string> exceptionDomains,
        Dictionary<string, HostsAnswer> hostsAnswers,
        List<Regex> blockRegexes)
    {
        if (line.Length == 0 || line[0] == '!' || line[0] == '#')
        {
            return;
        }

        bool isException = line.StartsWith("@@", StringComparison.Ordinal);

        if (isException)
        {
            line = line.Substring(2);
        }

        if (line.Length >= 2 && line[0] == '/' && line[^1] == '/')
        {
            // Pas de syntaxe documentée chez AdGuard Home pour une exception par expression régulière :
            // seul le blocage est pris en charge pour ce format.
            if (isException)
            {
                return;
            }

            string pattern = line.Substring(1, line.Length - 2);

            try
            {
                blockRegexes.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            catch (ArgumentException)
            {
                // Expression régulière invalide : cette ligne est ignorée plutôt que de faire échouer le
                // rechargement de toutes les autres règles.
            }

            return;
        }

        if (line.StartsWith("||", StringComparison.Ordinal))
        {
            string candidate = line.Substring(2);
            int cut = candidate.IndexOfAny(AdblockTerminators);

            if (cut >= 0)
            {
                candidate = candidate.Substring(0, cut);
            }

            if (DomainListRepository.TryNormalizeDomain(candidate, out string domain))
            {
                (isException ? exceptionDomains : blockDomains).Add(domain);
            }

            return;
        }

        string[] tokens = line.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2 && IPAddress.TryParse(tokens[0], out IPAddress? hostsAddress))
        {
            if (!DomainListRepository.TryNormalizeDomain(tokens[1], out string hostsDomain))
            {
                return;
            }

            if (isException)
            {
                // Pas de syntaxe documentée pour "@@adresse domaine" : traité comme une exception simple
                // sur le domaine (avec couverture des sous-domaines), en ignorant l'adresse fournie.
                exceptionDomains.Add(hostsDomain);
                return;
            }

            // Syntaxe fichier hosts : répond avec cette adresse pour ce domaine exact uniquement, sans
            // couvrir ses sous-domaines (contrairement à "||domaine.tld^" ci-dessus).
            HostsAnswer answer = hostsAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? new HostsAnswer(ipv4: null, ipv6: hostsAddress)
                : new HostsAnswer(ipv4: hostsAddress, ipv6: null);

            hostsAnswers[hostsDomain] = answer;
            return;
        }

        if (tokens.Length == 1 && DomainListRepository.TryNormalizeDomain(tokens[0], out string plainDomain))
        {
            (isException ? exceptionDomains : blockDomains).Add(plainDomain);
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
