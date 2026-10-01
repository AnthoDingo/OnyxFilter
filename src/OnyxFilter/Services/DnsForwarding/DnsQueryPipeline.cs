using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Services.BlockedServices;
using OnyxFilter.Services.BrowsingSecurity;
using OnyxFilter.Services.Filtering;
using OnyxFilter.Services.ParentalControl;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Rewrites;
using OnyxFilter.Services.SafeSearch;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Services.DnsForwarding;

// Implémentation du pipeline de résolution DNS partagé (voir IDnsQueryPipeline). Logique extraite de
// DnsProxyService pour être réutilisée à l'identique par le service DNS-over-TLS : mêmes filtres de
// blocage, même cache (avec rafraîchissement optimiste en arrière-plan), mêmes serveurs en amont, mêmes
// statistiques et même journal des requêtes, quel que soit le transport par lequel la requête est arrivée.
public sealed class DnsQueryPipeline : IDnsQueryPipeline
{
    private readonly IUpstreamResolver upstreamResolver;
    private readonly IDnsCache dnsCache;
    private readonly IDnsRateLimiter rateLimiter;
    private readonly IDnsAccessControl accessControl;
    private readonly IDnsProtectionState protectionState;
    private readonly IDnsFilterService filterService;
    private readonly IDnsAllowlistService allowlistService;
    private readonly ICustomFilterRulesService customFilterRulesService;
    private readonly IBlockedServicesService blockedServicesService;
    private readonly IDnsRewriteService rewriteService;
    private readonly IBrowsingSecurityService browsingSecurityService;
    private readonly IParentalControlService parentalControlService;
    private readonly ISafeSearchService safeSearchService;
    private readonly IDnsStatisticsService statisticsService;
    private readonly IDnsQueryLogService queryLogService;
    private readonly ILogger<DnsQueryPipeline> logger;

    // Garantit une initialisation unique même si plusieurs points d'écoute démarrent en parallèle.
    private readonly SemaphoreSlim initializationLock = new SemaphoreSlim(1, 1);
    private volatile bool isInitialized;

    // Clés de cache en cours de rafraîchissement en arrière-plan (cache optimiste), pour éviter de
    // lancer plusieurs requêtes amont simultanées pour la même entrée expirée.
    private readonly HashSet<string> pendingRefreshKeys = new HashSet<string>(StringComparer.Ordinal);

    public DnsQueryPipeline(
        IUpstreamResolver upstreamResolver,
        IDnsCache dnsCache,
        IDnsRateLimiter rateLimiter,
        IDnsAccessControl accessControl,
        IDnsProtectionState protectionState,
        IDnsFilterService filterService,
        IDnsAllowlistService allowlistService,
        ICustomFilterRulesService customFilterRulesService,
        IBlockedServicesService blockedServicesService,
        IDnsRewriteService rewriteService,
        IBrowsingSecurityService browsingSecurityService,
        IParentalControlService parentalControlService,
        ISafeSearchService safeSearchService,
        IDnsStatisticsService statisticsService,
        IDnsQueryLogService queryLogService,
        ILogger<DnsQueryPipeline> logger)
    {
        this.upstreamResolver = upstreamResolver;
        this.dnsCache = dnsCache;
        this.rateLimiter = rateLimiter;
        this.accessControl = accessControl;
        this.protectionState = protectionState;
        this.filterService = filterService;
        this.allowlistService = allowlistService;
        this.customFilterRulesService = customFilterRulesService;
        this.blockedServicesService = blockedServicesService;
        this.rewriteService = rewriteService;
        this.browsingSecurityService = browsingSecurityService;
        this.parentalControlService = parentalControlService;
        this.safeSearchService = safeSearchService;
        this.statisticsService = statisticsService;
        this.queryLogService = queryLogService;
        this.logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (isInitialized)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken);

        try
        {
            if (isInitialized)
            {
                return;
            }

            await upstreamResolver.InitializeAsync(cancellationToken);
            await dnsCache.InitializeAsync();
            await rateLimiter.InitializeAsync();
            await accessControl.InitializeAsync();
            await filterService.InitializeAsync(cancellationToken);
            await allowlistService.InitializeAsync(cancellationToken);
            await customFilterRulesService.InitializeAsync(cancellationToken);
            await blockedServicesService.InitializeAsync(cancellationToken);
            await rewriteService.InitializeAsync(cancellationToken);
            await browsingSecurityService.InitializeAsync(cancellationToken);
            await parentalControlService.InitializeAsync(cancellationToken);
            await safeSearchService.InitializeAsync(cancellationToken);
            await statisticsService.InitializeAsync(cancellationToken);
            await queryLogService.InitializeAsync(cancellationToken);

            isInitialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    // Requêtes non traitées du tout (ni réponse, ni journal, ni statistiques) lorsque le domaine
    // demandé figure dans la liste des domaines interdits.
    public bool IsQueryDisallowed(byte[] query)
    {
        return DnsMessageParser.TryReadQuestionName(query, out string name) && accessControl.IsDomainDisallowed(name);
    }

    // Point d'entrée commun à tous les transports : sert la réponse depuis le cache si possible, sinon
    // interroge les serveurs en amont et met le résultat en cache lorsque c'est pertinent (réponse
    // réussie, avec un TTL strictement positif). "clientAddress" est transmis à IUpstreamResolver pour
    // l'option EDNS Client Subnet quand elle est activée.
    public async Task<byte[]?> ResolveAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        // Sockets double pile : un client IPv4 arrive sous la forme "::ffff:a.b.c.d" (issue #10).
        if (clientAddress is { IsIPv4MappedToIPv6: true })
        {
            clientAddress = clientAddress.MapToIPv4();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        string domain = DnsMessageParser.TryReadQuestionName(query, out string parsedDomain) ? parsedDomain : string.Empty;
        ushort queryType = DnsMessageParser.TryReadQuestionType(query, out ushort parsedQueryType) ? parsedQueryType : (ushort)0;

        // "Protection" (tableau de bord, Home.razor) : lue une seule fois pour toute la requête. Quand
        // elle est désactivée, tout le filtrage/blocage ci-dessous est court-circuité (comme la bascule
        // "Protection" d'AdGuard Home) ; les réécritures DNS, le cache et les statistiques restent actifs.
        bool protectionEnabled = protectionState.IsEnabled;

        // "Réécritures DNS" (/filters/rewrites) : vérifiée en tout premier, avant même les listes
        // d'autorisation/blocage ci-dessous. Une réponse personnalisée explicitement configurée par
        // l'utilisateur pour ce domaine prend toujours le dessus, comme chez AdGuard Home.
        byte[]? rewriteResponse = await rewriteService.TryBuildRewriteResponseAsync(query, clientAddress, cancellationToken);

        if (rewriteResponse is not null)
        {
            stopwatch.Stop();
            RecordActivity(domain, clientAddress, queryType, QueryLogReason.Rewritten, reasonDetail: null, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
            return rewriteResponse;
        }

        // "Règles de filtrage personnalisées" (/filters/custom-rules) : vérifiées juste après les
        // réécritures, avant les listes d'autorisation/blocage abonnées ci-dessous. Une exception
        // ("@@||domaine.tld^") prime sur tout, y compris les listes de blocage abonnées ; une règle de
        // blocage personnalisée (domaine, expression régulière, ligne "hosts") est, elle, appliquée avant
        // les listes de blocage abonnées.
        bool isCustomExcepted = domain.Length != 0 && customFilterRulesService.IsExcepted(domain);

        if (protectionEnabled && !isCustomExcepted && customFilterRulesService.TryBuildBlockResponse(query, out byte[]? customRuleResponse, out string? customRuleDetail))
        {
            stopwatch.Stop();
            RecordActivity(domain, clientAddress, queryType, QueryLogReason.CustomRule, customRuleDetail, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
            return customRuleResponse;
        }

        // "Listes d'autorisation DNS" (/filters/allowlists) : un domaine qui y figure est toujours
        // résolu normalement, même s'il figure aussi dans une liste de blocage ci-dessous. Ne couvre que
        // les listes de blocage : la Sécurité de navigation et le Contrôle parental restent appliqués
        // indépendamment, comme sur AdGuard Home.
        bool isAllowlisted = domain.Length != 0 && allowlistService.IsAllowed(domain);

        // Protection désactivée : équivaut à contourner tous les blocages ci-dessous, comme si le
        // domaine était à la fois en liste d'autorisation et excepté des règles personnalisées.
        bool bypassSubscribedBlocking = !protectionEnabled || isAllowlisted || isCustomExcepted;

        // "Services bloqués" (/filters/blocked-services) : même priorité que les listes de blocage
        // abonnées ci-dessous (contournée par les mêmes exceptions), et respecte "Suspendre le blocage
        // des services" en interne.
        if (!bypassSubscribedBlocking && blockedServicesService.TryBuildBlockResponse(query, out byte[]? blockedServiceResponse, out string? blockedServiceDetail))
        {
            stopwatch.Stop();
            RecordActivity(domain, clientAddress, queryType, QueryLogReason.BlockedService, blockedServiceDetail, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
            return blockedServiceResponse;
        }

        // Domaine bloqué par une liste de filtres ("Bloquer les domaines à l'aide de filtres") :
        // réponse construite selon le mode de blocage configuré, sans jamais consulter le cache DNS ni
        // les serveurs en amont. Recalculée à chaque requête (coût négligeable, pas d'appel réseau) pour
        // toujours refléter le mode de blocage et les listes actuellement en vigueur. Une exception des
        // règles personnalisées ci-dessus bloque aussi cette vérification, exactement comme
        // "isAllowlisted".
        if (!bypassSubscribedBlocking && filterService.TryBuildBlockResponse(query, out byte[]? blockedResponse, out string? filterDetail))
        {
            stopwatch.Stop();
            RecordActivity(domain, clientAddress, queryType, QueryLogReason.Filtered, filterDetail, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
            return blockedResponse;
        }

        // Sécurité de navigation, contrôle parental et recherche sécurisée : coupés eux aussi tant que la
        // protection est désactivée, comme les blocages ci-dessus. Le bloc entier est sauté (pas
        // seulement le résultat ignoré) pour éviter les appels réseau/E-S inutiles de ces services.
        if (protectionEnabled)
        {
            // "Utilisez le service Sécurité de navigation d'OnyxFilter" : vérifié après les listes de
            // filtres (moins coûteux, purement local) mais avant le cache et les serveurs en amont, pour
            // les mêmes raisons (le résultat peut changer sans que la requête n'ait de rapport avec le
            // cache DNS classique). Échec ouvert intégré à TryBuildBlockResponseAsync : une panne du
            // service tiers ne bloque jamais la résolution.
            byte[]? browsingSecurityBlockedResponse = await browsingSecurityService.TryBuildBlockResponseAsync(query, cancellationToken);

            if (browsingSecurityBlockedResponse is not null)
            {
                stopwatch.Stop();
                RecordActivity(domain, clientAddress, queryType, QueryLogReason.SecurityThreat, reasonDetail: null, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
                return browsingSecurityBlockedResponse;
            }

            // "Utiliser le contrôle parental d'OnyxFilter" : même principe et même échec ouvert que la
            // Sécurité de navigation ci-dessus (protocole identique côté AdGuard Home, seul le jeu de
            // domaines signalés diffère).
            byte[]? parentalControlBlockedResponse = await parentalControlService.TryBuildBlockResponseAsync(query, cancellationToken);

            if (parentalControlBlockedResponse is not null)
            {
                stopwatch.Stop();
                RecordActivity(domain, clientAddress, queryType, QueryLogReason.ParentalControl, reasonDetail: null, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
                return parentalControlBlockedResponse;
            }

            // "Utiliser la Recherche Sécurisée" : ni un blocage ni une réponse mise en cache (elle combine
            // un enregistrement CNAME synthétisé et une adresse déjà résolue), donc vérifiée ici aussi,
            // avant le cache DNS habituel et les serveurs en amont pour la requête d'origine (la
            // résolution de la cible de la redirection, elle, passe bien par les serveurs en amont
            // configurés).
            byte[]? safeSearchResponse = await safeSearchService.TryBuildRewriteResponseAsync(query, clientAddress, cancellationToken);

            if (safeSearchResponse is not null)
            {
                stopwatch.Stop();
                RecordActivity(domain, clientAddress, queryType, QueryLogReason.SafeSearch, reasonDetail: null, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
                return safeSearchResponse;
            }
        }

        // EDNS Client Subnet actif : la réponse dépend du sous-réseau du client, que la clé de cache
        // (nom+type+classe) ne distingue pas. Le cache est donc entièrement contourné pour ne jamais
        // servir à un client la réponse géolocalisée obtenue pour un autre.
        if (upstreamResolver.IsClientSubnetEnabled)
        {
            UpstreamResolutionResult ecsResult = await upstreamResolver.ResolveAsync(query, clientAddress, cancellationToken);
            stopwatch.Stop();
            RecordActivity(domain, clientAddress, queryType, QueryLogReason.Resolved, reasonDetail: null, ecsResult.UpstreamServer, ecsResult.UpstreamServer is null ? null : ecsResult.ResponseTimeMs, stopwatch.ElapsedMilliseconds);
            return ecsResult.Response;
        }

        ushort transactionId = 0;
        string cacheKey = string.Empty;
        bool hasCacheKey = false;

        if (DnsMessageParser.TryReadTransactionId(query, out transactionId) && DnsMessageParser.TryBuildCacheKey(query, out cacheKey))
        {
            hasCacheKey = true;

            byte[]? cached = dnsCache.TryGet(cacheKey, out bool isStale);

            if (cached is not null)
            {
                // Cache optimiste : la réponse expirée est servie immédiatement, et l'entrée est
                // rafraîchie en arrière-plan pour les prochaines requêtes.
                if (isStale)
                {
                    StartBackgroundRefresh(cacheKey, query, cancellationToken);
                }

                DnsMessageParser.WriteTransactionId(cached, transactionId);
                stopwatch.Stop();
                RecordActivity(domain, clientAddress, queryType, QueryLogReason.Cached, reasonDetail: null, upstreamServer: null, upstreamResponseTimeMs: null, stopwatch.ElapsedMilliseconds);
                return cached;
            }
        }

        UpstreamResolutionResult result = await upstreamResolver.ResolveAsync(query, clientAddress, cancellationToken);

        if (result.Response is not null && hasCacheKey && DnsMessageParser.TryGetCacheableTtl(result.Response, out int ttlSeconds))
        {
            dnsCache.Set(cacheKey, result.Response, TimeSpan.FromSeconds(ttlSeconds));
        }

        stopwatch.Stop();
        RecordActivity(domain, clientAddress, queryType, QueryLogReason.Resolved, reasonDetail: null, result.UpstreamServer, result.UpstreamServer is null ? null : result.ResponseTimeMs, stopwatch.ElapsedMilliseconds);

        return result.Response;
    }

    // N'enregistre rien si le nom de domaine n'a pas pu être lu (requête malformée) : ce cas ne
    // correspond à aucune requête client exploitable, ni pour les statistiques ni pour le journal des
    // requêtes.
    private void RecordActivity(string domain, IPAddress? clientAddress, ushort queryType, QueryLogReason reason, string? reasonDetail, string? upstreamServer, long? upstreamResponseTimeMs, long processingTimeMs)
    {
        if (domain.Length == 0)
        {
            return;
        }

        bool blocked = reason is QueryLogReason.CustomRule
            or QueryLogReason.Filtered
            or QueryLogReason.BlockedService
            or QueryLogReason.SecurityThreat
            or QueryLogReason.ParentalControl;

        statisticsService.RecordQuery(domain, clientAddress, blocked, upstreamServer, upstreamResponseTimeMs, processingTimeMs);
        queryLogService.RecordQuery(domain, clientAddress, queryType, reason, reasonDetail, upstreamServer, processingTimeMs);
    }

    // Cache optimiste : rafraîchit une entrée expirée en interrogeant les serveurs en amont, sans
    // bloquer la réponse au client. Un seul rafraîchissement à la fois par clé de cache.
    private void StartBackgroundRefresh(string cacheKey, byte[] query, CancellationToken cancellationToken)
    {
        lock (pendingRefreshKeys)
        {
            if (!pendingRefreshKeys.Add(cacheKey))
            {
                return;
            }
        }

        _ = RefreshCacheEntryAsync(cacheKey, query, cancellationToken);
    }

    private async Task RefreshCacheEntryAsync(string cacheKey, byte[] query, CancellationToken cancellationToken)
    {
        try
        {
            // Pas de client précis pour un rafraîchissement en arrière-plan (aucune incidence : ce
            // chemin n'est emprunté que lorsque le cache est actif, donc lorsque EDNS Client Subnet
            // est désactivé). Ce rafraîchissement interne n'est pas non plus compté dans les
            // statistiques du tableau de bord : il ne correspond à aucune requête d'un client.
            UpstreamResolutionResult result = await upstreamResolver.ResolveAsync(query, clientAddress: null, cancellationToken);

            if (result.Response is not null && DnsMessageParser.TryGetCacheableTtl(result.Response, out int ttlSeconds))
            {
                dnsCache.Set(cacheKey, result.Response, TimeSpan.FromSeconds(ttlSeconds));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec du rafraîchissement en arrière-plan de l'entrée de cache {CacheKey}.", cacheKey);
        }
        finally
        {
            lock (pendingRefreshKeys)
            {
                pendingRefreshKeys.Remove(cacheKey);
            }
        }
    }
}
