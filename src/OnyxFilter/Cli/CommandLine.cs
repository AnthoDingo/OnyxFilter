using System;
using System.Collections.Generic;
using System.Linq;
using OnyxFilter.Services.Updates;
using Spectre.Console;

namespace OnyxFilter.Cli;

// Aiguillage des arguments du binaire unique OnyxFilter : « --server » lance le serveur, les autres
// commandes administrent une installation (comptes, règles, listes, version…) sans la démarrer. Sans
// argument, l'aide est affichée.
internal static class CommandLine
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;

    private const string ServerFlag = "--server";
    private const string DataDirectoryOption = "--data-dir";

    public static int Run(string[] args)
    {
        ConfigureConsole();

        List<string> arguments = args.ToList();
        string? dataDirectory;

        try
        {
            dataDirectory = ExtractOptionValue(arguments, DataDirectoryOption);
        }
        catch (CommandLineException ex)
        {
            return ReportError(ex);
        }

        // « --server » peut être accompagné d'options ASP.NET Core (--urls, --environment…), transmises
        // telles quelles à l'hôte web.
        if (arguments.Remove(ServerFlag))
        {
            return Program.RunServer(arguments.ToArray(), dataDirectory);
        }

        if (arguments.Count == 0)
        {
            HelpCommand.Show();
            return Success;
        }

        string command = arguments[0];
        string[] commandArguments = arguments.Skip(1).ToArray();

        try
        {
            using CommandContext context = new CommandContext(dataDirectory);

            return command.ToLowerInvariant() switch
            {
                "-h" or "--help" or "help" => HelpCommand.ShowAndSucceed(),
                "-v" or "--version" or "version" => ShowVersion(),
                "status" => StatusCommand.Run(context),
                "user:list" => UserCommands.List(context),
                "user:add" => UserCommands.Add(context, commandArguments),
                "user:reset-password" or "--reset" => UserCommands.ResetPassword(context, commandArguments),
                "user:remove" => UserCommands.Remove(context, commandArguments),
                "rules:list" => FilteringCommands.ListRules(context),
                "rules:add" => FilteringCommands.AddRule(context, commandArguments),
                "rules:remove" => FilteringCommands.RemoveRule(context, commandArguments),
                "lists:list" => FilteringCommands.ListLists(context),
                "update:check" => UpdateCommand.Check(context, commandArguments),
                _ => throw new CommandLineException($"Commande inconnue : « {command} ». Lancez « OnyxFilter --help » pour la liste des commandes."),
            };
        }
        catch (CommandLineException ex)
        {
            return ReportError(ex);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Erreur :[/] {Markup.Escape(ex.Message)}");
            return Failure;
        }
    }

    // Sortie redirigée (tube, fichier, journal systemd) : Spectre.Console ne sait pas en déterminer la
    // largeur et n'écrirait rien. On fixe une largeur (COLUMNS, sinon 120) et on retire couleurs et
    // séquences ANSI, pour une sortie exploitable par un script.
    private static void ConfigureConsole()
    {
        if (!Console.IsOutputRedirected)
        {
            return;
        }

        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(Console.Out),
        });

        AnsiConsole.Profile.Width = int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out int columns) && columns > 0 ? columns : 120;
    }

    private static int ShowVersion()
    {
        AnsiConsole.WriteLine("OnyxFilter " + AppVersion.Display);
        return Success;
    }

    private static int ReportError(CommandLineException ex)
    {
        AnsiConsole.MarkupLine($"[red]Erreur :[/] {Markup.Escape(ex.Message)}");
        return ex.ExitCode;
    }

    // Retire « --option valeur » (ou « --option=valeur ») de la liste et retourne la valeur, null si
    // l'option est absente.
    private static string? ExtractOptionValue(List<string> arguments, string option)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].StartsWith(option + "=", StringComparison.Ordinal))
            {
                string inlineValue = arguments[i].Substring(option.Length + 1);
                arguments.RemoveAt(i);
                return inlineValue;
            }

            if (arguments[i] == option)
            {
                if (i + 1 >= arguments.Count)
                {
                    throw new CommandLineException($"L'option {option} attend un chemin de dossier.");
                }

                string value = arguments[i + 1];
                arguments.RemoveRange(i, 2);
                return value;
            }
        }

        return null;
    }
}

// Erreur d'utilisation ou d'exécution d'une commande, affichée sans pile d'appels.
internal sealed class CommandLineException : Exception
{
    public CommandLineException(string message, int exitCode = CommandLine.UsageError)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
