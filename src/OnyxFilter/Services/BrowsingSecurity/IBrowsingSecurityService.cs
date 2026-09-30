using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.BrowsingSecurity;

// Applique "Utilisez le service Sécurité de navigation d'OnyxFilter" (Paramètres généraux) : vérifie
// chaque domaine demandé auprès du service de recherche de préfixes de hachage d'AdGuard, et répond aux
// requêtes concernant un domaine dangereux selon le "Mode de blocage" configuré sur /settings/dns.
public interface IBrowsingSecurityService
{
    // Charge les réglages (activation, mode de blocage). À appeler une fois avant le premier
    // TryBuildBlockResponseAsync (le service DNS s'en charge au démarrage, comme pour IDnsFilterService).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Si "Utilisez le service Sécurité de navigation d'OnyxFilter" est actif et que le domaine demandé
    // par "query" est signalé comme dangereux, construit la réponse de blocage correspondant au mode
    // configuré et la retourne. Retourne null sinon (fonctionnalité désactivée, domaine non signalé,
    // requête malformée, ou service de sécurité de navigation injoignable — échec ouvert : la requête
    // n'est jamais bloquée à cause d'une panne de ce service tiers).
    Task<byte[]?> TryBuildBlockResponseAsync(byte[] query, CancellationToken cancellationToken);
}
