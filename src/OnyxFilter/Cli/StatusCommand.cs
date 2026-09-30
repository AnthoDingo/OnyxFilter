using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OnyxFilter.Models;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.Updates;
using Spectre.Console;

namespace OnyxFilter.Cli;

// Résumé hors ligne d'une installation : dossiers, base, protections et réglages enregistrés. L'état
// d'exécution (protection suspendue, statistiques en cours) n'est connu que du serveur démarré : voir
// l'interface ou l'API (/api/v1/protection, /api/v1/stats).
internal static class StatusCommand
{
    public static int Run(CommandContext context)
    {
        context.EnsureDatabase();

        AppLocalSettings settings = context.Services.GetRequiredService<ILocalSettingsStore>().LoadAsync().GetAwaiter().GetResult();
        UpdateOptions updateOptions = context.Services.GetRequiredService<IOptions<UpdateOptions>>().Value;

        int userCount;
        using (IServiceScope scope = context.Services.CreateScope())
        {
            userCount = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.Count();
        }

        GeneralSettingsData general = settings.General;
        int customRuleCount = (settings.CustomFilterRules.RulesText ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Count(line => line.Length > 0 && !line.StartsWith('!') && !line.StartsWith('#'));

        Table table = new Table().Border(TableBorder.Rounded).HideHeaders();
        table.AddColumn("Élément");
        table.AddColumn("Valeur");

        table.AddRow("[bold]Version[/]", Markup.Escape(AppVersion.Display));
        table.AddRow("Dossier des données", Markup.Escape(context.DataDirectory));
        table.AddRow("Installation", Markup.Escape(UpdateEnvironment.InstallDirectory));
        table.AddRow("Base de données", File.Exists(Path.Combine(context.DataDirectory, "OnyxFilter.db"))
            ? $"OnyxFilter.db · {userCount} compte(s)"
            : "[yellow]absente (créée au premier démarrage)[/]");
        table.AddEmptyRow();

        table.AddRow("[bold]Blocage par listes[/]", OnOff(general.BlockDomainsWithFilters));
        table.AddRow("Listes de blocage", CountEnabled(settings.FilterLists.Lists));
        table.AddRow("Listes d'autorisation", CountEnabled(settings.Allowlist.Lists));
        table.AddRow("Règles personnalisées", customRuleCount.ToString());
        table.AddRow("Réécritures DNS", $"{settings.Rewrites.Entries.Count(entry => entry.Enabled)} active(s) sur {settings.Rewrites.Entries.Count}" + (settings.Rewrites.Enabled ? string.Empty : " [yellow](en pause)[/]"));
        table.AddRow("Services bloqués", settings.BlockedServices.BlockedServiceIds.Count.ToString());
        table.AddRow("Sécurité de navigation", OnOff(general.UseBrowsingSecurity));
        table.AddRow("Contrôle parental", OnOff(general.UseParentalControl));
        table.AddRow("Recherche sécurisée", OnOff(general.UseSafeSearch));
        table.AddEmptyRow();

        table.AddRow("[bold]Journal des requêtes[/]", OnOff(general.EnableQueryLog));
        table.AddRow("Statistiques", OnOff(general.EnableStatistics));
        table.AddRow("Clients déclarés", settings.Clients.Clients.Count.ToString());
        table.AddRow("Jetons d'API", settings.Api.Tokens.Count.ToString());
        table.AddRow("Recherche de mises à jour", settings.Updates.AutoCheck
            ? $"automatique · {Markup.Escape(updateOptions.Repository)}" + (settings.Updates.IncludePreReleases ? " · préversions incluses" : string.Empty)
            : "[grey]manuelle[/]");

        AnsiConsole.Write(table);
        return CommandLine.Success;
    }

    private static string OnOff(bool enabled) => enabled ? "[green]oui[/]" : "[grey]non[/]";

    private static string CountEnabled(System.Collections.Generic.IReadOnlyCollection<FilterListEntry> lists)
    {
        return $"{lists.Count(list => list.Enabled)} active(s) sur {lists.Count}";
    }
}
