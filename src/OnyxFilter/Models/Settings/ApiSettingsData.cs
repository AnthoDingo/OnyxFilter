using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Réglages de la page « Accès API » (/settings/api).
public sealed class ApiSettingsData
{
    public List<ApiTokenEntry> Tokens { get; set; } = new List<ApiTokenEntry>();
}
