using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OnyxFilter.Models;
using Spectre.Console;

namespace OnyxFilter.Cli;

// Comptes de l'interface d'administration (ASP.NET Core Identity, base SQLite).
internal static class UserCommands
{
    public static int List(CommandContext context)
    {
        context.EnsureDatabase();
        using IServiceScope scope = context.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        List<ApplicationUser> users = userManager.Users.OrderBy(user => user.UserName).ToList();

        if (users.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Aucun compte : le compte « admin » sera créé au premier démarrage du serveur (ou créez-en un avec user:add).[/]");
            return CommandLine.Success;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Nom");
        table.AddColumn("Rôle");
        table.AddColumn("Clés d'accès");
        table.AddColumn("État");

        foreach (ApplicationUser user in users)
        {
            int passkeyCount = userManager.GetPasskeysAsync(user).GetAwaiter().GetResult().Count;
            bool lockedOut = user.LockoutEnd is DateTimeOffset lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow;

            table.AddRow(
                Markup.Escape(user.UserName ?? "?"),
                Markup.Escape(user.Role),
                passkeyCount.ToString(),
                lockedOut ? "[red]verrouillé[/]" : "[green]actif[/]");
        }

        AnsiConsole.Write(table);
        return CommandLine.Success;
    }

    public static int Add(CommandContext context, string[] arguments)
    {
        string userName = RequireArgument(arguments, 0, "user:add <nom> [mot de passe]");
        string password = arguments.Length > 1 ? arguments[1] : PromptNewPassword();

        context.EnsureDatabase();
        using IServiceScope scope = context.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (userManager.FindByNameAsync(userName).GetAwaiter().GetResult() is not null)
        {
            throw new CommandLineException($"Le compte « {userName} » existe déjà.", CommandLine.Failure);
        }

        // Tous les comptes ont accès à l'ensemble de l'administration : le rôle n'est qu'informatif.
        ApplicationUser user = new ApplicationUser { UserName = userName, Role = "Admin" };
        EnsureSucceeded(userManager.CreateAsync(user, password).GetAwaiter().GetResult(), "Création impossible");

        AnsiConsole.MarkupLine($"[green]Compte « {Markup.Escape(userName)} » créé.[/]");
        return CommandLine.Success;
    }

    public static int ResetPassword(CommandContext context, string[] arguments)
    {
        string userName = RequireArgument(arguments, 0, "user:reset-password <nom> [mot de passe]");
        string password = arguments.Length > 1 ? arguments[1] : PromptNewPassword();

        context.EnsureDatabase();
        using IServiceScope scope = context.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = userManager.FindByNameAsync(userName).GetAwaiter().GetResult()
            ?? throw new CommandLineException($"Compte « {userName} » introuvable.", CommandLine.Failure);

        string resetToken = userManager.GeneratePasswordResetTokenAsync(user).GetAwaiter().GetResult();
        EnsureSucceeded(userManager.ResetPasswordAsync(user, resetToken, password).GetAwaiter().GetResult(), "Réinitialisation impossible");

        // Un compte verrouillé après trop d'échecs redevient utilisable avec son nouveau mot de passe.
        userManager.SetLockoutEndDateAsync(user, null).GetAwaiter().GetResult();
        userManager.ResetAccessFailedCountAsync(user).GetAwaiter().GetResult();

        AnsiConsole.MarkupLine($"[green]Mot de passe de « {Markup.Escape(userName)} » réinitialisé.[/]");
        return CommandLine.Success;
    }

    public static int Remove(CommandContext context, string[] arguments)
    {
        string userName = RequireArgument(arguments.Where(argument => argument != "--yes" && argument != "-y").ToArray(), 0, "user:remove <nom> [--yes]");
        bool confirmed = arguments.Contains("--yes") || arguments.Contains("-y");

        context.EnsureDatabase();
        using IServiceScope scope = context.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = userManager.FindByNameAsync(userName).GetAwaiter().GetResult()
            ?? throw new CommandLineException($"Compte « {userName} » introuvable.", CommandLine.Failure);

        if (userManager.Users.Count() <= 1)
        {
            throw new CommandLineException("Impossible de supprimer le dernier compte : l'interface deviendrait inaccessible.", CommandLine.Failure);
        }

        if (!confirmed)
        {
            if (Console.IsInputRedirected)
            {
                throw new CommandLineException("Suppression non confirmée : ajoutez --yes pour l'exécuter sans question.");
            }

            if (!AnsiConsole.Confirm($"Supprimer définitivement le compte « {Markup.Escape(userName)} » ?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]Suppression annulée.[/]");
                return CommandLine.Success;
            }
        }

        EnsureSucceeded(userManager.DeleteAsync(user).GetAwaiter().GetResult(), "Suppression impossible");
        AnsiConsole.MarkupLine($"[green]Compte « {Markup.Escape(userName)} » supprimé.[/]");
        return CommandLine.Success;
    }

    private static string RequireArgument(string[] arguments, int index, string usage)
    {
        if (arguments.Length <= index || string.IsNullOrWhiteSpace(arguments[index]))
        {
            throw new CommandLineException("Usage : OnyxFilter " + usage);
        }

        return arguments[index];
    }

    // Saisie masquée et confirmée : évite que le mot de passe apparaisse dans l'historique du shell ou la
    // liste des processus.
    private static string PromptNewPassword()
    {
        if (Console.IsInputRedirected)
        {
            throw new CommandLineException("Mot de passe manquant : passez-le en argument ou lancez la commande dans un terminal interactif.");
        }

        string password = AnsiConsole.Prompt(new TextPrompt<string>("Nouveau mot de passe :").Secret());
        string confirmation = AnsiConsole.Prompt(new TextPrompt<string>("Confirmation :").Secret());

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new CommandLineException("Les deux saisies ne correspondent pas.", CommandLine.Failure);
        }

        return password;
    }

    private static void EnsureSucceeded(IdentityResult result, string context)
    {
        if (!result.Succeeded)
        {
            string details = string.Join(" ", result.Errors.Select(error => error.Description));
            throw new CommandLineException($"{context} : {details}", CommandLine.Failure);
        }
    }
}
