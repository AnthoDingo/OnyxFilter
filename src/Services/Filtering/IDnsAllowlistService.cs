using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Filtering;

// Applique la page "Listes d'autorisation DNS" (/filters/allowlists) : un domaine figurant dans une
// liste d'autorisation activée est toujours résolu normalement, même s'il figure par ailleurs dans une
// liste de blocage (IDnsFilterService). Ne concerne que les listes de blocage : la Sécurité de
// navigation et le Contrôle parental restent appliqués indépendamment.
public interface IDnsAllowlistService
{
    // Charge les réglages et recharge les listes activées depuis leur cache disque (aucun appel réseau) :
    // l'autorisation est active dès le retour de cette méthode. À appeler une fois avant le premier
    // IsAllowed (le service DNS s'en charge au démarrage). Le premier téléchargement réseau est ensuite
    // lancé en arrière-plan (voir RefreshAsync) sans retarder le démarrage.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Retélécharge et réanalyse toutes les listes actuellement activées. Utilisé par la mise à jour
    // périodique automatique et par le bouton "Vérifier les mises à jour" de la page.
    Task RefreshAsync(CancellationToken cancellationToken);

    // Indique si "domain" (ou un de ses domaines parents) figure dans une liste d'autorisation activée.
    bool IsAllowed(string domain);

    // Statut de chaque abonnement, nombre total de domaines autorisés et date de la dernière tentative
    // de mise à jour, pour la page "Listes d'autorisation DNS".
    FilterListsSnapshot GetSnapshot();
}
