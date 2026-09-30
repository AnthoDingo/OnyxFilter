using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.SafeSearch;

// Implémente "Utiliser la Recherche Sécurisée" (Paramètres généraux) en reproduisant les règles de
// réécriture DNS ("dnsrewrite") d'AdGuard Home pour ses 7 moteurs pris en charge, reprises telles quelles
// de https://github.com/AdguardTeam/AdGuardHome/tree/master/internal/filtering/safesearch/rules :
//   - Bing, DuckDuckGo, Ecosia, Google, Pixabay, YouTube : redirigés par CNAME vers la variante
//     "recherche sécurisée" du moteur (ex. "www.google.fr" -> CNAME "forcesafesearch.google.com"), dont
//     l'adresse est ensuite résolue normalement auprès des serveurs en amont configurés.
//   - Yandex : redirigé directement vers une adresse IPv4 fixe (213.180.193.56), sans CNAME.
// Pour Google, dont la liste AdGuard Home énumère un domaine "www.google.<TLD>" par pays (plus de 200
// entrées), OnyxFilter reconnaît génériquement tout hôte commençant par "www.google." plutôt que
// d'embarquer cette liste (équivalent en pratique, sans avoir à la tenir à jour). Les autres moteurs ont
// des listes de domaines courtes, reprises intégralement.
public sealed class SafeSearchService : ISafeSearchService, IDisposable
{
    private enum SafeSearchEngine
    {
        Bing,
        DuckDuckGo,
        Ecosia,
        Google,
        Pixabay,
        Yandex,
        YouTube,
    }

    private enum RewriteKind
    {
        Cname,
        StaticIpv4,
    }

    private sealed class SafeSearchRule
    {
        public SafeSearchRule(SafeSearchEngine engine, RewriteKind kind, string target)
        {
            Engine = engine;
            Kind = kind;
            Target = target;
        }

        public SafeSearchEngine Engine { get; }

        public RewriteKind Kind { get; }

        // Nom CNAME cible (Kind = Cname) ou adresse IPv4 (Kind = StaticIpv4).
        public string Target { get; }
    }

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

    // Bornes de mise en cache local de l'adresse résolue pour une cible de redirection (ex.
    // "forcesafesearch.google.com") : évite de la re-résoudre auprès des serveurs en amont à chaque
    // requête vers un moteur de recherche protégé, tout en ne conservant jamais une adresse trop
    // longtemps si son TTL réel est très élevé.
    private const int MinCacheTtlSeconds = 60;
    private const int MaxCacheTtlSeconds = 3600;

    private static readonly SafeSearchRule GoogleRule = new SafeSearchRule(SafeSearchEngine.Google, RewriteKind.Cname, "forcesafesearch.google.com");

    private static readonly Dictionary<string, SafeSearchRule> ExactHostRules = BuildExactHostRules();

    private static Dictionary<string, SafeSearchRule> BuildExactHostRules()
    {
        Dictionary<string, SafeSearchRule> rules = new Dictionary<string, SafeSearchRule>(StringComparer.Ordinal);

        void AddCname(SafeSearchEngine engine, string cnameTarget, params string[] hosts)
        {
            foreach (string host in hosts)
            {
                rules[host] = new SafeSearchRule(engine, RewriteKind.Cname, cnameTarget);
            }
        }

        AddCname(SafeSearchEngine.Bing, "strict.bing.com", "www.bing.com", "edgeservices.bing.com");
        AddCname(SafeSearchEngine.DuckDuckGo, "safe.duckduckgo.com", "duckduckgo.com", "start.duckduckgo.com", "www.duckduckgo.com");
        AddCname(SafeSearchEngine.Ecosia, "strict-safe-search.ecosia.org", "www.ecosia.org");
        AddCname(SafeSearchEngine.Pixabay, "safesearch.pixabay.com", "pixabay.com");
        AddCname(SafeSearchEngine.YouTube, "restrictmoderate.youtube.com", "www.youtube.com", "m.youtube.com", "youtubei.googleapis.com", "youtube.googleapis.com", "www.youtube-nocookie.com");

        // Yandex : adresse IPv4 fixe (pas de CNAME), avec et sans "www.", plus "ya.ru" et sa variante en
        // toutes lettres cyrilliques encodée en punycode ("ยандекс.рф").
        const string yandexSafeSearchIpv4 = "213.180.193.56";

        string[] yandexBaseHosts =
        {
            "ya.ru",
            "yandex.az", "yandex.by", "yandex.co.il", "yandex.com.am", "yandex.com.ge", "yandex.com.ru",
            "yandex.com.tr", "yandex.com", "yandex.de", "yandex.ee", "yandex.eu", "yandex.fi", "yandex.fr",
            "yandex.kz", "yandex.lt", "yandex.lv", "yandex.md", "yandex.net", "yandex.org", "yandex.pl",
            "yandex.ru", "yandex.tj", "yandex.tm", "yandex.uz", "xn--d1acpjx3f.xn--p1ai",
        };

        foreach (string host in yandexBaseHosts)
        {
            SafeSearchRule rule = new SafeSearchRule(SafeSearchEngine.Yandex, RewriteKind.StaticIpv4, yandexSafeSearchIpv4);
            rules[host] = rule;
            rules["www." + host] = rule;
        }

        return rules;
    }

    private readonly ILocalSettingsStore settingsStore;
    private readonly IUpstreamResolver upstreamResolver;
    private readonly ILogger<SafeSearchService> logger;
    private readonly object syncRoot = new object();
    private readonly Dictionary<string, CachedAddress> addressCache = new Dictionary<string, CachedAddress>(StringComparer.Ordinal);

    private bool enabled;
    private bool bingEnabled;
    private bool duckDuckGoEnabled;
    private bool ecosiaEnabled;
    private bool googleEnabled;
    private bool pixabayEnabled;
    private bool yandexEnabled;
    private bool youTubeEnabled;

    public SafeSearchService(ILocalSettingsStore settingsStore, IUpstreamResolver upstreamResolver, ILogger<SafeSearchService> logger)
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
        bool isBingEnabled;
        bool isDuckDuckGoEnabled;
        bool isEcosiaEnabled;
        bool isGoogleEnabled;
        bool isPixabayEnabled;
        bool isYandexEnabled;
        bool isYouTubeEnabled;

        lock (syncRoot)
        {
            isEnabled = enabled;
            isBingEnabled = bingEnabled;
            isDuckDuckGoEnabled = duckDuckGoEnabled;
            isEcosiaEnabled = ecosiaEnabled;
            isGoogleEnabled = googleEnabled;
            isPixabayEnabled = pixabayEnabled;
            isYandexEnabled = yandexEnabled;
            isYouTubeEnabled = youTubeEnabled;
        }

        if (!isEnabled)
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

        // Comme AdGuard Home, seules les questions A/AAAA sont réécrites (AdGuard prend aussi en charge
        // le type HTTPS, non géré ailleurs dans OnyxFilter ; ces questions passent donc sans réécriture).
        if (queryType != DnsMessageParser.TypeA && queryType != DnsMessageParser.TypeAaaa)
        {
            return null;
        }

        SafeSearchRule? rule = FindRule(name);

        if (rule is null || !IsEngineEnabled(rule.Engine, isBingEnabled, isDuckDuckGoEnabled, isEcosiaEnabled, isGoogleEnabled, isPixabayEnabled, isYandexEnabled, isYouTubeEnabled))
        {
            return null;
        }

        if (rule.Kind == RewriteKind.StaticIpv4)
        {
            // Adresse fixe (Yandex) : même construction de réponse que le mode de blocage "Adresse IP
            // personnalisée", avec une IPv6 vide (repli sur NXDOMAIN pour les questions AAAA, faute
            // d'équivalent IPv6 chez Yandex — comme chez AdGuard Home, qui ne renvoie alors aucune
            // adresse non plus).
            return DnsBlockResponseBuilder.Build(query, queryType, DnsBlockingMode.CustomIp, rule.Target, string.Empty);
        }

        try
        {
            (IPAddress? resolvedAddress, int ttlSeconds) = await ResolveTargetAsync(rule.Target, queryType, clientAddress, cancellationToken);

            if (resolvedAddress is null)
            {
                return null;
            }

            return DnsRewriteResponseBuilder.BuildCnameChain(query, queryType, rule.Target, resolvedAddress, ttlSeconds);
        }
        catch (Exception ex)
        {
            // Échec ouvert ("fail open") : si la cible de la redirection ne peut pas être résolue, la
            // requête suit sa résolution normale plutôt que d'échouer.
            logger.LogDebug(ex, "Réécriture Recherche Sécurisée impossible pour {Domain} : requête laissée passer.", name);
            return null;
        }
    }

    private static SafeSearchRule? FindRule(string host)
    {
        if (host.StartsWith("www.google.", StringComparison.Ordinal))
        {
            return GoogleRule;
        }

        return ExactHostRules.TryGetValue(host, out SafeSearchRule? rule) ? rule : null;
    }

    private static bool IsEngineEnabled(
        SafeSearchEngine engine,
        bool bing,
        bool duckDuckGo,
        bool ecosia,
        bool google,
        bool pixabay,
        bool yandex,
        bool youTube)
    {
        return engine switch
        {
            SafeSearchEngine.Bing => bing,
            SafeSearchEngine.DuckDuckGo => duckDuckGo,
            SafeSearchEngine.Ecosia => ecosia,
            SafeSearchEngine.Google => google,
            SafeSearchEngine.Pixabay => pixabay,
            SafeSearchEngine.Yandex => yandex,
            SafeSearchEngine.YouTube => youTube,
            _ => false,
        };
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
            logger.LogError(ex, "Impossible de recharger les réglages de la Recherche Sécurisée.");
        }
    }

    private void ApplySettingsSnapshot(AppLocalSettings settings)
    {
        lock (syncRoot)
        {
            enabled = settings.General.UseSafeSearch;
            bingEnabled = settings.General.SafeSearchBing;
            duckDuckGoEnabled = settings.General.SafeSearchDuckDuckGo;
            ecosiaEnabled = settings.General.SafeSearchEcosia;
            googleEnabled = settings.General.SafeSearchGoogle;
            pixabayEnabled = settings.General.SafeSearchPixabay;
            yandexEnabled = settings.General.SafeSearchYandex;
            youTubeEnabled = settings.General.SafeSearchYoutube;
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
    }
}
