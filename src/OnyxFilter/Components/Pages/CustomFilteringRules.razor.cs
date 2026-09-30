using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;

namespace OnyxFilter.Components.Pages;

public partial class CustomFilteringRules : ComponentBase
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    private string RulesText { get; set; } = string.Empty;

    private string? StatusMessage { get; set; }

    private bool IsSaving { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        RulesText = settings.CustomFilterRules.RulesText;
    }

    private async Task ApplyAsync()
    {
        string textSnapshot = RulesText;
        IsSaving = true;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.CustomFilterRules.RulesText = textSnapshot);
            StatusMessage = "Règles de filtrage personnalisées enregistrées.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Erreur lors de l'enregistrement : " + ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
