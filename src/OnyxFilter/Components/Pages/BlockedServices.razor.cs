using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.BlockedServices;

namespace OnyxFilter.Components.Pages;

public partial class BlockedServices : ComponentBase
{
    private static readonly string[] DayLabels =
    {
        "Dimanche", "Lundi", "Mardi", "Mercredi", "Jeudi", "Vendredi", "Samedi",
    };

    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IBlockedServicesCatalog Catalog { get; set; } = default!;

    private HashSet<string> BlockedServiceIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    private BlockedServicesSchedule? Schedule { get; set; }

    private string? StatusMessage { get; set; }

    private bool IsSaving { get; set; }

    // Boîte de dialogue de l'horaire de suspension.
    private bool ShowScheduleDialog { get; set; }

    private bool[] DialogDayEnabled { get; set; } = new bool[7];

    // TimeOnly, et non une chaîne : c'est le type que le binding intégré de Blazor pour
    // "input type=time" convertit automatiquement en attribut "value" HTML (format "HH:mm").
    private TimeOnly[] DialogDayStart { get; set; } = new TimeOnly[7];

    private TimeOnly[] DialogDayEnd { get; set; } = new TimeOnly[7];

    private string? ScheduleDialogError { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        BlockedServiceIds = new HashSet<string>(settings.BlockedServices.BlockedServiceIds, StringComparer.Ordinal);
        Schedule = settings.BlockedServices.Schedule;
    }

    private IEnumerable<(string Id, string Label, IReadOnlyList<BlockedServiceDefinition> Services)> GroupedServices
    {
        get
        {
            foreach ((string Id, string Label) group in BlockedServicesCatalog.Groups)
            {
                List<BlockedServiceDefinition> services = Catalog.All
                    .Where(service => string.Equals(service.Group, group.Id, StringComparison.Ordinal))
                    .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (services.Count > 0)
                {
                    yield return (group.Id, group.Label, services);
                }
            }
        }
    }

    private bool IsBlocked(string serviceId)
    {
        return BlockedServiceIds.Contains(serviceId);
    }

    private void ToggleService(string serviceId, ChangeEventArgs e)
    {
        bool isChecked = e.Value is bool value && value;

        if (isChecked)
        {
            BlockedServiceIds.Add(serviceId);
        }
        else
        {
            BlockedServiceIds.Remove(serviceId);
        }
    }

    private void BlockAll()
    {
        foreach (BlockedServiceDefinition service in Catalog.All)
        {
            BlockedServiceIds.Add(service.Id);
        }
    }

    private void UnblockAll()
    {
        BlockedServiceIds.Clear();
    }

    private void BlockAllInGroup(string groupId)
    {
        foreach (BlockedServiceDefinition service in Catalog.All)
        {
            if (string.Equals(service.Group, groupId, StringComparison.Ordinal))
            {
                BlockedServiceIds.Add(service.Id);
            }
        }
    }

    private void UnblockAllInGroup(string groupId)
    {
        foreach (BlockedServiceDefinition service in Catalog.All)
        {
            if (string.Equals(service.Group, groupId, StringComparison.Ordinal))
            {
                BlockedServiceIds.Remove(service.Id);
            }
        }
    }

    private async Task SaveAsync()
    {
        List<string> idsSnapshot = BlockedServiceIds.ToList();
        IsSaving = true;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.BlockedServices.BlockedServiceIds = idsSnapshot);
            StatusMessage = L["Services bloqués enregistrés."];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void OpenScheduleDialog()
    {
        for (int day = 0; day < 7; day++)
        {
            BlockedServicesDayRange? range = Schedule?.GetRangeFor((DayOfWeek)day);
            DialogDayEnabled[day] = range is not null;
            DialogDayStart[day] = MinutesToTimeOnly(range?.StartMinutes ?? 0);
            DialogDayEnd[day] = MinutesToTimeOnly(range?.EndMinutes ?? 0);
        }

        ScheduleDialogError = null;
        ShowScheduleDialog = true;
    }

    private void CloseScheduleDialog()
    {
        ShowScheduleDialog = false;
    }

    private void OnScheduleDayToggled(int dayIndex, ChangeEventArgs e)
    {
        DialogDayEnabled[dayIndex] = e.Value is bool value && value;
    }

    private async Task SaveScheduleAsync()
    {
        BlockedServicesSchedule newSchedule = new BlockedServicesSchedule();
        bool hasAnyDay = false;

        for (int day = 0; day < 7; day++)
        {
            if (!DialogDayEnabled[day])
            {
                continue;
            }

            int startMinutes = TimeOnlyToMinutes(DialogDayStart[day]);
            int endMinutes = TimeOnlyToMinutes(DialogDayEnd[day]);

            if (endMinutes <= startMinutes)
            {
                ScheduleDialogError = L["Chaque jour activé doit avoir une heure de début antérieure à l'heure de fin."];
                return;
            }

            newSchedule.SetRangeFor((DayOfWeek)day, new BlockedServicesDayRange { StartMinutes = startMinutes, EndMinutes = endMinutes });
            hasAnyDay = true;
        }

        Schedule = hasAnyDay ? newSchedule : null;
        BlockedServicesSchedule? scheduleSnapshot = Schedule;

        try
        {
            await SettingsStore.UpdateAsync(settings => settings.BlockedServices.Schedule = scheduleSnapshot);
            StatusMessage = L["Horaire de suspension enregistré."];
        }
        catch (Exception ex)
        {
            StatusMessage = L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }

        ShowScheduleDialog = false;
    }

    private static TimeOnly MinutesToTimeOnly(int minutes)
    {
        int clamped = Math.Clamp(minutes, 0, (24 * 60) - 1);
        return new TimeOnly(clamped / 60, clamped % 60);
    }

    private static int TimeOnlyToMinutes(TimeOnly time)
    {
        return (time.Hour * 60) + time.Minute;
    }
}
