using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace OnyxFilter.Services.Updates;

// Lecture des publications (releases) du dépôt configuré via l'API GitHub, sans authentification (60
// appels par heure et par adresse IP : largement suffisant pour une vérification toutes les 12 heures).
public sealed partial class GitHubReleaseClient
{
    public const string HttpClientName = "OnyxFilterUpdates";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly UpdateOptions options;

    public GitHubReleaseClient(IHttpClientFactory httpClientFactory, IOptions<UpdateOptions> options)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options.Value;
    }

    // Publication la plus récente éligible (hors brouillons, préversions seulement si demandé), ou null si
    // le dépôt n'en a aucune.
    public async Task<ReleaseInfo?> GetLatestAsync(bool includePreReleases, CancellationToken cancellationToken)
    {
        if (!RepositoryPattern().IsMatch(options.Repository))
        {
            throw new InvalidOperationException($"Dépôt de mise à jour invalide : « {options.Repository} » (format attendu : propriétaire/nom).");
        }

        Uri releasesUrl = new Uri(options.ApiBaseUrl.TrimEnd('/') + "/repos/" + options.Repository + "/releases?per_page=30");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Réponse {(int)response.StatusCode} de {releasesUrl.Host} lors de la recherche des publications.");
        }

        List<GitHubRelease> releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(timeout.Token)
            ?? new List<GitHubRelease>();

        return releases
            .Where(release => !release.Draft && (includePreReleases || !release.PreRelease))
            .Select(ToReleaseInfo)
            .Where(release => release is not null)
            .Select(release => release!)
            .Where(release => includePreReleases || !release.Version.IsPreRelease)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
    }

    // Une adresse de téléchargement doit être en HTTPS, ou provenir de la même origine que l'API
    // configurée (miroir interne en HTTP choisi explicitement par l'administrateur).
    public bool IsTrustedDownloadUrl(Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out Uri? apiBase)
            && string.Equals(url.GetLeftPart(UriPartial.Authority), apiBase.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private static ReleaseInfo? ToReleaseInfo(GitHubRelease release)
    {
        if (!SemanticVersion.TryParse(release.TagName, out SemanticVersion version))
        {
            return null;
        }

        string packageSuffix = "-" + UpdateEnvironment.SupportedRuntimeIdentifier + ".tar.gz";

        return new ReleaseInfo
        {
            Version = version,
            TagName = release.TagName ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(release.Name) ? release.TagName ?? string.Empty : release.Name,
            Notes = release.Body ?? string.Empty,
            PublishedAt = release.PublishedAt,
            HtmlUrl = release.HtmlUrl ?? string.Empty,
            IsPreRelease = release.PreRelease || version.IsPreRelease,
            Package = FindAsset(release, name => name.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase)),
            Checksums = FindAsset(release, name => string.Equals(name, "checksums.txt", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static ReleaseAsset? FindAsset(GitHubRelease release, Func<string, bool> nameMatches)
    {
        GitHubAsset? asset = release.Assets?.FirstOrDefault(candidate => candidate.Name is not null && nameMatches(candidate.Name));

        if (asset?.Name is null || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? url))
        {
            return null;
        }

        return new ReleaseAsset { Name = asset.Name, DownloadUrl = url, Size = asset.Size, Digest = asset.Digest };
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool PreRelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
