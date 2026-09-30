using OnyxFilter.Services.Updates;
using Spectre.Console;

namespace OnyxFilter.Cli;

internal static class HelpCommand
{
    public static int ShowAndSucceed()
    {
        Show();
        return CommandLine.Success;
    }

    public static void Show()
    {
        AnsiConsole.MarkupLine($"[bold yellow]OnyxFilter[/] {Markup.Escape(AppVersion.Display)} [grey]· résolveur DNS filtrant[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Utilisation :[/] OnyxFilter <commande> [[arguments]] [[--data-dir <dossier>]]");

        // Une seule grille pour toutes les sections : descriptions alignées sur la même colonne.
        Grid grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2).PadRight(3));
        grid.AddColumn();

        AddSection(grid, "Serveur", new[]
        {
            ("--server [[options]]", "Lance le serveur : DNS, interface web et API. Les options ASP.NET Core sont acceptées, ex. --urls http://0.0.0.0:8080."),
        });

        AddSection(grid, "Comptes", new[]
        {
            ("user:list", "Liste les comptes de l'interface d'administration."),
            ("user:add <nom> [[mot de passe]]", "Crée un compte. Sans mot de passe en argument, il est demandé (saisie masquée)."),
            ("user:reset-password <nom> [[mot de passe]]", "Réinitialise un mot de passe (ancienne forme : --reset <nom> <mot de passe>)."),
            ("user:remove <nom> [[--yes]]", "Supprime un compte ; le dernier compte ne peut pas être supprimé."),
        });

        AddSection(grid, "Filtrage", new[]
        {
            ("rules:list", "Affiche les règles de filtrage personnalisées."),
            ("rules:add <règle>", "Ajoute une règle, ex. « ||exemple.org^ » ou « 127.0.0.1 nas.maison »."),
            ("rules:remove <règle>", "Retire une règle."),
            ("lists:list", "Affiche les listes de blocage et d'autorisation."),
        });

        AddSection(grid, "Maintenance", new[]
        {
            ("status", "Résumé de l'installation : dossiers, base, protections et réglages."),
            ("update:check [[--pre]]", "Recherche une nouvelle version sur GitHub (--pre : inclut les préversions)."),
        });

        AddSection(grid, "Options générales", new[]
        {
            ("--data-dir <dossier>", "Dossier des données (base, réglages, caches). Par défaut : le dossier courant s'il en contient, sinon celui de l'exécutable."),
            ("-v, --version", "Affiche la version."),
            ("-h, --help", "Affiche cette aide."),
        });

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Les commandes de filtrage modifient les réglages sur disque : redémarrez le serveur pour les appliquer (sudo systemctl restart onyxfilter).[/]");
    }

    private static void AddSection(Grid grid, string title, (string Usage, string Description)[] commands)
    {
        grid.AddEmptyRow();
        grid.AddRow(new Markup($"[bold yellow]{Markup.Escape(title)}[/]").Overflow(Overflow.Crop), new Text(string.Empty));

        foreach ((string usage, string description) in commands)
        {
            grid.AddRow(new Markup($"[aqua]{usage}[/]"), new Markup($"[grey]{Markup.Escape(description)}[/]"));
        }
    }
}
