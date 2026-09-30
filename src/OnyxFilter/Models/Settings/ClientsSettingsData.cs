using System;
using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Paramètres du client" (/settings/client) : la liste des clients
// persistants configurés.
public sealed class ClientsSettingsData
{
    public IReadOnlyList<PersistentClientEntry> Clients { get; set; } = Array.Empty<PersistentClientEntry>();
}
