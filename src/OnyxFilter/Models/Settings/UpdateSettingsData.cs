namespace OnyxFilter.Models.Settings;

// Réglages de la page « Mises à jour » (/settings/updates).
public sealed class UpdateSettingsData
{
    // Recherche périodique d'une nouvelle version (voir UpdateCheckBackgroundService). Ne télécharge et
    // n'installe jamais rien : l'installation reste une action explicite de l'administrateur.
    public bool AutoCheck { get; set; } = true;

    // Propose aussi les préversions publiées sur GitHub (tags « v1.5.0-beta.1 »…).
    public bool IncludePreReleases { get; set; }
}
