using System;
using System.Buffers.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace OnyxFilter.Services.DnsForwarding;

// Service DNS-over-HTTPS (RFC 8484) : répond sur /dns-query, en GET (paramètre "dns" encodé en
// base64url) et en POST (corps binaire "application/dns-message"). Servi par le Kestrel existant,
// il bénéficie donc du HTTPS déjà configuré pour l'interface web, sans écouteur ni port
// supplémentaire. Actif uniquement si "Activer le chiffrement" (EnableEncryption) est coché dans
// /settings/encryption (sinon 404), avec prise en compte à chaud de chaque enregistrement de la
// page, comme le service DNS-over-TLS. La résolution passe par le même pipeline partagé que le
// port 53 (IDnsQueryPipeline) : mêmes filtres de blocage, même cache, mêmes serveurs en amont et
// mêmes statistiques. Les vérifications propres à la connexion (client autorisé, limite de
// requêtes) sont appliquées ici, comme dans chaque point d'écoute.
public static class DnsOverHttpsEndpoints
{
    private const string DnsQueryPath = "/dns-query";
    private const string DnsMessageContentType = "application/dns-message";

    // Taille minimale d'un message DNS (en-tête seul) et taille maximale acceptée, alignée sur la
    // limite TCP du service DNS (DnsProxyService.MaxTcpMessageSize).
    private const int MinMessageSize = 12;
    private const int MaxMessageSize = 4096;

    public static void MapDnsOverHttps(this IEndpointRouteBuilder endpoints)
    {
        // Endpoints anonymes : les clients DoH (navigateurs, systèmes d'exploitation) ne peuvent pas
        // s'authentifier auprès de l'interface web. Le contrôle d'accès par adresse IP de la page
        // "Paramètres DNS" reste appliqué. L'antiforgery est désactivé sur le POST : le corps est un
        // message DNS binaire, pas un formulaire.
        endpoints.MapGet(DnsQueryPath, HandleGetAsync)
            .AllowAnonymous();

        endpoints.MapPost(DnsQueryPath, HandlePostAsync)
            .AllowAnonymous()
            .DisableAntiforgery();
    }

    // Chiffrement désactivé dans /settings/encryption : l'endpoint se comporte comme s'il
    // n'existait pas (404), y compris pour les requêtes malformées.
    private static async Task<bool> IsServiceEnabledAsync(HttpContext context)
    {
        IDnsOverHttpsAvailability availability = context.RequestServices.GetRequiredService<IDnsOverHttpsAvailability>();

        if (await availability.IsEnabledAsync())
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return false;
    }

    // GET /dns-query?dns=<message DNS encodé en base64url, sans padding> (RFC 8484, section 4.1).
    private static async Task HandleGetAsync(HttpContext context)
    {
        if (!await IsServiceEnabledAsync(context))
        {
            return;
        }

        string? dnsParameter = context.Request.Query["dns"];

        if (string.IsNullOrEmpty(dnsParameter) || dnsParameter.Length > MaxMessageSize * 2)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        byte[] query;

        try
        {
            query = Base64Url.DecodeFromChars(dnsParameter.AsSpan());
        }
        catch (FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await ProcessQueryAsync(context, query);
    }

    // POST /dns-query avec un corps binaire "application/dns-message" (RFC 8484, section 4.1).
    private static async Task HandlePostAsync(HttpContext context)
    {
        if (!await IsServiceEnabledAsync(context))
        {
            return;
        }

        if (!string.Equals(context.Request.ContentType, DnsMessageContentType, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (context.Request.ContentLength is long contentLength && contentLength > MaxMessageSize)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        // Lecture bornée du corps : un octet au-delà de la limite suffit à rejeter la requête,
        // même sans en-tête Content-Length.
        byte[] readBuffer = new byte[MaxMessageSize + 1];
        int totalRead = 0;

        while (totalRead < readBuffer.Length)
        {
            int read = await context.Request.Body.ReadAsync(
                readBuffer.AsMemory(totalRead, readBuffer.Length - totalRead),
                context.RequestAborted);

            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead > MaxMessageSize)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        byte[] query = readBuffer.AsSpan(0, totalRead).ToArray();

        await ProcessQueryAsync(context, query);
    }

    // Chemin commun GET/POST : vérifications d'accès puis résolution via le pipeline partagé.
    private static async Task ProcessQueryAsync(HttpContext context, byte[] query)
    {
        if (query.Length < MinMessageSize)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        IDnsQueryPipeline queryPipeline = context.RequestServices.GetRequiredService<IDnsQueryPipeline>();
        IDnsAccessControl accessControl = context.RequestServices.GetRequiredService<IDnsAccessControl>();
        IDnsRateLimiter rateLimiter = context.RequestServices.GetRequiredService<IDnsRateLimiter>();
        CancellationToken cancellationToken = context.RequestAborted;

        // Idempotent : sans effet une fois l'initialisation faite par le premier point d'écoute.
        await queryPipeline.InitializeAsync(cancellationToken);

        IPAddress? clientAddress = context.Connection.RemoteIpAddress;

        if (clientAddress is not null)
        {
            // Client non autorisé : 403 (contrairement à l'UDP, la connexion HTTP révèle de toute
            // façon la présence du serveur ; une réponse explicite est plus utile au diagnostic).
            if (!accessControl.IsClientAllowed(clientAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Limite de requêtes atteinte pour le sous-réseau du client.
            if (!rateLimiter.IsAllowed(clientAddress))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
        }

        // Domaine interdit : requête non traitée (ni statistiques, ni résolution).
        if (queryPipeline.IsQueryDisallowed(query))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        byte[]? response = await queryPipeline.ResolveAsync(query, clientAddress, cancellationToken);

        // Aucun serveur en amont n'a répondu.
        if (response is null)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = DnsMessageContentType;
        context.Response.ContentLength = response.Length;

        // Durée de vie HTTP alignée sur le TTL DNS de la réponse (RFC 8484, section 5.1), pour que
        // les caches HTTP intermédiaires ne servent pas de réponse plus longtemps que le DNS ne
        // l'autorise. Sans TTL exploitable, la réponse n'est pas mise en cache.
        if (DnsMessageParser.TryGetCacheableTtl(response, out int ttlSeconds))
        {
            context.Response.Headers.CacheControl = $"max-age={ttlSeconds}";
        }
        else
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        await context.Response.Body.WriteAsync(response, cancellationToken);
    }
}
