using System;
using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

public enum CountryFilterMode
{
    Disabled,

    // Les clients des pays listés sont refusés.
    Blocklist,

    // Seuls les clients des pays listés sont servis.
    Allowlist,
}

// Reflète la page "Filtrage par pays" (/filters/countries). Les clients sans pays connu (réseau local,
// adresses privées, base pas encore téléchargée) sont toujours servis, comme ceux des listes d'accès
// des paramètres DNS (clients autorisés ou toujours autorisés), prioritaires sur ce filtre.
public sealed class CountryFilterSettingsData
{
    public CountryFilterMode Mode { get; set; } = CountryFilterMode.Disabled;

    // Codes pays ISO 3166-1 alpha-2, en majuscules.
    public IReadOnlyList<string> Countries { get; set; } = Array.Empty<string>();
}
