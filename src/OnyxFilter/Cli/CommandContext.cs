using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace OnyxFilter.Cli;

// Accès des commandes d'administration aux services de l'application (base, réglages…), construits comme
// pour le serveur mais sans le démarrer (aucun port ouvert, aucun service en arrière-plan). L'application
// n'est construite qu'à la première utilisation : l'aide et la version restent instantanées.
internal sealed class CommandContext : IDisposable
{
    private readonly string? dataDirectory;
    private WebApplication? application;
    private bool databaseReady;

    public CommandContext(string? dataDirectory)
    {
        this.dataDirectory = dataDirectory;
    }

    public IServiceProvider Services => Application.Services;

    public string DataDirectory => Application.Environment.ContentRootPath;

    private WebApplication Application => application ??= Program.BuildWebApplication(Array.Empty<string>(), dataDirectory, commandLineMode: true);

    // Crée ou met à niveau le schéma de la base, comme au démarrage du serveur.
    public void EnsureDatabase()
    {
        if (!databaseReady)
        {
            Program.ApplyMigrations(Application);
            databaseReady = true;
        }
    }

    public void Dispose()
    {
        (application as IDisposable)?.Dispose();
    }
}
