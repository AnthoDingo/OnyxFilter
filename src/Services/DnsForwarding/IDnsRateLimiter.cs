using System.Net;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

// Limite le nombre de requêtes DNS par seconde et par client (regroupé par sous-réseau), selon
// RateLimitPerSecond, RateLimitSubnetLengthIpv4 et RateLimitSubnetLengthIpv6 de la page
// "Paramètres DNS". Une valeur RateLimitPerSecond de 0 désactive la limite.
public interface IDnsRateLimiter
{
    // Charge la configuration depuis les paramètres. À appeler une fois avant le premier IsAllowed
    // (le service DNS s'en charge au démarrage).
    Task InitializeAsync();

    // Retourne true si la requête du client est acceptée, false si elle doit être ignorée car le
    // sous-réseau du client a dépassé la limite pour la seconde en cours.
    bool IsAllowed(IPAddress clientAddress);
}
