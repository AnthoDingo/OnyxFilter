using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.Filtering;

namespace OnyxFilter.Components.Pages;

public partial class Filters : ComponentBase
{
    private static CultureInfo DisplayCulture => CultureInfo.CurrentCulture;

    private static readonly int[] PageSizeOptions = { 10, 25, 50, 100 };

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IDnsFilterService FilterService { get; set; } = default!;

    private List<FilterListEntry> Lists { get; set; } = new List<FilterListEntry>();

    private Dictionary<string, FilterListStatus> StatusById { get; set; } = new Dictionary<string, FilterListStatus>(StringComparer.Ordinal);

    private string? StatusMessage { get; set; }

    private bool IsRefreshing { get; set; }

    // Pagination.
    private int PageSize { get; set; } = 10;

    private int CurrentPage { get; set; } = 1;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(Lists.Count / (double)PageSize));

    private IEnumerable<FilterListEntry> PagedLists => Lists.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

    // Boîte de dialogue d'ajout/modification.
    private bool ShowDialog { get; set; }

    private bool IsEditMode { get; set; }

    private FilterListEntry? EditingEntry { get; set; }

    private string DialogName { get; set; } = string.Empty;

    private string DialogUrl { get; set; } = string.Empty;

    private string? DialogError { get; set; }

    private bool IsSavingDialog { get; set; }

    // Confirmation avant suppression d'une liste.
    private bool ShowDeleteConfirm { get; set; }

    private FilterListEntry? EntryPendingDeletion { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        Lists = settings.FilterLists.Lists.ToList();
        ApplySnapshot();
    }

    private void ApplySnapshot()
    {
        FilterListsSnapshot snapshot = FilterService.GetSnapshot();
        StatusById = snapshot.Lists.ToDictionary(status => status.Id, status => status, StringComparer.Ordinal);
    }

    private FilterListStatus? GetStatus(FilterListEntry entry)
    {
        return StatusById.TryGetValue(entry.Id, out FilterListStatus? status) ? status : null;
    }

    private string FormatRuleCount(FilterListStatus? status)
    {
        if (status is null)
        {
            return "—";
        }

        if (!string.IsNullOrEmpty(status.LastError))
        {
            return L["Erreur"];
        }

        return status.DomainCount.ToString("N0", DisplayCulture);
    }

    private string FormatLastUpdated(FilterListStatus? status)
    {
        if (status is null || status.LastUpdatedUtc is null)
        {
            return "—";
        }

        DateTime localTime = status.LastUpdatedUtc.Value.ToLocalTime();
        return localTime.ToString(L["d MMMM yyyy 'à' HH:mm"], DisplayCulture);
    }

    private void OpenAddDialog()
    {
        IsEditMode = false;
        EditingEntry = null;
        DialogName = string.Empty;
        DialogUrl = string.Empty;
        DialogError = null;
        ShowDialog = true;
    }

    private void OpenEditDialog(FilterListEntry entry)
    {
        IsEditMode = true;
        EditingEntry = entry;
        DialogName = entry.Name;
        DialogUrl = entry.Url;
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

    private async Task SaveDialogAsync()
    {
        string name = DialogName.Trim();
        string url = DialogUrl.Trim();

        if (name.Length == 0 || url.Length == 0)
        {
            DialogError = L["Le nom et l'URL sont obligatoires."];
            return;
        }

        bool isValidHttpUrl = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUrl)
            && (parsedUrl.Scheme == Uri.UriSchemeHttp || parsedUrl.Scheme == Uri.UriSchemeHttps);

        if (!isValidHttpUrl)
        {
            DialogError = L["L'URL doit être une adresse http:// ou https:// valide."];
            return;
        }

        if (IsEditMode && EditingEntry is not null)
        {
            EditingEntry.Name = name;
            EditingEntry.Url = url;
        }
        else
        {
            Lists.Add(new FilterListEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Url = url,
                Enabled = true,
            });

            ClampCurrentPage();
        }

        IsSavingDialog = true;

        try
        {
            await PersistAsync();

            // Téléchargement immédiat : plutôt que d'attendre le rafraîchissement en arrière-plan
            // déclenché par le changement de réglages, on retélécharge tout de suite pour que la liste
            // ajoutée/modifiée affiche sans délai son nombre de règles réel au lieu de "–".
            await FilterService.RefreshAsync(CancellationToken.None);
            ApplySnapshot();
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de la mise à jour : {0}", ex.Message];
        }
        finally
        {
            IsSavingDialog = false;
        }

        ShowDialog = false;
    }

    private void RequestDelete(FilterListEntry entry)
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

        Lists.Remove(EntryPendingDeletion);
        ClampCurrentPage();

        ShowDeleteConfirm = false;
        EntryPendingDeletion = null;

        await PersistAsync();
    }

    private async Task ToggleEnabledAsync(FilterListEntry entry, ChangeEventArgs e)
    {
        entry.Enabled = e.Value is bool value && value;
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        List<FilterListEntry> listsSnapshot = Lists;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.FilterLists.Lists = listsSnapshot);
            StatusMessage = L["Abonnements enregistrés. La mise à jour des listes se fait en arrière-plan."];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }
    }

    private async Task RefreshNowAsync()
    {
        IsRefreshing = true;

        try
        {
            await FilterService.RefreshAsync(CancellationToken.None);
            ApplySnapshot();
            StatusMessage = L["Listes de blocage mises à jour."];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de la mise à jour : {0}", ex.Message];
        }
        finally
        {
            IsRefreshing = false;
        }
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
}
