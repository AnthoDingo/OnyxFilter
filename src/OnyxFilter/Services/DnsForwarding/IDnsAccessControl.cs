using System.Net;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

// Applique les paramètres d'accès de la page "Paramètres DNS" : clients autorisés (liste blanche),
// clients interdits (liste noire, ignorée si la liste blanche n'est pas vide) et domaines interdits
// (requêtes ignorées sans réponse).
public interface IDnsAccessControl
{
    // Charge la configuration depuis les paramètres. À appeler une fois avant le premier appel aux
    // méthodes de vérification (le service DNS s'en charge au démarrage).
    Task InitializeAsync();

    // Retourne true si le client a le droit d'utiliser le serveur DNS : soit la liste blanche est
    // vide et le client n'est pas dans la liste noire, soit le client figure dans la liste blanche.
    bool IsClientAllowed(IPAddress clientAddress);

    // Retourne true si le domaine demandé est interdit et que la requête ne doit pas être traitée.
    bool IsDomainDisallowed(string domain);
}
