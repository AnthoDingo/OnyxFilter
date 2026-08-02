using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.QueryLog;

// Persistance du journal des requêtes DNS : une ligne par requête traitée, pour que la page /query-log
// survive à un redémarrage du serveur. Implémentation SQLite dans SqliteDnsQueryLogRepository.
public interface IDnsQueryLogRepository
{
    // Crée le schéma (et son index sur la date) si nécessaire. À appeler avant toute autre méthode.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Insère les lignes fournies en une seule transaction (écriture par lot : voir DnsQueryLogService).
    Task InsertBatchAsync(IReadOnlyList<DnsQueryLogRecord> records, CancellationToken cancellationToken);

    // Une page de lignes, les plus récentes en premier. Lue directement en base (jamais chargée en
    // mémoire dans son intégralité), pour rester léger quel que soit le volume du journal.
    // searchText : filtre LIKE sur Domain et ClientKey (null = pas de filtre).
    // reason     : filtre sur la raison précise (null = toutes les requêtes).
    Task<IReadOnlyList<DnsQueryLogRecord>> QueryPageAsync(int skip, int take, string? searchText, QueryLogReason? reason, CancellationToken cancellationToken);

    // Supprime les lignes plus anciennes que la limite de rétention configurée.
    Task DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken);

    // Supprime toutes les lignes ("Effacer journal des requêtes", /settings/general).
    Task ClearAsync(CancellationToken cancellationToken);

    // Sondage minimal (sans écrire ni lire de ligne) pour détecter si la base est de nouveau accessible,
    // même quand il n'y a aucune ligne à insérer. Ne lève jamais : retourne false en cas d'échec.
    Task<bool> PingAsync(CancellationToken cancellationToken);
}
