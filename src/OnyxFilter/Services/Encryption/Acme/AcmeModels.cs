using System;
using System.Collections.Generic;
using System.Linq;

namespace OnyxFilter.Services.Encryption.Acme;

// Objets ACME (RFC 8555) utiles à l'émission d'un certificat. Les URL restent des chaînes, reprises telles
// que le serveur les a données : elles figurent dans l'en-tête signé de chaque requête et doivent y être
// identiques à l'adresse appelée.

public sealed record AcmeOrder(
    string Url,
    string Status,
    IReadOnlyList<string> Authorizations,
    string Finalize,
    string? Certificate,
    string? Error);

public sealed record AcmeChallenge(string Type, string Url, string Token, string Status, string? Error);

public sealed record AcmeAuthorization(
    string Url,
    string Domain,
    string Status,
    IReadOnlyList<AcmeChallenge> Challenges,
    string? Error)
{
    // Erreur du défi en échec (ex. « Connection refused »), plus parlante que celle de l'autorisation.
    public string? FailureDetail => Challenges.Select(challenge => challenge.Error).FirstOrDefault(error => error is not null) ?? Error;
}

// Erreur renvoyée par le serveur ACME (document « application/problem+json ») ou réponse inattendue.
public sealed class AcmeException : Exception
{
    public AcmeException(string message, string? problemType = null, int? statusCode = null)
        : base(message)
    {
        ProblemType = problemType;
        StatusCode = statusCode;
    }

    // Ex. « urn:ietf:params:acme:error:rateLimited ».
    public string? ProblemType { get; }

    public int? StatusCode { get; }
}

// Documents JSON échangés avec le serveur (désérialisés en camelCase).
internal sealed class AcmeDirectoryDocument
{
    public string? NewNonce { get; set; }

    public string? NewAccount { get; set; }

    public string? NewOrder { get; set; }
}

internal sealed class AcmeIdentifierDocument
{
    public string? Type { get; set; }

    public string? Value { get; set; }
}

internal sealed class AcmeProblemDocument
{
    public string? Type { get; set; }

    public string? Detail { get; set; }

    public AcmeIdentifierDocument? Identifier { get; set; }

    public List<AcmeProblemDocument>? Subproblems { get; set; }

    // « detail », suivi des sous-problèmes (un par nom de domaine) s'il y en a.
    public string Describe()
    {
        string description = string.IsNullOrWhiteSpace(Detail) ? Type ?? "erreur inconnue" : Detail.Trim();

        if (Subproblems is { Count: > 0 })
        {
            IEnumerable<string> details = Subproblems.Select(problem =>
                problem.Identifier?.Value is { } domain ? $"{domain} : {problem.Describe()}" : problem.Describe());
            description += " (" + string.Join(" ; ", details) + ")";
        }

        return description;
    }
}

internal sealed class AcmeOrderDocument
{
    public string? Status { get; set; }

    public List<string>? Authorizations { get; set; }

    public string? Finalize { get; set; }

    public string? Certificate { get; set; }

    public AcmeProblemDocument? Error { get; set; }
}

internal sealed class AcmeChallengeDocument
{
    public string? Type { get; set; }

    public string? Url { get; set; }

    public string? Token { get; set; }

    public string? Status { get; set; }

    public AcmeProblemDocument? Error { get; set; }
}

internal sealed class AcmeAuthorizationDocument
{
    public AcmeIdentifierDocument? Identifier { get; set; }

    public string? Status { get; set; }

    public List<AcmeChallengeDocument>? Challenges { get; set; }

    public AcmeProblemDocument? Error { get; set; }
}
