using System.Collections.Generic;
using System.Threading.Tasks;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Api;

// Jetons d'accès à l'API HTTP (/api/v1) : création et révocation depuis la page « Accès API »
// (/settings/api), vérification à chaque appel par ApiTokenAuthenticationHandler.
public interface IApiTokenService
{
    Task<IReadOnlyList<ApiTokenEntry>> ListAsync();

    // Crée un jeton nommé et retourne sa valeur en clair : elle n'est jamais conservée (seule son empreinte
    // l'est) et ne peut donc plus être retrouvée ensuite.
    Task<ApiTokenCreationResult> CreateAsync(string name);

    // Retourne false si aucun jeton ne porte cet identifiant.
    Task<bool> RevokeAsync(string id);

    // Retourne le jeton correspondant à la valeur présentée, ou null si elle ne correspond à aucun jeton
    // existant. Comparaison en temps constant sur les empreintes.
    Task<ApiTokenEntry?> ValidateAsync(string presentedToken);
}

public sealed record ApiTokenCreationResult(ApiTokenEntry Entry, string Token);
