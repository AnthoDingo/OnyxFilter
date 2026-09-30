using System;
using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Section « Certificat Let's Encrypt » de la page "Paramètres de chiffrement" : certificat obtenu et renouvelé
// automatiquement pour le nom du serveur (voir Services/Encryption/LetsEncryptService).
public sealed class LetsEncryptSettingsData
{
    public bool Enabled { get; set; }

    // Noms à ajouter au certificat en plus du nom du serveur (ex. un alias).
    public IReadOnlyList<string> AdditionalDomains { get; set; } = Array.Empty<string>();

    // Facultative : associée au compte Let's Encrypt.
    public string Email { get; set; } = string.Empty;

    // Environnement de test de Let's Encrypt : certificats non reconnus par les appareils, mais limites
    // d'émission bien plus larges, pour vérifier la configuration.
    public bool UseStaging { get; set; }

    public bool AcceptTermsOfService { get; set; }

    // Port local du serveur de validation. Let's Encrypt contacte toujours le port 80 du domaine : à changer
    // seulement si la box redirige ce port vers un autre port de cette machine.
    public int ChallengePort { get; set; } = 80;

    // Renseignée par le service à chaque nouveau certificat : ce changement fait recharger les services
    // chiffrés (DNS-over-TLS, DNS-over-QUIC), qui comparent les paramètres de chiffrement.
    public string CertificateThumbprint { get; set; } = string.Empty;
}
