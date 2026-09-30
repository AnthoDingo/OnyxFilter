using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using OnyxFilter.Models.Settings;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.Filtering;

// Infrastructure commune de téléchargement/mise en cache/analyse des abonnements à des listes de
// domaines (une entrée par ligne, format "hosts", ou règles Adblock Plus), partagée par DnsFilterService
// (listes de blocage, /filters/blocklists) et DnsAllowlistService (listes d'autorisation,
// /filters/allowlists) : seuls le répertoire de cache et le libellé utilisé dans les journaux diffèrent
// entre les deux usages. Conçue pour rester légère : téléchargement en flux vers un fichier de cache
// disque (jamais chargé en entier en mémoire), domaines stockés dans un HashSet unique partagé entre
// toutes les listes activées.
//
// Chaque liste activée est mise en cache sur le disque (un fichier par liste, contenu brut téléchargé).
// Au démarrage, ce cache est chargé en premier (rapide, sans réseau) via LoadFromDiskCacheAsync ; le
// téléchargement réseau (RefreshAsync) est ensuite relancé en arrière-plan sans retarder le démarrage du
// service DNS. Si un rafraîchissement réseau échoue ultérieurement (liste temporairement indisponible),
// le contenu déjà en cache est conservé tel quel plutôt que d'être vidé.
public sealed class DomainListRepository : IDisposable
{
    private static readonly char[] WhitespaceChars = { ' ', '\t' };
    private static readonly char[] AdblockTerminators = { '^', '/', '$', '*' };

    private static readonly HashSet<string> HostsFileAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "localhost.localdomain",
        "local",
        "broadcasthost",
        "ip6-localhost",
        "ip6-loopback",
        "ip6-allnodes",
        "ip6-allrouters",
        "ip6-mcastprefix",
    };

    private readonly string cacheDirectory;
    private readonly string listKindLabel;
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    // "listKindLabel" (ex. "liste de blocage" / "liste d'autorisation") n'apparaît que dans les messages
    // de journalisation, pour distinguer les deux usages sans dupliquer cette classe.
    public DomainListRepository(string cacheDirectory, string listKindLabel, ILogger logger)
    {
        this.cacheDirectory = cacheDirectory;
        this.listKindLabel = listKindLabel;
        this.logger = logger;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // Charge chaque liste activée depuis son fichier de cache disque uniquement (aucun appel réseau) :
    // à appeler une fois au démarrage, avant le premier RefreshAsync en arrière-plan.
    public async Task<(CompactDomainSet Domains, List<FilterListStatus> Statuses, IReadOnlyList<(string Name, CompactDomainSet Domains)> PerListDomains)> LoadFromDiskCacheAsync(
        IReadOnlyList<FilterListEntry> lists,
        CancellationToken cancellationToken)
    {
        HashSet<string> merged = new HashSet<string>(StringComparer.Ordinal);
        List<FilterListStatus> statuses = new List<FilterListStatus>(lists.Count);
        List<(string Name, CompactDomainSet Domains)> perList = new List<(string Name, CompactDomainSet Domains)>();

        foreach (FilterListEntry entry in lists)
        {
            FilterListStatus status = new FilterListStatus
            {
                Id = entry.Id,
                Name = entry.Name,
                Url = entry.Url,
                Enabled = entry.Enabled,
            };

            if (entry.Enabled)
            {
                HashSet<string> listDomains = new HashSet<string>(StringComparer.Ordinal);
                (int DomainCount, DateTime LastUpdatedUtc)? cached = await TryLoadFromDiskCacheAsync(entry.Id, listDomains, cancellationToken);

                if (cached is not null)
                {
                    status.DomainCount = cached.Value.DomainCount;
                    status.LastUpdatedUtc = cached.Value.LastUpdatedUtc;
                    merged.UnionWith(listDomains);
                    perList.Add((entry.Name, new CompactDomainSet(listDomains)));
                }
            }

            statuses.Add(status);
        }

        return (new CompactDomainSet(merged), statuses, perList);
    }

    // Retélécharge et réanalyse toutes les listes fournies (les listes désactivées gardent un statut
    // vide, sans être interrogées), puis nettoie les fichiers de cache orphelins (liste supprimée par
    // l'utilisateur depuis le dernier appel).
    public async Task<(CompactDomainSet Domains, List<FilterListStatus> Statuses, IReadOnlyList<(string Name, CompactDomainSet Domains)> PerListDomains)> RefreshAsync(
        IReadOnlyList<FilterListEntry> lists,
        CancellationToken cancellationToken)
    {
        List<FilterListStatus> statuses = new List<FilterListStatus>(lists.Count);
        HashSet<string> merged = new HashSet<string>(StringComparer.Ordinal);
        List<(string Name, CompactDomainSet Domains)> perList = new List<(string Name, CompactDomainSet Domains)>();

        foreach (FilterListEntry entry in lists)
        {
            HashSet<string> listDomains = new HashSet<string>(StringComparer.Ordinal);
            FilterListStatus status = await FetchListAsync(entry, listDomains, cancellationToken);
            statuses.Add(status);

            if (entry.Enabled && listDomains.Count > 0)
            {
                merged.UnionWith(listDomains);
                perList.Add((entry.Name, new CompactDomainSet(listDomains)));
            }
        }

        CleanupOrphanedCacheFiles(lists);

        return (new CompactDomainSet(merged), statuses, perList);
    }

    // Vérifie le domaine demandé, puis remonte ses domaines parents ("sub.ads.example.com" ->
    // "ads.example.com" -> "example.com"), pour qu'une entrée "example.com" couvre aussi ses
    // sous-domaines, sans jamais faire correspondre un domaine parent à partir d'un sous-domaine listé.
    // Découpe le domaine en tranches (ReadOnlySpan<char>) plutôt qu'avec Substring : cette méthode est
    // appelée pour chaque requête DNS traitée, une allocation par niveau de sous-domaine serait donc
    // une pression inutile sur le ramasse-miettes.
    public static bool ContainsDomainOrParent(string domain, CompactDomainSet domains)
    {
        ReadOnlySpan<char> current = domain;

        while (true)
        {
            if (domains.Contains(current))
            {
                return true;
            }

            int dotIndex = current.IndexOf('.');

            if (dotIndex < 0)
            {
                return false;
            }

            current = current.Slice(dotIndex + 1);
        }
    }

    // Même logique que ci-dessus, pour les ensembles de domaines qui restent de petite taille (règles
    // de filtrage personnalisées saisies par l'utilisateur, catalogue de services bloqués) : pas assez
    // volumineux pour justifier la conversion en CompactDomainSet.
    public static bool ContainsDomainOrParent(string domain, HashSet<string> domains)
    {
        ReadOnlySpan<char> current = domain;

        while (true)
        {
            if (domains.Contains(current.ToString()))
            {
                return true;
            }

            int dotIndex = current.IndexOf('.');

            if (dotIndex < 0)
            {
                return false;
            }

            current = current.Slice(dotIndex + 1);
        }
    }

    // Télécharge la liste vers son fichier de cache disque (écriture atomique : le cache précédent n'est
    // remplacé qu'une fois le téléchargement intégral réussi), puis l'analyse depuis ce fichier. En cas
    // d'échec réseau, retombe sur le contenu déjà en cache (s'il existe) plutôt que de considérer la
    // liste comme vide.
    private async Task<FilterListStatus> FetchListAsync(FilterListEntry entry, HashSet<string> destination, CancellationToken cancellationToken)
    {
        FilterListStatus status = new FilterListStatus
        {
            Id = entry.Id,
            Name = entry.Name,
            Url = entry.Url,
            Enabled = entry.Enabled,
        };

        if (!entry.Enabled)
        {
            return status;
        }

        string cacheFilePath = GetCacheFilePath(entry.Id);
        string temporaryFilePath = cacheFilePath + ".tmp";

        try
        {
            Directory.CreateDirectory(cacheDirectory);

            using (HttpResponseMessage httpResponse = await httpClient.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                httpResponse.EnsureSuccessStatusCode();

                using Stream networkStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
                using FileStream temporaryFileStream = new FileStream(temporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await networkStream.CopyToAsync(temporaryFileStream, cancellationToken);
            }

            File.Move(temporaryFilePath, cacheFilePath, overwrite: true);

            int domainCount = await ParseCacheFileAsync(cacheFilePath, destination, cancellationToken);

            status.DomainCount = domainCount;
            status.LastUpdatedUtc = DateTime.UtcNow;
            status.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeleteFile(temporaryFilePath);

            (int DomainCount, DateTime LastUpdatedUtc)? cached = await TryLoadFromDiskCacheAsync(entry.Id, destination, cancellationToken);

            if (cached is not null)
            {
                logger.LogWarning(
                    ex,
                    "Échec du téléchargement de la {ListKind} « {Name} » ({Url}) : utilisation du cache disque précédent.",
                    listKindLabel,
                    entry.Name,
                    entry.Url);

                status.DomainCount = cached.Value.DomainCount;
                status.LastUpdatedUtc = cached.Value.LastUpdatedUtc;
                status.LastError = "Téléchargement impossible (" + ex.Message + "), cache précédent utilisé.";
            }
            else
            {
                logger.LogWarning(ex, "Échec du téléchargement de la {ListKind} « {Name} » ({Url}).", listKindLabel, entry.Name, entry.Url);
                status.DomainCount = 0;
                status.LastUpdatedUtc = null;
                status.LastError = ex.Message;
            }
        }

        return status;
    }

    private async Task<(int DomainCount, DateTime LastUpdatedUtc)?> TryLoadFromDiskCacheAsync(string entryId, HashSet<string> destination, CancellationToken cancellationToken)
    {
        string cacheFilePath = GetCacheFilePath(entryId);

        if (!File.Exists(cacheFilePath))
        {
            return null;
        }

        try
        {
            int domainCount = await ParseCacheFileAsync(cacheFilePath, destination, cancellationToken);
            DateTime lastUpdatedUtc = File.GetLastWriteTimeUtc(cacheFilePath);
            return (domainCount, lastUpdatedUtc);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impossible de lire le cache disque de la {ListKind} {EntryId}.", listKindLabel, entryId);
            return null;
        }
    }

    private static async Task<int> ParseCacheFileAsync(string cacheFilePath, HashSet<string> destination, CancellationToken cancellationToken)
    {
        int domainCount = 0;

        using FileStream fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using StreamReader reader = new StreamReader(fileStream);

        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (TryParseDomain(line, out string domain))
            {
                destination.Add(domain);
                domainCount++;
            }
        }

        return domainCount;
    }

    // Supprime les fichiers de cache disque qui ne correspondent plus à aucune liste configurée (liste
    // supprimée par l'utilisateur), pour ne pas accumuler indéfiniment des fichiers orphelins.
    private void CleanupOrphanedCacheFiles(IReadOnlyList<FilterListEntry> currentLists)
    {
        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return;
            }

            HashSet<string> validFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FilterListEntry entry in currentLists)
            {
                validFileNames.Add(Path.GetFileName(GetCacheFilePath(entry.Id)));
            }

            foreach (string filePath in Directory.EnumerateFiles(cacheDirectory, "*.cache"))
            {
                if (!validFileNames.Contains(Path.GetFileName(filePath)))
                {
                    TryDeleteFile(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impossible de nettoyer les fichiers de cache orphelins des {ListKind}s.", listKindLabel);
        }
    }

    // Nom de fichier dérivé d'un hachage de l'identifiant de liste (plutôt que l'identifiant lui-même),
    // pour ne jamais dépendre de la validité de cet identifiant en tant que nom de fichier.
    private string GetCacheFilePath(string entryId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(entryId));
        string fileName = Convert.ToHexString(hash).ToLowerInvariant() + ".cache";
        return Path.Combine(cacheDirectory, fileName);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort : un fichier résiduel sera simplement écrasé/ignoré lors du prochain essai.
        }
    }

    // Reconnaît les formats usuels des listes publiques : domaine brut (une entrée par ligne), fichier
    // "hosts" ("0.0.0.0 domaine.tld"), et règles Adblock Plus ("||domaine.tld^"). Les commentaires, règles
    // d'exception ("@@...") et filtres cosmétiques ("##") sont ignorés.
    private static bool TryParseDomain(string rawLine, out string domain)
    {
        domain = string.Empty;
        string line = rawLine.Trim();

        if (line.Length == 0 || line[0] == '#' || line[0] == '!')
        {
            return false;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Contains("##", StringComparison.Ordinal) || line.Contains("#@#", StringComparison.Ordinal))
        {
            return false;
        }

        if (line.StartsWith("||", StringComparison.Ordinal))
        {
            string candidate = line.Substring(2);
            int cut = candidate.IndexOfAny(AdblockTerminators);

            if (cut >= 0)
            {
                candidate = candidate.Substring(0, cut);
            }

            return TryNormalizeDomain(candidate, out domain);
        }

        string[] tokens = line.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length >= 2 && IPAddress.TryParse(tokens[0], out _))
        {
            string candidate = tokens[1];

            if (HostsFileAliases.Contains(candidate))
            {
                return false;
            }

            return TryNormalizeDomain(candidate, out domain);
        }

        if (tokens.Length == 1)
        {
            return TryNormalizeDomain(tokens[0], out domain);
        }

        return false;
    }

    // Publique : réutilisée telle quelle par CustomFilterRulesService pour normaliser un domaine issu
    // d'une règle "||domaine.tld^" ou d'une ligne au format fichier hosts.
    public static bool TryNormalizeDomain(string candidate, out string domain)
    {
        domain = string.Empty;
        string trimmed = candidate.Trim().TrimEnd('.').ToLowerInvariant();

        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            trimmed = trimmed.Substring(2);
        }

        if (trimmed.Length == 0 || trimmed.Length > 253 || !trimmed.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in trimmed)
        {
            bool isValidCharacter = char.IsLetterOrDigit(character) || character == '-' || character == '.';

            if (!isValidCharacter)
            {
                return false;
            }
        }

        domain = trimmed;
        return true;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
