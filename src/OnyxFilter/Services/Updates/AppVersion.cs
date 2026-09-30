using System.Reflection;

namespace OnyxFilter.Services.Updates;

// Version de l'application en cours d'exécution, lue dans la version informative de l'assembly : fixée
// par la chaîne de publication à partir du tag (dotnet publish -p:Version=1.4.0, voir
// .github/workflows/release.yml), « 0.0.0-dev » pour une compilation locale (voir OnyxFilter.csproj).
public static class AppVersion
{
    private static readonly SemanticVersion Fallback = Parse("0.0.0-dev");

    public static SemanticVersion Current { get; } = ReadCurrent();

    // Forme courte pour l'affichage (« 1.4.0 », « 0.0.0-dev »), sans métadonnées de build.
    public static string Display => Current.ToString();

    private static SemanticVersion ReadCurrent()
    {
        string? informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return SemanticVersion.TryParse(informational, out SemanticVersion version) ? version : Fallback;
    }

    private static SemanticVersion Parse(string text)
    {
        SemanticVersion.TryParse(text, out SemanticVersion version);
        return version;
    }
}
