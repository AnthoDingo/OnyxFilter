using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Statistics;

// Statistiques du tableau de bord (Home.razor), agrégées par heure en mémoire (chemin chaud RecordQuery
// sans aucune E/S, pour rester léger sur un Raspberry Pi 3) et persistées périodiquement en base SQLite
// (IDnsStatisticsRepository) : seules les tranches horaires modifiées sont réécrites, toutes les
// FlushIntervalSeconds secondes et une dernière fois à l'arrêt, puis rechargées au démarrage. Elles
// survivent donc à un redémarrage du serveur (au pire, la dernière minute est perdue en cas d'arrêt
// brutal). "Intervalle de conservation des statistiques" borne la durée de conservation des buckets
// horaires (mémoire et base) ; le tableau de bord affiche toujours, lui, une fenêtre fixe des 24
// dernières heures (ou moins si la rétention configurée est plus courte).
public sealed class DnsStatisticsService : IDnsStatisticsService, IDisposable
{
    // Nombre maximal de lignes retournées par panneau ("Meilleurs clients", "Domaines les plus
    // recherchés", "Top amonts", etc.) : au-delà, seules les entrées les plus fréquentes sont conservées,
    // pour rester lisible et éviter de trier des ensembles arbitrairement grands.
    private const int TopEntryLimit = 10;

    private static readonly int DisplayWindowHours = 24;

    // Intervalle d'écriture en base des tranches horaires modifiées. 60 s = compromis entre usure/charge
    // SQLite (une transaction par minute au plus) et perte maximale de données en cas d'arrêt brutal.
    private const int FlushIntervalSeconds = 60;

    private sealed class HourlyBucket
    {
        public long TotalQueries;
        public long BlockedQueries;
        public long TotalProcessingTimeMs;
        public long ProcessingTimeSampleCount;
        public readonly Dictionary<string, long> QueriesByClient = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> QueriesByDomain = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> BlockedQueriesByDomain = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> QueriesByUpstream = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> UpstreamResponseTimeTotalMs = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly Dictionary<string, long> UpstreamResponseTimeSampleCount = new Dictionary<string, long>(StringComparer.Ordinal);
    }

    private readonly ILocalSettingsStore settingsStore;
    private readonly IDnsStatisticsRepository repository;
    private readonly ILogger<DnsStatisticsService> logger;
    private readonly object syncRoot = new object();

    // Clé = nombre d'heures écoulées depuis l'epoch (DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour), donc
    // naturellement croissante et triée dans le temps.
    private readonly SortedDictionary<long, HourlyBucket> bucketsByHourKey = new SortedDictionary<long, HourlyBucket>();

    // Tranches horaires modifiées depuis la dernière écriture en base (protégé par syncRoot).
    private readonly HashSet<long> dirtyHourKeys = new HashSet<long>();

    private Timer? flushTimer;

    private bool isEnabled = true;
    private int retentionHours = 24;
    private bool ignoreDomains;
    private HashSet<string> ignoredExactDomains = new HashSet<string>(StringComparer.Ordinal);
    private string[] ignoredDomainSuffixes = Array.Empty<string>();
    private bool anonymizeClientIp;

    public DnsStatisticsService(
        ILocalSettingsStore settingsStore,
        IDnsStatisticsRepository repository,
        ILogger<DnsStatisticsService> logger)
    {
        this.settingsStore = settingsStore;
        this.repository = repository;
        this.logger = logger;
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await ReloadConfigurationAsync();

        try
        {
            await repository.InitializeAsync(cancellationToken);

            bool loadPersistedBuckets;

            lock (syncRoot)
            {
                loadPersistedBuckets = isEnabled;
            }

            if (loadPersistedBuckets)
            {
                IReadOnlyList<DnsStatisticsBucketRecord> records = await repository.LoadAsync(cancellationToken);
                long currentHourKey = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour;
                int loadedCount = 0;

                lock (syncRoot)
                {
                    long cutoffHourKey = currentHourKey - retentionHours;

                    foreach (DnsStatisticsBucketRecord record in records)
                    {
                        if (record.HourKey < cutoffHourKey)
                        {
                            continue;
                        }

                        bucketsByHourKey[record.HourKey] = ToBucket(record);
                        loadedCount++;
                    }
                }

                logger.LogInformation("Statistiques DNS rechargées depuis la base : {Count} tranche(s) horaire(s).", loadedCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de recharger les statistiques DNS depuis la base : démarrage avec des statistiques vides.");
        }

        TimeSpan flushInterval = TimeSpan.FromSeconds(FlushIntervalSeconds);
        flushTimer = new Timer(OnFlushTimer, null, flushInterval, flushInterval);
    }

    public void RecordQuery(
        string domain,
        IPAddress? clientAddress,
        bool blocked,
        string? upstreamServer,
        long? upstreamResponseTimeMs,
        long processingTimeMs)
    {
        bool enabled;
        bool ignore;
        HashSet<string> exactDomains;
        string[] suffixes;
        bool anonymize;

        lock (syncRoot)
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

        string clientKey = FormatClientKey(clientAddress, anonymize);
        long hourKey = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour;

        lock (syncRoot)
        {
            if (!bucketsByHourKey.TryGetValue(hourKey, out HourlyBucket? bucket))
            {
                bucket = new HourlyBucket();
                bucketsByHourKey[hourKey] = bucket;
            }

            bucket.TotalQueries++;
            IncrementCount(bucket.QueriesByClient, clientKey);
            IncrementCount(bucket.QueriesByDomain, normalizedDomain);

            if (blocked)
            {
                bucket.BlockedQueries++;
                IncrementCount(bucket.BlockedQueriesByDomain, normalizedDomain);
            }

            if (!string.IsNullOrEmpty(upstreamServer))
            {
                IncrementCount(bucket.QueriesByUpstream, upstreamServer);

                if (upstreamResponseTimeMs is long responseTimeMs)
                {
                    bucket.UpstreamResponseTimeTotalMs[upstreamServer] = bucket.UpstreamResponseTimeTotalMs.GetValueOrDefault(upstreamServer) + responseTimeMs;
                    bucket.UpstreamResponseTimeSampleCount[upstreamServer] = bucket.UpstreamResponseTimeSampleCount.GetValueOrDefault(upstreamServer) + 1;
                }
            }

            bucket.TotalProcessingTimeMs += processingTimeMs;
            bucket.ProcessingTimeSampleCount++;

            dirtyHourKeys.Add(hourKey);

            PruneOldBuckets_NoLock(hourKey);
        }
    }

    public DnsStatisticsSnapshot GetSnapshot()
    {
        lock (syncRoot)
        {
            if (!isEnabled)
            {
                return DnsStatisticsSnapshot.Empty;
            }

            long currentHourKey = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour;
            long windowStartHourKey = currentHourKey - DisplayWindowHours + 1;

            long totalQueries = 0;
            long blockedQueries = 0;
            long totalProcessingTimeMs = 0;
            long processingTimeSampleCount = 0;
            Dictionary<string, long> queriesByClient = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> queriesByDomain = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> blockedQueriesByDomain = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> queriesByUpstream = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> upstreamResponseTimeTotalMs = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> upstreamResponseTimeSampleCount = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (KeyValuePair<long, HourlyBucket> pair in bucketsByHourKey)
            {
                if (pair.Key < windowStartHourKey || pair.Key > currentHourKey)
                {
                    continue;
                }

                HourlyBucket bucket = pair.Value;

                totalQueries += bucket.TotalQueries;
                blockedQueries += bucket.BlockedQueries;
                totalProcessingTimeMs += bucket.TotalProcessingTimeMs;
                processingTimeSampleCount += bucket.ProcessingTimeSampleCount;

                MergeCounts(queriesByClient, bucket.QueriesByClient);
                MergeCounts(queriesByDomain, bucket.QueriesByDomain);
                MergeCounts(blockedQueriesByDomain, bucket.BlockedQueriesByDomain);
                MergeCounts(queriesByUpstream, bucket.QueriesByUpstream);
                MergeCounts(upstreamResponseTimeTotalMs, bucket.UpstreamResponseTimeTotalMs);
                MergeCounts(upstreamResponseTimeSampleCount, bucket.UpstreamResponseTimeSampleCount);
            }

            List<DnsStatisticsResponseTimeEntry> responseTimes = queriesByUpstream.Keys
                .Select(upstream =>
                {
                    long totalMs = upstreamResponseTimeTotalMs.GetValueOrDefault(upstream);
                    long sampleCount = upstreamResponseTimeSampleCount.GetValueOrDefault(upstream);
                    int averageMs = sampleCount > 0 ? (int)(totalMs / sampleCount) : 0;
                    return new DnsStatisticsResponseTimeEntry(upstream, averageMs);
                })
                .OrderByDescending(entry => queriesByUpstream.GetValueOrDefault(entry.Upstream))
                .Take(TopEntryLimit)
                .ToList();

            int averageProcessingTimeMs = processingTimeSampleCount > 0
                ? (int)(totalProcessingTimeMs / processingTimeSampleCount)
                : 0;

            List<DnsStatisticsHourlyPoint> hourlySeries = BuildHourlySeries_NoLock(windowStartHourKey, currentHourKey);

            return new DnsStatisticsSnapshot(
                totalQueries,
                blockedQueries,
                averageProcessingTimeMs,
                ToTopEntries(queriesByClient),
                ToTopEntries(queriesByDomain),
                ToTopEntries(blockedQueriesByDomain),
                ToTopEntries(queriesByUpstream),
                responseTimes,
                hourlySeries);
        }
    }

    // Un point par heure de windowStartHourKey à currentHourKey inclus (24 points avec la fenêtre
    // d'affichage actuelle), zéro-rempli pour les heures sans tranche en mémoire. Appelé sous syncRoot.
    private List<DnsStatisticsHourlyPoint> BuildHourlySeries_NoLock(long windowStartHourKey, long currentHourKey)
    {
        List<DnsStatisticsHourlyPoint> series = new List<DnsStatisticsHourlyPoint>((int)(currentHourKey - windowStartHourKey + 1));

        for (long hourKey = windowStartHourKey; hourKey <= currentHourKey; hourKey++)
        {
            long hourTotalQueries = 0;
            long hourBlockedQueries = 0;

            if (bucketsByHourKey.TryGetValue(hourKey, out HourlyBucket? hourlyBucket))
            {
                hourTotalQueries = hourlyBucket.TotalQueries;
                hourBlockedQueries = hourlyBucket.BlockedQueries;
            }

            DateTime hourStartUtc = new DateTime(hourKey * TimeSpan.TicksPerHour, DateTimeKind.Utc);
            series.Add(new DnsStatisticsHourlyPoint(hourStartUtc, hourTotalQueries, hourBlockedQueries));
        }

        return series;
    }

    // Même fenêtre glissante que GetSnapshot(), mais sans la limite TopEntryLimit : voir
    // IDnsStatisticsService.GetAllClients.
    public IReadOnlyList<DnsStatisticsEntry> GetAllClients()
    {
        lock (syncRoot)
        {
            if (!isEnabled)
            {
                return Array.Empty<DnsStatisticsEntry>();
            }

            long currentHourKey = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour;
            long windowStartHourKey = currentHourKey - DisplayWindowHours + 1;

            Dictionary<string, long> queriesByClient = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (KeyValuePair<long, HourlyBucket> pair in bucketsByHourKey)
            {
                if (pair.Key < windowStartHourKey || pair.Key > currentHourKey)
                {
                    continue;
                }

                MergeCounts(queriesByClient, pair.Value.QueriesByClient);
            }

            return queriesByClient
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new DnsStatisticsEntry(pair.Key, pair.Value))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            bucketsByHourKey.Clear();
            dirtyHourKeys.Clear();
        }

        _ = ClearDatabaseAsync();

        logger.LogInformation("Statistiques DNS effacées.");
    }

    private async Task ClearDatabaseAsync()
    {
        try
        {
            await repository.ClearAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible d'effacer les statistiques DNS en base.");
        }
    }

    private void OnFlushTimer(object? state)
    {
        _ = FlushAsync();
    }

    // Écrit en base les tranches horaires modifiées depuis le dernier passage, puis purge les lignes plus
    // anciennes que la rétention. Les copies sont faites sous verrou (dictionnaires de petite taille), les
    // E/S SQLite en dehors. En cas d'échec, les clés sont re-marquées sales pour retenter au prochain tick.
    private async Task FlushAsync()
    {
        List<DnsStatisticsBucketRecord> records = new List<DnsStatisticsBucketRecord>();
        long cutoffHourKey;

        lock (syncRoot)
        {
            if (dirtyHourKeys.Count == 0)
            {
                return;
            }

            cutoffHourKey = DateTime.UtcNow.Ticks / TimeSpan.TicksPerHour - retentionHours;

            foreach (long hourKey in dirtyHourKeys)
            {
                if (bucketsByHourKey.TryGetValue(hourKey, out HourlyBucket? bucket))
                {
                    records.Add(ToRecord(hourKey, bucket));
                }
            }

            dirtyHourKeys.Clear();
        }

        try
        {
            await repository.SaveAsync(records, CancellationToken.None);
            await repository.DeleteOlderThanAsync(cutoffHourKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible d'écrire les statistiques DNS en base : nouvelle tentative au prochain cycle.");

            lock (syncRoot)
            {
                foreach (DnsStatisticsBucketRecord record in records)
                {
                    dirtyHourKeys.Add(record.HourKey);
                }
            }
        }
    }

    private static DnsStatisticsBucketRecord ToRecord(long hourKey, HourlyBucket bucket)
    {
        return new DnsStatisticsBucketRecord
        {
            HourKey = hourKey,
            TotalQueries = bucket.TotalQueries,
            BlockedQueries = bucket.BlockedQueries,
            TotalProcessingTimeMs = bucket.TotalProcessingTimeMs,
            ProcessingTimeSampleCount = bucket.ProcessingTimeSampleCount,
            QueriesByClient = new Dictionary<string, long>(bucket.QueriesByClient, StringComparer.Ordinal),
            QueriesByDomain = new Dictionary<string, long>(bucket.QueriesByDomain, StringComparer.Ordinal),
            BlockedQueriesByDomain = new Dictionary<string, long>(bucket.BlockedQueriesByDomain, StringComparer.Ordinal),
            QueriesByUpstream = new Dictionary<string, long>(bucket.QueriesByUpstream, StringComparer.Ordinal),
            UpstreamResponseTimeTotalMs = new Dictionary<string, long>(bucket.UpstreamResponseTimeTotalMs, StringComparer.Ordinal),
            UpstreamResponseTimeSampleCount = new Dictionary<string, long>(bucket.UpstreamResponseTimeSampleCount, StringComparer.Ordinal),
        };
    }

    private static HourlyBucket ToBucket(DnsStatisticsBucketRecord record)
    {
        HourlyBucket bucket = new HourlyBucket
        {
            TotalQueries = record.TotalQueries,
            BlockedQueries = record.BlockedQueries,
            TotalProcessingTimeMs = record.TotalProcessingTimeMs,
            ProcessingTimeSampleCount = record.ProcessingTimeSampleCount,
        };

        MergeCounts(bucket.QueriesByClient, record.QueriesByClient);
        MergeCounts(bucket.QueriesByDomain, record.QueriesByDomain);
        MergeCounts(bucket.BlockedQueriesByDomain, record.BlockedQueriesByDomain);
        MergeCounts(bucket.QueriesByUpstream, record.QueriesByUpstream);
        MergeCounts(bucket.UpstreamResponseTimeTotalMs, record.UpstreamResponseTimeTotalMs);
        MergeCounts(bucket.UpstreamResponseTimeSampleCount, record.UpstreamResponseTimeSampleCount);

        return bucket;
    }

    private static void IncrementCount(Dictionary<string, long> counts, string key)
    {
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static void MergeCounts(Dictionary<string, long> target, Dictionary<string, long> source)
    {
        foreach (KeyValuePair<string, long> pair in source)
        {
            target[pair.Key] = target.GetValueOrDefault(pair.Key) + pair.Value;
        }
    }

    private static IReadOnlyList<DnsStatisticsEntry> ToTopEntries(Dictionary<string, long> counts)
    {
        return counts
            .OrderByDescending(pair => pair.Value)
            .Take(TopEntryLimit)
            .Select(pair => new DnsStatisticsEntry(pair.Key, pair.Value))
            .ToList();
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

    // "Anonymiser les adresses IP des clients" : conserve un préfixe de sous-réseau plutôt que l'adresse
    // complète (dernier octet à zéro en IPv4, préfixe /48 conservé en IPv6), pour que le panneau "Meilleurs
    // clients" reste utile sans stocker l'adresse exacte d'un client.
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

    private void PruneOldBuckets_NoLock(long currentHourKey)
    {
        long cutoffHourKey = currentHourKey - retentionHours;
        List<long> staleKeys = bucketsByHourKey.Keys.Where(key => key < cutoffHourKey).ToList();

        foreach (long staleKey in staleKeys)
        {
            bucketsByHourKey.Remove(staleKey);
        }
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
            string ignoredDomainsText = settings.General.IgnoredStatisticsDomainsText ?? string.Empty;

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

            // "Personnalisé" utilise StatisticsRetentionCustomHours (bornée à [1, 24*365] pour éviter à la
            // fois une purge immédiate en cas de valeur nulle/négative et une conservation illimitée en
            // cas de valeur aberrante, sur un serveur volontairement léger).
            int customRetentionHours = Math.Clamp(settings.General.StatisticsRetentionCustomHours, 1, 24 * 365);

            int mappedRetentionHours = settings.General.StatisticsRetention switch
            {
                RetentionPeriod.Custom => customRetentionHours,
                RetentionPeriod.SixHours => 6,
                RetentionPeriod.TwentyFourHours => 24,
                RetentionPeriod.SevenDays => 24 * 7,
                RetentionPeriod.ThirtyDays => 24 * 30,
                RetentionPeriod.NinetyDays => 24 * 90,
                _ => 24,
            };

            lock (syncRoot)
            {
                isEnabled = settings.General.EnableStatistics;
                retentionHours = mappedRetentionHours;
                ignoreDomains = settings.General.IgnoreDomainsInStatistics;
                ignoredExactDomains = exactDomains;
                ignoredDomainSuffixes = suffixes.ToArray();
                anonymizeClientIp = settings.General.AnonymizeClientIp;

                if (!isEnabled)
                {
                    bucketsByHourKey.Clear();
                    dirtyHourKeys.Clear();
                }
            }

            if (!settings.General.EnableStatistics)
            {
                // Statistiques désactivées : la base est vidée aussi, par cohérence avec la mémoire.
                _ = ClearDatabaseAsync();
            }

            logger.LogInformation(
                "Configuration des statistiques DNS rechargée : activées={Enabled}, rétention={RetentionHours}h.",
                settings.General.EnableStatistics,
                mappedRetentionHours);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger la configuration des statistiques DNS.");
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;

        flushTimer?.Dispose();
        flushTimer = null;

        // Dernière écriture synchrone à l'arrêt de l'hôte : les compteurs accumulés depuis le dernier
        // cycle ne sont pas perdus. FlushAsync intercepte déjà ses propres erreurs.
        FlushAsync().GetAwaiter().GetResult();
    }
}
