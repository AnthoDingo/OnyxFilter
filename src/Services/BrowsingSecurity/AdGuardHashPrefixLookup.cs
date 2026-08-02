using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Services.BrowsingSecurity;

// Client bas niveau du protocole utilisé par AdGuard Home pour ses deux services de vérification de
// domaine par préfixe de hachage (voir
// https://github.com/AdguardTeam/AdGuardHome/blob/master/internal/filtering/safebrowsing.go) : "Safe
// Browsing" (malwares/hameçonnage) et "Contrôle parental" (contenu pour adultes). Les deux services
// partagent exactement le même protocole côté AdGuard Home (même fonction "check"), seul le suffixe de
// la question DNS TXT diffère ("sb.dns.adguard.com" / "pc.dns.adguard.com") ; cette classe encapsule ce
// protocole commun, IBrowsingSecurityService et IParentalControlService n'ayant chacun qu'à lui fournir
// leur suffixe et interroger IsHostBlockedAsync.
//
// Pour un domaine demandé, seuls les 2 premiers octets (4 caractères hexadécimaux) du hachage SHA256 du
// domaine — et de ses domaines parents — sont envoyés au serveur, encodés comme labels de la question,
// interrogée en DNS-over-HTTPS auprès de family.adguard-dns.com. Le serveur répond par la liste des
// hachages complets (32 octets) partageant ce préfixe (généralement plusieurs par requête, la plupart
// sans rapport avec le domaine demandé) ; la comparaison finale avec le hachage complet du domaine se
// fait localement, sans jamais transmettre le nom de domaine en clair à ce service tiers. Les résultats
// sont mis en cache localement par préfixe (une instance de cette classe par service, donc un cache
// séparé pour chacun) pour éviter de réinterroger le service à chaque requête.
public sealed class AdGuardHashPrefixLookup : IDisposable
{
    // Service interrogé en DNS-over-HTTPS (RFC 8484) plutôt qu'en DNS brut (port 53) : évite toute
    // dépendance à une résolution UDP/TCP sortante qui peut être bloquée sur certains réseaux, et reste
    // cohérent avec le reste d'OnyxFilter (UpstreamResolver sait déjà parler DoH). Le nom d'hôte
    // "family.adguard-dns.com" est résolu par le résolveur DNS habituel du système d'exploitation :
    // contrairement aux serveurs en amont configurés par l'utilisateur, ce service tiers fixe ne fait pas
    // partie d'une chaîne de résolution pouvant créer une dépendance circulaire, donc aucun besoin de
    // passer par les serveurs d'amorçage.
    private const string DohEndpoint = "https://family.adguard-dns.com/dns-query";

    private const ushort DnsTypeTxt = 16;

    // Durée de mise en cache local d'un préfixe de hachage interrogé (2 octets), pour éviter de
    // réinterroger le service à chaque requête concernant des domaines partageant ce préfixe. Valeur
    // reprise de la configuration par défaut historique d'AdGuard Home pour ce même cache ; aucun réglage
    // dédié n'est exposé pour rester simple.
    private static readonly TimeSpan PrefixCacheDuration = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    // Nombre maximal de domaines parents vérifiés pour un nom demandé (ex. pour
    // "a.b.c.d.e.exemple.com" : "a.b.c.d.e.exemple.com", "b.c.d.e.exemple.com", ... jusqu'à cette limite),
    // pour borner le travail effectué sur un nom anormalement profond. Reprend la même limite (4) que
    // l'implémentation d'AdGuard Home.
    private const int MaxHostLevels = 4;

    private sealed class CachedPrefixResult
    {
        public CachedPrefixResult(List<byte[]> hashes, DateTime expiresAtUtc)
        {
            Hashes = hashes;
            ExpiresAtUtc = expiresAtUtc;
        }

        public List<byte[]> Hashes { get; }

        public DateTime ExpiresAtUtc { get; }
    }

    private readonly string questionSuffix;
    private readonly ILogger logger;
    private readonly HttpClient httpClient;
    private readonly object syncRoot = new object();
    private readonly Dictionary<string, CachedPrefixResult> prefixCache = new Dictionary<string, CachedPrefixResult>(StringComparer.Ordinal);

    // "questionSuffix" doit se terminer par un point (ex. "sb.dns.adguard.com."), tel qu'exigé par
    // DnsQueryBuilder.BuildQuery pour les autres labels du nom.
    public AdGuardHashPrefixLookup(string questionSuffix, ILogger logger)
    {
        this.questionSuffix = questionSuffix;
        this.logger = logger;
        httpClient = new HttpClient { Timeout = QueryTimeout };
    }

    public async Task<bool> IsHostBlockedAsync(string host, CancellationToken cancellationToken)
    {
        List<byte[]> hashes = ComputeHostHashes(host);

        if (hashes.Count == 0)
        {
            return false;
        }

        List<byte[]> hashesNeedingLookup = new List<byte[]>();

        foreach (byte[] hash in hashes)
        {
            CachedPrefixResult? cached = TryGetCachedPrefix(hash);

            if (cached is not null)
            {
                if (ContainsHash(cached.Hashes, hash))
                {
                    return true;
                }

                continue;
            }

            hashesNeedingLookup.Add(hash);
        }

        if (hashesNeedingLookup.Count == 0)
        {
            return false;
        }

        List<byte[]> receivedHashes = await QueryUpstreamAsync(hashesNeedingLookup, cancellationToken);
        StoreInCache(hashesNeedingLookup, receivedHashes);

        foreach (byte[] hash in hashesNeedingLookup)
        {
            if (ContainsHash(receivedHashes, hash))
            {
                return true;
            }
        }

        return false;
    }

    // Calcule le hachage SHA256 du domaine demandé, puis de ses domaines parents successifs (jusqu'à
    // MaxHostLevels niveaux), pour que bloquer "exemple.com" bloque aussi ses sous-domaines. S'arrête dès
    // qu'il ne reste plus qu'une seule étiquette (le TLD) : sans liste des suffixes publics embarquée
    // (pour rester léger sur un Raspberry Pi), un domaine sous un suffixe public à plusieurs étiquettes
    // (ex. "exemple.co.uk") fait interroger un niveau de plus qu'AdGuard Home ("co.uk" en plus
    // d'"exemple.co.uk"), sans incidence pratique : ce niveau supplémentaire n'a simplement aucune chance
    // de correspondre à un hachage connu du service.
    private static List<byte[]> ComputeHostHashes(string host)
    {
        List<byte[]> hashes = new List<byte[]>(MaxHostLevels);
        string current = host;
        int levels = 0;

        while (current.Length > 0 && levels < MaxHostLevels)
        {
            int dotIndex = current.IndexOf('.');

            if (dotIndex < 0)
            {
                break;
            }

            hashes.Add(SHA256.HashData(Encoding.ASCII.GetBytes(current)));
            levels++;

            current = current.Substring(dotIndex + 1);
        }

        return hashes;
    }

    private static string GetPrefixKey(byte[] hash)
    {
        return Convert.ToHexString(hash.AsSpan(0, 2));
    }

    private CachedPrefixResult? TryGetCachedPrefix(byte[] hash)
    {
        string key = GetPrefixKey(hash);

        lock (syncRoot)
        {
            if (prefixCache.TryGetValue(key, out CachedPrefixResult? cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            {
                return cached;
            }

            return null;
        }
    }

    private void StoreInCache(List<byte[]> queriedHashes, List<byte[]> receivedHashes)
    {
        DateTime expiresAtUtc = DateTime.UtcNow.Add(PrefixCacheDuration);

        // Regroupe les hachages reçus par préfixe (2 octets) : plusieurs niveaux de domaine interrogés en
        // une seule question DNS peuvent partager le même préfixe.
        Dictionary<string, List<byte[]>> receivedByPrefix = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (byte[] hash in receivedHashes)
        {
            string key = GetPrefixKey(hash);

            if (!receivedByPrefix.TryGetValue(key, out List<byte[]>? list))
            {
                list = new List<byte[]>();
                receivedByPrefix[key] = list;
            }

            list.Add(hash);
        }

        lock (syncRoot)
        {
            foreach (byte[] queriedHash in queriedHashes)
            {
                string key = GetPrefixKey(queriedHash);
                List<byte[]> hashesForPrefix = receivedByPrefix.TryGetValue(key, out List<byte[]>? list) ? list : new List<byte[]>();
                prefixCache[key] = new CachedPrefixResult(hashesForPrefix, expiresAtUtc);
            }
        }
    }

    private static bool ContainsHash(List<byte[]> hashes, byte[] target)
    {
        foreach (byte[] candidate in hashes)
        {
            if (candidate.AsSpan().SequenceEqual(target))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<List<byte[]>> QueryUpstreamAsync(List<byte[]> hashes, CancellationToken cancellationToken)
    {
        HashSet<string> uniquePrefixes = new HashSet<string>(StringComparer.Ordinal);
        StringBuilder questionBuilder = new StringBuilder();

        foreach (byte[] hash in hashes)
        {
            string prefixHex = GetPrefixKey(hash).ToLowerInvariant();

            if (uniquePrefixes.Add(prefixHex))
            {
                questionBuilder.Append(prefixHex).Append('.');
            }
        }

        questionBuilder.Append(questionSuffix);

        byte[] dnsQuery = DnsQueryBuilder.BuildQuery(questionBuilder.ToString(), DnsTypeTxt, out _);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, DohEndpoint)
        {
            Content = new ByteArrayContent(dnsQuery),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(QueryTimeout);

        using HttpResponseMessage response = await httpClient.SendAsync(request, timeoutSource.Token);
        response.EnsureSuccessStatusCode();

        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(timeoutSource.Token);

        List<string> txtStrings = DnsMessageParser.ExtractTxtStrings(responseBytes);
        List<byte[]> receivedHashes = new List<byte[]>(txtStrings.Count);

        foreach (string txt in txtStrings)
        {
            if (txt.Length == 64 && TryDecodeHexHash(txt, out byte[] hash))
            {
                receivedHashes.Add(hash);
            }
        }

        return receivedHashes;
    }

    private static bool TryDecodeHexHash(string hex, out byte[] hash)
    {
        try
        {
            hash = Convert.FromHexString(hex);
            return hash.Length == 32;
        }
        catch (FormatException)
        {
            hash = Array.Empty<byte>();
            return false;
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
