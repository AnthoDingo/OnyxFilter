using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.QueryLog;

namespace OnyxFilter.Components.Pages;

public partial class QueryLog : ComponentBase, IDisposable
{
    private static readonly int[] PageSizeOptions = { 25, 50, 100, 200 };

    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

    [Inject]
    public IDnsQueryLogService QueryLogService { get; set; } = default!;

    private List<DnsQueryLogRecord> Entries { get; set; } = new List<DnsQueryLogRecord>();

    private string SearchText { get; set; } = string.Empty;

    private bool IsCompact { get; set; } = true;

    // null = toutes les requêtes
    private QueryLogReason? FilterReason { get; set; }

    // Une page de plus que nécessaire n'est jamais demandée pour l'affichage : PageSize+1 lignes sont
    // chargées, et la dernière sert uniquement à savoir s'il existe une page suivante (voir
    // LoadPageAsync), sans jamais exécuter de COUNT(*) coûteux sur une table potentiellement volumineuse.
    // Faute de ce total, la pagination affiche une plage de requêtes plutôt qu'un nombre de pages.
    private int PageSize { get; set; } = 50;

    private int CurrentPageIndex { get; set; }

    private bool HasNextPage { get; set; }

    private bool IsLoading { get; set; } = true;

    private bool IsLogEnabled { get; set; } = true;

    private bool IsDatabaseHealthy { get; set; } = true;

    private string? FallbackFilePath { get; set; }

    private bool AutoRefreshEnabled { get; set; }

    // Non-null uniquement pendant que l'actualisation automatique est active (voir
    // StartAutoRefresh/StopAutoRefresh) : jamais deux boucles en parallèle, et toujours arrêtée à la
    // suppression du composant (Dispose) pour ne pas laisser de tâche d'arrière-plan orpheline par
    // circuit Blazor Server.
    private CancellationTokenSource? autoRefreshCts;

    // Première et dernière requête affichées sur la page courante (1-based), pour donner un repère utile
    // à l'utilisateur en l'absence de nombre total de pages.
    private string DisplayedRangeText
    {
        get
        {
            if (Entries.Count == 0)
            {
                return L["Aucune requête"];
            }

            int firstIndex = (CurrentPageIndex * PageSize) + 1;
            int lastIndex = firstIndex + Entries.Count - 1;
            return L["Requêtes {0}–{1}", firstIndex.ToString(CultureInfo.InvariantCulture), lastIndex.ToString(CultureInfo.InvariantCulture)];
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        StateHasChanged();

        IsLogEnabled = QueryLogService.IsEnabled;
        IsDatabaseHealthy = QueryLogService.IsDatabaseHealthy;
        FallbackFilePath = QueryLogService.FallbackFilePath;

        // "PageSize + 1" lignes demandées : la ligne supplémentaire (non affichée) sert uniquement à
        // savoir s'il existe une page suivante, sans exécuter de COUNT(*) sur toute la table.
        string? search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        IReadOnlyList<DnsQueryLogRecord> page = await QueryLogService.GetPageAsync(CurrentPageIndex * PageSize, PageSize + 1, search, FilterReason, default);

        HasNextPage = page.Count > PageSize;
        int displayedCount = HasNextPage ? page.Count - 1 : page.Count;
        Entries = new List<DnsQueryLogRecord>(displayedCount);

        for (int i = 0; i < displayedCount; i++)
        {
            Entries.Add(page[i]);
        }

        IsLoading = false;
    }

    private async Task RefreshAsync()
    {
        await LoadPageAsync();
    }

    private async Task GoToNextPageAsync()
    {
        if (!HasNextPage)
        {
            return;
        }

        CurrentPageIndex++;
        await LoadPageAsync();
    }

    private async Task GoToPreviousPageAsync()
    {
        if (CurrentPageIndex <= 0)
        {
            return;
        }

        CurrentPageIndex--;
        await LoadPageAsync();
    }

    private async Task OnSearchChanged(ChangeEventArgs e)
    {
        SearchText = e.Value?.ToString() ?? string.Empty;
        CurrentPageIndex = 0;
        await LoadPageAsync();
    }

    private async Task OnStatusFilterChanged(ChangeEventArgs e)
    {
        string value = e.Value?.ToString() ?? string.Empty;

        FilterReason = value switch
        {
            "Resolved" => QueryLogReason.Resolved,
            "Cached" => QueryLogReason.Cached,
            "Rewritten" => QueryLogReason.Rewritten,
            "SafeSearch" => QueryLogReason.SafeSearch,
            "CustomRule" => QueryLogReason.CustomRule,
            "Filtered" => QueryLogReason.Filtered,
            "BlockedService" => QueryLogReason.BlockedService,
            "SecurityThreat" => QueryLogReason.SecurityThreat,
            "ParentalControl" => QueryLogReason.ParentalControl,
            _ => null,
        };

        CurrentPageIndex = 0;
        await LoadPageAsync();
    }

    private async Task OnPageSizeChanged(ChangeEventArgs e)
    {
        if (e.Value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newSize) && newSize > 0)
        {
            PageSize = newSize;
            CurrentPageIndex = 0;
            await LoadPageAsync();
        }
    }

    private void OnAutoRefreshToggled(ChangeEventArgs e)
    {
        AutoRefreshEnabled = e.Value is bool value && value;

        if (AutoRefreshEnabled)
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
        StopAutoRefresh();

        CancellationTokenSource cts = new CancellationTokenSource();
        autoRefreshCts = cts;
        _ = AutoRefreshLoopAsync(cts.Token);
    }

    private void StopAutoRefresh()
    {
        autoRefreshCts?.Cancel();
        autoRefreshCts?.Dispose();
        autoRefreshCts = null;
    }

    // Recharge la page actuellement affichée toutes les AutoRefreshInterval, tant que la bascule
    // "Actualisation automatique" reste activée. Invoquée depuis une tâche d'arrière-plan (pas un
    // gestionnaire d'événement UI) : contrairement aux autres méthodes ci-dessus, elle doit donc
    // explicitement passer par InvokeAsync et appeler StateHasChanged elle-même.
    private async Task AutoRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(AutoRefreshInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    await LoadPageAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt normal : bascule désactivée ou composant supprimé (voir StopAutoRefresh/Dispose).
        }
    }

    private static string GetRowClass(QueryLogReason reason) => reason switch
    {
        QueryLogReason.Filtered
        or QueryLogReason.CustomRule
        or QueryLogReason.BlockedService
        or QueryLogReason.SecurityThreat
        or QueryLogReason.ParentalControl => "querylog-row-blocked",

        QueryLogReason.Rewritten
        or QueryLogReason.SafeSearch => "querylog-row-allowed",

        _ => string.Empty,
    };

    public void Dispose()
    {
        StopAutoRefresh();
    }
}
