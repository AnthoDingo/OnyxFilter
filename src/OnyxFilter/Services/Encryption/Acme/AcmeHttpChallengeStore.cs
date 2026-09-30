using System;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace OnyxFilter.Services.Encryption.Acme;

// Réponses aux défis HTTP-01 en cours : le serveur ACME vient lire
// http://<domaine>/.well-known/acme-challenge/<token> et attend la « key authorization » correspondante.
// Servies à la fois par l'interface web (UseAcmeHttpChallenge, utile derrière une redirection de port ou un
// proxy) et par l'écouteur temporaire du port 80 (AcmeHttpChallengeListener).
public sealed class AcmeHttpChallengeStore
{
    public const string PathPrefix = "/.well-known/acme-challenge/";

    private readonly ConcurrentDictionary<string, string> responses = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

    public bool IsEmpty => responses.IsEmpty;

    public void Add(string token, string keyAuthorization)
    {
        responses[token] = keyAuthorization;
    }

    public void Remove(string token)
    {
        responses.TryRemove(token, out string? _);
    }

    // Chemin de requête complet (ex. « /.well-known/acme-challenge/abc »), éventuelle chaîne de requête exclue.
    public bool TryGetResponse(string path, out string keyAuthorization)
    {
        keyAuthorization = string.Empty;

        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string token = path.Substring(PathPrefix.Length);
        return token.Length > 0 && responses.TryGetValue(token, out keyAuthorization!);
    }
}

public static class AcmeHttpChallengeExtensions
{
    // À placer en tête du pipeline : répond aux défis connus avant toute redirection ou authentification ;
    // les autres adresses suivent leur cours normal.
    public static IApplicationBuilder UseAcmeHttpChallenge(this IApplicationBuilder app)
    {
        AcmeHttpChallengeStore store = app.ApplicationServices.GetRequiredService<AcmeHttpChallengeStore>();

        return app.Use(async (context, next) =>
        {
            if (!store.IsEmpty
                && HttpMethods.IsGet(context.Request.Method)
                && store.TryGetResponse(context.Request.Path.Value ?? string.Empty, out string keyAuthorization))
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(keyAuthorization);
                return;
            }

            await next();
        });
    }
}
