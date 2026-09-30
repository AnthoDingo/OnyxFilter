using System.Net;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

// Applique les paramètres d'accès de la page "Paramètres DNS" : clients autorisés (liste blanche),
// clients interdits (liste noire, ignorée si la liste blanche n'est pas vide), clients toujours autorisés
// (prioritaires sur les deux autres listes) et domaines interdits (requêtes ignorées sans réponse).
public interface IDnsAccessControl
{
    // Charge la configuration depuis les paramètres si ce n'est pas déjà fait. À appeler avant le premier
    // appel aux méthodes de vérification (le service DNS s'en charge au démarrage) ; ensuite, chaque
    // modification des paramètres est rechargée automatiquement.
    Task InitializeAsync();

    // Recharge immédiatement la configuration (ex. l'API, pour que ses modifications soient en vigueur
    // avant de répondre).
    Task ReloadAsync();

    // Retourne true si le client a le droit d'utiliser le serveur DNS : il figure parmi les clients
    // toujours autorisés, ou dans la liste blanche, ou la liste blanche est vide et il n'est pas dans la
    // liste noire.
    bool IsClientAllowed(IPAddress clientAddress);

    // Même décision qu'IsClientAllowed, avec le mode en vigueur et la règle en cause.
    ClientAccessDecision Evaluate(IPAddress clientAddress);

    // Retourne true si le domaine demandé est interdit et que la requête ne doit pas être traitée.
    bool IsDomainDisallowed(string domain);
}
