using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;

namespace OnyxFilter.Components.Pages;

public partial class DnsRewrites : ComponentBase
{
    private static readonly int[] PageSizeOptions = { 10, 25, 50, 100 };

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    private List<DnsRewriteEntry> Entries { get; set; } = new List<DnsRewriteEntry>();

    private bool RewritesEnabled { get; set; } = true;

    private string? StatusMessage { get; set; }

    // Pagination.
    private int PageSize { get; set; } = 10;

    private int CurrentPage { get; set; } = 1;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(Entries.Count / (double)PageSize));

    private IEnumerable<DnsRewriteEntry> PagedEntries => Entries.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

    // Boîte de dialogue d'ajout/modification.
    private bool ShowDialog { get; set; }

    private bool IsEditMode { get; set; }

    private DnsRewriteEntry? EditingEntry { get; set; }

    private string DialogDomain { get; set; } = string.Empty;

    private string DialogAnswer { get; set; } = string.Empty;

    private string? DialogError { get; set; }

    private bool IsSavingDialog { get; set; }

    // Confirmation avant suppression d'une règle.
    private bool ShowDeleteConfirm { get; set; }

    private DnsRewriteEntry? EntryPendingDeletion { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        Entries = settings.Rewrites.Entries.ToList();
        RewritesEnabled = settings.Rewrites.Enabled;
    }

    private void OpenAddDialog()
    {
        IsEditMode = false;
        EditingEntry = null;
        DialogDomain = string.Empty;
        DialogAnswer = string.Empty;
        DialogError = null;
        ShowDialog = true;
    }

    private void OpenEditDialog(DnsRewriteEntry entry)
    {
        IsEditMode = true;
        EditingEntry = entry;
        DialogDomain = entry.Domain;
        DialogAnswer = entry.Answer;
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
        string domain = DialogDomain.Trim();
        string answer = DialogAnswer.Trim();

        if (domain.Length == 0 || answer.Length == 0)
        {
            DialogError = L["Le domaine et la réponse sont obligatoires."];
            return;
        }

        if (IsEditMode && EditingEntry is not null)
        {
            EditingEntry.Domain = domain;
            EditingEntry.Answer = answer;
        }
        else
        {
            Entries.Add(new DnsRewriteEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Domain = domain,
                Answer = answer,
                Enabled = true,
            });

            ClampCurrentPage();
        }

        IsSavingDialog = true;

        try
        {
            await PersistAsync();
        }
        finally
        {
            IsSavingDialog = false;
        }

        ShowDialog = false;
    }

    private void RequestDelete(DnsRewriteEntry entry)
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

        Entries.Remove(EntryPendingDeletion);
        ClampCurrentPage();

        ShowDeleteConfirm = false;
        EntryPendingDeletion = null;

        await PersistAsync();
    }

    private async Task ToggleEnabledAsync(DnsRewriteEntry entry, ChangeEventArgs e)
    {
        entry.Enabled = e.Value is bool value && value;
        await PersistAsync();
    }

    private async Task ToggleRewritesEnabledAsync()
    {
        RewritesEnabled = !RewritesEnabled;
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        List<DnsRewriteEntry> entriesSnapshot = Entries;
        bool enabledSnapshot = RewritesEnabled;

        try
        {
            await SettingsStore.UpdateAsync(settings =>
            {
                settings.Rewrites.Entries = entriesSnapshot;
                settings.Rewrites.Enabled = enabledSnapshot;
            });
            StatusMessage = L["Réécritures DNS enregistrées."];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
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
