using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnyxFilter.Models;

namespace OnyxFilter.Components.Account;

// AuthenticationStateProvider côté serveur qui revalide le security stamp de l'utilisateur
// toutes les 30 minutes tant qu'un circuit interactif est connecté.
internal sealed class IdentityRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<IdentityOptions> options;

    public IdentityRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> options)
        : base(loggerFactory)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // Récupère le UserManager depuis un nouveau scope pour garantir des données à jour.
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await ValidateSecurityStampAsync(userManager, authenticationState.User);
    }

    private async Task<bool> ValidateSecurityStampAsync(UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
    {
        ApplicationUser? user = await userManager.GetUserAsync(principal);

        if (user is null)
        {
            return false;
        }

        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        string? principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        string? userStamp = await userManager.GetSecurityStampAsync(user);
        return principalStamp == userStamp;
    }
}
