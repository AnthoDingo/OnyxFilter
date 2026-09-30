using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using OnyxFilter.Services;
using OnyxFilter.Services.Updates;
using Spectre.Console;

namespace OnyxFilter.Cli;

internal static class UpdateCommand
{
    public static int Check(CommandContext context, string[] arguments)
    {
        bool includePreReleases = arguments.Contains("--pre")
            || context.Services.GetRequiredService<ILocalSettingsStore>().LoadAsync().GetAwaiter().GetResult().Updates.IncludePreReleases;

        GitHubReleaseClient client = context.Services.GetRequiredService<GitHubReleaseClient>();
        ReleaseInfo? latest = AnsiConsole.Status().Start("Recherche des publications…", _ =>
            client.GetLatestAsync(includePreReleases, CancellationToken.None).GetAwaiter().GetResult());

        AnsiConsole.MarkupLine($"Version installée : [bold]{Markup.Escape(AppVersion.Display)}[/]");

        if (latest is null)
        {
            AnsiConsole.MarkupLine("[grey]Aucune publication disponible pour le moment.[/]");
            return CommandLine.Success;
        }

        AnsiConsole.MarkupLine($"Dernière publication : [bold]{Markup.Escape(latest.Version.ToString())}[/]" + (latest.IsPreRelease ? " [yellow](préversion)[/]" : string.Empty));

        if (latest.Version > AppVersion.Current)
        {
            AnsiConsole.MarkupLine("[yellow]Une nouvelle version est disponible.[/] Installez-la depuis l'interface (Configuration › Mises à jour) ou l'API (POST /api/v1/update/install).");

            if (!string.IsNullOrEmpty(latest.HtmlUrl))
            {
                AnsiConsole.MarkupLine($"[grey]Notes de version : {Markup.Escape(latest.HtmlUrl)}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[green]OnyxFilter est à jour.[/]");
        }

        return CommandLine.Success;
    }
}
