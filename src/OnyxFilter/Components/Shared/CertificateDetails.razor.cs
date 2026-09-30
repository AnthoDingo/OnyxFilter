using System;
using System.Globalization;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.Encryption;

namespace OnyxFilter.Components.Shared;

// Certificat serveur en service (page « Chiffrement ») : noms, émetteur, validité et temps restant, avec un
// avertissement si le nom du serveur n'y figure pas.
public partial class CertificateDetails : ComponentBase
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("fr-FR");

    [Parameter, EditorRequired]
    public CertificateSummary Certificate { get; set; } = default!;

    [Parameter]
    public string? ServerName { get; set; }

    private bool MissingServerName => !string.IsNullOrWhiteSpace(ServerName) && !Certificate.Covers(ServerName);

    private TimeSpan Remaining => Certificate.NotAfterUtc - DateTime.UtcNow;

    private string RemainingText
    {
        get
        {
            if (Certificate.NotBeforeUtc > DateTime.UtcNow)
            {
                return "pas encore valide";
            }

            if (Remaining <= TimeSpan.Zero)
            {
                return "expiré";
            }

            int days = (int)Remaining.TotalDays;
            return days switch
            {
                0 => "expire aujourd'hui",
                1 => "encore 1 jour",
                _ => $"encore {days} jours",
            };
        }
    }

    // Moins de 15 jours : bientôt renouvelé (Let's Encrypt) ou à remplacer (certificat fourni à la main).
    private string RemainingTone
    {
        get
        {
            if (Remaining <= TimeSpan.Zero || Certificate.NotBeforeUtc > DateTime.UtcNow)
            {
                return "is-bad";
            }

            return Remaining < LetsEncryptPolicy.RenewBefore ? "is-warn" : "is-ok";
        }
    }

    private static string FormatDay(DateTime utc)
    {
        return utc.ToLocalTime().ToString("d MMMM yyyy", DisplayCulture);
    }
}
