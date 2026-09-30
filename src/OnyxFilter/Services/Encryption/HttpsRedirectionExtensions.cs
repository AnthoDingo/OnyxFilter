using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace OnyxFilter.Services.Encryption;

public static class HttpsRedirectionExtensions
{
    // « Rediriger HTTP vers HTTPS » (page « Chiffrement »), pris en compte à chaud : une fois le port HTTPS
    // actif, les requêtes HTTP sont renvoyées vers lui (308, méthode conservée) et les réponses HTTPS
    // annoncent HSTS. Réglage désactivé : ni redirection ni HSTS, l'interface reste accessible en HTTP. Les
    // défis Let's Encrypt restent servis en HTTP.
    public static IApplicationBuilder UseOnyxHttpsRedirection(this IApplicationBuilder app)
    {
        IHttpsEndpointService https = app.ApplicationServices.GetRequiredService<IHttpsEndpointService>();

        return app.Use(async (context, next) =>
        {
            if (https.RedirectPort is int port)
            {
                HttpRequest request = context.Request;

                if (request.IsHttps)
                {
                    // 30 jours, comme UseHsts par défaut.
                    context.Response.Headers.StrictTransportSecurity = "max-age=2592000";
                }
                else if (!request.Path.StartsWithSegments("/.well-known/acme-challenge"))
                {
                    HostString host = port == 443 ? new HostString(request.Host.Host) : new HostString(request.Host.Host, port);
                    string location = UriHelper.BuildAbsolute("https", host, request.PathBase, request.Path, request.QueryString);
                    context.Response.Redirect(location, permanent: true, preserveMethod: true);
                    return;
                }
            }

            await next();
        });
    }
}
