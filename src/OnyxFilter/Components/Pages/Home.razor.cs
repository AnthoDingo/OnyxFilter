using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Components.Shared;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Components.Pages;

public partial class Home : ComponentBase, IDisposable
{
    private const int AutoRefreshIntervalSeconds = 5;

    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    private Timer? AutoRefreshTimer;

    private bool AutoRefreshEnabled { get; set; }

    [Inject]
    public IDnsStatisticsService StatisticsService { get; set; } = default!;

    [Inject]
    public IDnsProtectionState ProtectionState { get; set; } = default!;

    private sealed class ProtectionPauseOption
    {
        public string Label { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public Func<DateTime> ComputeUntil { get; init; } = () => DateTime.Now;
    }

    private static readonly ProtectionPauseOption[] PauseOptions =
    [
        new ProtectionPauseOption { Label = "30 s", Description = "Suspendre 30 secondes", ComputeUntil = () => DateTime.Now.AddSeconds(30) },
        new ProtectionPauseOption { Label = "1 min", Description = "Suspendre 1 minute", ComputeUntil = () => DateTime.Now.AddMinutes(1) },
        new ProtectionPauseOption { Label = "10 min", Description = "Suspendre 10 minutes", ComputeUntil = () => DateTime.Now.AddMinutes(10) },
        new ProtectionPauseOption { Label = "1 h", Description = "Suspendre 1 heure", ComputeUntil = () => DateTime.Now.AddHours(1) },
        new ProtectionPauseOption { Label = "Jusqu'à demain", Description = "Suspendre jusqu'à minuit", ComputeUntil = () => DateTime.Today.AddDays(1) },
    ];

    // Copie locale de IDnsProtectionState.IsEnabled/DisabledUntilUtc pour l'affichage : resynchronisée à
    // l'initialisation, à chaque rafraîchissement et à chaque IDnsProtectionState.Changed (y compris la
    // réactivation automatique à l'échéance, survenue en arrière-plan).
    private bool ProtectionEnabled { get; set; } = true;

    private DateTime? ProtectionDisabledUntil { get; set; }

    private long DnsQueriesCount { get; set; }
    private long BlockedByFiltersCount { get; set; }
    private int AverageProcessingTimeMs { get; set; }

    // Détections spécifiques du panneau « Protection avancée ». Restent à 0 : le moteur de filtrage n'a
    // pour le moment qu'un blocage générique par listes (pas de catégorisation malware/hameçonnage,
    // contenu adulte ou recherche sécurisée forcée dans les statistiques).
    private long MalwareBlockedCount { get; set; }
    private long AdultContentBlockedCount { get; set; }
    private long SafeSearchEnforcedCount { get; set; }

    private long AllowedCount => Math.Max(0, DnsQueriesCount - BlockedByFiltersCount);

    private double BlockedRatio => DnsQueriesCount <= 0 ? 0 : Math.Min(1.0, BlockedByFiltersCount / (double)DnsQueriesCount);

    private string BlockedPercentText => (BlockedRatio * 100).ToString(BlockedRatio > 0 && BlockedRatio < 0.1 ? "0.#" : "0", DisplayCulture) + " %";

    // Longueur de l'arc de l'anneau (cercle SVG avec pathLength="100"), en culture invariante.
    private string BlockedRingDash => (BlockedRatio * 100).ToString("0.##", CultureInfo.InvariantCulture) + " 100";

    // Série horaire (24 points, la plus ancienne en premier) : voir DnsStatisticsSnapshot.HourlySeries.
    private List<DnsStatisticsHourlyPoint> HourlySeries { get; } = new List<DnsStatisticsHourlyPoint>();

    private long AveragePerHour => HourlySeries.Count == 0 ? 0 : HourlySeries.Sum(point => point.TotalQueries) / HourlySeries.Count;

    private DnsStatisticsHourlyPoint? PeakHour { get; set; }

    private List<RankItem> TopClients { get; } = new List<RankItem>();
    private List<RankItem> TopSearchedDomains { get; } = new List<RankItem>();
    private List<RankItem> TopBlockedDomains { get; } = new List<RankItem>();

    // Panneau « Serveurs en amont » : volume de requêtes et latence moyenne réunis par serveur.
    private IReadOnlyList<DnsStatisticsUpstreamSummary> Upstreams { get; set; } = Array.Empty<DnsStatisticsUpstreamSummary>();

    private DateTime LastRefreshedAt { get; set; } = DateTime.Now;

    // true une fois Dispose() appelé : garde contre RefreshOnceAfterFirstRenderAsync ou un
    // IDnsProtectionState.Changed qui arriveraient après que l'utilisateur a quitté la page
    // (StateHasChanged sur un composant supprimé lèverait).
    private bool isDisposed;

    protected override Task OnInitializedAsync()
    {
        ApplySnapshot(StatisticsService.GetSnapshot());
        SyncProtectionState();
        ProtectionState.Changed += OnProtectionStateChanged;
        return Task.CompletedTask;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            // Un seul rafraîchissement automatique peu après l'affichage initial de la page, indépendant
            // du mode direct (désactivé par défaut) : couvre l'écart entre la donnée envoyée au
            // chargement (voir OnInitializedAsync) et l'état réel au moment où l'utilisateur consulte
            // effectivement la page.
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

    // Relit IDnsProtectionState (source de vérité, partagée par tous les transports DNS).
    private void SyncProtectionState()
    {
        ProtectionEnabled = ProtectionState.IsEnabled;
        ProtectionDisabledUntil = ProtectionEnabled ? null : ProtectionState.DisabledUntilUtc?.ToLocalTime();
    }

    private void OnProtectionStateChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            if (isDisposed)
            {
                return;
            }

            SyncProtectionState();
            StateHasChanged();
        });
    }

    // GetSnapshot() agrège l'ensemble des panneaux en une seule fois (coût négligeable : quelques
    // dictionnaires en mémoire) : toute la page est donc rafraîchie d'un bloc.
    private void ApplySnapshot(DnsStatisticsSnapshot snapshot)
    {
        DnsQueriesCount = snapshot.TotalQueries;
        BlockedByFiltersCount = snapshot.BlockedQueries;
        AverageProcessingTimeMs = snapshot.AverageProcessingTimeMs;

        FillRanking(TopClients, snapshot.TopClients);
        FillRanking(TopSearchedDomains, snapshot.TopSearchedDomains);
        FillRanking(TopBlockedDomains, snapshot.TopBlockedDomains);

        Upstreams = snapshot.BuildUpstreamSummaries();

        HourlySeries.Clear();
        HourlySeries.AddRange(snapshot.HourlySeries);

        PeakHour = null;
        foreach (DnsStatisticsHourlyPoint point in HourlySeries)
        {
            if (point.TotalQueries > 0 && (PeakHour is null || point.TotalQueries > PeakHour.Value.TotalQueries))
            {
                PeakHour = point;
            }
        }

        LastRefreshedAt = DateTime.Now;
    }

    private static void FillRanking(List<RankItem> destination, IReadOnlyList<DnsStatisticsEntry> source)
    {
        destination.Clear();
        foreach (DnsStatisticsEntry entry in source)
        {
            destination.Add(new RankItem(entry.Label, entry.Count));
        }
    }

    private void DisableProtection()
    {
        // Coupure sans échéance : jusqu'à réactivation manuelle.
        ProtectionState.Disable();
        SyncProtectionState();
    }

    private void DisableProtectionUntil(DateTime until)
    {
        // "until" est en heure locale (voir PauseOptions.ComputeUntil ci-dessus) : IDnsProtectionState
        // travaille en UTC en interne.
        ProtectionState.DisableUntil(until.ToUniversalTime());
        SyncProtectionState();
    }

    private void EnableProtection()
    {
        ProtectionState.Enable();
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
            if (isDisposed)
            {
                return;
            }

            ApplySnapshot(StatisticsService.GetSnapshot());
            SyncProtectionState();
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        isDisposed = true;
        ProtectionState.Changed -= OnProtectionStateChanged;
        StopAutoRefresh();
    }

    private static string FormatCount(long value) => value.ToString("N0", DisplayCulture);

    private string FormatHourRange(DnsStatisticsHourlyPoint point)
    {
        DateTime start = point.HourStartUtc.ToLocalTime();
        return L["requêtes de {0} h à {1} h", start.Hour, start.AddHours(1).Hour];
    }

    private string ProtectionDetail
    {
        get
        {
            if (ProtectionEnabled)
            {
                return L["Listes de blocage, services bloqués, règles personnalisées et protections avancées s'appliquent à chaque requête."];
            }

            if (ProtectionDisabledUntil is null)
            {
                return L["Les requêtes sont résolues sans filtrage jusqu'à ce que vous réactiviez la protection."];
            }

            DateTime until = ProtectionDisabledUntil.Value;
            return until.Date == DateTime.Today
                ? L["Les requêtes sont résolues sans filtrage. Reprise automatique à {0}.", until.ToString("t", DisplayCulture)]
                : L["Les requêtes sont résolues sans filtrage. Reprise automatique le {0} à {1}.", until.ToString("dddd d MMMM", DisplayCulture), until.ToString("t", DisplayCulture)];
        }
    }
}
