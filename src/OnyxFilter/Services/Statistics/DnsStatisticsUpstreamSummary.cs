namespace OnyxFilter.Services.Statistics;

// Un serveur en amont avec son volume de requêtes et sa latence moyenne réunis (voir
// DnsStatisticsSnapshot.BuildUpstreamSummaries). L'une ou l'autre valeur peut manquer : un serveur peut
// figurer dans le classement des volumes sans mesure de latence, et inversement.
public sealed class DnsStatisticsUpstreamSummary
{
    public DnsStatisticsUpstreamSummary(string upstream, long? requestCount, int? averageResponseTimeMs)
    {
        Upstream = upstream;
        RequestCount = requestCount;
        AverageResponseTimeMs = averageResponseTimeMs;
    }

    public string Upstream { get; }

    public long? RequestCount { get; }

    public int? AverageResponseTimeMs { get; }
}
