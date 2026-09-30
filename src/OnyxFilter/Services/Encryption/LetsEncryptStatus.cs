using System;

namespace OnyxFilter.Services.Encryption;

public enum LetsEncryptState
{
    // Option désactivée.
    Disabled,

    // Configuration incomplète (nom du serveur, conditions d'utilisation…) : voir Message.
    NotConfigured,

    // Certificat en place ; renouvellement programmé (RenewalDueUtc).
    Valid,

    // Obtention ou renouvellement en cours : Message donne l'étape.
    Issuing,

    // Dernière tentative en échec (Message) ; nouvel essai à NextCheckUtc.
    Error,
}

public sealed record LetsEncryptStatus(
    LetsEncryptState State,
    string? Message,
    ManagedCertificate? Certificate,
    DateTime? RenewalDueUtc,
    DateTime? NextCheckUtc,
    DateTime? LastCheckUtc)
{
    public static LetsEncryptStatus Initial { get; } = new LetsEncryptStatus(LetsEncryptState.Disabled, null, null, null, null, null);

    public bool IsBusy => State == LetsEncryptState.Issuing;
}
