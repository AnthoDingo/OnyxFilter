using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Components.Layout;

// État de la protection DNS affiché en permanence dans la barre latérale (NavMenu). Rendu en mode
// interactif (voir NavMenu.razor) pour suivre IDnsProtectionState.Changed : une pause lancée depuis la
// vue d'ensemble, ou la réactivation automatique à l'échéance, se reflète sans recharger la page.
public partial class ProtectionIndicator : ComponentBase, IDisposable
{
    [Inject]
    private IDnsProtectionState ProtectionState { get; set; } = default!;

    private bool IsEnabled { get; set; } = true;

    private string Detail { get; set; } = string.Empty;

    protected override void OnInitialized()
    {
        Sync();
        ProtectionState.Changed += OnProtectionStateChanged;
    }

    private void Sync()
    {
        IsEnabled = ProtectionState.IsEnabled;

        if (IsEnabled)
        {
            Detail = "Filtrage appliqué";
            return;
        }

        DateTime? until = ProtectionState.DisabledUntilUtc?.ToLocalTime();
        Detail = until is null
            ? "Jusqu'à réactivation"
            : "Reprise à " + until.Value.ToString(until.Value.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm");
    }

    private void OnProtectionStateChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(() =>
        {
            Sync();
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        ProtectionState.Changed -= OnProtectionStateChanged;
    }
}
