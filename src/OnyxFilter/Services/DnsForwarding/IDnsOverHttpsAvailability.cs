using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

// Indique si le service DNS-over-HTTPS (/dns-query) est actif, selon "Activer le chiffrement"
// (EnableEncryption) de la page /settings/encryption. La valeur est mise en cache en mémoire et
// invalidée à chaque enregistrement des paramètres (reconfiguration à chaud, sans redémarrage).
public interface IDnsOverHttpsAvailability
{
    ValueTask<bool> IsEnabledAsync();
}
