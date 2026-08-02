namespace OnyxFilter.Models.Settings;

// Un abonnement à une liste de blocage DNS (page "Listes de blocage DNS", /filters/blocklists).
public sealed class FilterListEntry
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
