using System;
using System.Threading.Tasks;

namespace OnyxFilter.Services.DnsForwarding;

public interface IDnsCache
{
    // Charge la configuration (activation, taille, TTL min/max) depuis les paramètres. À appeler une
    // fois avant le premier TryGet/Set (le service DNS s'en charge au démarrage).
    Task InitializeAsync();

    // Retourne une copie de la réponse mise en cache pour cette clé, ou null si absente, expirée, ou si
    // le cache est désactivé. Avec le cache optimiste activé, une entrée expirée est tout de même
    // retournée (avec un TTL court) et "isStale" vaut true : l'appelant doit alors rafraîchir l'entrée
    // en arrière-plan via les serveurs en amont.
    byte[]? TryGet(string cacheKey, out bool isStale);

    // Enregistre une réponse dans le cache pour la durée indiquée (après application des bornes
    // min/max configurées). Sans effet si le cache est désactivé.
    void Set(string cacheKey, byte[] response, TimeSpan ttl);

    // Vide entièrement le cache (utilisé par le bouton "Vider le cache" de /settings/dns).
    void Clear();
}
