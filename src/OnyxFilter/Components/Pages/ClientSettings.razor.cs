using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Components.Pages;

public partial class ClientSettings : ComponentBase
{
    private static readonly int[] PageSizeOptions = { 10, 25, 50, 100 };

    // Exemple de la zone « Adresses IP ou plages CIDR » : chaîne C# plutôt qu'entité HTML (&#10;), que
    // Blazor encoderait telle quelle dans l'attribut.
    private const string IdentifiersPlaceholder = "192.168.1.42\n192.168.1.0/24";

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IDnsStatisticsService StatisticsService { get; set; } = default!;

    // "Clients persistants".
    private List<PersistentClientEntry> Clients { get; set; } = new List<PersistentClientEntry>();

    private IReadOnlyList<DnsStatisticsEntry> AllClientStats { get; set; } = Array.Empty<DnsStatisticsEntry>();

    private string? StatusMessage { get; set; }

    private int PageSize { get; set; } = 10;

    private int CurrentPage { get; set; } = 1;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(Clients.Count / (double)PageSize));

    private IEnumerable<PersistentClientEntry> PagedClients => Clients.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

    // "Clients d'exécution" : adresses observées dans les statistiques mais ne correspondant à aucun
    // client persistant configuré ci-dessus (comme AdGuard Home, qui ne les liste pas deux fois).
    private List<DnsStatisticsEntry> RuntimeClients { get; set; } = new List<DnsStatisticsEntry>();

    private int RuntimePageSize { get; set; } = 10;

    private int RuntimeCurrentPage { get; set; } = 1;

    private int RuntimeTotalPages => Math.Max(1, (int)Math.Ceiling(RuntimeClients.Count / (double)RuntimePageSize));

    private IEnumerable<DnsStatisticsEntry> PagedRuntimeClients => RuntimeClients.Skip((RuntimeCurrentPage - 1) * RuntimePageSize).Take(RuntimePageSize);

    // Boîte de dialogue d'ajout/modification d'un client persistant.
    private bool ShowDialog { get; set; }

    private bool IsEditMode { get; set; }

    private PersistentClientEntry? EditingEntry { get; set; }

    private string DialogName { get; set; } = string.Empty;

    private string DialogIdentifiersText { get; set; } = string.Empty;

    private string DialogTagsText { get; set; } = string.Empty;

    private bool DialogUseGlobalSettings { get; set; } = true;

    private bool DialogFilteringEnabled { get; set; } = true;

    private bool DialogBrowsingSecurityEnabled { get; set; }

    private bool DialogParentalControlEnabled { get; set; }

    private bool DialogSafeSearchEnabled { get; set; }

    private bool DialogUseGlobalBlockedServices { get; set; } = true;

    private HashSet<string> DialogBlockedServiceIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    private string DialogUpstreamServersText { get; set; } = string.Empty;

    private bool DialogIgnoreQueryLog { get; set; }

    private bool DialogIgnoreStatistics { get; set; }

    private string? DialogError { get; set; }

    private bool IsSavingDialog { get; set; }

    // Confirmation avant suppression d'un client persistant.
    private bool ShowDeleteConfirm { get; set; }

    private PersistentClientEntry? EntryPendingDeletion { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        Clients = settings.Clients.Clients.ToList();
        RefreshStatistics();
    }

    private void RefreshStatistics()
    {
        AllClientStats = StatisticsService.GetAllClients();

        // "Inconnu" (adresse client non résolue, ex. requête sans adresse associée) n'est pas un
        // appareil réel : il n'a pas sa place dans une liste de clients observés sur le réseau.
        RuntimeClients = AllClientStats
            .Where(entry => !string.Equals(entry.Label, "Inconnu", StringComparison.Ordinal) && !IsCoveredByPersistentClient(entry.Label))
            .ToList();

        ClampRuntimePage();
    }

    private bool IsCoveredByPersistentClient(string clientKey)
    {
        if (!IPAddress.TryParse(clientKey, out IPAddress? address))
        {
            // "Inconnu" (adresse non résolue) ou clé mal formée : jamais couvert par un client persistant.
            return false;
        }

        foreach (PersistentClientEntry entry in Clients)
        {
            if (ClientIdentifierMatcher.MatchesAny(address, entry.Identifiers))
            {
                return true;
            }
        }

        return false;
    }

    // Somme des requêtes de tous les clients de statistiques correspondant aux identifiants de "entry"
    // (une plage CIDR peut recouvrir plusieurs adresses observées séparément dans les statistiques).
    private long GetQueryCount(PersistentClientEntry entry)
    {
        long total = 0;

        foreach (DnsStatisticsEntry stat in AllClientStats)
        {
            if (IPAddress.TryParse(stat.Label, out IPAddress? address) && ClientIdentifierMatcher.MatchesAny(address, entry.Identifiers))
            {
                total += stat.Count;
            }
        }

        return total;
    }

    private void OpenAddDialog()
    {
        IsEditMode = false;
        EditingEntry = null;
        DialogName = string.Empty;
        DialogIdentifiersText = string.Empty;
        DialogTagsText = string.Empty;
        DialogUseGlobalSettings = true;
        DialogFilteringEnabled = true;
        DialogBrowsingSecurityEnabled = false;
        DialogParentalControlEnabled = false;
        DialogSafeSearchEnabled = false;
        DialogUseGlobalBlockedServices = true;
        DialogBlockedServiceIds = new HashSet<string>(StringComparer.Ordinal);
        DialogUpstreamServersText = string.Empty;
        DialogIgnoreQueryLog = false;
        DialogIgnoreStatistics = false;
        DialogError = null;
        ShowDialog = true;
    }

    private void OpenEditDialog(PersistentClientEntry entry)
    {
        IsEditMode = true;
        EditingEntry = entry;
        DialogName = entry.Name;
        DialogIdentifiersText = JoinLines(entry.Identifiers);
        DialogTagsText = JoinLines(entry.Tags);
        DialogUseGlobalSettings = entry.UseGlobalSettings;
        DialogFilteringEnabled = entry.FilteringEnabled;
        DialogBrowsingSecurityEnabled = entry.BrowsingSecurityEnabled;
        DialogParentalControlEnabled = entry.ParentalControlEnabled;
        DialogSafeSearchEnabled = entry.SafeSearchEnabled;
        DialogUseGlobalBlockedServices = entry.UseGlobalBlockedServices;
        DialogBlockedServiceIds = new HashSet<string>(entry.BlockedServiceIds, StringComparer.Ordinal);
        DialogUpstreamServersText = JoinLines(entry.UpstreamServers);
        DialogIgnoreQueryLog = entry.IgnoreQueryLog;
        DialogIgnoreStatistics = entry.IgnoreStatistics;
        DialogError = null;
        ShowDialog = true;
    }

    private void CloseDialog()
    {
        if (IsSavingDialog)
        {
            return;
        }

        ShowDialog = false;
    }

    private void ToggleBlockedService(string serviceId, ChangeEventArgs e)
    {
        bool isChecked = e.Value is bool value && value;

        if (isChecked)
        {
            DialogBlockedServiceIds.Add(serviceId);
        }
        else
        {
            DialogBlockedServiceIds.Remove(serviceId);
        }
    }

    private async Task SaveDialogAsync()
    {
        string name = DialogName.Trim();

        if (name.Length == 0)
        {
            DialogError = "Le nom est obligatoire.";
            return;
        }

        string[] identifiers = SplitLines(DialogIdentifiersText);

        if (identifiers.Length == 0)
        {
            DialogError = "Au moins une adresse IP ou plage CIDR est obligatoire.";
            return;
        }

        string[] invalidIdentifiers = identifiers.Where(identifier => !IsValidIdentifier(identifier)).ToArray();

        if (invalidIdentifiers.Length > 0)
        {
            DialogError = "Adresse(s) invalide(s) : " + string.Join(", ", invalidIdentifiers) + " (adresse IP ou notation CIDR attendue, ex. 192.168.1.42 ou 192.168.1.0/24).";
            return;
        }

        PersistentClientEntry data = new PersistentClientEntry
        {
            Id = IsEditMode && EditingEntry is not null ? EditingEntry.Id : Guid.NewGuid().ToString("N"),
            Name = name,
            Identifiers = identifiers,
            Tags = SplitLines(DialogTagsText),
            UseGlobalSettings = DialogUseGlobalSettings,
            FilteringEnabled = DialogFilteringEnabled,
            BrowsingSecurityEnabled = DialogBrowsingSecurityEnabled,
            ParentalControlEnabled = DialogParentalControlEnabled,
            SafeSearchEnabled = DialogSafeSearchEnabled,
            UseGlobalBlockedServices = DialogUseGlobalBlockedServices,
            BlockedServiceIds = DialogBlockedServiceIds.ToArray(),
            UpstreamServers = SplitLines(DialogUpstreamServersText),
            IgnoreQueryLog = DialogIgnoreQueryLog,
            IgnoreStatistics = DialogIgnoreStatistics,
        };

        if (IsEditMode && EditingEntry is not null)
        {
            int index = Clients.FindIndex(client => client.Id == EditingEntry.Id);

            if (index >= 0)
            {
                Clients[index] = data;
            }
        }
        else
        {
            Clients.Add(data);
        }

        IsSavingDialog = true;

        try
        {
            await PersistAsync();
            RefreshStatistics();
        }
        finally
        {
            IsSavingDialog = false;
        }

        ShowDialog = false;
    }

    private void RequestDelete(PersistentClientEntry entry)
    {
        EntryPendingDeletion = entry;
        ShowDeleteConfirm = true;
    }

    private void CancelDelete()
    {
        EntryPendingDeletion = null;
        ShowDeleteConfirm = false;
    }

    private async Task ConfirmDeleteAsync()
    {
        if (EntryPendingDeletion is null)
        {
            ShowDeleteConfirm = false;
            return;
        }

        Clients.Remove(EntryPendingDeletion);
        ClampCurrentPage();

        ShowDeleteConfirm = false;
        EntryPendingDeletion = null;

        await PersistAsync();
        RefreshStatistics();
    }

    private async Task PersistAsync()
    {
        List<PersistentClientEntry> snapshot = Clients;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.Clients.Clients = snapshot);
            StatusMessage = "Clients persistants enregistrés.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Erreur lors de l'enregistrement : " + ex.Message;
        }
    }

    // Accepte une adresse IP exacte ou une plage CIDR ("adresse/longueur de préfixe").
    private static bool IsValidIdentifier(string identifier)
    {
        int slashIndex = identifier.IndexOf('/');

        if (slashIndex < 0)
        {
            return IPAddress.TryParse(identifier, out _);
        }

        string addressPart = identifier.Substring(0, slashIndex);

        if (!IPAddress.TryParse(addressPart, out IPAddress? address))
        {
            return false;
        }

        if (!int.TryParse(identifier.AsSpan(slashIndex + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefixLength))
        {
            return false;
        }

        int maxPrefixLength = address.GetAddressBytes().Length * 8;
        return prefixLength >= 0 && prefixLength <= maxPrefixLength;
    }

    private static string[] SplitLines(string text)
    {
        return text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string JoinLines(IReadOnlyList<string> lines)
    {
        return string.Join('\n', lines);
    }

    private void GoToPreviousPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
        }
    }

    private void GoToNextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
        }
    }

    private void OnPageSizeChanged(ChangeEventArgs e)
    {
        if (e.Value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newSize) && newSize > 0)
        {
            PageSize = newSize;
            CurrentPage = 1;
        }
    }

    private void ClampCurrentPage()
    {
        if (CurrentPage > TotalPages)
        {
            CurrentPage = TotalPages;
        }

        if (CurrentPage < 1)
        {
            CurrentPage = 1;
        }
    }

    private void GoToPreviousRuntimePage()
    {
        if (RuntimeCurrentPage > 1)
        {
            RuntimeCurrentPage--;
        }
    }

    private void GoToNextRuntimePage()
    {
        if (RuntimeCurrentPage < RuntimeTotalPages)
        {
            RuntimeCurrentPage++;
        }
    }

    private void OnRuntimePageSizeChanged(ChangeEventArgs e)
    {
        if (e.Value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newSize) && newSize > 0)
        {
            RuntimePageSize = newSize;
            RuntimeCurrentPage = 1;
        }
    }

    private void ClampRuntimePage()
    {
        if (RuntimeCurrentPage > RuntimeTotalPages)
        {
            RuntimeCurrentPage = RuntimeTotalPages;
        }

        if (RuntimeCurrentPage < 1)
        {
            RuntimeCurrentPage = 1;
        }
    }
}
