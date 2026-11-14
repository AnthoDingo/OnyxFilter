using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.BootstrapServices.Toasts;

namespace OnyxFilter.Components.Pages;

public partial class CustomFilteringRules : ComponentBase
{
    [Inject] public ILocalSettingsStore SettingsStore { get; set; } = null!;
    [Inject] private ToastService ToastService { get; set; } = null!;

    private string RulesText { get; set; } = string.Empty;

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

            ToastService.Show(L["Règles de filtrage personnalisées enregistrées."], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ToastService.Show(L["Erreur lors de l'enregistrement : {0}", ex.Message], ToastSeverity.Error);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
