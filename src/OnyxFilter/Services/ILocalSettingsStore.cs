using System;
using System.Threading.Tasks;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services;

// Persiste les paramètres de l'application dans appsettings.local.json, un bloc par page.
public interface ILocalSettingsStore
{
    Task<AppLocalSettings> LoadAsync();

    Task UpdateAsync(Action<AppLocalSettings> mutate);

    // Déclenché après chaque écriture réussie sur le disque, pour permettre aux services
    // (ex. le service DNS) de recharger leur configuration sans redémarrer l'application.
    event Action? SettingsChanged;
}
