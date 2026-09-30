using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Cli;
using OnyxFilter.Components;
using OnyxFilter.Components.Account;
using OnyxFilter.DbContexts;
using OnyxFilter.Models;
using OnyxFilter.Services;
using OnyxFilter.Services.Api;
using OnyxFilter.Services.BlockedServices;
using OnyxFilter.Services.BrowsingSecurity;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Encryption;
using OnyxFilter.Services.Encryption.Acme;
using OnyxFilter.Services.Filtering;
using OnyxFilter.Services.ParentalControl;
using OnyxFilter.Services.Rewrites;
using OnyxFilter.Services.SafeSearch;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Statistics;
using OnyxFilter.Services.Updates;

namespace OnyxFilter;

public class Program
{
    // Point d'entrée unique : serveur (--server) et commandes d'administration (voir Cli/CommandLine.cs).
    // Sans argument, l'aide est affichée.
    public static int Main(string[] args)
    {
        return CommandLine.Run(args);
    }

    // Lance le serveur (DNS, interface web, API) jusqu'à son arrêt.
    internal static int RunServer(string[] args, string? dataDirectory)
    {
        WebApplication app = BuildWebApplication(args, dataDirectory, commandLineMode: false);

        ApplyMigrations(app);
        EnsureDefaultAdminAccountAsync(app).GetAwaiter().GetResult();

        app.Run();
        return 0;
    }

    // Construit l'application complète (services, pipeline HTTP, points d'accès) sans la démarrer. Les
    // commandes d'administration s'en servent pour accéder aux mêmes services (base, réglages) que le
    // serveur, sans ses journaux.
    internal static WebApplication BuildWebApplication(string[] args, string? dataDirectory, bool commandLineMode)
    {
        // Les chemins relatifs (base SQLite, caches) se résolvent depuis le dossier courant : on se place
        // dans le dossier des données (voir DataDirectory).
        string contentRoot = DataDirectory.Resolve(dataDirectory);
        Directory.SetCurrentDirectory(contentRoot);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
        });

        // Sans appsettings.json dans le dossier des données (ex. --data-dir vers un dossier vide), la base reste
        // OnyxFilter.db : une chaîne de connexion vide ouvrirait une base SQLite temporaire, différente à chaque
        // connexion, et les tables créées par les migrations seraient aussitôt perdues.
        if (string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=OnyxFilter.db",
            });
        }

        // Port HTTPS ouvert et fermé à chaud, quand un certificat est en place (voir HttpsEndpointService).
        KestrelEndpointsConfigurationSource kestrelEndpoints = new KestrelEndpointsConfigurationSource();
        ((IConfigurationBuilder)builder.Configuration).Add(kestrelEndpoints);
        builder.Services.AddSingleton(kestrelEndpoints.Provider);

        // HTTPS : certificat choisi à chaque connexion (un certificat renouvelé est servi aussitôt), avec sa
        // chaîne intermédiaire, que Kestrel n'enverrait pas avec un simple certificat.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureHttpsDefaults(https =>
            {
                https.ServerCertificateSelector = (connection, name) =>
                    options.ApplicationServices.GetRequiredService<IHttpsEndpointService>().Certificate?.Certificate;
                https.OnAuthenticate = (connection, sslOptions) =>
                {
                    if (options.ApplicationServices.GetRequiredService<IHttpsEndpointService>().Certificate is { } current)
                    {
                        sslOptions.ServerCertificateSelectionCallback = null;
                        sslOptions.ServerCertificate = null;
                        sslOptions.ServerCertificateContext = current.Context;
                    }
                };
            });
        });

        if (commandLineMode)
        {
            // Sortie réservée aux messages des commandes (pas de journaux EF Core ou d'hébergement).
            builder.Logging.ClearProviders();
        }

        // Service systemd (deploy/onyxfilter.service, Type=notify) : signale le démarrage effectif à
        // systemd et adapte le format des journaux. Sans effet lorsque l'application n'est pas lancée par
        // systemd.
        builder.Services.AddSystemd();

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<IdentityRedirectManager>();
        builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        // Add DbContext
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

        // Add Authentication (ASP.NET Core Identity, avec support natif des passkeys .NET 10)
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        builder.Services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Pas d'infrastructure d'envoi d'e-mail dans ce projet : la confirmation de compte reste désactivée.
                options.SignIn.RequireConfirmedAccount = false;
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // IdentityPasskeyOptions.ServerDomain reste à sa valeur par défaut (null) : le Relying Party ID
        // est déduit du header Host. L'hébergeur (Kestrel/reverse proxy) doit valider ce header en production.
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
        });

        // API HTTP (/api/v1, voir Services/Api/ApiEndpoints.cs) : authentification par jeton uniquement,
        // jetons gérés depuis la page « Accès API » (/settings/api). Le cookie de session de l'interface
        // n'y donne pas accès (pas de requête intersite possible depuis le navigateur d'un administrateur).
        builder.Services.AddSingleton<IApiTokenService, ApiTokenService>();
        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, configureOptions: null);

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(ApiEndpoints.PolicyName, policy => policy
                .AddAuthenticationSchemes(ApiTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());
        });

        // Persistance des pages de paramètres dans appsettings.local.json.
        builder.Services.AddSingleton<ILocalSettingsStore, LocalSettingsStore>();

        // Service DNS : écoute sur le port 53 (UDP/TCP) et relaie les requêtes vers les serveurs en amont
        // configurés dans /settings/dns, avec un cache en mémoire borné par CacheSizeBytes. Les noms
        // d'hôte des serveurs en amont sont résolus via BootstrapResolver (Serveurs DNS d'amorçage).
        builder.Services.AddSingleton<IBootstrapResolver, BootstrapResolver>();
        builder.Services.AddSingleton<IUpstreamResolver, UpstreamResolver>();
        builder.Services.AddSingleton<IDnsCache, DnsCache>();
        builder.Services.AddSingleton<IDnsRateLimiter, DnsRateLimiter>();
        builder.Services.AddSingleton<IDnsAccessControl, DnsAccessControl>();

        // Bascule "Protection" du tableau de bord (Home.razor) : arrêt temporaire de tout le filtrage,
        // consultée par IDnsQueryPipeline à chaque requête. Toujours réactivée au démarrage (pas de
        // persistance), pour ne jamais démarrer avec le filtrage silencieusement coupé.
        builder.Services.AddSingleton<IDnsProtectionState, DnsProtectionState>();

        // Pipeline de résolution partagé entre le port 53 (DnsProxyService) et le service DNS-over-TLS
        // (DnsOverTlsService) : filtres, cache, serveurs en amont et statistiques identiques quel que
        // soit le transport.
        builder.Services.AddSingleton<IDnsQueryPipeline, DnsQueryPipeline>();

        // Activation du DNS-over-HTTPS selon "Activer le chiffrement" (/settings/encryption), avec
        // mise en cache du réglage et invalidation à chaque enregistrement.
        builder.Services.AddSingleton<IDnsOverHttpsAvailability, DnsOverHttpsAvailability>();

        // Listes de blocage DNS ("Bloquer les domaines à l'aide de filtres", /filters/blocklists) :
        // téléchargement/analyse des listes abonnées, et construction de la réponse selon le "Mode de
        // blocage" de /settings/dns. Rafraîchies au démarrage (DnsProxyService) puis périodiquement
        // (FilterListUpdateService), selon FilterUpdateIntervalHours.
        builder.Services.AddSingleton<IDnsFilterService, DnsFilterService>();
        builder.Services.AddHostedService<FilterListUpdateService>();

        // Listes d'autorisation DNS ("Listes d'autorisation DNS", /filters/allowlists) : un domaine
        // qui y figure est toujours résolu normalement, même s'il figure aussi dans une liste de
        // blocage ci-dessus (IDnsFilterService). Même mécanique de téléchargement/cache/rafraîchissement
        // que les listes de blocage (DomainListRepository partagé).
        builder.Services.AddSingleton<IDnsAllowlistService, DnsAllowlistService>();
        builder.Services.AddHostedService<AllowlistUpdateService>();

        // Règles de filtrage personnalisées ("Règles de filtrage personnalisées", /filters/custom-rules) :
        // syntaxe des règles de blocage/listes hosts, saisie directement par l'utilisateur (pas
        // d'abonnement à une URL). Vérifiée avant les listes de blocage/autorisation abonnées ci-dessus
        // dans le pipeline de résolution (DnsQueryPipeline) : des règles saisies explicitement priment
        // toujours sur un abonnement. Pas de mise à jour périodique nécessaire, comme pour les
        // réécritures DNS ci-dessous.
        builder.Services.AddSingleton<ICustomFilterRulesService, CustomFilterRulesService>();

        // Services bloqués ("Services bloqués", /filters/blocked-services) : catalogue de services
        // populaires (ChatGPT, réseaux sociaux, streaming, jeux vidéo, etc.), repris de
        // AdguardTeam/HostlistsRegistry, avec blocage à la demande par service et suspension temporaire
        // programmable ("Suspendre le blocage des services"). Même priorité que les listes de blocage
        // abonnées dans le pipeline de résolution (DnsQueryPipeline).
        builder.Services.AddSingleton<IBlockedServicesCatalog, BlockedServicesCatalog>();
        builder.Services.AddSingleton<IBlockedServicesService, BlockedServicesService>();

        // Réécritures DNS ("Réécritures DNS", /filters/rewrites) : réponse personnalisée définie par
        // l'utilisateur pour un domaine donné (adresse directe ou chaîne CNAME). Vérifiée en tout premier
        // dans le pipeline de résolution (DnsQueryPipeline), avant les listes de blocage/autorisation.
        // Pas de mise à jour périodique : contrairement à celles-ci, ces règles sont saisies directement
        // (pas d'abonnement à une URL), seul le rechargement à chaud des réglages est nécessaire.
        builder.Services.AddSingleton<IDnsRewriteService, DnsRewriteService>();

        // "Utilisez le service Sécurité de navigation d'OnyxFilter" (Paramètres généraux) : reproduit le
        // service "Safe Browsing" d'AdGuard Home (vérification par préfixe de hachage SHA256, en
        // DNS-over-HTTPS auprès de family.adguard-dns.com, sans jamais transmettre le domaine en clair).
        builder.Services.AddSingleton<IBrowsingSecurityService, BrowsingSecurityService>();

        // "Utiliser le contrôle parental d'OnyxFilter" (Paramètres généraux) : même protocole qu'AdGuard
        // Home pour son Contrôle parental (partage l'implémentation bas niveau AdGuardHashPrefixLookup
        // avec IBrowsingSecurityService ci-dessus, seul le suffixe de question DNS diffère).
        builder.Services.AddSingleton<IParentalControlService, ParentalControlService>();

        // "Utiliser la Recherche Sécurisée" (Paramètres généraux) : reproduit les règles de réécriture
        // DNS d'AdGuard Home pour ses 7 moteurs pris en charge (Bing, DuckDuckGo, Ecosia, Google,
        // Pixabay, Yandex, YouTube). S'appuie sur IUpstreamResolver pour résoudre la cible des
        // redirections par CNAME auprès des serveurs en amont configurés.
        builder.Services.AddSingleton<ISafeSearchService, SafeSearchService>();

        // Statistiques du tableau de bord ("/", Home.razor) : compteurs agrégés par heure en mémoire,
        // alimentés par DnsProxyService à chaque requête traitée, et persistés périodiquement dans la
        // base SQLite existante (table DnsStatisticsBuckets, hors modèle EF Core) pour survivre à un
        // redémarrage du serveur.
        builder.Services.AddSingleton<IDnsStatisticsRepository, SqliteDnsStatisticsRepository>();
        builder.Services.AddSingleton<IDnsStatisticsService, DnsStatisticsService>();

        // Journal des requêtes ("/query-log") : une ligne par requête traitée, mise en file d'attente en
        // mémoire (bornée, pour rester léger) puis écrite par lot en base SQLite (table
        // DnsQueryLogEntries, hors modèle EF Core). Respecte "Activer le journal", "Anonymiser l'IP du
        // client", "Rotation des journaux de requêtes" et "Domaines ignorés" (/settings/general).
        builder.Services.AddSingleton<IDnsQueryLogRepository, SqliteDnsQueryLogRepository>();
        builder.Services.AddSingleton<IDnsQueryLogService, DnsQueryLogService>();

        builder.Services.AddHostedService<DnsProxyService>();

        // Service DNS-over-TLS (RFC 7858) : actif uniquement si "Activer le chiffrement" est coché dans
        // /settings/encryption. Port (853 par défaut) et certificat configurés sur la même page ;
        // reconfiguration à chaud à chaque enregistrement, sans redémarrage.
        builder.Services.AddHostedService<DnsOverTlsService>();

        // Service DNS-over-QUIC (RFC 9250) : mêmes conditions d'activation et même certificat que le
        // DNS-over-TLS, port dédié (784 par défaut, en UDP). Nécessite msquic (paquet 'libmsquic' sous
        // Linux) ; sans lui, le service se met en attente avec un avertissement, sans bloquer le reste.
        builder.Services.AddHostedService<DnsOverQuicService>();

        // Mises à jour (« Mises à jour », /settings/updates) : recherche des publications GitHub, puis
        // installation en un clic de l'archive autonome linux-x64 et redémarrage par systemd. Source et
        // contraintes réglables dans la section « Updates » d'appsettings.json (voir UpdateOptions).
        builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection(UpdateOptions.SectionName));
        builder.Services.AddHttpClient(GitHubReleaseClient.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OnyxFilter/" + AppVersion.Display);
            // Délais gérés par appel (API : 30 s, téléchargement : 20 min).
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        builder.Services.AddSingleton<GitHubReleaseClient>();
        builder.Services.AddSingleton<UpdateInstaller>();
        builder.Services.AddSingleton<IUpdateService, UpdateService>();
        builder.Services.AddHostedService<UpdateCheckBackgroundService>();

        // Certificat Let's Encrypt automatique (« Chiffrement », /settings/encryption) : obtention par défi
        // HTTP-01, vérification toutes les 12 heures et renouvellement 15 jours avant l'expiration. Annuaires
        // ACME réglables dans la section « LetsEncrypt » d'appsettings.json (voir LetsEncryptOptions).
        builder.Services.Configure<LetsEncryptOptions>(builder.Configuration.GetSection(LetsEncryptOptions.SectionName));
        builder.Services.AddHttpClient(LetsEncryptService.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OnyxFilter/" + AppVersion.Display);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddSingleton<AcmeHttpChallengeStore>();
        builder.Services.AddSingleton<LetsEncryptService>();
        builder.Services.AddSingleton<ILetsEncryptService>(serviceProvider => serviceProvider.GetRequiredService<LetsEncryptService>());
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<LetsEncryptService>());

        // Port HTTPS de l'interface et du DNS-over-HTTPS, actif dès qu'un certificat valide est en place.
        builder.Services.AddSingleton<HttpsEndpointService>();
        builder.Services.AddSingleton<IHttpsEndpointService>(serviceProvider => serviceProvider.GetRequiredService<HttpsEndpointService>());
        builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<HttpsEndpointService>());

        WebApplication app = builder.Build();

        // Défis HTTP-01 de Let's Encrypt, avant toute redirection, page d'erreur ou authentification.
        app.UseAcmeHttpChallenge();

        // Redirection vers HTTPS et HSTS, selon « Rediriger HTTP vers HTTPS » (page « Chiffrement »).
        app.UseOnyxHttpsRedirection();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        // Les réponses d'erreur de l'API restent en JSON : pas de substitution par la page HTML ci-dessus.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                IStatusCodePagesFeature? statusCodePages = context.Features.Get<IStatusCodePagesFeature>();
                if (statusCodePages is not null)
                {
                    statusCodePages.Enabled = false;
                }
            }

            await next();
        });

        app.UseAntiforgery();

        // Add Authentication and Authorization
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapStaticAssets();

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        // Endpoints requis par les composants Identity (login par mot de passe/passkey, logout).
        app.MapAdditionalIdentityEndpoints();

        // Service DNS-over-HTTPS (RFC 8484) sur /dns-query : servi par le Kestrel existant (même
        // port HTTPS que l'interface web), sans écouteur supplémentaire. Actif uniquement si
        // "Activer le chiffrement" est coché dans /settings/encryption (sinon 404), avec prise en
        // compte à chaud de chaque enregistrement. Résolution via le même pipeline partagé que le
        // port 53 et le DNS-over-TLS (filtres, cache, amont, statistiques).
        app.MapDnsOverHttps();

        // API HTTP d'automatisation : état et suspension temporaire du filtrage, statistiques d'usage,
        // journal des requêtes.
        app.MapOnyxApi();

        return app;
    }

    internal static void ApplyMigrations(WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // N'applique que les migrations déjà générées (dotnet ef migrations add ...).
        // Si aucune migration n'existe encore dans le projet, cet appel ne crée aucune table.
        dbContext.Database.Migrate();
    }

    private static async Task EnsureDefaultAdminAccountAsync(WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (userManager.Users.Any())
        {
            return;
        }

        const string defaultUsername = "admin";
        string defaultPassword = Guid.NewGuid().ToString("N").Substring(0, 16) + "!Aa1";

        ApplicationUser adminUser = new ApplicationUser
        {
            UserName = defaultUsername,
            Role = "Admin",
        };

        IdentityResult result = await userManager.CreateAsync(adminUser, defaultPassword);

        if (result.Succeeded)
        {
            Console.WriteLine("========================================================");
            Console.WriteLine("Compte administrateur créé automatiquement :");
            Console.WriteLine($"  Nom d'utilisateur : {defaultUsername}");
            Console.WriteLine($"  Mot de passe      : {defaultPassword}");
            Console.WriteLine("Connectez-vous puis ajoutez une clé d'accès depuis /account/passkeys.");
            Console.WriteLine("========================================================");
        }
        else
        {
            foreach (IdentityError error in result.Errors)
            {
                Console.Error.WriteLine($"Erreur lors de la création du compte admin : {error.Description}");
            }
        }
    }
}
