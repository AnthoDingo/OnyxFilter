using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.Api;

namespace OnyxFilter.Components.Pages;

public partial class ApiAccess : ComponentBase
{
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    [Inject]
    public IApiTokenService TokenService { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    private IReadOnlyList<ApiTokenEntry> Tokens { get; set; } = Array.Empty<ApiTokenEntry>();

    private string NewTokenName { get; set; } = string.Empty;

    private bool IsCreating { get; set; }

    // Valeur en clair du jeton qui vient d'être créé : affichée une seule fois, jamais relue ensuite.
    private string? CreatedToken { get; set; }

    private string? CreatedTokenName { get; set; }

    private bool TokenCopied { get; set; }

    private string? StatusMessage { get; set; }

    private ApiTokenEntry? TokenPendingRevocation { get; set; }

    private string ApiBaseUrl => NavigationManager.BaseUri.TrimEnd('/') + "/api/v1";

    protected override async Task OnInitializedAsync()
    {
        Tokens = await TokenService.ListAsync();
    }

    private async Task CreateTokenAsync()
    {
        IsCreating = true;
        StatusMessage = null;
        CreatedToken = null;
        TokenCopied = false;

        try
        {
            ApiTokenCreationResult result = await TokenService.CreateAsync(NewTokenName);
            CreatedToken = result.Token;
            CreatedTokenName = result.Entry.Name;
            NewTokenName = string.Empty;
            Tokens = await TokenService.ListAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusMessage = L["Erreur : {0}", ex.Message];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de l'enregistrement du jeton : {0}", ex.Message];
        }
        finally
        {
            IsCreating = false;
        }
    }

    private async Task CopyTokenAsync()
    {
        if (CreatedToken is null)
        {
            return;
        }

        try
        {
            // L'API Presse-papiers n'est disponible qu'en HTTPS ou sur localhost : en HTTP sur le réseau
            // local, l'utilisateur sélectionne et copie le champ lui-même.
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", CreatedToken);
            TokenCopied = true;
        }
        catch (JSException)
        {
            StatusMessage = L["Erreur : copie automatique indisponible (HTTPS requis). Sélectionnez le jeton et copiez-le manuellement."];
        }
    }

    private void DismissCreatedToken()
    {
        CreatedToken = null;
        CreatedTokenName = null;
        TokenCopied = false;
    }

    private void RequestRevoke(ApiTokenEntry token)
    {
        TokenPendingRevocation = token;
    }

    private void CancelRevoke()
    {
        TokenPendingRevocation = null;
    }

    private async Task ConfirmRevokeAsync()
    {
        if (TokenPendingRevocation is null)
        {
            return;
        }

        string name = TokenPendingRevocation.Name;

        try
        {
            bool revoked = await TokenService.RevokeAsync(TokenPendingRevocation.Id);
            StatusMessage = revoked
                ? L["Jeton « {0} » révoqué : il ne donne plus accès à l'API.", name]
                : L["Erreur : le jeton « {0} » n'existe plus.", name];
            Tokens = await TokenService.ListAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de la révocation : {0}", ex.Message];
        }
        finally
        {
            TokenPendingRevocation = null;
        }
    }

    private string FormatCreatedAt(DateTime createdUtc)
    {
        return createdUtc.ToLocalTime().ToString(L["d MMMM yyyy 'à' HH:mm"], DisplayCulture);
    }
}
