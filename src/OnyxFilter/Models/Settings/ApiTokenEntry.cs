using System;

namespace OnyxFilter.Models.Settings;

// Un jeton d'accès à l'API HTTP (/api/v1, voir Services/Api/ApiEndpoints.cs), créé depuis la page
// « Accès API » (/settings/api). Le jeton lui-même n'est jamais conservé : seule son empreinte SHA-256
// l'est, il n'est donc montré qu'une fois, à sa création.
public sealed class ApiTokenEntry
{
    // Identifiant public (sert à la révocation), sans rapport avec la valeur du jeton.
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Premiers caractères du jeton (ex. « onyx_AbCd »), pour le reconnaître dans la liste.
    public string Prefix { get; set; } = string.Empty;

    // Empreinte SHA-256 du jeton complet, en hexadécimal minuscule.
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
