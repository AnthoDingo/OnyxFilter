using System;

namespace OnyxFilter.Services.Encryption;

// Certificat Let's Encrypt automatique (page « Chiffrement ») : vérification périodique, renouvellement
// 15 jours avant l'expiration, état en direct pour l'interface.
public interface ILetsEncryptService
{
    LetsEncryptStatus Status { get; }

    // Déclenché à chaque changement d'état (étapes d'une émission comprises), depuis un thread quelconque.
    event Action? StatusChanged;

    // Vérifie le certificat sans attendre la prochaine échéance. forceRenewal : en demande un nouveau même
    // s'il est encore valide (et même pendant l'attente qui suit un échec).
    void RequestCheck(bool forceRenewal);
}
