using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Components.Pages;

public partial class Settings : ComponentBase
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IDnsStatisticsService StatisticsService { get; set; } = default!;

    [Inject]
    public IDnsQueryLogService QueryLogService { get; set; } = default!;

    // Panneau "Bloquer les domaines / services intégrés".
    private bool BlockDomainsWithFilters { get; set; } = true;

    private int FilterUpdateIntervalHours { get; set; } = 24;

    private bool UseBrowsingSecurity { get; set; }

    private bool UseParentalControl { get; set; }

    private bool UseSafeSearch { get; set; }

    private bool SafeSearchBing { get; set; } = true;

    private bool SafeSearchDuckDuckGo { get; set; } = true;

    private bool SafeSearchEcosia { get; set; } = true;

    private bool SafeSearchGoogle { get; set; } = true;

    private bool SafeSearchPixabay { get; set; } = true;

    private bool SafeSearchYandex { get; set; } = true;

    private bool SafeSearchYoutube { get; set; } = true;

    // Panneau "Configuration du journal".
    private bool EnableQueryLog { get; set; } = true;

    private bool AnonymizeClientIp { get; set; }

    private RetentionPeriod LogRetention { get; set; } = RetentionPeriod.TwentyFourHours;

    private int LogRetentionCustomHours { get; set; } = 48;

    private bool IgnoreDomainsInLog { get; set; } = true;

    private string IgnoredLogDomainsText { get; set; } = string.Empty;

    // Panneau "Configuration des statistiques".
    private bool EnableStatistics { get; set; } = true;

    private RetentionPeriod StatisticsRetention { get; set; } = RetentionPeriod.TwentyFourHours;

    private int StatisticsRetentionCustomHours { get; set; } = 48;

    private bool IgnoreDomainsInStatistics { get; set; }

    private string IgnoredStatisticsDomainsText { get; set; } = string.Empty;

    private string? BrowsingSecurityStatusMessage { get; set; }

    private string? ParentalControlStatusMessage { get; set; }

    private string? SafeSearchStatusMessage { get; set; }

    private string? QueryLogStatusMessage { get; set; }

    private string? StatisticsStatusMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        ApplyData(settings.General);
    }

    private void ApplyData(GeneralSettingsData data)
    {
        BlockDomainsWithFilters = data.BlockDomainsWithFilters;
        FilterUpdateIntervalHours = data.FilterUpdateIntervalHours;
        UseBrowsingSecurity = data.UseBrowsingSecurity;
        UseParentalControl = data.UseParentalControl;
        UseSafeSearch = data.UseSafeSearch;
        SafeSearchBing = data.SafeSearchBing;
        SafeSearchDuckDuckGo = data.SafeSearchDuckDuckGo;
        SafeSearchEcosia = data.SafeSearchEcosia;
        SafeSearchGoogle = data.SafeSearchGoogle;
        SafeSearchPixabay = data.SafeSearchPixabay;
        SafeSearchYandex = data.SafeSearchYandex;
        SafeSearchYoutube = data.SafeSearchYoutube;
        EnableQueryLog = data.EnableQueryLog;
        AnonymizeClientIp = data.AnonymizeClientIp;
        LogRetention = data.LogRetention;
        LogRetentionCustomHours = data.LogRetentionCustomHours;
        IgnoreDomainsInLog = data.IgnoreDomainsInLog;
        IgnoredLogDomainsText = data.IgnoredLogDomainsText;
        EnableStatistics = data.EnableStatistics;
        StatisticsRetention = data.StatisticsRetention;
        StatisticsRetentionCustomHours = data.StatisticsRetentionCustomHours;
        IgnoreDomainsInStatistics = data.IgnoreDomainsInStatistics;
        IgnoredStatisticsDomainsText = data.IgnoredStatisticsDomainsText;
    }

    private GeneralSettingsData BuildData()
    {
        return new GeneralSettingsData
        {
            BlockDomainsWithFilters = BlockDomainsWithFilters,
            FilterUpdateIntervalHours = FilterUpdateIntervalHours,
            UseBrowsingSecurity = UseBrowsingSecurity,
            UseParentalControl = UseParentalControl,
            UseSafeSearch = UseSafeSearch,
            SafeSearchBing = SafeSearchBing,
            SafeSearchDuckDuckGo = SafeSearchDuckDuckGo,
            SafeSearchEcosia = SafeSearchEcosia,
            SafeSearchGoogle = SafeSearchGoogle,
            SafeSearchPixabay = SafeSearchPixabay,
            SafeSearchYandex = SafeSearchYandex,
            SafeSearchYoutube = SafeSearchYoutube,
            EnableQueryLog = EnableQueryLog,
            AnonymizeClientIp = AnonymizeClientIp,
            LogRetention = LogRetention,
            LogRetentionCustomHours = LogRetentionCustomHours,
            IgnoreDomainsInLog = IgnoreDomainsInLog,
            IgnoredLogDomainsText = IgnoredLogDomainsText,
            EnableStatistics = EnableStatistics,
            StatisticsRetention = StatisticsRetention,
            StatisticsRetentionCustomHours = StatisticsRetentionCustomHours,
            IgnoreDomainsInStatistics = IgnoreDomainsInStatistics,
            IgnoredStatisticsDomainsText = IgnoredStatisticsDomainsText,
        };
    }

    private async Task<string?> PersistAsync()
    {
        try
        {
            GeneralSettingsData data = BuildData();
            await SettingsStore.UpdateAsync(settings => settings.General = data);
            return "Paramètres enregistrés.";
        }
        catch (Exception ex)
        {
            return "Erreur lors de l'enregistrement : " + ex.Message;
        }
    }

    private void OnBlockDomainsWithFiltersChanged(ChangeEventArgs e)
    {
        BlockDomainsWithFilters = e.Value is bool value && value;

        // TODO : brancher sur le moteur de filtrage DNS une fois disponible.
    }

    private async Task OnUseBrowsingSecurityChanged(ChangeEventArgs e)
    {
        UseBrowsingSecurity = e.Value is bool value && value;

        // Enregistré immédiatement (pas de bouton "Enregistrer" dédié pour ce panneau) : IBrowsingSecurityService
        // recharge sa configuration automatiquement via ILocalSettingsStore.SettingsChanged, déclenché par
        // PersistAsync ci-dessous, et prend donc effet sans redémarrage.
        BrowsingSecurityStatusMessage = await PersistAsync();
    }

    private async Task OnUseParentalControlChanged(ChangeEventArgs e)
    {
        UseParentalControl = e.Value is bool value && value;

        // Enregistré immédiatement (pas de bouton "Enregistrer" dédié pour ce panneau) : IParentalControlService
        // recharge sa configuration automatiquement via ILocalSettingsStore.SettingsChanged, déclenché par
        // PersistAsync ci-dessous, et prend donc effet sans redémarrage.
        ParentalControlStatusMessage = await PersistAsync();
    }

    private async Task OnUseSafeSearchChanged(ChangeEventArgs e)
    {
        UseSafeSearch = e.Value is bool value && value;

        // Enregistré immédiatement (pas de bouton "Enregistrer" dédié pour ce panneau) : ISafeSearchService
        // recharge sa configuration automatiquement via ILocalSettingsStore.SettingsChanged, déclenché par
        // PersistAsync ci-dessous, et prend donc effet sans redémarrage.
        SafeSearchStatusMessage = await PersistAsync();
    }

    private async Task OnSafeSearchEngineChanged(ChangeEventArgs e, Action<bool> setter)
    {
        bool value = e.Value is bool isChecked && isChecked;
        setter(value);

        SafeSearchStatusMessage = await PersistAsync();
    }

    private void OnEnableQueryLogChanged(ChangeEventArgs e)
    {
        EnableQueryLog = e.Value is bool value && value;
    }

    private void OnAnonymizeClientIpChanged(ChangeEventArgs e)
    {
        AnonymizeClientIp = e.Value is bool value && value;
    }

    private void SetLogRetention(RetentionPeriod period)
    {
        LogRetention = period;
    }

    private void OnIgnoreDomainsInLogChanged(ChangeEventArgs e)
    {
        IgnoreDomainsInLog = e.Value is bool value && value;
    }

    private void OnEnableStatisticsChanged(ChangeEventArgs e)
    {
        EnableStatistics = e.Value is bool value && value;
    }

    private void SetStatisticsRetention(RetentionPeriod period)
    {
        StatisticsRetention = period;
    }

    private void OnIgnoreDomainsInStatisticsChanged(ChangeEventArgs e)
    {
        IgnoreDomainsInStatistics = e.Value is bool value && value;
    }

    private async Task SaveQueryLogConfigurationAsync()
    {
        // IDnsQueryLogService recharge sa configuration automatiquement via ILocalSettingsStore.SettingsChanged
        // (déclenché par PersistAsync ci-dessous) : aucun appel supplémentaire n'est nécessaire ici.
        QueryLogStatusMessage = await PersistAsync();
    }

    private async Task ClearQueryLogAsync()
    {
        bool cleared = await QueryLogService.ClearAsync();
        QueryLogStatusMessage = cleared
            ? "Journal des requêtes effacé."
            : "Erreur : la base est inaccessible, le journal en base n'a pas pu être effacé (il le sera une fois la base rétablie).";
    }

    private async Task SaveStatisticsConfigurationAsync()
    {
        // IDnsStatisticsService recharge sa configuration automatiquement via ILocalSettingsStore.SettingsChanged
        // (déclenché par PersistAsync ci-dessous) : aucun appel supplémentaire n'est nécessaire ici.
        StatisticsStatusMessage = await PersistAsync();
    }

    private Task ClearStatisticsAsync()
    {
        StatisticsService.Clear();
        StatisticsStatusMessage = "Statistiques effacées.";
        return Task.CompletedTask;
    }
}
