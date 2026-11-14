using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OnyxFilter.Services.BootstrapServices.Toasts;

namespace OnyxFilter.Components.Shared.Toasts;

public partial class Toast : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;

    [Parameter] [EditorRequired] public string Message { get; set; } = string.Empty;
    [Parameter] public ToastSeverity Severity { get; set; } = ToastSeverity.Info;
    [Parameter] public EventCallback OnHidden { get; set; }

    private ElementReference ToastRef { get; set; }

    private string CssClass
        => Severity switch
        {
            ToastSeverity.Warning => "toast-warning",
            ToastSeverity.Error => "toast-danger",
            ToastSeverity.Success => "toast-success",
            ToastSeverity.Info => "toast-info",
            _ => "toast-info"
        };

    private IJSObjectReference? module;
    private DotNetObjectReference<Toast>? dotNetRef;

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

            dotNetRef = DotNetObjectReference.Create(this);
            
            await module.InvokeVoidAsync(
                "displayToast",
                ToastRef,
                options,
                dotNetRef
            );
        }
    }

    [JSInvokable]
    public async Task HideAsync()
    {
        await OnHidden.InvokeAsync();
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
        
        if (dotNetRef != null)
        {
            dotNetRef.Dispose();
        }
    }
}
