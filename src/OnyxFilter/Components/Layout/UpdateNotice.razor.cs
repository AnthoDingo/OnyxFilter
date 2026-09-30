using System;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.Updates;

namespace OnyxFilter.Components.Layout;

// Pastille « nouvelle version disponible » de la barre latérale (NavMenu), rendue en mode interactif pour
// suivre IUpdateService.StatusChanged (vérification périodique, installation lancée depuis une autre page
// ou l'API). Masquée sur une compilation de développement, que toute publication dépasse.
public partial class UpdateNotice : ComponentBase, IDisposable
{
    [Inject]
    private IUpdateService UpdateService { get; set; } = default!;

    private UpdateStatus Status { get; set; } = default!;

    private bool IsInProgress => Status.State is UpdateState.Downloading or UpdateState.Verifying or UpdateState.Installing or UpdateState.Restarting;

    private bool IsVisible => IsInProgress || (Status.IsUpdateAvailable && !Status.IsDevelopmentBuild);

    private string LatestVersion => Status.LatestRelease?.Version.ToString() ?? string.Empty;

    protected override void OnInitialized()
    {
        Status = UpdateService.Status;
        UpdateService.StatusChanged += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            Status = UpdateService.Status;
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        UpdateService.StatusChanged -= OnStatusChanged;
    }
}
