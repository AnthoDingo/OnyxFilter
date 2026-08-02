using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Filtering;

// Applique "Bloquer les domaines à l'aide de filtres" (Paramètres généraux) : télécharge les listes de
// blocage abonnées (/filters/blocklists), et répond aux requêtes concernant un domaine bloqué selon le
// "Mode de blocage" configuré sur /settings/dns.
public interface IDnsFilterService
{
    // Charge les réglages et recharge les listes activées depuis leur cache disque (aucun appel réseau) :
    // le blocage est actif dès le retour de cette méthode, même si les serveurs des listes sont
    // injoignables. À appeler une fois avant le premier TryBuildBlockResponse (le service DNS s'en charge
    // au démarrage). Le premier téléchargement réseau est ensuite lancé en arrière-plan (voir RefreshAsync)
    // sans retarder le démarrage ; ses résultats (et échecs éventuels, capturés par liste) apparaissent
    // dans GetSnapshot() une fois terminé.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Retélécharge et réanalyse toutes les listes actuellement activées. Utilisé par la mise à jour
    // périodique automatique et par le bouton "Mettre à jour maintenant" de la page.
    Task RefreshAsync(CancellationToken cancellationToken);

    // Si "Bloquer les domaines à l'aide de filtres" est actif et que le domaine demandé par "query"
    // figure dans une liste de blocage (lui-même ou un de ses domaines parents), construit la réponse de
    // blocage correspondant au mode configuré et retourne true. Retourne false sans construire de réponse
    // sinon (filtrage désactivé, domaine non bloqué, ou requête malformée).
    bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedListName);

    // Statut de chaque abonnement, nombre total de domaines bloqués et date de la dernière tentative de
    // mise à jour, pour la page "Listes de blocage DNS".
    FilterListsSnapshot GetSnapshot();
}
