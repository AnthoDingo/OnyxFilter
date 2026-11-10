using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OnyxFilter.Services.BootstrapServices.Toasts;

namespace OnyxFilter.Components.Shared.Toasts;

public partial class Toast : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;

    [Parameter] [EditorRequired] public string Message { get; set; } = string.Empty;
    [Parameter] public ToastSeverity Severity { get; set; } = ToastSeverity.Info;

    private ElementReference ToastRef { get; set; }

    private string CssClass
        => Severity switch
        {
            ToastSeverity.Warning => "text-bg-warning",
            ToastSeverity.Error => "text-bg-danger",
            ToastSeverity.Success => "text-bg-success",
            ToastSeverity.Info => "text-bg-info",
            _ => "text-bg-info"
        };

    private string BsTheme
        => Severity switch
        {
            ToastSeverity.Error => "dark",
            ToastSeverity.Success => "dark",
            ToastSeverity.Info => "dark",
            ToastSeverity.Warning => "dark",
            _ => "light"
        };
    
    private IJSObjectReference? module;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./Components/Shared/Toasts/Toast.razor.js"
            );

            object options = new
            {
                autohide = Severity != ToastSeverity.Error,
            };
            
            await module.InvokeVoidAsync(
                "displayToast",
                ToastRef,
                options
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module != null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
