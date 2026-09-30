using System;

namespace OnyxFilter.Models.Settings;

// Horaire de suspension du blocage des services (/filters/blocked-services, "Suspendre le blocage des
// services") : un jour de la semaine sans plage associée (propriété null) n'a jamais de suspension. Ne
// prend actuellement en charge que le fuseau horaire local du serveur (voir BlockedServicesService) :
// "TimeZoneId" reste à sa valeur par défaut "Local", conservée pour rester fidèle au format d'AdGuard
// Home plutôt que pour être réellement modifiable pour l'instant.
public sealed class BlockedServicesSchedule
{
    public string TimeZoneId { get; set; } = "Local";

    public BlockedServicesDayRange? Sunday { get; set; }

    public BlockedServicesDayRange? Monday { get; set; }

    public BlockedServicesDayRange? Tuesday { get; set; }

    public BlockedServicesDayRange? Wednesday { get; set; }

    public BlockedServicesDayRange? Thursday { get; set; }

    public BlockedServicesDayRange? Friday { get; set; }

    public BlockedServicesDayRange? Saturday { get; set; }

    public BlockedServicesDayRange? GetRangeFor(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Sunday => Sunday,
            DayOfWeek.Monday => Monday,
            DayOfWeek.Tuesday => Tuesday,
            DayOfWeek.Wednesday => Wednesday,
            DayOfWeek.Thursday => Thursday,
            DayOfWeek.Friday => Friday,
            DayOfWeek.Saturday => Saturday,
            _ => null,
        };
    }

    public void SetRangeFor(DayOfWeek day, BlockedServicesDayRange? range)
    {
        switch (day)
        {
            case DayOfWeek.Sunday:
                Sunday = range;
                break;
            case DayOfWeek.Monday:
                Monday = range;
                break;
            case DayOfWeek.Tuesday:
                Tuesday = range;
                break;
            case DayOfWeek.Wednesday:
                Wednesday = range;
                break;
            case DayOfWeek.Thursday:
                Thursday = range;
                break;
            case DayOfWeek.Friday:
                Friday = range;
                break;
            case DayOfWeek.Saturday:
                Saturday = range;
                break;
        }
    }
}
