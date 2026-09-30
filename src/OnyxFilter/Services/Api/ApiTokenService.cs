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

        if (limitReached)
        {
            throw new InvalidOperationException($"Nombre maximal de jetons atteint ({MaxTokenCount}) : révoquez-en un avant d'en créer un autre.");
        }

        return new ApiTokenCreationResult(entry, token);
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

        return match;
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
