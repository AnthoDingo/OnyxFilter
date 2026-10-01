using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using OnyxFilter.Models;

namespace OnyxFilter.Components.Account.Pages.Manage;

public partial class Passkeys : ComponentBase
{
    private const int MaxPasskeyCount = 20;

    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    [Inject]
    public UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    public SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    public IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    public HttpContext HttpContext { get; set; } = default!;

    // .NET 10 n'a pas [SupplyParameterFromTempData] (ajouté en .NET 11) : le message de statut
    // transite via la query string après la redirection post-action.
    [SupplyParameterFromQuery(Name = "message")]
    public string? Message { get; set; }

    [SupplyParameterFromForm]
    public string? CredentialId { get; set; }

    [SupplyParameterFromForm(FormName = "add-passkey")]
    public PasskeyInputModel Input { get; set; } = default!;

    private ApplicationUser? CurrentUser { get; set; }

    private IList<UserPasskeyInfo>? CurrentPasskeys { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Input ??= new PasskeyInputModel();

        CurrentUser = await UserManager.GetUserAsync(HttpContext.User);

        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        CurrentPasskeys = await UserManager.GetPasskeysAsync(CurrentUser);
    }

    private static string EncodeCredentialId(byte[] credentialId)
    {
        return Base64Url.EncodeToString(credentialId);
    }

    private static string FormatCreatedAt(DateTimeOffset createdAt)
    {
        return createdAt.UtcDateTime.ToString("d MMMM yyyy", DisplayCulture);
    }

    private void RedirectWithMessage(string message)
    {
        RedirectManager.RedirectToCurrentPage(new Dictionary<string, object?>
        {
            ["message"] = message,
        });
    }

    private async Task AddPasskeyAsync()
    {
        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        if (!string.IsNullOrEmpty(Input.Error))
        {
            RedirectWithMessage(L["Erreur : {0}", Input.Error!]);
            return;
        }

        if (string.IsNullOrEmpty(Input.CredentialJson))
        {
            RedirectWithMessage(L["Erreur : le navigateur n'a pas fourni de clé d'accès."]);
            return;
        }

        if (CurrentPasskeys is { Count: >= MaxPasskeyCount })
        {
            RedirectWithMessage(L["Erreur : nombre maximal de clés d'accès atteint."]);
            return;
        }

        PasskeyAttestationResult attestationResult = await SignInManager.PerformPasskeyAttestationAsync(Input.CredentialJson);

        if (!attestationResult.Succeeded)
        {
            RedirectWithMessage(L["Erreur : impossible d'ajouter la clé d'accès : {0}", attestationResult.Failure!.Message]);
            return;
        }

        attestationResult.Passkey.Name ??= "Clé d'accès";

        IdentityResult addPasskeyResult = await UserManager.AddOrUpdatePasskeyAsync(CurrentUser, attestationResult.Passkey);

        if (!addPasskeyResult.Succeeded)
        {
            RedirectWithMessage(L["Erreur : la clé d'accès n'a pas pu être enregistrée."]);
            return;
        }

        RedirectWithMessage(L["Votre clé d'accès a été ajoutée."]);
    }

    private async Task DeletePasskeyAsync()
    {
        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        if (string.IsNullOrEmpty(CredentialId))
        {
            RedirectWithMessage(L["Erreur : identifiant de clé d'accès manquant."]);
            return;
        }

        byte[] credentialIdBytes;

        try
        {
            credentialIdBytes = Base64Url.DecodeFromChars(CredentialId);
        }
        catch (FormatException)
        {
            RedirectWithMessage(L["Erreur : identifiant de clé d'accès invalide."]);
            return;
        }

        IdentityResult result = await UserManager.RemovePasskeyAsync(CurrentUser, credentialIdBytes);

        if (!result.Succeeded)
        {
            RedirectWithMessage(L["Erreur : la clé d'accès n'a pas pu être supprimée."]);
            return;
        }

        RedirectWithMessage(L["Clé d'accès supprimée."]);
    }
}
