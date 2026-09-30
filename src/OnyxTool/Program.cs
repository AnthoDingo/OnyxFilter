using OnyxFilter.Services;
using Spectre.Console;
using System;

namespace OnyxTool
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=======================================");
            Console.WriteLine("  OnyxFilter Management Console Tool");
            Console.WriteLine("=======================================");
            
            // Ici, nous allons lire les arguments passés par l'utilisateur pour déterminer la tâche à exécuter.

            if (args == null || args.Length == 0)
            {
                ShowHelp();
                return;
            }

            string command = args[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "status":
                        //RunStatusCheck();
                        break;
                    case "manage-rules":
                        ManageFilteringRules(args);
                        break;
                    // Ajoutez d'autres commandes ici : 
                    // case "reload-cache":
                    //     ReloadDnsCache();
                    case "user:list":
                        ListUsers();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\nUne erreur critique est survenue lors de l'exécution de la commande '{command}': {ex.Message}");
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("\n--- Disponibles ---");
            Console.WriteLine("status                 : Vérifie l'état général du filtre DNS et des services.");
            Console.WriteLine("user:list                   : Liste tous les utilisateurs enregistrés dans le système.");
            Console.WriteLine("manage-rules           : Outils de gestion des listes de blocage, autorisation ou règles personnalisées.");
            // Ajoutez d'autres commandes ici.
        }

        static void ListUsers()
        {
            Console.WriteLine("\n[UTILISATEURS] Récupération des utilisateurs...");
            // ATTENTION : Dans un contexte de console autonome, l'accès à IdentityManager est complexe car le scope ASP.NET Core n'est pas démarré ici.
            // En production, ce service nécessiterait d'initialiser ou d'injecter manuellement un UserManager<ApplicationUser> avec les bonnes configurations DB.

            // Simulation pour l'instant : liste des utilisateurs connus.
            var users = new[] { "admin", "utilisateur_test", "super_user" };

            if (users.Length == 0)
            {
                Console.WriteLine("Aucun utilisateur trouvé.");
                return;
            }

            // Utilisation de Spectre.Console pour une belle table
            //var table = Table.createColumn("Nom d'utilisateur")->addColumns(
            //    new TableColumn("Status"),
            //    new TableColumn("Dernière connexion")
            //).addRows(
            //    new Row("admin", "Actif", "Jamais", DateTimeOffset.Now - TimeSpan.FromDays(365)),
            //    new Row("utilisateur_test", "Inactif", "2024-01-15", DateTimeOffset.Now - TimeSpan.FromDays(90)),
            //    new Row("super_user", "Actif", "Aujourd'hui", DateTimeOffset.Now)
            //);
            Table table = new Table();
            table.AddColumn("Username");
            table.AddColumn("Status");
            table.AddColumn("Last connection");

            table.AddRow("Admin", "Enable", "Never");


            AnsiConsole.WriteLine("\nLes utilisateurs enregistrés dans le système sont :");
            AnsiConsole.Write(table);
        }

        static void ManageFilteringRules(string[] args)
        {
            if (args.Length < 2)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\nUsage: OnyxTool manage-rules <subcommand> [arguments]");
                Console.WriteLine("Exemples: status-blocklist, add-rule, delete-rule");
                Console.ResetColor();
                return;
            }

            string subcommand = args[1].ToLowerInvariant();
            Console.WriteLine($"\n[GESTION] Exécution de la sous-commande : {subcommand}");
            // TODO: Implémenter ici la logique pour chaque sous-commande (ex: lire/écrire dans la base SQLite via les services).
        }

        static void ReloadDnsCache()
        {
            Console.WriteLine("[INFO] Fonctionnalité de rechargement du cache DNS non implémentée.");
        }
    }
}