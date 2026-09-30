namespace OnyxFilter.Models.Settings;

// Reflète le champ de la page "Règles de filtrage personnalisées" (/filters/custom-rules) : une règle
// par ligne, saisie librement par l'utilisateur (syntaxe des règles de blocage ou des fichiers hosts,
// voir CustomFilterRulesService pour le détail des formats reconnus).
public sealed class CustomFilterRulesSettingsData
{
    public string RulesText { get; set; } = string.Empty;
}
