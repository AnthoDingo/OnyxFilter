using System;
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
    // existant. Comparaison en temps constant sur les empreintes. Un jeton d'appairage encore valide est
    // enregistré comme jeton permanent à sa première utilisation (voir CreatePairingToken).
    Task<ApiTokenEntry?> ValidateAsync(string presentedToken);

    // Jeton d'appairage (QR code de connexion d'un smartphone) : gardé en mémoire seulement, valable
    // "lifetime", à usage unique. Il ne devient un jeton permanent (listé, révocable) qu'à sa première
    // utilisation par l'API, avant expiration.
    ApiTokenCreationResult CreatePairingToken(string name, TimeSpan lifetime);

    PairingTokenState GetPairingTokenState(string id);

    // Oublie un jeton d'appairage non utilisé (QR code fermé ou remplacé).
    void DiscardPairingToken(string id);
}

public sealed record ApiTokenCreationResult(ApiTokenEntry Entry, string Token);

public enum PairingTokenState
{
    // En attente d'utilisation, encore valable.
    Pending,

    // Utilisé : enregistré comme jeton permanent.
    Consumed,

    // Utilisé, mais non enregistré (nombre maximal de jetons atteint).
    Rejected,

    // Expiré, oublié ou inconnu.
    Expired,
}
