using System;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OnyxFilter.Models;

namespace Microsoft.AspNetCore.Routing;

// Endpoints requis par les composants Identity (connexion par mot de passe / passkey, déconnexion).
internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapGet("/Logout", async (
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            [FromQuery] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            return TypedResults.LocalRedirect(string.IsNullOrEmpty(returnUrl) ? "~/" : $"~/{returnUrl}");
        });

        accountGroup.MapPost("/PasskeyCreationOptions", [RequireAntiforgeryToken] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager) =>
        {
            IAntiforgeryValidationFeature? antiforgeryValidationFeature = context.Features.Get<IAntiforgeryValidationFeature>();

            if (antiforgeryValidationFeature is not { IsValid: true })
            {
                return Results.BadRequest(antiforgeryValidationFeature?.Error?.Message ?? "Échec de la validation antiforgery.");
            }

            ApplicationUser? user = await userManager.GetUserAsync(context.User);

            if (user is null)
            {
                return Results.NotFound($"Utilisateur introuvable (ID '{userManager.GetUserId(context.User)}').");
            }

            string userId = await userManager.GetUserIdAsync(user);
            string userName = await userManager.GetUserNameAsync(user) ?? "Utilisateur";

            string optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(new()
            {
                Id = userId,
                Name = userName,
                DisplayName = userName,
            });

            return TypedResults.Content(optionsJson, contentType: "application/json");
        });

        accountGroup.MapPost("/PasskeyRequestOptions", [RequireAntiforgeryToken] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            [FromQuery] string? username) =>
        {
            IAntiforgeryValidationFeature? antiforgeryValidationFeature = context.Features.Get<IAntiforgeryValidationFeature>();

            if (antiforgeryValidationFeature is not { IsValid: true })
            {
                return Results.BadRequest(antiforgeryValidationFeature?.Error?.Message ?? "Échec de la validation antiforgery.");
            }

            ApplicationUser? user = string.IsNullOrEmpty(username)
                ? null
                : await userManager.FindByNameAsync(username);

            string optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(user);

            return TypedResults.Content(optionsJson, contentType: "application/json");
        });

        return accountGroup;
    }
}
