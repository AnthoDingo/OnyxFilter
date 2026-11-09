using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace OnyxFilter.Components.Shared;

public partial class Toast : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JsRuntime { get; set; } = null!;

    [Parameter] [EditorRequired] public string Message { get; set; } = string.Empty;

    private ElementReference ToastRef { get; set; }

    private IJSObjectReference? module;
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            module = await JsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                "./Components/Shared/Toast.razor.js"
            );
            
            await module.InvokeVoidAsync("displayToast", ToastRef);
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
