using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.ParentalControl;

// Applique "Utiliser le contrôle parental d'OnyxFilter" (Paramètres généraux) : vérifie chaque domaine
// demandé auprès du même service de recherche de préfixes de hachage qu'AdGuard Home utilise pour son
// Contrôle parental (contenu pour adultes), et répond aux requêtes concernant un domaine signalé selon
// le "Mode de blocage" configuré sur /settings/dns.
public interface IParentalControlService
{
    // Charge les réglages (activation, mode de blocage). À appeler une fois avant le premier
    // TryBuildBlockResponseAsync (le service DNS s'en charge au démarrage, comme pour
    // IBrowsingSecurityService).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Si "Utiliser le contrôle parental d'OnyxFilter" est actif et que le domaine demandé par "query" est
    // signalé comme contenu pour adultes, construit la réponse de blocage correspondant au mode configuré
    // et la retourne. Retourne null sinon (fonctionnalité désactivée, domaine non signalé, requête
    // malformée, ou service injoignable — échec ouvert : la requête n'est jamais bloquée à cause d'une
    // panne de ce service tiers).
    Task<byte[]?> TryBuildBlockResponseAsync(byte[] query, CancellationToken cancellationToken);
}
