using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.SafeSearch;

// Applique "Utiliser la Recherche Sécurisée" (Paramètres généraux) : si le domaine demandé correspond à
// l'un des moteurs de recherche pris en charge (Google, Bing, DuckDuckGo, Ecosia, Pixabay, Yandex,
// YouTube) et que ce moteur est activé, réécrit la réponse pour pointer vers sa variante "recherche
// sécurisée" au lieu du site normal — exactement comme AdGuard Home.
public interface ISafeSearchService
{
    // Charge les réglages (activation globale et par moteur). À appeler une fois avant le premier
    // TryBuildRewriteResponseAsync (le service DNS s'en charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Si "Utiliser la Recherche Sécurisée" et le moteur concerné par "query" sont tous deux activés,
    // construit et retourne la réponse réécrite (adresse fixe pour Yandex, ou chaîne CNAME résolue pour
    // les autres moteurs). Retourne null sinon (fonctionnalité désactivée, domaine non concerné, requête
    // malformée, type de question autre que A/AAAA, ou résolution de la cible impossible — échec ouvert :
    // la requête suit alors sa résolution normale, non filtrée, plutôt que d'échouer).
    Task<byte[]?> TryBuildRewriteResponseAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken);
}
