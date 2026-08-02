using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.QueryLog;

// Journal des requêtes DNS (Home.razor -> /query-log). Chemin chaud RecordQuery sans aucune E/S (juste
// une mise en file d'attente bornée en mémoire), pour rester léger sur un Raspberry Pi 3 : les écritures
// SQLite sont regroupées par lot toutes les FlushIntervalSeconds secondes par un minuteur d'arrière-plan.
// La file d'attente est volontairement bornée (MaxQueuedEntries) : au-delà, les nouvelles requêtes sont
// simplement ignorées jusqu'au prochain vidage plutôt que de laisser la mémoire croître sans limite lors
// d'un pic de trafic ou d'un ralentissement de la base. Contrairement à DnsStatisticsService, désactiver
// le journal n'efface pas l'historique déjà écrit : seul le bouton "Effacer journal des requêtes" le fait.
//
// Tolérance de panne (base SQLite inaccessible ou corrompue) : la résolution DNS n'est jamais affectée
// (RecordQuery ne fait aucune E/S). En cas d'échec d'écriture, le lot est remis en file d'attente et
// réessayé avec un intervalle croissant (5s, 10s, 20s, 40s, 60s maximum). Si l'échec persiste 60 secondes
// ou plus, le journal bascule dans un fichier quotidien de secours (JSON Lines, un fichier par jour) tant
// que la base reste inaccessible ; il repasse automatiquement en base dès qu'une écriture y réussit à
// nouveau. Les lignes écrites dans le fichier de secours ne sont ni affichées par GetPageAsync, ni
// réimportées automatiquement en base : elles restent consultables manuellement sur disque.
public sealed class DnsQueryLogService : IDnsQueryLogService, IDisposable
{
    // Borne haute de la file d'attente en mémoire : au débit maximal réaliste sur un Raspberry Pi 3
    // (quelques centaines de requêtes/s), 20 000 lignes représentent au plus quelques secondes de trafic
    // et un budget mémoire de l'ordre du mégaoctet (chaque DnsQueryLogRecord ne retient que de courtes
    // chaînes). Au-delà, on préfère perdre des lignes de journal plutôt que de menacer la mémoire du
    // serveur ou de ralentir la résolution DNS.
    private const int MaxQueuedEntries = 20_000;

    // Intervalle d'écriture en base des lignes en attente en fonctionnement normal. Plus court que celui
    // des statistiques (60 s) car le journal doit rester consultable "en direct" ; 5 s reste un compromis
    // raisonnable entre fraîcheur d'affichage et nombre de transactions SQLite (usure de la carte SD).
    private const int FlushIntervalSeconds = 5;

    // Intervalle maximal entre deux tentatives d'écriture en base lorsque celle-ci échoue de façon
    // répétée : au-delà, on cesse d'insister toutes les 5 secondes (bruit inutile dans les journaux,
    // coût de connexion répété) sans pour autant abandonner la détection d'un retour à la normale.
    private const int MaxBackoffSeconds = 60;

    // Durée d'échec continu au-delà de laquelle le journal bascule dans le fichier de secours plutôt que
    // de continuer à s'appuyer uniquement sur la file d'attente en mémoire.
    private const int FileFallbackThresholdSeconds = 60;

    private static readonly JsonSerializerOptions FallbackSerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
    };

    private readonly ILocalSettingsStore settingsStore;
    private readonly IDnsQueryLogRepository repository;
    private readonly ILogger<DnsQueryLogService> logger;
    private readonly string fallbackDirectory;

    private readonly ConcurrentQueue<DnsQueryLogRecord> pendingEntries = new ConcurrentQueue<DnsQueryLogRecord>();
    private int pendingCount;

    private readonly object configSyncRoot = new object();
    private Timer? flushTimer;
    private readonly SemaphoreSlim flushLock = new SemaphoreSlim(1, 1);

    private bool isEnabled = true;
    private int retentionHours = 24;
    private bool ignoreDomains;
    private HashSet<string> ignoredExactDomains = new HashSet<string>(StringComparer.Ordinal);
    private string[] ignoredDomainSuffixes = Array.Empty<string>();
    private bool anonymizeClientIp;

    // Protège l'état de santé de la persistance (indépendant de configSyncRoot, qui protège les réglages).
    private readonly object healthSyncRoot = new object();
    private DateTime? failureStartUtc;
    private int consecutiveFailures;
    private DateTime nextDbRetryUtc = DateTime.MinValue;
    private bool fallbackDirectoryEnsured;

    public DnsQueryLogService(
        ILocalSettingsStore settingsStore,
        IDnsQueryLogRepository repository,
        IHostEnvironment hostEnvironment,
        ILogger<DnsQueryLogService> logger)
    {
        this.settingsStore = settingsStore;
        this.repository = repository;
        this.logger = logger;
        this.fallbackDirectory = Path.Combine(hostEnvironment.ContentRootPath, "query-log-fallback");
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public bool IsEnabled
    {
        get
        {
            lock (configSyncRoot)
            {
                return isEnabled;
            }
        }
    }

    public bool IsDatabaseHealthy
    {
        get
        {
            lock (healthSyncRoot)
            {
                return !failureStartUtc.HasValue;
            }
        }
    }

    public string? FallbackFilePath
    {
        get
        {
            lock (healthSyncRoot)
            {
                return IsInFileFallback_NoLock() ? BuildFallbackFilePath(DateTime.UtcNow) : null;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ReloadConfigurationAsync();

        try
        {
            await repository.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Impossible d'initialiser le journal des requêtes DNS en base : les nouvelles requêtes seront mises en attente puis, si la base reste inaccessible, écrites dans un fichier de secours.");

            lock (healthSyncRoot)
            {
                RegisterFailure_NoLock();
            }
        }

        TimeSpan flushInterval = TimeSpan.FromSeconds(FlushIntervalSeconds);
        flushTimer = new Timer(OnFlushTimer, null, flushInterval, flushInterval);
    }

    public void RecordQuery(
        string domain,
        IPAddress? clientAddress,
        ushort queryType,
        QueryLogReason reason,
        string? reasonDetail,
        string? upstreamServer,
        long processingTimeMs)
    {
        bool enabled;
        bool ignore;
        HashSet<string> exactDomains;
        string[] suffixes;
        bool anonymize;

        lock (configSyncRoot)
        {
            enabled = isEnabled;
            ignore = ignoreDomains;
            exactDomains = ignoredExactDomains;
            suffixes = ignoredDomainSuffixes;
            anonymize = anonymizeClientIp;
        }

        if (!enabled)
        {
            return;
        }

        string normalizedDomain = string.IsNullOrEmpty(domain) ? "?" : domain.TrimEnd('.').ToLowerInvariant();

        if (ignore && IsDomainIgnored(normalizedDomain, exactDomains, suffixes))
        {
            return;
        }

        EnqueueIfUnderCap(new DnsQueryLogRecord
        {
            TimestampUtc = DateTime.UtcNow,
            Domain = normalizedDomain,
            ClientKey = FormatClientKey(clientAddress, anonymize),
            ReasonDetail = reasonDetail,
            QueryType = FormatQueryType(queryType),
            Reason = reason,
            UpstreamServer = upstreamServer,
            ProcessingTimeMs = processingTimeMs,
        });
    }

    // Utilisé aussi bien pour les nouvelles requêtes (RecordQuery) que pour remettre en file un lot dont
    // l'écriture en base a échoué (RequeueForRetry) : Interlocked.Increment reste correct même dépassé de
    // quelques unités par des threads concurrents, ce n'est qu'une borne approximative, pas un compteur
    // exact. File pleine : on abandonne la ligne plutôt que de laisser la mémoire croître sans limite.
    private bool EnqueueIfUnderCap(DnsQueryLogRecord record)
    {
        if (Interlocked.Increment(ref pendingCount) > MaxQueuedEntries)
        {
            Interlocked.Decrement(ref pendingCount);
            return false;
        }

        pendingEntries.Enqueue(record);
        return true;
    }

    public async Task<IReadOnlyList<DnsQueryLogRecord>> GetPageAsync(int skip, int take, string? searchText, QueryLogReason? reason, CancellationToken cancellationToken)
    {
        if (skip < 0)
        {
            skip = 0;
        }

        if (take <= 0)
        {
            take = 50;
        }

        try
        {
            return await repository.QueryPageAsync(skip, take, searchText, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de lire le journal des requêtes DNS en base.");
            return Array.Empty<DnsQueryLogRecord>();
        }
    }

    public async Task<bool> ClearAsync()
    {
        // Vide la file d'attente en mémoire (les lignes non encore écrites ne doivent pas réapparaître
        // après l'effacement).
        while (pendingEntries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }

        ClearFallbackFiles();

        try
        {
            await repository.ClearAsync(CancellationToken.None);

            lock (healthSyncRoot)
            {
                RegisterSuccess_NoLock();
            }

            logger.LogInformation("Journal des requêtes DNS effacé.");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible d'effacer le journal des requêtes DNS en base.");
            return false;
        }
    }

    private void ClearFallbackFiles()
    {
        try
        {
            if (Directory.Exists(fallbackDirectory))
            {
                Directory.Delete(fallbackDirectory, recursive: true);
            }

            fallbackDirectoryEnsured = false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impossible de supprimer les fichiers de secours du journal des requêtes DNS.");
        }
    }

    private void OnFlushTimer(object? state)
    {
        _ = FlushAsync();
    }

    // Vide la file d'attente en un seul lot (une transaction SQLite), puis purge les lignes plus
    // anciennes que la rétention configurée. flushLock garantit qu'un seul vidage s'exécute à la fois
    // (le minuteur peut se redéclencher avant la fin d'un vidage lent).
    //
    // Tant que la base répond, ce vidage se comporte comme avant (une transaction toutes les 5 s). En cas
    // d'échec, le rythme des tentatives ralentit (backoff jusqu'à 60 s) pour ne pas marteler une base en
    // panne ; le lot en attente est remis en file (RequeueForRetry) tant que l'échec dure moins de 60 s,
    // puis basculé dans le fichier de secours quotidien au-delà.
    private async Task FlushAsync()
    {
        if (!await flushLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            List<DnsQueryLogRecord> batch = new List<DnsQueryLogRecord>();

            while (pendingEntries.TryDequeue(out DnsQueryLogRecord? entry))
            {
                Interlocked.Decrement(ref pendingCount);
                batch.Add(entry);
            }

            int retention;

            lock (configSyncRoot)
            {
                retention = retentionHours;
            }

            bool attemptDb;

            lock (healthSyncRoot)
            {
                attemptDb = DateTime.UtcNow >= nextDbRetryUtc;
            }

            bool inFileFallback;

            if (attemptDb)
            {
                try
                {
                    if (batch.Count > 0)
                    {
                        await repository.InsertBatchAsync(batch, CancellationToken.None);
                    }
                    else if (!await repository.PingAsync(CancellationToken.None))
                    {
                        // Rien à écrire, mais on profite de ce tour pour détecter un éventuel retour de la
                        // base sans attendre la prochaine requête DNS.
                        throw new IOException("Sondage du journal des requêtes DNS en échec.");
                    }

                    bool wasUnhealthy;

                    lock (healthSyncRoot)
                    {
                        wasUnhealthy = RegisterSuccess_NoLock();
                    }

                    if (wasUnhealthy)
                    {
                        logger.LogInformation("Le journal des requêtes DNS a retrouvé l'accès à la base ; fin du repli fichier éventuel.");
                    }

                    batch.Clear();
                    inFileFallback = false;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Impossible d'écrire le journal des requêtes DNS en base ({Count} ligne(s) en attente).", batch.Count);

                    lock (healthSyncRoot)
                    {
                        RegisterFailure_NoLock();
                        inFileFallback = IsInFileFallback_NoLock();
                    }
                }
            }
            else
            {
                lock (healthSyncRoot)
                {
                    inFileFallback = IsInFileFallback_NoLock();
                }
            }

            if (batch.Count > 0)
            {
                if (inFileFallback)
                {
                    await WriteToFallbackFileAsync(batch);
                }
                else
                {
                    RequeueForRetry(batch);
                }
            }

            if (attemptDb)
            {
                await repository.DeleteOlderThanAsync(DateTime.UtcNow.AddHours(-retention), CancellationToken.None);
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    // Remet en file un lot dont l'écriture a échoué, pour nouvelle tentative au prochain vidage où la
    // base sera retestée. N'a d'effet que tant que l'échec dure moins de FileFallbackThresholdSeconds :
    // au-delà, WriteToFallbackFileAsync prend le relais.
    private void RequeueForRetry(List<DnsQueryLogRecord> batch)
    {
        foreach (DnsQueryLogRecord record in batch)
        {
            EnqueueIfUnderCap(record);
        }
    }

    private async Task WriteToFallbackFileAsync(List<DnsQueryLogRecord> batch)
    {
        try
        {
            EnsureFallbackDirectory();

            // Un lot de quelques secondes ne franchit presque jamais minuit : ce regroupement par date ne
            // coûte donc quasiment jamais plus d'une itération, mais reste correct si cela arrive.
            foreach (IGrouping<DateTime, DnsQueryLogRecord> group in batch.GroupBy(record => record.TimestampUtc.Date))
            {
                string path = BuildFallbackFilePath(group.Key);
                StringBuilder builder = new StringBuilder();

                foreach (DnsQueryLogRecord record in group)
                {
                    builder.Append(JsonSerializer.Serialize(record, FallbackSerializerOptions));
                    builder.Append('\n');
                }

                await File.AppendAllTextAsync(path, builder.ToString());
            }

            logger.LogWarning(
                "Journal des requêtes DNS : base indisponible, {Count} ligne(s) écrite(s) dans le fichier de secours ({Directory}).",
                batch.Count,
                fallbackDirectory);
        }
        catch (Exception ex)
        {
            // Dernier recours en échec lui aussi (disque plein/inaccessible) : les lignes de ce lot sont
            // perdues, mais la résolution DNS elle-même n'a jamais été affectée.
            logger.LogError(ex, "Impossible d'écrire le journal des requêtes DNS dans le fichier de secours ({Count} ligne(s) perdue(s)).", batch.Count);
        }
    }

    private void EnsureFallbackDirectory()
    {
        if (fallbackDirectoryEnsured)
        {
            return;
        }

        Directory.CreateDirectory(fallbackDirectory);
        fallbackDirectoryEnsured = true;
    }

    private string BuildFallbackFilePath(DateTime dateUtc)
    {
        return Path.Combine(fallbackDirectory, dateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
    }

    // À appeler sous healthSyncRoot.
    private bool IsInFileFallback_NoLock()
    {
        return failureStartUtc.HasValue && (DateTime.UtcNow - failureStartUtc.Value).TotalSeconds >= FileFallbackThresholdSeconds;
    }

    // À appeler sous healthSyncRoot. Calcule un intervalle avant nouvelle tentative croissant (5s, 10s,
    // 20s, 40s, 60s puis plafonné), pour ne pas marteler une base en panne toutes les 5 secondes.
    private void RegisterFailure_NoLock()
    {
        failureStartUtc ??= DateTime.UtcNow;
        consecutiveFailures++;
        double backoffSeconds = Math.Min(FlushIntervalSeconds * Math.Pow(2, consecutiveFailures - 1), MaxBackoffSeconds);
        nextDbRetryUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
    }

    // À appeler sous healthSyncRoot. Retourne true si l'état passait de "en échec" à "rétabli" (pour ne
    // logger la reprise qu'une seule fois).
    private bool RegisterSuccess_NoLock()
    {
        bool wasUnhealthy = failureStartUtc.HasValue;
        failureStartUtc = null;
        consecutiveFailures = 0;
        nextDbRetryUtc = DateTime.MinValue;
        return wasUnhealthy;
    }

    // Même convention de correspondance que IDnsAccessControl.IsDomainDisallowed : correspondance exacte,
    // ou "*.example.org" qui ignore "example.org" et tous ses sous-domaines.
    private static bool IsDomainIgnored(string normalizedDomain, HashSet<string> exactDomains, string[] suffixes)
    {
        if (exactDomains.Count == 0 && suffixes.Length == 0)
        {
            return false;
        }

        if (exactDomains.Contains(normalizedDomain))
        {
            return true;
        }

        foreach (string suffix in suffixes)
        {
            if (normalizedDomain.Length == suffix.Length)
            {
                if (string.Equals(normalizedDomain, suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (normalizedDomain.Length > suffix.Length
                && normalizedDomain.EndsWith(suffix, StringComparison.Ordinal)
                && normalizedDomain[normalizedDomain.Length - suffix.Length - 1] == '.')
            {
                return true;
            }
        }

        return false;
    }

    // "Anonymiser l'IP du client" : conserve un préfixe de sous-réseau plutôt que l'adresse complète
    // (dernier octet à zéro en IPv4, préfixe /48 conservé en IPv6). Même logique que DnsStatisticsService.
    private static string FormatClientKey(IPAddress? clientAddress, bool anonymize)
    {
        if (clientAddress is null)
        {
            return "Inconnu";
        }

        if (!anonymize)
        {
            return clientAddress.ToString();
        }

        byte[] addressBytes = clientAddress.GetAddressBytes();

        if (clientAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            addressBytes[^1] = 0;
        }
        else
        {
            for (int i = 6; i < addressBytes.Length; i++)
            {
                addressBytes[i] = 0;
            }
        }

        return new IPAddress(addressBytes).ToString();
    }

    // Types d'enregistrement DNS les plus courants, pour un affichage lisible dans le journal ; les
    // autres restent affichés sous la forme numérique "TYPE12345" (RFC 3597).
    private static string FormatQueryType(ushort queryType)
    {
        return queryType switch
        {
            1 => "A",
            2 => "NS",
            5 => "CNAME",
            6 => "SOA",
            12 => "PTR",
            15 => "MX",
            16 => "TXT",
            28 => "AAAA",
            33 => "SRV",
            41 => "OPT",
            43 => "DS",
            48 => "DNSKEY",
            65 => "HTTPS",
            257 => "CAA",
            _ => "TYPE" + queryType,
        };
    }

    private void OnSettingsChanged()
    {
        _ = ReloadConfigurationAsync();
    }

    private async Task ReloadConfigurationAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();

            HashSet<string> exactDomains = new HashSet<string>(StringComparer.Ordinal);
            List<string> suffixes = new List<string>();
            string ignoredDomainsText = settings.General.IgnoredLogDomainsText ?? string.Empty;

            foreach (string line in ignoredDomainsText.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string domain = line.Trim().TrimEnd('.').ToLowerInvariant();

                if (domain.Length == 0 || domain.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (domain.StartsWith("*.", StringComparison.Ordinal))
                {
                    string suffix = domain.Substring(2);

                    if (suffix.Length > 0)
                    {
                        suffixes.Add(suffix);
                    }
                }
                else
                {
                    exactDomains.Add(domain);
                }
            }

            // "Personnalisé" utilise LogRetentionCustomHours (bornée à [1, 24*365] pour éviter à la fois
            // une purge immédiate en cas de valeur nulle/négative et une conservation illimitée en cas de
            // valeur aberrante, sur un serveur volontairement léger).
            int customRetentionHours = Math.Clamp(settings.General.LogRetentionCustomHours, 1, 24 * 365);

            int mappedRetentionHours = settings.General.LogRetention switch
            {
                RetentionPeriod.Custom => customRetentionHours,
                RetentionPeriod.SixHours => 6,
                RetentionPeriod.TwentyFourHours => 24,
                RetentionPeriod.SevenDays => 24 * 7,
                RetentionPeriod.ThirtyDays => 24 * 30,
                RetentionPeriod.NinetyDays => 24 * 90,
                _ => 24,
            };

            lock (configSyncRoot)
            {
                isEnabled = settings.General.EnableQueryLog;
                retentionHours = mappedRetentionHours;
                ignoreDomains = settings.General.IgnoreDomainsInLog;
                ignoredExactDomains = exactDomains;
                ignoredDomainSuffixes = suffixes.ToArray();
                anonymizeClientIp = settings.General.AnonymizeClientIp;
            }

            // Réagit immédiatement à un raccourcissement de la rétention ("Rotation des journaux de
            // requêtes"), sans attendre le prochain vidage périodique. Best-effort : une base indisponible
            // à cet instant sera de toute façon retentée par le vidage périodique.
            _ = repository.DeleteOlderThanAsync(DateTime.UtcNow.AddHours(-mappedRetentionHours), CancellationToken.None);

            logger.LogInformation(
                "Configuration du journal des requêtes DNS rechargée : activé={Enabled}, rétention={RetentionHours}h.",
                settings.General.EnableQueryLog,
                mappedRetentionHours);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger la configuration du journal des requêtes DNS.");
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;

        flushTimer?.Dispose();
        flushTimer = null;

        // Dernière écriture synchrone à l'arrêt de l'hôte : les lignes accumulées depuis le dernier
        // cycle ne sont pas perdues. FlushAsync intercepte déjà ses propres erreurs.
        FlushAsync().GetAwaiter().GetResult();
    }
}
