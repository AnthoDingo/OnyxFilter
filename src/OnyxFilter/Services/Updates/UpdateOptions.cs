namespace OnyxFilter.Services.Updates;

// Section « Updates » d'appsettings.json : source des publications et contraintes d'installation. Les
// valeurs par défaut conviennent à une installation standard ; elles ne se modifient qu'à la main (fork,
// miroir interne, autre superviseur que systemd).
public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    // Dépôt GitHub « propriétaire/nom » dont les publications (releases) sont suivies.
    public string Repository { get; set; } = "AnthoDingo/OnyxFilter";

    // Racine de l'API GitHub (ou d'un miroir compatible).
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    // À activer si OnyxFilter est relancé automatiquement après son arrêt par un autre superviseur que
    // systemd (runit, supervisord…) : sans superviseur, l'application ne redémarrerait pas après la mise à
    // jour, l'installation intégrée est donc refusée par défaut hors systemd.
    public bool AssumeSupervised { get; set; }
}
