using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using OnyxFilter.Components.Account;
using OnyxFilter.Models;

namespace OnyxFilter.Components.Pages;

public partial class Login : ComponentBase
{
    [Inject]
    public UserManager<ApplicationUser> UserManager { get; set; } = default!;

    [Inject]
    public SignInManager<ApplicationUser> SignInManager { get; set; } = default!;

    [Inject]
    public ILogger<Login> Logger { get; set; } = default!;

    [Inject]
    public IdentityRedirectManager RedirectManager { get; set; } = default!;

    [CascadingParameter]
    public HttpContext HttpContext { get; set; } = default!;

    [SupplyParameterFromForm]
    public LoginInputModel Input { get; set; } = default!;

    [SupplyParameterFromQuery]
    public string? ReturnUrl { get; set; }

    private string? ErrorMessage { get; set; }

    private EditContext CurrentEditContext { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        Input ??= new LoginInputModel();
        CurrentEditContext = new EditContext(Input);

        if (HttpMethods.IsGet(HttpContext.Request.Method))
        {
            // Nettoie le cookie externe pour repartir d'un état propre.
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        }
    }

    private async Task LoginUserAsync()
    {
        if (!string.IsNullOrEmpty(Input.Passkey?.Error))
        {
            ErrorMessage = $"Erreur : {Input.Passkey.Error}";
            return;
        }

        SignInResult result;

        if (!string.IsNullOrEmpty(Input.Passkey?.CredentialJson))
        {
            result = await SignInManager.PasskeySignInAsync(Input.Passkey.CredentialJson);
        }
        else
        {
            if (!CurrentEditContext.Validate())
            {
                return;
            }

            // Ne compte pas les échecs pour le verrouillage de compte.
            result = await SignInManager.PasswordSignInAsync(Input.Username, Input.Password, isPersistent: true, lockoutOnFailure: false);
        }

        if (result.Succeeded)
        {
            Logger.LogInformation("Utilisateur connecté.");
            RedirectManager.RedirectTo(ReturnUrl);
        }
        else if (result.IsLockedOut)
        {
            Logger.LogWarning("Compte verrouillé.");
            ErrorMessage = "Erreur : ce compte est temporairement verrouillé.";
        }
        else
        {
            ErrorMessage = "Erreur : identifiants invalides.";
        }
    }

    public sealed class LoginInputModel
    {
        [Required(ErrorMessage = "Le nom d'utilisateur est requis.")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Le mot de passe est requis.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public PasskeyInputModel? Passkey { get; set; }
    }
}
