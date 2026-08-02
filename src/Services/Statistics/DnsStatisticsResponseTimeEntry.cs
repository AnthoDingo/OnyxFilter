namespace OnyxFilter.Services.Statistics;

// Une ligne du panneau "Temps de réponse moyen en amont" du tableau de bord.
public sealed class DnsStatisticsResponseTimeEntry
{
    public DnsStatisticsResponseTimeEntry(string upstream, int averageResponseTimeMs)
    {
        Upstream = upstream;
        AverageResponseTimeMs = averageResponseTimeMs;
    }

    public string Upstream { get; }

    public int AverageResponseTimeMs { get; }
}
