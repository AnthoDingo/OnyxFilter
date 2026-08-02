using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Catalogue des services pouvant être bloqués par client (sélection dans la boîte de dialogue "Ajouter
// un client" de /settings/client). Liste volontairement restreinte à quelques services très connus
// plutôt que le catalogue complet d'AdGuard Home (une centaine d'entrées, régulièrement mis à jour) :
// OnyxFilter ne bloque pas encore réellement ces services par domaine (voir TODO dans
// ClientSettings.razor.cs), cette liste ne sert pour l'instant qu'à l'interface.
public static class KnownBlockedService
{
    public sealed record Entry(string Id, string DisplayName);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("youtube", "YouTube"),
        new Entry("facebook", "Facebook"),
        new Entry("instagram", "Instagram"),
        new Entry("tiktok", "TikTok"),
        new Entry("twitter", "Twitter (X)"),
        new Entry("netflix", "Netflix"),
        new Entry("whatsapp", "WhatsApp"),
        new Entry("discord", "Discord"),
        new Entry("twitch", "Twitch"),
        new Entry("snapchat", "Snapchat"),
        new Entry("pinterest", "Pinterest"),
        new Entry("reddit", "Reddit"),
        new Entry("spotify", "Spotify"),
        new Entry("amazon", "Amazon"),
        new Entry("ebay", "eBay"),
        new Entry("steam", "Steam"),
        new Entry("epic_games", "Epic Games"),
        new Entry("roblox", "Roblox"),
        new Entry("minecraft", "Minecraft"),
        new Entry("telegram", "Telegram"),
    };

    public static string GetDisplayName(string id)
    {
        foreach (Entry entry in All)
        {
            if (entry.Id == id)
            {
                return entry.DisplayName;
            }
        }

        return id;
    }
}
