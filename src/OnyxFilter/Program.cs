using System;
using System.Linq;
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
using OnyxFilter.Components;
using OnyxFilter.Components.Account;
using OnyxFilter.DbContexts;
using OnyxFilter.Models;
using OnyxFilter.Services;
using OnyxFilter.Services.Api;
using OnyxFilter.Services.BlockedServices;
using OnyxFilter.Services.BrowsingSecurity;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Filtering;
using OnyxFilter.Services.ParentalControl;
using OnyxFilter.Services.Rewrites;
using OnyxFilter.Services.SafeSearch;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter;

public class Program
{
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
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

        app.UseHttpsRedirection();

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

        ApplyMigrations(app);
        EnsureDefaultAdminAccountAsync(app).GetAwaiter().GetResult();

        if (ResetPasswordAsync(app, args).GetAwaiter().GetResult())
        {
            return;
        }

        app.Run();
    }

    /// <summary>
    /// Réinitialise le mot de passe d'un utilisateur si l'argument --reset est présent.
    /// Retourne true si la commande a été traitée (l'application doit s'arrêter).
    /// Usage : OnyxFilter --reset &lt;username&gt; &lt;newpassword&gt;
    /// </summary>
    private static async Task<bool> ResetPasswordAsync(WebApplication app, string[] args)
    {
        int resetIndex = Array.IndexOf(args, "--reset");
        if (resetIndex < 0)
        {
            return false;
        }

        if (resetIndex + 2 >= args.Length)
        {
            Console.Error.WriteLine("Usage : OnyxFilter --reset <username> <newpassword>");
            return true;
        }

        string username = args[resetIndex + 1];
        string newPassword = args[resetIndex + 2];

        using IServiceScope scope = app.Services.CreateScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByNameAsync(username);
        if (user is null)
        {
            Console.Error.WriteLine($"Erreur : utilisateur '{username}' introuvable.");
            return true;
        }

        string resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        IdentityResult result = await userManager.ResetPasswordAsync(user, resetToken, newPassword);

        if (result.Succeeded)
        {
            Console.WriteLine($"Mot de passe de '{username}' réinitialisé avec succès.");
        }
        else
        {
            foreach (IdentityError error in result.Errors)
            {
                Console.Error.WriteLine($"Erreur : {error.Description}");
            }
        }

        return true;
    }

    private static void ApplyMigrations(WebApplication app)
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
