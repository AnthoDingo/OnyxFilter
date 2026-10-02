namespace OnyxFilter;

// Ressource unique de traduction de l'interface : les textes français servent de clés
// (L["Mon compte"]) et les traductions vivent dans Resources/SharedResource.<langue>.resx.
// Une clé absente d'un .resx s'affiche telle quelle, donc en français.
public sealed class SharedResource
{
}

public static class SupportedLanguages
{
    public const string Default = "fr";

    public static readonly string[] All = ["fr", "en", "de", "it", "es"];
}
