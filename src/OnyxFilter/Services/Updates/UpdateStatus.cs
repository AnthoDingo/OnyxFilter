using System;

namespace OnyxFilter.Services.Updates;

public enum UpdateState
{
    // Aucune vérification encore effectuée depuis le démarrage.
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Verifying,
    Installing,
    // Mise à jour installée : l'application s'arrête pour être relancée par systemd.
    Restarting,
    Failed,
}

// Photo immuable de l'état des mises à jour (voir IUpdateService.Status).
public sealed record UpdateStatus
{
    public required string CurrentVersion { get; init; }

    public bool IsDevelopmentBuild { get; init; }

    public UpdateState State { get; init; }

    // Publication la plus récente trouvée lors de la dernière vérification (même si elle n'est pas plus
    // récente que la version en cours), null avant toute vérification réussie ou si le dépôt n'en a aucune.
    public ReleaseInfo? LatestRelease { get; init; }

    public bool IsUpdateAvailable { get; init; }

    public DateTimeOffset? LastCheckedUtc { get; init; }

    // Message de la dernière erreur (vérification ou installation).
    public string? LastError { get; init; }

    // Progression du téléchargement (0 à 1), pendant l'état Downloading.
    public double? Progress { get; init; }

    // Raison pour laquelle l'installation intégrée est impossible ici, null si elle est possible.
    public string? InstallBlocker { get; init; }

    public bool CanInstall => InstallBlocker is null && IsUpdateAvailable && LatestRelease?.Package is not null && LatestRelease.Checksums is not null;

    // Mise à jour ayant abouti à la version en cours (affichée après le redémarrage).
    public LastUpdateRecord? LastUpdate { get; init; }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing or UpdateState.Restarting;
}
