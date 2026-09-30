using System;
using System.Collections.Generic;

namespace OnyxFilter.Services.Statistics;

// Photo instantanée des statistiques DNS sur la fenêtre glissante des dernières 24 heures (ou moins si
// "Intervalle de conservation des statistiques", Paramètres généraux, est réglé plus court), utilisée par
// le tableau de bord (Home.razor).
public sealed class DnsStatisticsSnapshot
{
    public static readonly DnsStatisticsSnapshot Empty = new DnsStatisticsSnapshot(
        totalQueries: 0,
        blockedQueries: 0,
        averageProcessingTimeMs: 0,
        topClients: Array.Empty<DnsStatisticsEntry>(),
        topSearchedDomains: Array.Empty<DnsStatisticsEntry>(),
        topBlockedDomains: Array.Empty<DnsStatisticsEntry>(),
        topUpstreams: Array.Empty<DnsStatisticsEntry>(),
        upstreamResponseTimes: Array.Empty<DnsStatisticsResponseTimeEntry>(),
        hourlySeries: Array.Empty<DnsStatisticsHourlyPoint>());

    public DnsStatisticsSnapshot(
        long totalQueries,
        long blockedQueries,
        int averageProcessingTimeMs,
        IReadOnlyList<DnsStatisticsEntry> topClients,
        IReadOnlyList<DnsStatisticsEntry> topSearchedDomains,
        IReadOnlyList<DnsStatisticsEntry> topBlockedDomains,
        IReadOnlyList<DnsStatisticsEntry> topUpstreams,
        IReadOnlyList<DnsStatisticsResponseTimeEntry> upstreamResponseTimes,
        IReadOnlyList<DnsStatisticsHourlyPoint> hourlySeries)
    {
        TotalQueries = totalQueries;
        BlockedQueries = blockedQueries;
        AverageProcessingTimeMs = averageProcessingTimeMs;
        TopClients = topClients;
        TopSearchedDomains = topSearchedDomains;
        TopBlockedDomains = topBlockedDomains;
        TopUpstreams = topUpstreams;
        UpstreamResponseTimes = upstreamResponseTimes;
        HourlySeries = hourlySeries;
    }

    public long TotalQueries { get; }

    public long BlockedQueries { get; }

    public int AverageProcessingTimeMs { get; }

    public IReadOnlyList<DnsStatisticsEntry> TopClients { get; }

    public IReadOnlyList<DnsStatisticsEntry> TopSearchedDomains { get; }

    public IReadOnlyList<DnsStatisticsEntry> TopBlockedDomains { get; }

    public IReadOnlyList<DnsStatisticsEntry> TopUpstreams { get; }

    public IReadOnlyList<DnsStatisticsResponseTimeEntry> UpstreamResponseTimes { get; }

    // Série horaire (24 points, une valeur par heure sur les dernières 24h, la plus ancienne en premier),
    // pour les graphiques du tableau de bord (Home.razor / ActivityChart). Toujours exactement 24 points,
    // zéro-remplis pour les heures sans tranche persistée.
    public IReadOnlyList<DnsStatisticsHourlyPoint> HourlySeries { get; }
}
