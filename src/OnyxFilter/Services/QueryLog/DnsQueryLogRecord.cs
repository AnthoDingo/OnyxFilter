using System;

namespace OnyxFilter.Services.QueryLog;

// Une ligne du journal des requêtes (page /query-log), en mémoire (file d'attente avant écriture) comme
// en base (table DnsQueryLogEntries). "Id" reste nul tant que la ligne n'a pas été écrite en base
// (assigné par SQLite - AUTOINCREMENT).
public sealed class DnsQueryLogRecord
{
    public long? Id { get; set; }

    public DateTime TimestampUtc { get; set; }

    public string Domain { get; set; } = string.Empty;

    public string ClientKey { get; set; } = string.Empty;

    public string QueryType { get; set; } = string.Empty;

    public QueryLogReason Reason { get; set; } = QueryLogReason.Resolved;

    // Détail libre de la raison : nom de la liste de filtres ("Tracking"), du service bloqué ("YouTube"),
    // ou de la règle personnalisée correspondante. Null pour les requêtes Resolved, Cached et les cas où
    // le détail n'est pas disponible.
    public string? ReasonDetail { get; set; }

    /// <summary>
    /// Vrai si la requête a été bloquée (dérivé de Reason, stocké séparément pour la rétrocompatibilité
    /// avec les lignes existantes dont Reason vaut la valeur par défaut).
    /// </summary>
    public bool Blocked => Reason is QueryLogReason.CustomRule
        or QueryLogReason.Filtered
        or QueryLogReason.BlockedService
        or QueryLogReason.SecurityThreat
        or QueryLogReason.ParentalControl;

    public string? UpstreamServer { get; set; }

    public long ProcessingTimeMs { get; set; }
}
