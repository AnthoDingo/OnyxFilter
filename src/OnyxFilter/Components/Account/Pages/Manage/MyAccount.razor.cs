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

public partial class MyAccount : ComponentBase
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

    [SupplyParameterFromQuery(Name = "message")]
    public string? Message { get; set; }

    [SupplyParameterFromForm(FormName = "change-username")]
    public ChangeUsernameInputModel UsernameInput { get; set; } = default!;

    [SupplyParameterFromForm(FormName = "change-password")]
    public ChangePasswordInputModel PasswordInput { get; set; } = default!;

    [SupplyParameterFromForm]
    public string? CredentialId { get; set; }

    [SupplyParameterFromForm(FormName = "add-passkey")]
    public PasskeyInputModel PasskeyInput { get; set; } = default!;

    private ApplicationUser? CurrentUser { get; set; }

    private string CurrentUsername { get; set; } = string.Empty;

    private IList<UserPasskeyInfo>? CurrentPasskeys { get; set; }

    protected override async Task OnInitializedAsync()
    {
        UsernameInput ??= new ChangeUsernameInputModel();
        PasswordInput ??= new ChangePasswordInputModel();
        PasskeyInput ??= new PasskeyInputModel();

        CurrentUser = await UserManager.GetUserAsync(HttpContext.User);

        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        CurrentUsername = CurrentUser.UserName ?? string.Empty;
        CurrentPasskeys = await UserManager.GetPasskeysAsync(CurrentUser);
    }

    private void RedirectWithMessage(string message)
    {
        RedirectManager.RedirectToCurrentPage(new Dictionary<string, object?>
        {
            ["message"] = message,
        });
    }

    private async Task ChangeUsernameAsync()
    {
        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        if (string.IsNullOrWhiteSpace(UsernameInput.NewUsername))
        {
            RedirectWithMessage(L["Erreur : le nom d'utilisateur ne peut pas être vide."]);
            return;
        }

        IdentityResult result = await UserManager.SetUserNameAsync(CurrentUser, UsernameInput.NewUsername);

        if (!result.Succeeded)
        {
            RedirectWithMessage(L["Erreur : impossible de modifier le nom d'utilisateur."]);
            return;
        }

        await SignInManager.RefreshSignInAsync(CurrentUser);
        RedirectWithMessage(L["Nom d'utilisateur modifié avec succès."]);
    }

    private async Task ChangePasswordAsync()
    {
        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        if (PasswordInput.NewPassword != PasswordInput.ConfirmNewPassword)
        {
            RedirectWithMessage(L["Erreur : les mots de passe ne correspondent pas."]);
            return;
        }

        IdentityResult result = await UserManager.ChangePasswordAsync(
            CurrentUser,
            PasswordInput.CurrentPassword,
            PasswordInput.NewPassword);

        if (!result.Succeeded)
        {
            RedirectWithMessage(L["Erreur : impossible de modifier le mot de passe. Vérifiez votre mot de passe actuel."]);
            return;
        }

        await SignInManager.RefreshSignInAsync(CurrentUser);
        RedirectWithMessage(L["Mot de passe modifié avec succès."]);
    }

    private async Task AddPasskeyAsync()
    {
        if (CurrentUser is null)
        {
            RedirectManager.RedirectTo("login");
            return;
        }

        if (!string.IsNullOrEmpty(PasskeyInput.Error))
        {
            RedirectWithMessage(L["Erreur : {0}", PasskeyInput.Error!]);
            return;
        }

        if (string.IsNullOrEmpty(PasskeyInput.CredentialJson))
        {
            RedirectWithMessage(L["Erreur : le navigateur n'a pas fourni de clé d'accès."]);
            return;
        }

        if (CurrentPasskeys is { Count: >= MaxPasskeyCount })
        {
            RedirectWithMessage(L["Erreur : nombre maximal de clés d'accès atteint."]);
            return;
        }

        PasskeyAttestationResult attestationResult = await SignInManager.PerformPasskeyAttestationAsync(PasskeyInput.CredentialJson);

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

    private static string EncodeCredentialId(byte[] credentialId)
    {
        return Base64Url.EncodeToString(credentialId);
    }

    private static string FormatCreatedAt(DateTimeOffset createdAt)
    {
        return createdAt.UtcDateTime.ToString("d MMMM yyyy", DisplayCulture);
    }

    public sealed class ChangeUsernameInputModel
    {
        public string NewUsername { get; set; } = string.Empty;
    }

    public sealed class ChangePasswordInputModel
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }
}
