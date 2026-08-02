namespace OnyxFilter.Services.Statistics;

// Une ligne des panneaux "Meilleurs clients", "Domaines les plus recherchés", "Les domaines les plus
// fréquemment bloqués" ou "Top amonts" du tableau de bord.
public sealed class DnsStatisticsEntry
{
    public DnsStatisticsEntry(string label, long count)
    {
        Label = label;
        Count = count;
    }

    public string Label { get; }

    public long Count { get; }
}
