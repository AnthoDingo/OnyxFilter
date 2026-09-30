using System;

namespace OnyxFilter.Services.Updates;

// Une publication GitHub candidate à l'installation (voir GitHubReleaseClient).
public sealed class ReleaseInfo
{
    public required SemanticVersion Version { get; init; }

    public required string TagName { get; init; }

    public string Name { get; init; } = string.Empty;

    // Notes de version (Markdown brut de la publication).
    public string Notes { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }

    public string HtmlUrl { get; init; } = string.Empty;

    public bool IsPreRelease { get; init; }

    // Archive pour la plateforme courante (OnyxFilter-<version>-linux-x64.tar.gz), null si absente.
    public ReleaseAsset? Package { get; init; }

    // Liste des empreintes SHA-256 (checksums.txt), null si absente : l'installation est alors refusée.
    public ReleaseAsset? Checksums { get; init; }
}

public sealed class ReleaseAsset
{
    public required string Name { get; init; }

    public required Uri DownloadUrl { get; init; }

    public long Size { get; init; }

    // Empreinte fournie par GitHub (« sha256:… »), si disponible : vérifiée en plus de checksums.txt.
    public string? Digest { get; init; }
}
