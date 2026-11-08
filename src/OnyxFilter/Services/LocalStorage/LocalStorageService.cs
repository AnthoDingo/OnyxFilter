using Microsoft.JSInterop;

namespace OnyxFilter.Services.LocalStorage;

public sealed class LocalStorageService
{
    private const string GetItem = "localStorage.getItem";
    private const string SetItem = "localStorage.setItem";
    
    private readonly IJSRuntime jsRuntime;

    public LocalStorageService(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public async Task SetLiveState(bool state)
    {
        await jsRuntime.InvokeVoidAsync(SetItem, "live", state);
    }

    public async Task<bool> GetLiveState()
    {
        string rawValue = await jsRuntime.InvokeAsync<string>(GetItem, "live");
            
        if (bool.TryParse(rawValue, out bool result))
        {
            return result;
        }
        else
        {
            return false;
        }   
    }
}
