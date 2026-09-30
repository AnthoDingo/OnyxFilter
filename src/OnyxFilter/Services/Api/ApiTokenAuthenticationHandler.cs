using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Api;

// Authentification de l'API HTTP (/api/v1) par jeton, présenté dans l'en-tête
// « Authorization: Bearer <jeton> » ou « X-Api-Key: <jeton> ». Seul schéma accepté par l'API : le cookie
// de session de l'interface n'y donne pas accès, ce qui écarte toute requête intersite (CSRF) portée par
// le navigateur d'un administrateur connecté.
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OnyxApiToken";

    public const string ApiKeyHeaderName = "X-Api-Key";

    private readonly IApiTokenService tokenService;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiTokenService tokenService)
        : base(options, logger, encoder)
    {
        this.tokenService = tokenService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presentedToken = ReadPresentedToken(Request);

        if (presentedToken is null)
        {
            return AuthenticateResult.NoResult();
        }

        ApiTokenEntry? token = await tokenService.ValidateAsync(presentedToken);

        if (token is null)
        {
            return AuthenticateResult.Fail("Jeton d'API invalide ou révoqué.");
        }

        ClaimsIdentity identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "api-token:" + token.Id),
                new Claim(ClaimTypes.Name, token.Name),
            },
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    // Réponse JSON explicite plutôt qu'une réponse vide : la page d'erreur HTML de l'interface ne doit pas
    // s'y substituer (voir Program.cs, UseStatusCodePagesWithReExecute).
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        await Response.WriteAsJsonAsync(new ApiError(
            "unauthorized",
            "Jeton d'API manquant, invalide ou révoqué. Créez-en un depuis la page « Accès API » de l'interface."));
    }

    private static string? ReadPresentedToken(HttpRequest request)
    {
        string authorization = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";

        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string value = authorization.Substring(bearerPrefix.Length).Trim();
            return value.Length > 0 ? value : null;
        }

        string apiKey = request.Headers[ApiKeyHeaderName].ToString().Trim();
        return apiKey.Length > 0 ? apiKey : null;
    }
}
