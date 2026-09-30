using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Rewrites;

// Applique la page "Réécritures DNS" (/filters/rewrites) : reproduit les "DNS rewrites" d'AdGuard Home,
// une réponse personnalisée définie par l'utilisateur pour un domaine donné (adresse IPv4/IPv6 directe,
// ou chaîne CNAME vers un autre nom de domaine).
public interface IDnsRewriteService
{
    // Charge les réglages (activation globale et liste des règles). À appeler une fois avant le premier
    // TryBuildRewriteResponseAsync (le service DNS s'en charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Si les réécritures sont activées et qu'une règle activée correspond au domaine demandé par
    // "query" (correspondance exacte, ou motif "*.domaine.tld" pour ses sous-domaines), construit et
    // retourne la réponse correspondante. Retourne null sinon (fonctionnalité désactivée, aucune règle
    // ne correspond, requête malformée, type de question autre que A/AAAA, ou résolution de la cible
    // CNAME impossible — échec ouvert : la requête suit alors sa résolution normale plutôt que d'échouer).
    Task<byte[]?> TryBuildRewriteResponseAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken);
}
