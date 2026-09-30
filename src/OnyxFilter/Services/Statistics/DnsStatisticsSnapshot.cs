using System;
using System.Collections.Generic;
using System.Linq;

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

    // Réunit TopUpstreams (volumes) et UpstreamResponseTimes (latences) par serveur, dans l'ordre du
    // classement des volumes, suivi des serveurs qui n'ont qu'une mesure de latence. Utilisé par le panneau
    // « Serveurs en amont » de la vue d'ensemble et par l'API (/api/v1/stats).
    public IReadOnlyList<DnsStatisticsUpstreamSummary> BuildUpstreamSummaries()
    {
        Dictionary<string, int> latencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DnsStatisticsResponseTimeEntry entry in UpstreamResponseTimes)
        {
            latencies[entry.Upstream] = entry.AverageResponseTimeMs;
        }

        List<DnsStatisticsUpstreamSummary> summaries = new List<DnsStatisticsUpstreamSummary>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DnsStatisticsEntry entry in TopUpstreams)
        {
            if (seen.Add(entry.Label))
            {
                int? latency = latencies.TryGetValue(entry.Label, out int value) ? value : null;
                summaries.Add(new DnsStatisticsUpstreamSummary(entry.Label, entry.Count, latency));
            }
        }

        foreach (DnsStatisticsResponseTimeEntry entry in UpstreamResponseTimes.Where(entry => !seen.Contains(entry.Upstream)))
        {
            seen.Add(entry.Upstream);
            summaries.Add(new DnsStatisticsUpstreamSummary(entry.Upstream, null, entry.AverageResponseTimeMs));
        }

        return summaries;
    }
}
