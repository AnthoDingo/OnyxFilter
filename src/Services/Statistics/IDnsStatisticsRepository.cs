using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Statistics;

// Persistance des statistiques DNS : une ligne par tranche horaire, pour que les compteurs du tableau
// de bord survivent à un redémarrage du serveur. Implémentation SQLite dans SqliteDnsStatisticsRepository.
public interface IDnsStatisticsRepository
{
    // Crée le schéma si nécessaire. À appeler avant toute autre méthode.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Charge toutes les tranches horaires persistées (le filtrage par rétention est fait par l'appelant).
    Task<IReadOnlyList<DnsStatisticsBucketRecord>> LoadAsync(CancellationToken cancellationToken);

    // Insère ou remplace les tranches horaires fournies (upsert par HourKey).
    Task SaveAsync(IReadOnlyList<DnsStatisticsBucketRecord> records, CancellationToken cancellationToken);

    // Supprime les tranches plus anciennes que la limite de rétention.
    Task DeleteOlderThanAsync(long cutoffHourKey, CancellationToken cancellationToken);

    // Supprime toutes les tranches ("Effacer les statistiques", ou statistiques désactivées).
    Task ClearAsync(CancellationToken cancellationToken);
}
