using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Encryption;

// Règles du certificat Let's Encrypt : noms demandés, validation de la configuration, échéance de
// renouvellement.
public static class LetsEncryptPolicy
{
    // Renouvellement 15 jours avant l'expiration.
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(15);

    public const string TermsOfServiceUrl = "https://letsencrypt.org/repository/";

    private static readonly Regex HostnamePattern = new Regex(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant);

    // Date à partir de laquelle renouveler : 15 jours avant l'expiration, ou au dernier tiers de la durée de
    // validité pour un certificat de courte durée (sinon il serait redemandé en boucle dès son émission).
    public static DateTime GetRenewalDueUtc(CertificateSummary certificate)
    {
        TimeSpan lifetime = certificate.NotAfterUtc - certificate.NotBeforeUtc;
        TimeSpan window = lifetime / 3 < RenewBefore ? lifetime / 3 : RenewBefore;
        return certificate.NotAfterUtc - window;
    }

    // Nom du serveur puis noms supplémentaires, normalisés, sans doublon.
    public static IReadOnlyList<string> GetDomains(EncryptionSettingsData encryption)
    {
        return new[] { encryption.ServerName }
            .Concat(encryption.LetsEncrypt.AdditionalDomains)
            .Select(domain => domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // Null si la configuration permet de demander un certificat, sinon ce qu'il faut corriger.
    public static string? ValidateConfiguration(EncryptionSettingsData encryption, out IReadOnlyList<string> domains)
    {
        domains = GetDomains(encryption);

        if (string.IsNullOrWhiteSpace(encryption.ServerName))
        {
            return "Renseignez le nom du serveur : c'est le nom inscrit dans le certificat.";
        }

        foreach (string domain in domains)
        {
            if (domain.Contains('*', StringComparison.Ordinal))
            {
                return $"« {domain} » : les noms génériques (*) ne peuvent pas être validés par HTTP.";
            }

            if (IPAddress.TryParse(domain, out IPAddress? _))
            {
                return $"« {domain} » : indiquez un nom de domaine, pas une adresse IP.";
            }

            if (!HostnamePattern.IsMatch(domain))
            {
                return $"« {domain} » n'est pas un nom de domaine complet valide (ex. dns.exemple.fr).";
            }
        }

        if (domains.Count > 100)
        {
            return "Let's Encrypt accepte au plus 100 noms par certificat.";
        }

        if (encryption.LetsEncrypt.ChallengePort is < 1 or > 65535)
        {
            return "Port de validation invalide.";
        }

        if (!encryption.LetsEncrypt.AcceptTermsOfService)
        {
            return "Acceptez les conditions d'utilisation de Let's Encrypt pour obtenir un certificat.";
        }

        return null;
    }
}
