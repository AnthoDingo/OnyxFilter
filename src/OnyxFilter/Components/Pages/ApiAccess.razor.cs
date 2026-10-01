using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.Api;
using QRCoder;

namespace OnyxFilter.Components.Pages;

public partial class ApiAccess : ComponentBase, IDisposable
{
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    // Le QR code affiché change toutes les 30 secondes ; chaque jeton reste valable 30 secondes de plus,
    // pour un code scanné juste avant d'être remplacé.
    private static readonly TimeSpan PairingQrRefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PairingTokenLifetime = TimeSpan.FromSeconds(60);

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

    private bool ShowPairingDialog { get; set; }

    private string PairingDeviceName { get; set; } = "Smartphone";

    private string PairingServerUrl { get; set; } = string.Empty;

    private string? PairingQrSvg { get; set; }

    private string? PairingError { get; set; }

    private int PairingSecondsLeft { get; set; }

    private DateTime pairingCreatedUtc;

    // Jetons d'appairage créés par cette fenêtre, encore utilisables : surveillés jusqu'à leur expiration,
    // puis oubliés (et tous oubliés à la fermeture).
    private readonly List<string> pairingTokenIds = new List<string>();

    private CancellationTokenSource? pairingLoopCts;

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

    private void OpenPairing()
    {
        if (Tokens.Count >= ApiTokenService.MaxTokenCount)
        {
            StatusMessage = L["Erreur : nombre maximal de jetons atteint ({0}). Révoquez-en un avant de connecter un smartphone.", ApiTokenService.MaxTokenCount];
            return;
        }

        PairingServerUrl = NavigationManager.BaseUri.TrimEnd('/');
        ShowPairingDialog = true;
        RegeneratePairing();

        pairingLoopCts?.Cancel();
        pairingLoopCts?.Dispose();
        pairingLoopCts = new CancellationTokenSource();
        _ = RunPairingLoopAsync(pairingLoopCts.Token);
    }

    // Chaque seconde : compte à rebours, détection de l'utilisation d'un code (fenêtre fermée, liste des
    // jetons actualisée) et remplacement du code au bout de PairingQrRefreshInterval.
    private async Task RunPairingLoopAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                foreach (string id in pairingTokenIds.ToArray())
                {
                    switch (TokenService.GetPairingTokenState(id))
                    {
                        case PairingTokenState.Consumed:
                            ClosePairing();
                            StatusMessage = L["Smartphone « {0} » connecté : son jeton figure dans la liste.", PairingDeviceName.Trim()];
                            Tokens = await TokenService.ListAsync();
                            await InvokeAsync(StateHasChanged);
                            return;
                        case PairingTokenState.Rejected:
                            PairingError = L["Erreur : nombre maximal de jetons atteint ({0}). Révoquez-en un, puis réessayez.", ApiTokenService.MaxTokenCount];
                            pairingTokenIds.Remove(id);
                            break;
                        case PairingTokenState.Expired:
                            TokenService.DiscardPairingToken(id);
                            pairingTokenIds.Remove(id);
                            break;
                    }
                }

                TimeSpan elapsed = DateTime.UtcNow - pairingCreatedUtc;

                if (elapsed >= PairingQrRefreshInterval && PairingError is null)
                {
                    RegeneratePairing();
                }
                else
                {
                    PairingSecondsLeft = Math.Max(0, (int)Math.Ceiling((PairingQrRefreshInterval - elapsed).TotalSeconds));
                }

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Nouveau jeton d'appairage et nouveau QR code (ouverture, expiration, changement du nom ou de
    // l'adresse). Le code précédent reste valable jusqu'à sa propre expiration.
    private void RegeneratePairing()
    {
        PairingError = null;
        PairingQrSvg = null;

        try
        {
            ApiTokenCreationResult pairing = TokenService.CreatePairingToken(PairingDeviceName, PairingTokenLifetime);
            pairingTokenIds.Add(pairing.Entry.Id);
            pairingCreatedUtc = DateTime.UtcNow;
            PairingSecondsLeft = (int)PairingQrRefreshInterval.TotalSeconds;
            PairingQrSvg = BuildQrSvg(BuildPairingUri(PairingServerUrl, pairing.Token));
        }
        catch (ArgumentException ex)
        {
            PairingError = L["Erreur : {0}", ex.Message];
        }
    }

    private void ClosePairing()
    {
        pairingLoopCts?.Cancel();
        ShowPairingDialog = false;
        PairingQrSvg = null;

        foreach (string id in pairingTokenIds)
        {
            TokenService.DiscardPairingToken(id);
        }

        pairingTokenIds.Clear();
    }

    // Lien lu par l'application Android : adresse de l'instance et jeton, comme sur son écran de connexion.
    public static string BuildPairingUri(string serverUrl, string token)
    {
        return "onyxfilter://pair?server=" + Uri.EscapeDataString(serverUrl.Trim().TrimEnd('/')) + "&token=" + Uri.EscapeDataString(token);
    }

    // SVG produit par QRCoder à partir de la seule chaîne encodée (aucun balisage utilisateur injecté).
    private static string BuildQrSvg(string payload)
    {
        using QRCodeGenerator generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", true, SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    public void Dispose()
    {
        ClosePairing();
        pairingLoopCts?.Dispose();
    }

    private string FormatCreatedAt(DateTime createdUtc)
    {
        return createdUtc.ToLocalTime().ToString(L["d MMMM yyyy 'à' HH:mm"], DisplayCulture);
    }
}
