using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services;

// Implémentation légère (aucune dépendance externe) adaptée à un déploiement sur Raspberry Pi 3+ (2 Go de RAM) :
// un simple fichier JSON à côté d'appsettings.json, protégé par un verrou asynchrone pour éviter les
// écritures concurrentes entre plusieurs onglets/circuits Blazor Server.
public sealed class LocalSettingsStore : ILocalSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim fileLock = new SemaphoreSlim(1, 1);
    private readonly string filePath;

    public LocalSettingsStore(IHostEnvironment hostEnvironment)
    {
        filePath = Path.Combine(hostEnvironment.ContentRootPath, "appsettings.local.json");
    }

    public async Task<AppLocalSettings> LoadAsync()
    {
        await fileLock.WaitAsync();

        try
        {
            return await ReadFromDiskAsync();
        }
        finally
        {
            fileLock.Release();
        }
    }

    public event Action? SettingsChanged;

    public async Task UpdateAsync(Action<AppLocalSettings> mutate)
    {
        await fileLock.WaitAsync();

        try
        {
            AppLocalSettings settings = await ReadFromDiskAsync();
            mutate(settings);

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            await File.WriteAllTextAsync(filePath, json);
        }
        finally
        {
            fileLock.Release();
        }

        // Déclenché hors du verrou pour éviter qu'un abonné lent (ex. rechargement du service DNS)
        // ne bloque les prochains appels à LoadAsync/UpdateAsync.
        SettingsChanged?.Invoke();
    }

    private async Task<AppLocalSettings> ReadFromDiskAsync()
    {
        if (!File.Exists(filePath))
        {
            return new AppLocalSettings();
        }

        try
        {
            string json = await File.ReadAllTextAsync(filePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppLocalSettings();
            }

            AppLocalSettings? settings = JsonSerializer.Deserialize<AppLocalSettings>(json, SerializerOptions);
            return settings ?? new AppLocalSettings();
        }
        catch (JsonException)
        {
            // Fichier corrompu ou modifié manuellement de façon invalide : on repart des valeurs par défaut
            // plutôt que de faire planter l'application.
            return new AppLocalSettings();
        }
    }
}
