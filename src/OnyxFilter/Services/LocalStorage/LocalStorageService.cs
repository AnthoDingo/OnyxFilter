using Microsoft.JSInterop;

namespace OnyxFilter.Services.LocalStorage;

// Préférences d'interface mémorisées dans le localStorage du navigateur. Simple confort : toute erreur
// d'interop (stockage désactivé ou plein, navigation privée, circuit déconnecté) est ignorée plutôt que
// de faire tomber le circuit Blazor.
public sealed class LocalStorageService
{
    private const string LiveKey = "onyxfilter.live";

    private readonly IJSRuntime jsRuntime;

    public LocalStorageService(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public async Task SetLiveState(bool state)
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.setItem", LiveKey, state);
        }
        catch (JSException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async Task<bool> GetLiveState()
    {
        try
        {
            string? rawValue = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", LiveKey);
            return bool.TryParse(rawValue, out bool result) && result;
        }
        catch (JSException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
    }
}
