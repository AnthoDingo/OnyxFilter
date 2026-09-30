using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    private const int AutoRefreshIntervalSeconds = 5;

    private Timer? AutoRefreshTimer;

    private bool AutoRefreshEnabled { get; set; }

    [Inject]
    public IDnsStatisticsService StatisticsService { get; set; } = default!;

    [Inject]
    public IDnsProtectionState ProtectionState { get; set; } = default!;

    private sealed class ProtectionDisableOption
    {
        public string Label { get; init; } = string.Empty;

        public Func<DateTime> ComputeUntil { get; init; } = () => DateTime.Now;
    }

    private static readonly ProtectionDisableOption[] DisableOptions =
    [
        new ProtectionDisableOption { Label = "Pendant 30 secondes", ComputeUntil = () => DateTime.Now.AddSeconds(30) },
        new ProtectionDisableOption { Label = "Pendant 1 minute", ComputeUntil = () => DateTime.Now.AddMinutes(1) },
        new ProtectionDisableOption { Label = "Pendant 10 minutes", ComputeUntil = () => DateTime.Now.AddMinutes(10) },
        new ProtectionDisableOption { Label = "Pendant 1 heure", ComputeUntil = () => DateTime.Now.AddHours(1) },
        new ProtectionDisableOption { Label = "Jusqu'à demain", ComputeUntil = () => DateTime.Today.AddDays(1) },
    ];

    // Copie locale de IDnsProtectionState.IsEnabled/DisabledUntilUtc pour l'affichage : resynchronisée à
    // l'initialisation et à chaque rafraîchissement (manuel ou automatique), pour refléter une
    // réactivation automatique survenue en arrière-plan (échéance atteinte) même sans action de
    // l'utilisateur sur cette page.
    private bool ProtectionEnabled { get; set; } = true;

    private bool IsProtectionMenuOpen { get; set; }

    private DateTime? ProtectionDisabledUntil { get; set; }

    private long DnsQueriesCount { get; set; }
    private long BlockedByFiltersCount { get; set; }
    private long MalwareBlockedCount { get; set; }
    private long AdultContentBlockedCount { get; set; }

    private string BlockedByFiltersPercentText => FormatPercent(BlockedByFiltersCount, DnsQueriesCount);
    private string MalwareBlockedPercentText => FormatPercent(MalwareBlockedCount, DnsQueriesCount);
    private string AdultContentBlockedPercentText => FormatPercent(AdultContentBlockedCount, DnsQueriesCount);

    // Séries horaires (24 points, une valeur par heure sur les dernières 24h) pour les graphiques des
    // cartes "Requêtes DNS" et "Bloqué par Filtres" : voir DnsStatisticsSnapshot.HourlySeries. Malware et
    // contenu adulte n'ont pas de série dédiée (comptés à 0, non encore catégorisés) : leur StatCard garde
    // le tracé de repli "Flat".
    private List<long> DnsQueriesHourly { get; } = new List<long>();
    private List<long> BlockedByFiltersHourly { get; } = new List<long>();

    private sealed class TopClientEntry
    {
        public string Client { get; init; } = string.Empty;

        public long RequestCount { get; init; }
    }

    // Panneau "Statistiques générales". MalwareBlocked/AdultBlocked/SafeSearchEnforced restent à 0 : le
    // moteur de filtrage n'a pour le moment qu'un blocage générique par listes (pas de catégorisation
    // malware/hameçonnage, contenu adulte ou recherche sécurisée forcée).
    private long GeneralStatsDnsQueries { get; set; }
    private long GeneralStatsBlockedByFilters { get; set; }
    private long GeneralStatsMalwareBlocked { get; set; }
    private long GeneralStatsAdultBlocked { get; set; }
    private long GeneralStatsSafeSearchEnforced { get; set; }
    private int GeneralStatsAverageProcessingTimeMs { get; set; }

    // Panneau "Meilleurs clients".
    private List<TopClientEntry> TopClients { get; } = new List<TopClientEntry>();

    private sealed class TopDomainEntry
    {
        public string Domain { get; init; } = string.Empty;

        public long RequestCount { get; init; }
    }

    private sealed class UpstreamEntry
    {
        public string Upstream { get; init; } = string.Empty;

        public long RequestCount { get; init; }
    }

    private sealed class UpstreamResponseTimeEntry
    {
        public string Upstream { get; init; } = string.Empty;

        public int ResponseTimeMs { get; init; }
    }

    private List<TopDomainEntry> TopSearchedDomains { get; } = new List<TopDomainEntry>();
    private List<TopDomainEntry> TopBlockedDomains { get; } = new List<TopDomainEntry>();
    private List<UpstreamEntry> TopUpstreams { get; } = new List<UpstreamEntry>();
    private List<UpstreamResponseTimeEntry> UpstreamResponseTimes { get; } = new List<UpstreamResponseTimeEntry>();

    // true une fois Dispose() appelé : garde contre RefreshOnceAfterFirstRenderAsync qui se réveille après
    // que l'utilisateur a déjà quitté la page (StateHasChanged sur un composant supprimé lèverait).
    private bool isDisposed;

    protected override Task OnInitializedAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        SyncProtectionState();
        return Task.CompletedTask;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            // Un seul rafraîchissement automatique peu après l'affichage initial de la page, indépendant
            // de la bascule "Rafraîchissement auto" (désactivée par défaut) : couvre l'écart entre la
            // donnée envoyée au chargement (voir OnInitializedAsync) et l'état réel au moment où
            // l'utilisateur consulte effectivement la page.
            _ = RefreshOnceAfterFirstRenderAsync();
        }
    }

    private async Task RefreshOnceAfterFirstRenderAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        if (isDisposed)
        {
            return;
        }

        ApplySnapshot(StatisticsService.GetSnapshot());
        SyncProtectionState();
        StateHasChanged();
    }

    // Relit IDnsProtectionState (source de vérité, partagée par tous les transports DNS) pour tenir les
    // champs d'affichage à jour, y compris après une réactivation automatique survenue en arrière-plan
    // (échéance "Pendant 30 secondes"/"Jusqu'à demain"/etc. atteinte).
    private void SyncProtectionState()
    {
        ProtectionEnabled = ProtectionState.IsEnabled;
        ProtectionDisabledUntil = ProtectionEnabled ? null : ProtectionState.DisabledUntilUtc?.ToLocalTime();
    }

    // GetSnapshot() agrège déjà l'ensemble des panneaux en une seule fois (coût négligeable : quelques
    // dictionnaires en mémoire) : chaque bouton "Actualiser" de panneau individuel recharge donc la même
    // photo instantanée plutôt que de maintenir une logique de rafraîchissement partiel séparée.
    private void ApplySnapshot(DnsStatisticsSnapshot snapshot)
    {
        DnsQueriesCount = snapshot.TotalQueries;
        BlockedByFiltersCount = snapshot.BlockedQueries;

        GeneralStatsDnsQueries = snapshot.TotalQueries;
        GeneralStatsBlockedByFilters = snapshot.BlockedQueries;
        GeneralStatsAverageProcessingTimeMs = snapshot.AverageProcessingTimeMs;

        TopClients.Clear();
        foreach (DnsStatisticsEntry entry in snapshot.TopClients)
        {
            TopClients.Add(new TopClientEntry { Client = entry.Label, RequestCount = entry.Count });
        }

        TopSearchedDomains.Clear();
        foreach (DnsStatisticsEntry entry in snapshot.TopSearchedDomains)
        {
            TopSearchedDomains.Add(new TopDomainEntry { Domain = entry.Label, RequestCount = entry.Count });
        }

        TopBlockedDomains.Clear();
        foreach (DnsStatisticsEntry entry in snapshot.TopBlockedDomains)
        {
            TopBlockedDomains.Add(new TopDomainEntry { Domain = entry.Label, RequestCount = entry.Count });
        }

        TopUpstreams.Clear();
        foreach (DnsStatisticsEntry entry in snapshot.TopUpstreams)
        {
            TopUpstreams.Add(new UpstreamEntry { Upstream = entry.Label, RequestCount = entry.Count });
        }

        UpstreamResponseTimes.Clear();
        foreach (DnsStatisticsResponseTimeEntry entry in snapshot.UpstreamResponseTimes)
        {
            UpstreamResponseTimes.Add(new UpstreamResponseTimeEntry { Upstream = entry.Upstream, ResponseTimeMs = entry.AverageResponseTimeMs });
        }

        DnsQueriesHourly.Clear();
        BlockedByFiltersHourly.Clear();
        foreach (DnsStatisticsHourlyPoint point in snapshot.HourlySeries)
        {
            DnsQueriesHourly.Add(point.TotalQueries);
            BlockedByFiltersHourly.Add(point.BlockedQueries);
        }
    }

    private Task RefreshGeneralStatsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private Task RefreshTopClientsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private Task RefreshTopSearchedDomainsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private Task RefreshTopBlockedDomainsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private Task RefreshTopUpstreamsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private Task RefreshUpstreamResponseTimesAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        return Task.CompletedTask;
    }

    private void ToggleProtection()
    {
        if (ProtectionEnabled)
        {
            // Clic sur le bouton principal : désactivation immédiate, sans échéance.
            ProtectionState.Disable();
            IsProtectionMenuOpen = false;
            SyncProtectionState();
        }
        else
        {
            EnableProtection();
        }
    }

    private void ToggleProtectionMenu()
    {
        IsProtectionMenuOpen = !IsProtectionMenuOpen;
    }

    private void DisableProtectionUntil(DateTime until)
    {
        // "until" est en heure locale (voir DisableOptions.ComputeUntil ci-dessus) : IDnsProtectionState
        // travaille en UTC en interne.
        ProtectionState.DisableUntil(until.ToUniversalTime());
        IsProtectionMenuOpen = false;
        SyncProtectionState();
    }

    private void EnableProtection()
    {
        ProtectionState.Enable();
        IsProtectionMenuOpen = false;
        SyncProtectionState();
    }

    private Task RefreshStatisticsAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        SyncProtectionState();
        return Task.CompletedTask;
    }

    private void OnAutoRefreshChanged(ChangeEventArgs args)
    {
        bool enabled = args.Value is bool value && value;
        AutoRefreshEnabled = enabled;

        if (enabled)
        {
            StartAutoRefresh();
        }
        else
        {
            StopAutoRefresh();
        }
    }

    private void StartAutoRefresh()
    {
        if (AutoRefreshTimer is not null)
        {
            return;
        }

        TimeSpan interval = TimeSpan.FromSeconds(AutoRefreshIntervalSeconds);
        AutoRefreshTimer = new Timer(OnAutoRefreshTick, null, interval, interval);
    }

    private void StopAutoRefresh()
    {
        AutoRefreshTimer?.Dispose();
        AutoRefreshTimer = null;
    }

    // Le Timer déclenche le tick sur un thread du pool : InvokeAsync ramène l'exécution sur le
    // dispatcher Blazor avant de toucher l'état du composant et de redessiner.
    private void OnAutoRefreshTick(object? state)
    {
        _ = InvokeAsync(() =>
        {
            ApplySnapshot(StatisticsService.GetSnapshot());
            SyncProtectionState();
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        isDisposed = true;
        StopAutoRefresh();
    }

    private static string FormatPercent(long part, long total)
    {
        if (total <= 0)
        {
            return "0%";
        }

        double percentage = part * 100.0 / total;
        int roundedPercentage = (int)Math.Round(percentage);
        return roundedPercentage + "%";
    }
}
