namespace OnyxFilter.Services.QueryLog;

/// <summary>
/// Raison précise du traitement d'une requête DNS, enregistrée dans le journal.
/// Utilisée pour le filtre "Statut" de la page /query-log.
/// </summary>
public enum QueryLogReason
{
    /// <summary>Résolution normale via les serveurs en amont.</summary>
    Resolved,

    /// <summary>Réponse servie depuis le cache DNS.</summary>
    Cached,

    /// <summary>Réécriture DNS configurée par l'utilisateur (/filters/rewrites).</summary>
    Rewritten,

    /// <summary>Redirection Recherche Sécurisée (Safe Search).</summary>
    SafeSearch,

    /// <summary>Bloqué par une règle de filtrage personnalisée (/filters/custom-rules).</summary>
    CustomRule,

    /// <summary>Bloqué par une liste de filtres DNS (/filters/blocklists).</summary>
    Filtered,

    /// <summary>Bloqué par la liste des services bloqués (/filters/blocked-services).</summary>
    BlockedService,

    /// <summary>Bloqué par le service de Sécurité de navigation.</summary>
    SecurityThreat,

    /// <summary>Bloqué par le Contrôle parental.</summary>
    ParentalControl,
}
