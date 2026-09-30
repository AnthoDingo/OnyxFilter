using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using Spectre.Console;

namespace OnyxFilter.Cli;

// Règles de filtrage personnalisées et listes abonnées, lues et modifiées dans appsettings.local.json. Un
// serveur déjà démarré ne relit pas ce fichier : il faut le redémarrer pour appliquer une modification.
internal static class FilteringCommands
{
    public static int ListRules(CommandContext context)
    {
        List<string> rules = ReadRuleLines(LoadSettings(context));

        if (rules.All(string.IsNullOrWhiteSpace))
        {
            AnsiConsole.MarkupLine("[grey]Aucune règle personnalisée.[/]");
            return CommandLine.Success;
        }

        int number = 0;
        foreach (string rule in rules.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            number++;
            bool isComment = rule.StartsWith('!') || rule.StartsWith('#');
            string style = isComment ? "grey" : rule.StartsWith("@@", StringComparison.Ordinal) ? "green" : "white";
            AnsiConsole.MarkupLine($"[grey]{number,4}[/]  [{style}]{Markup.Escape(rule)}[/]");
        }

        return CommandLine.Success;
    }

    public static int AddRule(CommandContext context, string[] arguments)
    {
        string rule = RequireRule(arguments, "rules:add <règle>");
        ILocalSettingsStore store = context.Services.GetRequiredService<ILocalSettingsStore>();
        bool added = false;

        store.UpdateAsync(settings =>
        {
            List<string> rules = ReadRuleLines(settings);

            if (rules.Any(line => string.Equals(line.Trim(), rule, StringComparison.Ordinal)))
            {
                return;
            }

            // Pas de ligne vide finale conservée entre la dernière règle et la nouvelle.
            while (rules.Count > 0 && string.IsNullOrWhiteSpace(rules[^1]))
            {
                rules.RemoveAt(rules.Count - 1);
            }

            rules.Add(rule);
            settings.CustomFilterRules.RulesText = string.Join('\n', rules);
            added = true;
        }).GetAwaiter().GetResult();

        AnsiConsole.MarkupLine(added
            ? $"[green]Règle ajoutée :[/] {Markup.Escape(rule)}"
            : $"[grey]Règle déjà présente :[/] {Markup.Escape(rule)}");
        WriteRestartHint(added);
        return CommandLine.Success;
    }

    public static int RemoveRule(CommandContext context, string[] arguments)
    {
        string rule = RequireRule(arguments, "rules:remove <règle>");
        ILocalSettingsStore store = context.Services.GetRequiredService<ILocalSettingsStore>();
        int removed = 0;

        store.UpdateAsync(settings =>
        {
            List<string> rules = ReadRuleLines(settings);
            removed = rules.RemoveAll(line => string.Equals(line.Trim(), rule, StringComparison.Ordinal));

            if (removed > 0)
            {
                settings.CustomFilterRules.RulesText = string.Join('\n', rules);
            }
        }).GetAwaiter().GetResult();

        if (removed == 0)
        {
            throw new CommandLineException($"Règle introuvable : {rule}", CommandLine.Failure);
        }

        AnsiConsole.MarkupLine($"[green]Règle retirée :[/] {Markup.Escape(rule)}");
        WriteRestartHint(true);
        return CommandLine.Success;
    }

    public static int ListLists(CommandContext context)
    {
        AppLocalSettings settings = LoadSettings(context);

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Type");
        table.AddColumn("Active");
        table.AddColumn("Nom");
        table.AddColumn("Adresse");

        foreach (FilterListEntry list in settings.FilterLists.Lists)
        {
            AddListRow(table, "[red]blocage[/]", list);
        }

        foreach (FilterListEntry list in settings.Allowlist.Lists)
        {
            AddListRow(table, "[green]autorisation[/]", list);
        }

        if (table.Rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Aucune liste configurée.[/]");
            return CommandLine.Success;
        }

        AnsiConsole.Write(table);

        if (!settings.General.BlockDomainsWithFilters)
        {
            AnsiConsole.MarkupLine("[yellow]Le blocage par listes est désactivé dans les réglages généraux.[/]");
        }

        return CommandLine.Success;
    }

    private static void AddListRow(Table table, string type, FilterListEntry list)
    {
        table.AddRow(type, list.Enabled ? "oui" : "[grey]non[/]", Markup.Escape(list.Name), $"[grey]{Markup.Escape(list.Url)}[/]");
    }

    private static AppLocalSettings LoadSettings(CommandContext context)
    {
        return context.Services.GetRequiredService<ILocalSettingsStore>().LoadAsync().GetAwaiter().GetResult();
    }

    private static List<string> ReadRuleLines(AppLocalSettings settings)
    {
        return (settings.CustomFilterRules.RulesText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
    }

    // Les arguments sont réunis : « rules:add 127.0.0.1 nas.maison » fonctionne sans guillemets.
    private static string RequireRule(string[] arguments, string usage)
    {
        string rule = string.Join(' ', arguments).Trim();

        if (rule.Length == 0)
        {
            throw new CommandLineException("Usage : OnyxFilter " + usage);
        }

        if (rule.Contains('\n') || rule.Contains('\r'))
        {
            throw new CommandLineException("Une seule règle à la fois, sur une ligne.");
        }

        return rule;
    }

    private static void WriteRestartHint(bool changed)
    {
        if (changed)
        {
            AnsiConsole.MarkupLine("[grey]Si le serveur est démarré, redémarrez-le pour appliquer : sudo systemctl restart onyxfilter[/]");
        }
    }
}
