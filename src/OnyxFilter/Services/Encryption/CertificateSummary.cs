using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace OnyxFilter.Services.Encryption;

// Informations affichables d'un certificat serveur : noms couverts, émetteur, validité.
public sealed record CertificateSummary(
    string Subject,
    IReadOnlyList<string> DnsNames,
    string Issuer,
    DateTime NotBeforeUtc,
    DateTime NotAfterUtc,
    string Thumbprint)
{
    public static CertificateSummary FromCertificate(X509Certificate2 certificate)
    {
        List<string> dnsNames = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CertificateSummary(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            dnsNames,
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime(),
            certificate.Thumbprint);
    }

    // Vrai si le nom figure dans le certificat, directement ou via un nom générique (« *.exemple.fr »).
    public bool Covers(string domain)
    {
        string name = domain.Trim().TrimEnd('.');

        foreach (string dnsName in DnsNames)
        {
            if (string.Equals(dnsName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (dnsName.StartsWith("*.", StringComparison.Ordinal))
            {
                int firstDot = name.IndexOf('.');

                if (firstDot > 0 && string.Equals(name.Substring(firstDot + 1), dnsName.Substring(2), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
