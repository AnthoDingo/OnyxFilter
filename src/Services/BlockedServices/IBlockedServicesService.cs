using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.BlockedServices;

// Applique la page "Services bloqués" (/filters/blocked-services) : bloque les domaines des services
// choisis parmi le catalogue (IBlockedServicesCatalog), avec prise en charge de "Suspendre le blocage
// des services" (horaire de suspension temporaire).
public interface IBlockedServicesService
{
    // Charge les réglages et recompile les règles des services bloqués. À appeler une fois avant le
    // premier TryBuildBlockResponse (le service DNS s'en charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Si "Bloquer les domaines à l'aide de filtres" est actif, qu'aucune suspension n'est actuellement en
    // vigueur, et que le domaine demandé par "query" correspond à un service bloqué, construit la réponse
    // de blocage correspondant au mode configuré et la retourne. Retourne false sinon.
    bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedServiceName);
}
