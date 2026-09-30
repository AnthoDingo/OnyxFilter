using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace OnyxFilter.Components.Layout;

public enum AppTheme
{
    Auto,
    Dark,
    Light
}

public partial class Footer : ComponentBase
{
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    private AppTheme SelectedTheme { get; set; } = AppTheme.Auto;

    private int CurrentYear => DateTime.Now.Year;

    private string AssemblyVersion
    {
        get
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "inconnue" : version.ToString();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // Récupère la préférence persistée côté navigateur (localStorage) pour refléter
        // l'état réel sur les boutons du sélecteur de thème.
        string preference = await JSRuntime.InvokeAsync<string>("onyxTheme.getPreference");
        AppTheme storedTheme = MapPreference(preference);

        if (storedTheme != SelectedTheme)
        {
            SelectedTheme = storedTheme;
            StateHasChanged();
        }
    }

    private async Task SelectTheme(AppTheme theme)
    {
        SelectedTheme = theme;

        string preference = theme switch
        {
            AppTheme.Dark => "dark",
            AppTheme.Light => "light",
            _ => "auto"
        };

        // Applique le thème (attribut data-bs-theme sur <html>) et le persiste dans localStorage.
        await JSRuntime.InvokeVoidAsync("onyxTheme.setPreference", preference);
    }

    private static AppTheme MapPreference(string preference)
    {
        return preference switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => AppTheme.Auto
        };
    }

    private Task CheckForUpdatesAsync()
    {
        // TODO : brancher sur un vrai mécanisme de vérification de mise à jour.
        return Task.CompletedTask;
    }
}
