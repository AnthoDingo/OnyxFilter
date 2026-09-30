using System;

namespace OnyxFilter.Services.Statistics;

// Un point de la série horaire affichée par les graphiques du tableau de bord (Home.razor / ActivityChart) :
// une valeur par heure sur la fenêtre glissante des dernières 24 heures, la plus ancienne en premier.
// Zéro-rempli pour les heures sans tranche persistée, afin que la série ait toujours exactement 24 points
// régulièrement espacés (une par heure), même juste après un redémarrage ou un "Effacer les statistiques".
public readonly struct DnsStatisticsHourlyPoint
{
    public DnsStatisticsHourlyPoint(DateTime hourStartUtc, long totalQueries, long blockedQueries)
    {
        HourStartUtc = hourStartUtc;
        TotalQueries = totalQueries;
        BlockedQueries = blockedQueries;
    }

    public DateTime HourStartUtc { get; }

    public long TotalQueries { get; }

    public long BlockedQueries { get; }
}
