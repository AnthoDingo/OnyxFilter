using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.Rewrites;

// Implémente IDnsRewriteService. Contrairement aux listes de blocage/autorisation (DomainListRepository),
// ces règles sont saisies directement par l'utilisateur (pas d'abonnement à une URL) : aucun
// téléchargement, seul le rechargement des réglages à chaque enregistrement est nécessaire.
//
// Vérifiée en tout premier dans le pipeline de résolution (DnsQueryPipeline), avant les listes de
// blocage/autorisation, la Sécurité de navigation, le Contrôle parental et la Recherche Sécurisée : une
// réécriture explicitement configurée par l'utilisateur prend toujours le dessus, comme chez AdGuard
// Home.
public sealed class DnsRewriteService : IDnsRewriteService, IDisposable
{
    // Bornes de mise en cache local de l'adresse résolue pour une règle de type CNAME (même principe que
    // SafeSearchService.ResolveTargetAsync) : évite de la re-résoudre auprès des serveurs en amont à
    // chaque requête vers un domaine réécrit populaire.
    private const int MinCacheTtlSeconds = 60;
    private const int MaxCacheTtlSeconds = 3600;

    private sealed class CachedAddress
    {
        public CachedAddress(IPAddress address, DateTime expiresAtUtc)
        {
            Address = address;
            ExpiresAtUtc = expiresAtUtc;
        }

        public IPAddress Address { get; }

        public DateTime ExpiresAtUtc { get; }
    }

    // Règle pré-analysée (adresse déjà parsée, motif "*.": déjà détecté) pour ne refaire ce travail qu'au
    // rechargement des réglages plutôt qu'à chaque requête DNS.
    private sealed class CompiledRule
    {
        public CompiledRule(string domain, bool isWildcard, IPAddress? ipv4Answer, IPAddress? ipv6Answer, string? cnameAnswer)
        {
            Domain = domain;
            IsWildcard = isWildcard;
            Ipv4Answer = ipv4Answer;
            Ipv6Answer = ipv6Answer;
            CnameAnswer = cnameAnswer;
        }

        // Domaine de base (sans le "*." pour les motifs de sous-domaines).
        public string Domain { get; }

        public bool IsWildcard { get; }

        public IPAddress? Ipv4Answer { get; }

        public IPAddress? Ipv6Answer { get; }

        public string? CnameAnswer { get; }
    }

    private readonly ILocalSettingsStore settingsStore;
    private readonly IUpstreamResolver upstreamResolver;
    private readonly ILogger<DnsRewriteService> logger;
    private readonly object syncRoot = new object();
    private readonly Dictionary<string, CachedAddress> addressCache = new Dictionary<string, CachedAddress>(StringComparer.Ordinal);

    private bool enabled;
    private IReadOnlyList<CompiledRule> rules = Array.Empty<CompiledRule>();

    public DnsRewriteService(ILocalSettingsStore settingsStore, IUpstreamResolver upstreamResolver, ILogger<DnsRewriteService> logger)
    {
        this.settingsStore = settingsStore;
        this.upstreamResolver = upstreamResolver;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        ApplySettingsSnapshot(settings);
    }

    public async Task<byte[]?> TryBuildRewriteResponseAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        bool isEnabled;
        IReadOnlyList<CompiledRule> currentRules;

        lock (syncRoot)
        {
            isEnabled = enabled;
            currentRules = rules;
        }

        if (!isEnabled || currentRules.Count == 0)
        {
            return null;
        }

        if (!DnsMessageParser.TryReadQuestionName(query, out string name) || name.Length == 0)
        {
            return null;
        }

        if (!DnsMessageParser.TryReadQuestionType(query, out ushort queryType))
        {
            return null;
        }

        // Comme AdGuard Home, seules les questions A/AAAA sont réécrites.
        if (queryType != DnsMessageParser.TypeA && queryType != DnsMessageParser.TypeAaaa)
        {
            return null;
        }

        CompiledRule? rule = FindRule(name, currentRules);

        if (rule is null)
        {
            return null;
        }

        if (rule.CnameAnswer is not null)
        {
            try
            {
                (IPAddress? resolvedAddress, int ttlSeconds) = await ResolveTargetAsync(rule.CnameAnswer, queryType, clientAddress, cancellationToken);

                if (resolvedAddress is null)
                {
                    return null;
                }

                return DnsRewriteResponseBuilder.BuildCnameChain(query, queryType, rule.CnameAnswer, resolvedAddress, ttlSeconds);
            }
            catch (Exception ex)
            {
                // Échec ouvert : si la cible ne peut pas être résolue, la requête suit sa résolution
                // normale plutôt que d'échouer.
                logger.LogDebug(ex, "Réécriture DNS impossible pour {Domain} (cible {Target}) : requête laissée passer.", name, rule.CnameAnswer);
                return null;
            }
        }

        // Réutilise la construction de réponse du mode de blocage "Adresse IP personnalisée" : même
        // format de réponse (NOERROR + enregistrement A/AAAA unique), avec repli sur NXDOMAIN si le type
        // de question demandé ne correspond à aucune des deux adresses de la règle.
        string ipv4Text = rule.Ipv4Answer?.ToString() ?? string.Empty;
        string ipv6Text = rule.Ipv6Answer?.ToString() ?? string.Empty;

        return DnsBlockResponseBuilder.Build(query, queryType, DnsBlockingMode.CustomIp, ipv4Text, ipv6Text);
    }

    // Cherche une correspondance exacte d'abord, puis un motif "*.domaine.tld" couvrant "name" en tant
    // que sous-domaine (jamais le domaine de base lui-même : une règle dédiée est nécessaire pour
    // celui-ci, comme chez AdGuard Home). La première règle correspondante dans l'ordre de configuration
    // l'emporte.
    private static CompiledRule? FindRule(string name, IReadOnlyList<CompiledRule> rules)
    {
        foreach (CompiledRule rule in rules)
        {
            if (!rule.IsWildcard && string.Equals(rule.Domain, name, StringComparison.Ordinal))
            {
                return rule;
            }
        }

        foreach (CompiledRule rule in rules)
        {
            if (rule.IsWildcard && IsStrictSubdomainOf(name, rule.Domain))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool IsStrictSubdomainOf(string name, string baseDomain)
    {
        return name.Length > baseDomain.Length + 1
            && name.EndsWith(baseDomain, StringComparison.Ordinal)
            && name[name.Length - baseDomain.Length - 1] == '.';
    }

    private async Task<(IPAddress? Address, int TtlSeconds)> ResolveTargetAsync(string targetHost, ushort queryType, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        string cacheKey = targetHost + "|" + queryType;

        lock (syncRoot)
        {
            if (addressCache.TryGetValue(cacheKey, out CachedAddress? cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                int remainingSeconds = (int)Math.Max(1, (cached.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds);
                return (cached.Address, remainingSeconds);
            }
        }

        byte[] syntheticQuery = DnsQueryBuilder.BuildQuery(targetHost, queryType, out _);
        UpstreamResolutionResult result = await upstreamResolver.ResolveAsync(syntheticQuery, clientAddress, cancellationToken);

        if (result.Response is null || !DnsMessageParser.TryExtractFirstAddress(result.Response, queryType, out IPAddress? address, out int ttlSeconds) || address is null)
        {
            return (null, 0);
        }

        int clampedTtl = Math.Clamp(ttlSeconds, MinCacheTtlSeconds, MaxCacheTtlSeconds);

        lock (syncRoot)
        {
            addressCache[cacheKey] = new CachedAddress(address, DateTime.UtcNow.AddSeconds(clampedTtl));
        }

        return (address, clampedTtl);
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
            logger.LogError(ex, "Impossible de recharger les réglages des réécritures DNS.");
        }
    }

    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        List<CompiledRule> compiled = new List<CompiledRule>(settings.Rewrites.Entries.Count);

        foreach (DnsRewriteEntry entry in settings.Rewrites.Entries)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            CompiledRule? rule = TryCompileRule(entry);

            if (rule is not null)
            {
                compiled.Add(rule);
            }
        }

        lock (syncRoot)
        {
            enabled = settings.Rewrites.Enabled;
            rules = compiled;

            // La cible d'une réécriture CNAME a pu changer : on ne garde pas d'adresse potentiellement
            // obsolète pour un nom qui ne correspond plus à aucune règle courante.
            addressCache.Clear();
        }
    }

    private static CompiledRule? TryCompileRule(DnsRewriteEntry entry)
    {
        string domain = entry.Domain.Trim().TrimEnd('.').ToLowerInvariant();
        bool isWildcard = domain.StartsWith("*.", StringComparison.Ordinal);

        if (isWildcard)
        {
            domain = domain.Substring(2);
        }

        if (domain.Length == 0)
        {
            return null;
        }

        string answer = entry.Answer.Trim();

        if (answer.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(answer, out IPAddress? parsedAddress))
        {
            return parsedAddress.AddressFamily == AddressFamily.InterNetworkV6
                ? new CompiledRule(domain, isWildcard, ipv4Answer: null, ipv6Answer: parsedAddress, cnameAnswer: null)
                : new CompiledRule(domain, isWildcard, ipv4Answer: parsedAddress, ipv6Answer: null, cnameAnswer: null);
        }

        return new CompiledRule(domain, isWildcard, ipv4Answer: null, ipv6Answer: null, cnameAnswer: answer.TrimEnd('.').ToLowerInvariant());
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
