using System;

namespace OnyxFilter.Services.DnsForwarding;

// État "Protection" du tableau de bord (Home.razor) : arrêt temporaire volontaire de tout le filtrage
// DNS (listes de blocage, règles personnalisées, services bloqués, sécurité de navigation, contrôle
// parental, recherche sécurisée forcée), consulté par IDnsQueryPipeline à chaque requête. Les
// réécritures DNS, le cache et les statistiques restent actifs : seule la partie "filtrage/blocage" est
// concernée, comme la bascule "Protection" d'AdGuard Home.
public interface IDnsProtectionState
{
    // true = filtrage actif (comportement par défaut au démarrage).
    bool IsEnabled { get; }

    // Échéance de réactivation automatique (UTC), ou null si la protection est activée ou désactivée
    // sans échéance (clic sur le bouton principal du tableau de bord).
    DateTime? DisabledUntilUtc { get; }

    // Désactive le filtrage immédiatement, sans échéance de réactivation automatique.
    void Disable();

    // Désactive le filtrage jusqu'à "untilUtc" (échéance dans le passé ou immédiate : la protection
    // reste/redevient activée). Une réactivation automatique est programmée pour cette échéance.
    void DisableUntil(DateTime untilUtc);

    // Réactive le filtrage immédiatement et annule toute réactivation automatique programmée.
    void Enable();

    // Levé après chaque changement d'état (Disable, DisableUntil, Enable ou réactivation automatique à
    // l'échéance), hors verrou, depuis le thread qui a provoqué le changement (thread du pool pour la
    // réactivation automatique) : les composants Blazor abonnés doivent repasser par InvokeAsync.
    event EventHandler? Changed;
}
