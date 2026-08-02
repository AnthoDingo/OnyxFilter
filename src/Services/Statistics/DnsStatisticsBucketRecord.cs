using System;
using System.Collections.Generic;

namespace OnyxFilter.Services.Statistics;

// Représentation persistable d'une tranche horaire de statistiques DNS (table DnsStatisticsBuckets).
// Sérialisée en JSON dans la colonne Payload ; HourKey est la clé primaire de la table (nombre d'heures
// écoulées depuis l'epoch, même convention que DnsStatisticsService).
public sealed class DnsStatisticsBucketRecord
{
    public long HourKey { get; set; }

    public long TotalQueries { get; set; }

    public long BlockedQueries { get; set; }

    public long TotalProcessingTimeMs { get; set; }

    public long ProcessingTimeSampleCount { get; set; }

    public Dictionary<string, long> QueriesByClient { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public Dictionary<string, long> QueriesByDomain { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public Dictionary<string, long> BlockedQueriesByDomain { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public Dictionary<string, long> QueriesByUpstream { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public Dictionary<string, long> UpstreamResponseTimeTotalMs { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    public Dictionary<string, long> UpstreamResponseTimeSampleCount { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
}
