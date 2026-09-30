using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.Updates;

// Téléchargement, vérification et installation d'une publication, dans le dossier d'installation
// (UpdateEnvironment.InstallDirectory). Le processus en cours n'est pas affecté : sous Linux, remplacer un
// fichier par renommage laisse l'ancien contenu accessible au processus qui l'a ouvert. La nouvelle version
// ne prend effet qu'au redémarrage (voir UpdateService).
//
// Arborescence de travail : <installation>/.update/
//   download/   archive et checksums.txt téléchargés (supprimés après installation)
//   staging/    archive extraite (supprimée après installation)
//   backup/     fichiers remplacés par la dernière mise à jour, pour un retour arrière manuel
//   last-update.json   dernière installation réussie (affichée après le redémarrage)
public sealed partial class UpdateInstaller
{
    // Racine de l'archive publiée : OnyxFilter/… (voir .github/workflows/release.yml).
    public const string ArchiveRootFolder = "OnyxFilter";

    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxChecksumsBytes = 64 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly GitHubReleaseClient releaseClient;
    private readonly ILogger<UpdateInstaller> logger;

    public UpdateInstaller(IHttpClientFactory httpClientFactory, GitHubReleaseClient releaseClient, ILogger<UpdateInstaller> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.releaseClient = releaseClient;
        this.logger = logger;
    }

    public static string WorkDirectory => Path.Combine(UpdateEnvironment.InstallDirectory, ".update");

    public static string BackupDirectory => Path.Combine(WorkDirectory, "backup");

    private static string DownloadDirectory => Path.Combine(WorkDirectory, "download");

    private static string StagingDirectory => Path.Combine(WorkDirectory, "staging");

    private static string LastUpdatePath => Path.Combine(WorkDirectory, "last-update.json");

    public async Task InstallAsync(
        ReleaseInfo release,
        SemanticVersion currentVersion,
        Action<UpdateState, double?> reportProgress,
        CancellationToken cancellationToken)
    {
        if (release.Package is null)
        {
            throw new UpdateException($"La publication {release.TagName} ne contient pas d'archive pour {UpdateEnvironment.SupportedRuntimeIdentifier}.");
        }

        if (release.Checksums is null)
        {
            throw new UpdateException($"La publication {release.TagName} ne fournit pas de fichier checksums.txt : impossible de vérifier l'archive, installation refusée.");
        }

        ResetDirectory(DownloadDirectory);
        ResetDirectory(StagingDirectory);

        try
        {
            reportProgress(UpdateState.Downloading, 0);

            string checksums = await DownloadTextAsync(release.Checksums.DownloadUrl, cancellationToken);
            string expectedHash = FindExpectedHash(checksums, release.Package.Name)
                ?? throw new UpdateException($"L'archive {release.Package.Name} n'est pas référencée dans checksums.txt.");

            if (release.Package.Digest is { } digest
                && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(digest.Substring("sha256:".Length), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("L'empreinte annoncée par GitHub ne correspond pas à checksums.txt : installation refusée.");
            }

            string packagePath = Path.Combine(DownloadDirectory, release.Package.Name);
            string actualHash = await DownloadPackageAsync(release.Package, packagePath, progress => reportProgress(UpdateState.Downloading, progress), cancellationToken);

            reportProgress(UpdateState.Verifying, null);

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("L'empreinte SHA-256 de l'archive téléchargée ne correspond pas à checksums.txt : fichier corrompu ou altéré, installation refusée.");
            }

            string stagedRoot = await ExtractAsync(packagePath, cancellationToken);
            VerifyStagedVersion(stagedRoot, release.Version);

            reportProgress(UpdateState.Installing, null);
            ApplyStagedFiles(stagedRoot, currentVersion);

            await File.WriteAllTextAsync(
                LastUpdatePath,
                JsonSerializer.Serialize(new LastUpdateRecord(currentVersion.ToString(), release.Version.ToString(), DateTimeOffset.UtcNow)),
                CancellationToken.None);

            logger.LogInformation("Mise à jour {From} -> {To} installée ; fichiers remplacés sauvegardés dans {Backup}.", currentVersion, release.Version, BackupDirectory);
        }
        finally
        {
            TryDeleteDirectory(DownloadDirectory);
            TryDeleteDirectory(StagingDirectory);
        }
    }

    // Dernière installation réussie, si elle a abouti à la version en cours d'exécution.
    public static LastUpdateRecord? ReadLastUpdate(SemanticVersion currentVersion)
    {
        try
        {
            if (!File.Exists(LastUpdatePath))
            {
                return null;
            }

            LastUpdateRecord? record = JsonSerializer.Deserialize<LastUpdateRecord>(File.ReadAllText(LastUpdatePath));
            return record is not null && SemanticVersion.TryParse(record.To, out SemanticVersion installed) && installed.Equals(currentVersion)
                ? record
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<string> DownloadTextAsync(Uri url, CancellationToken cancellationToken)
    {
        EnsureTrusted(url);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));

        HttpClient client = httpClientFactory.CreateClient(GitHubReleaseClient.HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response, url);

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using MemoryStream buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, MaxChecksumsBytes, null, null, timeout.Token);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Télécharge l'archive en calculant son empreinte au fil de l'eau ; retourne l'empreinte hexadécimale.
    private async Task<string> DownloadPackageAsync(ReleaseAsset package, string destinationPath, Action<double> reportProgress, CancellationToken cancellationToken)
    {
        EnsureTrusted(package.DownloadUrl);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        HttpClient client = httpClientFactory.CreateClient(GitHubReleaseClient.HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response, package.DownloadUrl);

        long? expectedLength = response.Content.Headers.ContentLength ?? (package.Size > 0 ? package.Size : null);

        if (expectedLength > MaxPackageBytes)
        {
            throw new UpdateException("L'archive dépasse la taille maximale acceptée (512 Mo).");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using (FileStream destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await CopyBoundedAsync(source, destination, MaxPackageBytes, hash, bytes =>
            {
                if (expectedLength is > 0)
                {
                    reportProgress(Math.Min(1.0, bytes / (double)expectedLength.Value));
                }
            }, timeout.Token);
        }

        reportProgress(1.0);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes, IncrementalHash? hash, Action<long>? onProgress, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        long lastReported = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;

            if (total > maxBytes)
            {
                throw new UpdateException("Le fichier téléchargé dépasse la taille maximale acceptée.");
            }

            hash?.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            // Progression signalée tous les 512 Ko pour ne pas saturer l'interface.
            if (onProgress is not null && total - lastReported >= 512 * 1024)
            {
                lastReported = total;
                onProgress(total);
            }
        }
    }

    // Extraction entrée par entrée : chemins confinés sous staging/OnyxFilter/, fichiers et dossiers
    // uniquement (aucun lien), taille totale bornée (protection contre une archive piégée).
    private static async Task<string> ExtractAsync(string packagePath, CancellationToken cancellationToken)
    {
        string stagingRoot = Path.GetFullPath(StagingDirectory) + Path.DirectorySeparatorChar;
        long extractedBytes = 0;

        await using FileStream archive = File.OpenRead(packagePath);
        await using GZipStream gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using TarReader reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            string name = entry.Name.Replace('\\', '/').TrimStart('.', '/');

            if (name.Length == 0)
            {
                continue;
            }

            if (!name.StartsWith(ArchiveRootFolder + "/", StringComparison.Ordinal) && name != ArchiveRootFolder)
            {
                throw new UpdateException($"Archive inattendue : l'entrée « {entry.Name} » n'est pas sous {ArchiveRootFolder}/.");
            }

            string destination = Path.GetFullPath(Path.Combine(StagingDirectory, name));

            if (!destination.StartsWith(stagingRoot, StringComparison.Ordinal))
            {
                throw new UpdateException($"Archive refusée : l'entrée « {entry.Name} » sort du dossier d'extraction.");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    extractedBytes += entry.Length;
                    if (extractedBytes > MaxExtractedBytes)
                    {
                        throw new UpdateException("Archive refusée : contenu décompressé trop volumineux.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await entry.ExtractToFileAsync(destination, overwrite: false, cancellationToken);
                    break;

                default:
                    throw new UpdateException($"Archive refusée : type d'entrée non pris en charge ({entry.EntryType}) pour « {entry.Name} ».");
            }
        }

        string stagedRoot = Path.Combine(StagingDirectory, ArchiveRootFolder);
        string executable = Path.Combine(stagedRoot, UpdateEnvironment.ExecutableName);

        if (!File.Exists(Path.Combine(stagedRoot, "OnyxFilter.dll")) || !File.Exists(executable))
        {
            throw new UpdateException("Archive incomplète : OnyxFilter.dll ou l'exécutable OnyxFilter est absent.");
        }

        if (OperatingSystem.IsLinux())
        {
            // Filet de sécurité si le mode n'a pas été conservé dans l'archive.
            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return stagedRoot;
    }

    // L'archive doit contenir exactement la version annoncée par le tag de la publication.
    private static void VerifyStagedVersion(string stagedRoot, SemanticVersion expected)
    {
        string? productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(stagedRoot, "OnyxFilter.dll")).ProductVersion;

        if (!SemanticVersion.TryParse(productVersion, out SemanticVersion staged) || !staged.Equals(expected))
        {
            throw new UpdateException($"L'archive contient la version « {productVersion} » au lieu de {expected} : installation refusée.");
        }
    }

    // Remplace les fichiers de l'installation par ceux de l'archive, par renommage (atomique sur un même
    // système de fichiers). Les fichiers remplacés sont déplacés dans backup/ ; en cas d'échec, tout est
    // remis en place. Ne supprime jamais de fichier absent de l'archive (données, caches, journaux) et ne
    // remplace pas les appsettings*.json existants à la racine, que l'administrateur a pu modifier.
    private void ApplyStagedFiles(string stagedRoot, SemanticVersion currentVersion)
    {
        string installDirectory = UpdateEnvironment.InstallDirectory;

        TryDeleteDirectory(BackupDirectory);
        Directory.CreateDirectory(BackupDirectory);

        List<string> stagedFiles = Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories).ToList();
        List<(string Target, string? Backup)> applied = new List<(string Target, string? Backup)>();

        try
        {
            foreach (string staged in stagedFiles)
            {
                string relative = Path.GetRelativePath(stagedRoot, staged);
                string target = Path.Combine(installDirectory, relative);

                if (IsPreservedFile(relative) && File.Exists(target))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string? backup = null;
                if (File.Exists(target))
                {
                    backup = Path.Combine(BackupDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                }

                // Enregistré avant les déplacements : le retour arrière sait ainsi restaurer même un fichier
                // déjà mis de côté mais pas encore remplacé.
                applied.Add((target, backup));

                if (backup is not null)
                {
                    File.Move(target, backup);
                }

                File.Move(staged, target);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec de l'installation de la mise à jour : restauration des fichiers d'origine.");
            Rollback(applied);
            throw new UpdateException("Échec lors du remplacement des fichiers (" + ex.Message + ") : l'installation d'origine a été restaurée.");
        }

        File.WriteAllText(Path.Combine(BackupDirectory, "VERSION"), currentVersion.ToString());
    }

    private void Rollback(List<(string Target, string? Backup)> applied)
    {
        for (int i = applied.Count - 1; i >= 0; i--)
        {
            (string target, string? backup) = applied[i];

            try
            {
                if (backup is null)
                {
                    // Fichier nouveau dans cette version : retiré.
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else if (File.Exists(backup))
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(backup, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Impossible de restaurer {Target} ; copie d'origine : {Backup}.", target, backup);
            }
        }
    }

    private static bool IsPreservedFile(string relativePath)
    {
        return !relativePath.Contains(Path.DirectorySeparatorChar) && PreservedFilePattern().IsMatch(relativePath);
    }

    // Lignes « <sha256>  <fichier> » (format sha256sum, « * » optionnel devant le nom en mode binaire).
    private static string? FindExpectedHash(string checksums, string fileName)
    {
        foreach (string rawLine in checksums.Split('\n'))
        {
            Match match = ChecksumLinePattern().Match(rawLine.Trim());

            if (match.Success && string.Equals(match.Groups["file"].Value, fileName, StringComparison.Ordinal))
            {
                return match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    private void EnsureTrusted(Uri url)
    {
        if (!releaseClient.IsTrustedDownloadUrl(url))
        {
            throw new UpdateException($"Adresse de téléchargement refusée (HTTPS requis) : {url}");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri url)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"Téléchargement impossible ({(int)response.StatusCode}) : {url}");
        }
    }

    private static void ResetDirectory(string path)
    {
        TryDeleteDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nettoyage au mieux : ré-essayé à la prochaine mise à jour.
        }
    }

    [GeneratedRegex(@"^appsettings(\..+)?\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex PreservedFilePattern();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>\S.*)$")]
    private static partial Regex ChecksumLinePattern();
}

public sealed record LastUpdateRecord(string From, string To, DateTimeOffset InstalledAtUtc);

// Échec attendu (réseau, vérification, archive) dont le message s'affiche tel quel à l'administrateur.
public sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }
}
