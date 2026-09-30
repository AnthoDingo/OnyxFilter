using System;
using System.IO;
using System.Linq;

namespace OnyxFilter.Services;

// Dossier des données (base SQLite, appsettings*.json, caches des listes, fichiers de secours du journal).
// Les chemins relatifs de l'application (chaîne de connexion « Data Source=OnyxFilter.db », caches…) y sont
// résolus : le processus s'y place au démarrage (voir Program.BuildWebApplication), pour qu'une commande
// lancée depuis un autre dossier (ex. « /opt/onyxfilter/OnyxFilter user:list » depuis son dossier
// personnel) travaille sur les mêmes données que le serveur.
public static class DataDirectory
{
    // Présence de l'un de ces fichiers : le dossier contient une installation ou des données OnyxFilter.
    private static readonly string[] MarkerFiles = { "appsettings.json", "appsettings.local.json", "OnyxFilter.db" };

    // Ordre de priorité : dossier explicite (--data-dir), dossier courant s'il contient des données
    // OnyxFilter (développement, WorkingDirectory du service systemd), sinon dossier de l'exécutable
    // (installation publiée), à défaut le dossier courant.
    public static string Resolve(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            string directory = Path.GetFullPath(explicitDirectory);

            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Dossier des données introuvable : {directory}");
            }

            return directory;
        }

        string current = Directory.GetCurrentDirectory();

        if (ContainsMarker(current))
        {
            return current;
        }

        string executableDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return ContainsMarker(executableDirectory) ? executableDirectory : current;
    }

    private static bool ContainsMarker(string directory)
    {
        return MarkerFiles.Any(marker => File.Exists(Path.Combine(directory, marker)));
    }
}
