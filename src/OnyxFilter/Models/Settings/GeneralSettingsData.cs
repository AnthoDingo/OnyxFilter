namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Paramètres généraux" (/settings/general).
public sealed class GeneralSettingsData
{
    public bool BlockDomainsWithFilters { get; set; } = true;

    public int FilterUpdateIntervalHours { get; set; } = 24;

    public bool UseBrowsingSecurity { get; set; }

    public bool UseParentalControl { get; set; }

    public bool UseSafeSearch { get; set; }

    public bool SafeSearchBing { get; set; } = true;

    public bool SafeSearchDuckDuckGo { get; set; } = true;

    public bool SafeSearchEcosia { get; set; } = true;

    public bool SafeSearchGoogle { get; set; } = true;

    public bool SafeSearchPixabay { get; set; } = true;

    public bool SafeSearchYandex { get; set; } = true;

    public bool SafeSearchYoutube { get; set; } = true;

    public bool EnableQueryLog { get; set; } = true;

    public bool AnonymizeClientIp { get; set; }

    // Pays et fournisseur d'accès des clients (journal, tableau de bord, API) : nécessite le
    // téléchargement hebdomadaire d'une base publique (iptoasn.com, voir ClientLocationService).
    public bool ShowClientLocation { get; set; } = true;

    public RetentionPeriod LogRetention { get; set; } = RetentionPeriod.TwentyFourHours;

    // Durée de rétention (en heures) utilisée quand LogRetention vaut RetentionPeriod.Custom
    // ("Personnalisé"). Sans effet pour les autres valeurs de LogRetention.
    public int LogRetentionCustomHours { get; set; } = 48;

    public bool IgnoreDomainsInLog { get; set; } = true;

    public string IgnoredLogDomainsText { get; set; } = string.Empty;

    public bool EnableStatistics { get; set; } = true;

    public RetentionPeriod StatisticsRetention { get; set; } = RetentionPeriod.TwentyFourHours;

    // Durée de rétention (en heures) utilisée quand StatisticsRetention vaut RetentionPeriod.Custom
    // ("Personnalisé"). Sans effet pour les autres valeurs de StatisticsRetention.
    public int StatisticsRetentionCustomHours { get; set; } = 48;

    public bool IgnoreDomainsInStatistics { get; set; }

    public string IgnoredStatisticsDomainsText { get; set; } = string.Empty;
}
