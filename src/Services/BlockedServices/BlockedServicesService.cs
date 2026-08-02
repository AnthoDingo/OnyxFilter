using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Filtering;

namespace OnyxFilter.Services.BlockedServices;

// Implémente IBlockedServicesService. Chaque service du catalogue (IBlockedServicesCatalog) fournit ses
// propres règles au format des listes de blocage/règles personnalisées (voir ParseRule) ; celles des
// services actuellement bloqués sont fusionnées en un seul HashSet de domaines et une liste d'expressions
// régulières, exactement comme CustomFilterRulesService/DomainListRepository, pour rester léger.
//
// Formats de règle reconnus (repris de AdguardTeam/HostlistsRegistry) :
//   - "||domaine.tld^" ou "|domaine.tld^" : domaine (et ses sous-domaines pour "||").
//   - "||domaine-*.tld^" (motif "*") : converti en expression régulière (le HashSet ne sait matcher que
//     des domaines exacts/parents, pas de motif).
//   - "/expression régulière/" : utilisée telle quelle.
//   - Modificateurs "$..." (ex. "$dnstype=~CNAME", "$denyallow=...", "$dnsrewrite=NXDOMAIN;;") : ignorés,
//     la règle est appliquée comme un blocage de domaine simple — une simplification volontaire par
//     rapport au moteur de règles complet d'AdGuard Home.
public sealed class BlockedServicesService : IBlockedServicesService, IDisposable
{
    private readonly IBlockedServicesCatalog catalog;
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<BlockedServicesService> logger;
    private readonly object syncRoot = new object();

    private bool enabled;
    private DnsBlockingMode blockingMode = DnsBlockingMode.Default;
    private string customBlockingIpv4 = string.Empty;
    private string customBlockingIpv6 = string.Empty;
    private BlockedServicesSchedule? schedule;

    private HashSet<string> blockedDomains = new HashSet<string>(StringComparer.Ordinal);

    // Associe chaque domaine bloqué au nom du service dont il provient, pour pouvoir identifier dans
    // TryBuildBlockResponse quel service a causé le blocage (premier ajout gagne en cas de conflit).
    private Dictionary<string, string> domainToServiceName = new Dictionary<string, string>(StringComparer.Ordinal);
    private List<(string ServiceName, Regex Regex)> namedRegexes = new List<(string, Regex)>();

    public BlockedServicesService(IBlockedServicesCatalog catalog, ILocalSettingsStore settingsStore, ILogger<BlockedServicesService> logger)
    {
        this.catalog = catalog;
        this.settingsStore = settingsStore;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        ApplySettingsSnapshot(settings);
    }

    public bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedServiceName)
    {
        response = null;
        matchedServiceName = null;

        bool isEnabled;
        DnsBlockingMode mode;
        string ipv4;
        string ipv6;
        HashSet<string> domains;
        Dictionary<string, string> serviceNames;
        List<(string ServiceName, Regex Regex)> regexes;
        BlockedServicesSchedule? currentSchedule;

        lock (syncRoot)
        {
            isEnabled = enabled;
            mode = blockingMode;
            ipv4 = customBlockingIpv4;
            ipv6 = customBlockingIpv6;
            domains = blockedDomains;
            serviceNames = domainToServiceName;
            regexes = namedRegexes;
            currentSchedule = schedule;
        }

        if (!isEnabled || (domains.Count == 0 && regexes.Count == 0))
        {
            return false;
        }

        // "Suspendre le blocage des services" : pendant la plage configurée pour aujourd'hui (heure
        // locale du serveur — voir BlockedServicesSchedule), tous les services redeviennent accessibles.
        if (IsWithinPauseWindow(currentSchedule, DateTime.Now))
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

        bool matchesDomain = domains.Count != 0 && DomainListRepository.ContainsDomainOrParent(name, domains);

        if (matchesDomain)
        {
            // Remonte la hiérarchie pour trouver le domaine exact qui correspond dans serviceNames.
            ReadOnlySpan<char> current = name;

            while (true)
            {
                if (serviceNames.TryGetValue(current.ToString(), out string? svcName))
                {
                    matchedServiceName = svcName;
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
        else
        {
            foreach ((string serviceName, Regex regex) in regexes)
            {
                if (regex.IsMatch(name))
                {
                    matchedServiceName = serviceName;
                    break;
                }
            }

            if (matchedServiceName is null)
            {
                return false;
            }
        }

        response = DnsBlockResponseBuilder.Build(query, queryType, mode, ipv4, ipv6);
        return true;
    }

    private static bool IsWithinPauseWindow(BlockedServicesSchedule? currentSchedule, DateTime nowLocal)
    {
        BlockedServicesDayRange? range = currentSchedule?.GetRangeFor(nowLocal.DayOfWeek);

        if (range is null)
        {
            return false;
        }

        int minutesNow = (nowLocal.Hour * 60) + nowLocal.Minute;
        return minutesNow >= range.StartMinutes && minutesNow < range.EndMinutes;
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
            logger.LogError(ex, "Impossible de recharger les réglages des services bloqués.");
        }
    }

    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        HashSet<string> newDomains = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> newDomainToServiceName = new Dictionary<string, string>(StringComparer.Ordinal);
        List<(string ServiceName, Regex Regex)> newNamedRegexes = new List<(string, Regex)>();
        HashSet<string> blockedIds = new HashSet<string>(settings.BlockedServices.BlockedServiceIds, StringComparer.Ordinal);

        foreach (BlockedServiceDefinition definition in catalog.All)
        {
            if (!blockedIds.Contains(definition.Id))
            {
                continue;
            }

            foreach (string rule in definition.Rules)
            {
                ParseRule(rule, definition.Name, newDomains, newDomainToServiceName, newNamedRegexes);
            }
        }

        lock (syncRoot)
        {
            enabled = settings.General.BlockDomainsWithFilters;
            blockingMode = settings.Dns.BlockingMode;
            customBlockingIpv4 = settings.Dns.CustomBlockingIpv4;
            customBlockingIpv6 = settings.Dns.CustomBlockingIpv6;
            schedule = settings.BlockedServices.Schedule;
            blockedDomains = newDomains;
            domainToServiceName = newDomainToServiceName;
            namedRegexes = newNamedRegexes;
        }
    }

    private static void ParseRule(string rawRule, string serviceName, HashSet<string> domains, Dictionary<string, string> domainNames, List<(string ServiceName, Regex Regex)> regexes)
    {
        string rule = rawRule.Trim();

        if (rule.Length == 0)
        {
            return;
        }

        if (rule.Length >= 2 && rule[0] == '/' && rule[^1] == '/')
        {
            TryAddRegex(rule.Substring(1, rule.Length - 2), serviceName, regexes);
            return;
        }

        string candidate = rule;

        if (candidate.StartsWith("||", StringComparison.Ordinal))
        {
            candidate = candidate.Substring(2);
        }
        else if (candidate.StartsWith("|", StringComparison.Ordinal))
        {
            candidate = candidate.Substring(1);
        }

        // Modificateur "$..." (ex. "$dnstype=~CNAME", "$denyallow=...") : tronqué, la règle est appliquée
        // comme un blocage de domaine simple sans tenir compte de sa condition additionnelle.
        int modifierIndex = candidate.IndexOf('$');

        if (modifierIndex >= 0)
        {
            candidate = candidate.Substring(0, modifierIndex);
        }

        int terminatorIndex = candidate.IndexOf('^');

        if (terminatorIndex >= 0)
        {
            candidate = candidate.Substring(0, terminatorIndex);
        }

        if (candidate.Length == 0)
        {
            return;
        }

        if (candidate.Contains('*', StringComparison.Ordinal))
        {
            string pattern = string.Join(".*", candidate.Split('*').Select(Regex.Escape));
            TryAddRegex(pattern, serviceName, regexes);
            return;
        }

        if (DomainListRepository.TryNormalizeDomain(candidate, out string domain))
        {
            domains.Add(domain);
            domainNames.TryAdd(domain, serviceName);
        }
    }

    private static void TryAddRegex(string pattern, string serviceName, List<(string ServiceName, Regex Regex)> regexes)
    {
        try
        {
            regexes.Add((serviceName, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }
        catch (ArgumentException)
        {
            // Motif invalide : ignoré plutôt que de faire échouer le chargement de tout le service.
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
