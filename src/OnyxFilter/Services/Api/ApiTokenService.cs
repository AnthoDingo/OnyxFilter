using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Api;

// Jetons stockés dans appsettings.local.json (bloc "Api", voir ILocalSettingsStore), sous forme
// d'empreinte SHA-256 uniquement. Les empreintes sont gardées en mémoire pour ne pas relire le fichier à
// chaque appel de l'API, et rechargées à chaque modification des réglages.
public sealed class ApiTokenService : IApiTokenService
{
    public const string TokenPrefix = "onyx_";

    public const int MaxTokenCount = 20;

    public const int MaxNameLength = 64;

    // 32 octets aléatoires (256 bits) : une recherche exhaustive est hors de portée, inutile de limiter le
    // nombre de tentatives.
    private const int TokenEntropyBytes = 32;

    private const int DisplayedPrefixLength = 10;

    private readonly ILocalSettingsStore settingsStore;

    // Instantané immuable (identifiant, empreinte binaire) remplacé en bloc : lu sans verrou par
    // ValidateAsync, invalidé (null) à chaque écriture des réglages.
    private volatile IReadOnlyList<(ApiTokenEntry Entry, byte[] Hash)>? cachedTokens;

    // Incrémenté à chaque écriture des réglages : un rechargement commencé avant une écriture (ex. une
    // révocation) ne doit pas remettre en cache la liste qu'il a lue, déjà périmée. Lu et modifié sous
    // cacheLock, avec l'affectation de cachedTokens.
    private int settingsVersion;
    private readonly object cacheLock = new object();

    // Jetons d'appairage en attente (voir CreatePairingToken), par identifiant. Jamais écrits sur le
    // disque tant qu'ils n'ont pas été utilisés.
    private readonly Dictionary<string, PairingToken> pairingTokens = new Dictionary<string, PairingToken>(StringComparer.Ordinal);
    private readonly object pairingLock = new object();

    private sealed class PairingToken(ApiTokenEntry entry, byte[] hash, DateTime expiresUtc)
    {
        public ApiTokenEntry Entry { get; } = entry;

        public byte[] Hash { get; } = hash;

        public DateTime ExpiresUtc { get; } = expiresUtc;

        // Pending tant que non utilisé ; passe à Consumed/Rejected à la première utilisation.
        public PairingTokenState State { get; set; } = PairingTokenState.Pending;
    }

    public ApiTokenService(ILocalSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        settingsStore.SettingsChanged += () =>
        {
            lock (cacheLock)
            {
                settingsVersion++;
                cachedTokens = null;
            }
        };
    }

    public async Task<IReadOnlyList<ApiTokenEntry>> ListAsync()
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        return settings.Api.Tokens.OrderBy(token => token.CreatedUtc).ToList();
    }

    public async Task<ApiTokenCreationResult> CreateAsync(string name)
    {
        ApiTokenCreationResult result = GenerateToken(name);

        if (!await TryAddAsync(result.Entry))
        {
            throw new InvalidOperationException($"Nombre maximal de jetons atteint ({MaxTokenCount}) : révoquez-en un avant d'en créer un autre.");
        }

        return result;
    }

    public ApiTokenCreationResult CreatePairingToken(string name, TimeSpan lifetime)
    {
        ApiTokenCreationResult result = GenerateToken(name);
        DateTime now = DateTime.UtcNow;

        lock (pairingLock)
        {
            // Nettoyage des jetons expirés depuis longtemps (QR codes abandonnés sans être fermés).
            foreach (KeyValuePair<string, PairingToken> pair in pairingTokens)
            {
                if (now - pair.Value.ExpiresUtc > TimeSpan.FromMinutes(10))
                {
                    pairingTokens.Remove(pair.Key);
                }
            }

            pairingTokens[result.Entry.Id] = new PairingToken(result.Entry, HashToken(result.Token), now + lifetime);
        }

        return result;
    }

    public PairingTokenState GetPairingTokenState(string id)
    {
        lock (pairingLock)
        {
            if (!pairingTokens.TryGetValue(id, out PairingToken? pairing))
            {
                return PairingTokenState.Expired;
            }

            return pairing.State == PairingTokenState.Pending && pairing.ExpiresUtc <= DateTime.UtcNow
                ? PairingTokenState.Expired
                : pairing.State;
        }
    }

    public void DiscardPairingToken(string id)
    {
        lock (pairingLock)
        {
            pairingTokens.Remove(id);
        }
    }

    private static ApiTokenCreationResult GenerateToken(string name)
    {
        string trimmedName = (name ?? string.Empty).Trim();

        if (trimmedName.Length == 0)
        {
            throw new ArgumentException("Le nom du jeton est requis.", nameof(name));
        }

        if (trimmedName.Length > MaxNameLength)
        {
            throw new ArgumentException($"Le nom du jeton ne doit pas dépasser {MaxNameLength} caractères.", nameof(name));
        }

        string token = TokenPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenEntropyBytes));

        ApiTokenEntry entry = new ApiTokenEntry
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
            Name = trimmedName,
            Prefix = token.Substring(0, DisplayedPrefixLength),
            TokenHash = Convert.ToHexStringLower(HashToken(token)),
            CreatedUtc = DateTime.UtcNow,
        };

        return new ApiTokenCreationResult(entry, token);
    }

    // Retourne false si le nombre maximal de jetons est atteint.
    private async Task<bool> TryAddAsync(ApiTokenEntry entry)
    {
        bool limitReached = false;

        await settingsStore.UpdateAsync(settings =>
        {
            if (settings.Api.Tokens.Count >= MaxTokenCount)
            {
                limitReached = true;
                return;
            }

            settings.Api.Tokens.Add(entry);
        });

        return !limitReached;
    }

    public async Task<bool> RevokeAsync(string id)
    {
        bool removed = false;

        await settingsStore.UpdateAsync(settings =>
        {
            removed = settings.Api.Tokens.RemoveAll(token => string.Equals(token.Id, id, StringComparison.Ordinal)) > 0;
        });

        return removed;
    }

    public async Task<ApiTokenEntry?> ValidateAsync(string presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken) || !presentedToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        IReadOnlyList<(ApiTokenEntry Entry, byte[] Hash)> tokens = cachedTokens ?? await ReloadAsync();
        byte[] presentedHash = HashToken(presentedToken);
        ApiTokenEntry? match = null;

        // Parcours complet sans sortie anticipée : le temps de réponse ne dépend pas de la position du
        // jeton dans la liste.
        foreach ((ApiTokenEntry entry, byte[] hash) in tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, hash))
            {
                match = entry;
            }
        }

        return match ?? await TryConsumePairingTokenAsync(presentedHash);
    }

    // Première utilisation d'un jeton d'appairage valable : il est enregistré comme jeton permanent. Marqué
    // utilisé avant l'écriture (sous verrou), pour qu'une seconde requête simultanée ne l'enregistre pas
    // une seconde fois : un jeton d'appairage ne sert qu'une fois, même si l'enregistrement échoue.
    private async Task<ApiTokenEntry?> TryConsumePairingTokenAsync(byte[] presentedHash)
    {
        PairingToken? pairing = null;

        lock (pairingLock)
        {
            DateTime now = DateTime.UtcNow;

            foreach (PairingToken candidate in pairingTokens.Values)
            {
                if (CryptographicOperations.FixedTimeEquals(presentedHash, candidate.Hash)
                    && candidate.State == PairingTokenState.Pending
                    && candidate.ExpiresUtc > now)
                {
                    pairing = candidate;
                }
            }

            if (pairing is null)
            {
                return null;
            }

            pairing.State = PairingTokenState.Rejected;
        }

        bool added = await TryAddAsync(pairing.Entry);

        lock (pairingLock)
        {
            pairing.State = added ? PairingTokenState.Consumed : PairingTokenState.Rejected;
        }

        return added ? pairing.Entry : null;
    }

    private async Task<IReadOnlyList<(ApiTokenEntry Entry, byte[] Hash)>> ReloadAsync()
    {
        int versionBeforeLoad;
        lock (cacheLock)
        {
            versionBeforeLoad = settingsVersion;
        }

        AppLocalSettings settings = await settingsStore.LoadAsync();
        List<(ApiTokenEntry Entry, byte[] Hash)> tokens = new List<(ApiTokenEntry Entry, byte[] Hash)>();

        foreach (ApiTokenEntry entry in settings.Api.Tokens)
        {
            try
            {
                tokens.Add((entry, Convert.FromHexString(entry.TokenHash)));
            }
            catch (FormatException)
            {
                // Empreinte illisible (fichier modifié à la main) : ce jeton est simplement ignoré.
            }
        }

        lock (cacheLock)
        {
            if (settingsVersion == versionBeforeLoad)
            {
                cachedTokens = tokens;
            }
        }

        return tokens;
    }

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
