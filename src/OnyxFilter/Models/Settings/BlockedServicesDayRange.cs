namespace OnyxFilter.Models.Settings;

// Plage horaire d'un jour de la semaine pour "Suspendre le blocage des services"
// (/filters/blocked-services) : pendant [StartMinutes, EndMinutes), le blocage des services est
// suspendu (tous les services redeviennent accessibles), quels que soient les services activés.
// Exprimées en minutes depuis minuit (0-1439), heure locale du serveur.
public sealed class BlockedServicesDayRange
{
    public int StartMinutes { get; set; }

    public int EndMinutes { get; set; }
}
