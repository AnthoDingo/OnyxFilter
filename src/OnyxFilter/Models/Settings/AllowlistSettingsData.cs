using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Listes d'autorisation DNS" (/filters/allowlists). Contrairement aux
// listes de blocage (FilterListsSettingsData), aucune liste par défaut n'est proposée à l'installation :
// l'autorisation explicite est une exception que l'utilisateur configure lui-même, au cas par cas.
public sealed class AllowlistSettingsData
{
    public List<FilterListEntry> Lists { get; set; } = new List<FilterListEntry>();
}
