using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Filtering;

// Applique la page "Règles de filtrage personnalisées" (/filters/custom-rules) : reproduit la syntaxe
// des règles de blocage/listes hosts d'AdGuard Home, saisie directement par l'utilisateur (voir
// CustomFilterRulesService pour le détail des formats reconnus).
public interface ICustomFilterRulesService
{
    // Charge les réglages et recompile les règles. À appeler une fois avant le premier IsExcepted /
    // TryBuildBlockResponse (le service DNS s'en charge au démarrage).
    Task InitializeAsync(CancellationToken cancellationToken);

    // Indique si une règle d'exception ("@@||domaine.tld^") couvre "domain" (ou un de ses domaines
    // parents) : le domaine ne doit alors jamais être bloqué, ni par une autre règle personnalisée, ni
    // par les listes de blocage abonnées (IDnsFilterService).
    bool IsExcepted(string domain);

    // Si une règle de blocage personnalisée (domaine, expression régulière, ou ligne au format fichier
    // hosts) correspond au domaine demandé par "query" et qu'aucune exception ne s'applique, construit la
    // réponse correspondante et retourne true. À n'appeler que si IsExcepted a déjà été vérifié à false
    // pour ce domaine. Retourne false sinon (fonctionnalité désactivée, aucune règle ne correspond, ou
    // requête malformée).
    bool TryBuildBlockResponse(byte[] query, out byte[]? response, out string? matchedRule);
}
