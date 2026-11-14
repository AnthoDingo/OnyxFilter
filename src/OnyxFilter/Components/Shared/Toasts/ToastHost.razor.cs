using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.BootstrapServices.Toasts;

namespace OnyxFilter.Components.Shared.Toasts;

public partial class ToastHost : ComponentBase, IDisposable
{
    [Inject] private ToastService ToastService { get; set; } = null!;

    private readonly List<ToastInfos> toasts = [];

    protected override void OnInitialized()
    {
        ToastService.ToastRequested += HandleToast;
    }

    private void HandleToast(object? sender, ToastInfos toast)
    {
        _ = InvokeAsync(() =>
        {
            toasts.Add(toast);
            
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        ToastService.ToastRequested -= HandleToast;
    }
}
