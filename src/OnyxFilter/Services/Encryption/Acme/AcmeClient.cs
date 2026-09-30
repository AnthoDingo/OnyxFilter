using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OnyxFilter.Services.Encryption.Acme;

// Client ACME minimal (RFC 8555), sans dépendance externe : compte, commande, défi HTTP-01, finalisation et
// téléchargement du certificat. Chaque requête est un POST signé (JWS ES256, clé du compte P-256) portant un
// nonce à usage unique ; un nonce refusé (« badNonce », prévu par le protocole) est retenté avec le nouveau
// nonce fourni par la réponse.
public sealed class AcmeClient
{
    private const int MaxBadNonceRetries = 5;
    private const string JoseContentType = "application/jose+json";
    private const string BadNonceType = "urn:ietf:params:acme:error:badNonce";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly string directoryUrl;
    private readonly ECDsa accountKey;
    private readonly Dictionary<string, string> jwk;
    private readonly string jwkThumbprint;

    private AcmeDirectoryDocument? directory;
    private string? nonce;

    public AcmeClient(HttpClient httpClient, string directoryUrl, ECDsa accountKey, string? accountUrl)
    {
        this.httpClient = httpClient;
        this.directoryUrl = directoryUrl;
        this.accountKey = accountKey;
        AccountUrl = accountUrl;

        ECParameters parameters = accountKey.ExportParameters(includePrivateParameters: false);

        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value || parameters.Q.X is null || parameters.Q.Y is null)
        {
            throw new ArgumentException("La clé du compte ACME doit être une clé ECDSA P-256.", nameof(accountKey));
        }

        string x = Base64Url(parameters.Q.X);
        string y = Base64Url(parameters.Q.Y);
        jwk = new Dictionary<string, string> { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = x, ["y"] = y };

        // Empreinte RFC 7638 : membres obligatoires, ordre lexicographique, sans espace.
        string canonicalJwk = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        jwkThumbprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk)));
    }

    // Adresse du compte (« kid »), connue après RegisterAccountAsync ou fournie au constructeur.
    public string? AccountUrl { get; private set; }

    // Contenu à servir sur http://<domaine>/.well-known/acme-challenge/<token> pour le défi HTTP-01.
    public string GetKeyAuthorization(string token)
    {
        return token + "." + jwkThumbprint;
    }

    // Crée le compte, ou retrouve celui de cette clé (le serveur répond alors 200 avec la même adresse).
    public async Task<string> RegisterAccountAsync(string? email, CancellationToken cancellationToken)
    {
        AcmeDirectoryDocument acmeDirectory = await GetDirectoryAsync(cancellationToken);
        Dictionary<string, object> payload = new Dictionary<string, object> { ["termsOfServiceAgreed"] = true };

        if (!string.IsNullOrWhiteSpace(email))
        {
            payload["contact"] = new[] { "mailto:" + email.Trim() };
        }

        using HttpResponseMessage response = await PostAsync(Require(acmeDirectory.NewAccount, "newAccount"), payload, useJwk: true, accept: null, cancellationToken);
        AccountUrl = response.Headers.Location?.OriginalString
            ?? throw new AcmeException("Le serveur ACME n'a pas renvoyé l'adresse du compte.");
        return AccountUrl;
    }

    public async Task<AcmeOrder> CreateOrderAsync(IReadOnlyList<string> domains, CancellationToken cancellationToken)
    {
        AcmeDirectoryDocument acmeDirectory = await GetDirectoryAsync(cancellationToken);
        object payload = new { identifiers = domains.Select(domain => new { type = "dns", value = domain }).ToArray() };

        using HttpResponseMessage response = await PostAsync(Require(acmeDirectory.NewOrder, "newOrder"), payload, useJwk: false, accept: null, cancellationToken);
        string orderUrl = response.Headers.Location?.OriginalString
            ?? throw new AcmeException("Le serveur ACME n'a pas renvoyé l'adresse de la commande.");
        return ToOrder(orderUrl, await ReadJsonAsync<AcmeOrderDocument>(response, cancellationToken));
    }

    public async Task<AcmeOrder> GetOrderAsync(string orderUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostAsync(orderUrl, payload: null, useJwk: false, accept: null, cancellationToken);
        return ToOrder(orderUrl, await ReadJsonAsync<AcmeOrderDocument>(response, cancellationToken));
    }

    public async Task<AcmeAuthorization> GetAuthorizationAsync(string authorizationUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostAsync(authorizationUrl, payload: null, useJwk: false, accept: null, cancellationToken);
        AcmeAuthorizationDocument document = await ReadJsonAsync<AcmeAuthorizationDocument>(response, cancellationToken);

        List<AcmeChallenge> challenges = (document.Challenges ?? new List<AcmeChallengeDocument>())
            .Where(challenge => challenge.Type is not null && challenge.Url is not null)
            .Select(challenge => new AcmeChallenge(challenge.Type!, challenge.Url!, challenge.Token ?? string.Empty, challenge.Status ?? string.Empty, challenge.Error?.Describe()))
            .ToList();

        return new AcmeAuthorization(
            authorizationUrl,
            document.Identifier?.Value ?? string.Empty,
            document.Status ?? string.Empty,
            challenges,
            document.Error?.Describe());
    }

    // Signale au serveur que la réponse au défi est en place : il lance alors sa vérification.
    public async Task AcceptChallengeAsync(string challengeUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostAsync(challengeUrl, new Dictionary<string, object>(), useJwk: false, accept: null, cancellationToken);
    }

    public async Task<AcmeOrder> FinalizeAsync(AcmeOrder order, byte[] certificateSigningRequest, CancellationToken cancellationToken)
    {
        object payload = new { csr = Base64Url(certificateSigningRequest) };
        using HttpResponseMessage response = await PostAsync(order.Finalize, payload, useJwk: false, accept: null, cancellationToken);
        return ToOrder(order.Url, await ReadJsonAsync<AcmeOrderDocument>(response, cancellationToken));
    }

    // Chaîne complète au format PEM : certificat puis intermédiaires.
    public async Task<string> DownloadCertificateAsync(string certificateUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostAsync(certificateUrl, payload: null, useJwk: false, accept: "application/pem-certificate-chain", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<AcmeDirectoryDocument> GetDirectoryAsync(CancellationToken cancellationToken)
    {
        if (directory is not null)
        {
            return directory;
        }

        using HttpResponseMessage response = await httpClient.GetAsync(directoryUrl, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AcmeException($"Annuaire ACME inaccessible ({(int)response.StatusCode}) : {directoryUrl}", statusCode: (int)response.StatusCode);
        }

        directory = await ReadJsonAsync<AcmeDirectoryDocument>(response, cancellationToken);
        return directory;
    }

    private async Task<string> GetNonceAsync(CancellationToken cancellationToken)
    {
        if (nonce is { } available)
        {
            nonce = null;
            return available;
        }

        AcmeDirectoryDocument acmeDirectory = await GetDirectoryAsync(cancellationToken);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, Require(acmeDirectory.NewNonce, "newNonce"));
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values) || values.FirstOrDefault() is not { } fresh)
        {
            throw new AcmeException("Le serveur ACME n'a pas fourni de nonce.", statusCode: (int)response.StatusCode);
        }

        return fresh;
    }

    private async Task<HttpResponseMessage> PostAsync(string url, object? payload, bool useJwk, string? accept, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            string requestNonce = await GetNonceAsync(cancellationToken);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            HttpResponseMessage response;

            try
            {
                // Content-Type exact, sans « charset » : certains serveurs (Boulder) refusent toute variante.
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(BuildJws(url, payload, requestNonce, useJwk)));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(JoseContentType);

                if (accept is not null)
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                }

                response = await httpClient.SendAsync(request, cancellationToken);
            }
            finally
            {
                request.Dispose();
            }

            if (response.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values))
            {
                nonce = values.FirstOrDefault();
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                AcmeProblemDocument? problem = await TryReadProblemAsync(response, cancellationToken);

                if (problem?.Type == BadNonceType && attempt < MaxBadNonceRetries)
                {
                    continue;
                }

                string message = problem?.Describe() ?? $"réponse {(int)response.StatusCode} {response.ReasonPhrase}";

                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset? retryAt = retryAfter.Date ?? (retryAfter.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);

                    if (retryAt is { } date)
                    {
                        message += $" Nouvel essai possible après le {date.ToLocalTime():dd/MM/yyyy HH:mm}.";
                    }
                }

                throw new AcmeException(message, problem?.Type, (int)response.StatusCode);
            }
        }
    }

    private string BuildJws(string url, object? payload, string requestNonce, bool useJwk)
    {
        Dictionary<string, object> header = new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["nonce"] = requestNonce,
            ["url"] = url,
        };

        if (useJwk)
        {
            header["jwk"] = jwk;
        }
        else
        {
            header["kid"] = AccountUrl ?? throw new InvalidOperationException("Compte ACME non enregistré.");
        }

        string protectedPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));

        // Charge utile vide : requête « POST-as-GET » (lecture d'une ressource).
        string payloadPart = payload is null ? string.Empty : Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));

        // ES256 : signature brute r||s (format IEEE P1363, celui par défaut de .NET), comme l'exige JWS.
        byte[] signature = accountKey.SignData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), HashAlgorithmName.SHA256);

        Dictionary<string, string> jws = new Dictionary<string, string>
        {
            ["protected"] = protectedPart,
            ["payload"] = payloadPart,
            ["signature"] = Base64Url(signature),
        };

        return JsonSerializer.Serialize(jws, JsonOptions);
    }

    private static AcmeOrder ToOrder(string orderUrl, AcmeOrderDocument document)
    {
        return new AcmeOrder(
            orderUrl,
            document.Status ?? string.Empty,
            document.Authorizations ?? new List<string>(),
            document.Finalize ?? throw new AcmeException("Commande ACME sans adresse de finalisation."),
            document.Certificate,
            document.Error?.Describe());
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new AcmeException("Réponse ACME vide.");
        }
        catch (JsonException ex)
        {
            throw new AcmeException($"Réponse ACME illisible : {ex.Message}");
        }
    }

    private static async Task<AcmeProblemDocument?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<AcmeProblemDocument>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Require(string? url, string name)
    {
        return url ?? throw new AcmeException($"L'annuaire ACME ne fournit pas « {name} ».");
    }

    public static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
