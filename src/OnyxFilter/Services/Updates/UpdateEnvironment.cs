using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.Systemd;

namespace OnyxFilter.Services.Updates;

// Conditions de l'installation intégrée d'une mise à jour. Seule la cible publiée est prise en charge :
// version autonome linux-x64 (archive de .github/workflows/release.yml), lancée par systemd (ou un autre
// superviseur déclaré, voir UpdateOptions.AssumeSupervised) pour être relancée après l'installation, dans
// un dossier d'installation accessible en écriture au compte du service.
public static class UpdateEnvironment
{
    public const string SupportedRuntimeIdentifier = "linux-x64";

    // Nom de l'exécutable autonome publié (apphost).
    public const string ExecutableName = "OnyxFilter";

    // Dossier contenant les fichiers de l'application (et non les données, qui suivent le dossier de
    // travail : voir ContentRootPath).
    public static string InstallDirectory => AppContext.BaseDirectory;

    public static string RuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;

    // Retourne null si l'installation intégrée est possible, sinon la raison (affichée telle quelle dans
    // l'interface et l'API).
    public static string? GetInstallBlocker(UpdateOptions options)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return $"L'installation automatique n'est prise en charge que sous Linux x64 (plateforme actuelle : {RuntimeIdentifier}). Téléchargez la nouvelle version depuis GitHub.";
        }

        if (File.Exists("/.dockerenv"))
        {
            return "OnyxFilter s'exécute dans un conteneur : mettez à jour l'image plutôt que l'application.";
        }

        // Une publication autonome embarque le runtime .NET (libcoreclr.so) à côté de l'application.
        if (!File.Exists(Path.Combine(InstallDirectory, "libcoreclr.so")) || !File.Exists(Path.Combine(InstallDirectory, ExecutableName)))
        {
            return "Cette installation n'est pas une version publiée autonome (compilation locale ou dépendante du runtime .NET) : l'installation automatique est désactivée.";
        }

        if (!SystemdHelpers.IsSystemdService() && !options.AssumeSupervised)
        {
            return "OnyxFilter n'est pas lancé par systemd : il ne pourrait pas redémarrer seul après la mise à jour. Utilisez le service fourni (deploy/onyxfilter.service).";
        }

        if (!IsInstallDirectoryWritable())
        {
            return $"Le dossier d'installation ({InstallDirectory}) n'est pas accessible en écriture pour le compte du service.";
        }

        return null;
    }

    private static bool IsInstallDirectoryWritable()
    {
        string probe = Path.Combine(InstallDirectory, ".update-write-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Déjà supprimé (DeleteOnClose) ou jamais créé.
            }
        }
    }
}
