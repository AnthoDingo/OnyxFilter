using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Statistics;
using OnyxFilter.Services.Updates;

namespace OnyxFilter.Services.Api;

// API HTTP d'OnyxFilter, pour l'automatisation (domotique, scripts, supervision) :
//   GET  /api/v1/protection           état du filtrage
//   POST /api/v1/protection/disable   coupe le filtrage, durée ou échéance optionnelle
//   POST /api/v1/protection/enable    réactive le filtrage
//   GET  /api/v1/stats                statistiques d'usage (24 dernières heures)
//   GET  /api/v1/querylog             journal des requêtes, paginé
//   GET  /api/v1/update               état des mises à jour
//   POST /api/v1/update/check         recherche une nouvelle version sur GitHub
//   POST /api/v1/update/install       installe la nouvelle version puis redémarre (202, asynchrone)
//   GET  /api/v1/access               listes d'accès des clients, mode en vigueur
//   GET  /api/v1/access/check         une adresse IP est-elle servie par le DNS ?
//   POST /api/v1/access/block         bloque une adresse IP ou un sous-réseau
//   POST /api/v1/access/allow         autorise une adresse IP ou un sous-réseau
// Authentification par jeton uniquement (ApiTokenAuthenticationHandler) ; la référence utilisateur est
// affichée sur la page « Accès API » (/settings/api).
public static class ApiEndpoints
{
    public const string PolicyName = "OnyxApi";

    // Une coupure « temporaire » ne peut pas dépasser un an.
    private const long MaxDisableSeconds = 366L * 24 * 3600;

    private const int DefaultQueryLogLimit = 50;
    private const int MaxQueryLogLimit = 500;

    public static IEndpointRouteBuilder MapOnyxApi(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").RequireAuthorization(PolicyName);

        api.MapGet("/protection", GetProtection);
        api.MapPost("/protection/enable", EnableProtection);
        api.MapPost("/protection/disable", DisableProtectionAsync);
        api.MapGet("/stats", GetStats);
        api.MapGet("/querylog", GetQueryLogAsync);
        api.MapGet("/update", GetUpdateStatus);
        api.MapPost("/update/check", CheckForUpdateAsync);
        api.MapPost("/update/install", InstallUpdate);
        api.MapGet("/access", GetAccessAsync);
        api.MapGet("/access/check", CheckAccessAsync);
        api.MapPost("/access/block", BlockClientAsync);
        api.MapPost("/access/allow", AllowClientAsync);

        // Toute autre adresse sous /api : 404 en JSON plutôt que la page « introuvable » de l'interface.
        endpoints.MapFallback("/api/{**path}", () => Results.Json(
            new ApiError("not_found", "Point d'accès inconnu. Voir la page « Accès API » de l'interface."),
            statusCode: StatusCodes.Status404NotFound));

        return endpoints;
    }

    private static ProtectionStatusResponse GetProtection(IDnsProtectionState protectionState)
    {
        return ToProtectionResponse(protectionState);
    }

    private static ProtectionStatusResponse EnableProtection(IDnsProtectionState protectionState)
    {
        protectionState.Enable();
        return ToProtectionResponse(protectionState);
    }

    private static async Task<IResult> DisableProtectionAsync(HttpRequest request, IDnsProtectionState protectionState)
    {
        int? durationSeconds = null;
        DateTimeOffset? until = null;

        if (request.ContentLength is > 0 || (request.ContentLength is null && request.HasJsonContentType()))
        {
            if (!request.HasJsonContentType())
            {
                return Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Le corps de la requête doit être du JSON (Content-Type: application/json).");
            }

            try
            {
                DisableProtectionRequest? body = await request.ReadFromJsonAsync<DisableProtectionRequest>();
                durationSeconds = body?.DurationSeconds;
                until = body?.Until;
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_json", "Corps JSON invalide. Attendu : {\"durationSeconds\": 600} ou {\"until\": \"2026-01-01T08:00:00Z\"}.");
            }
        }
        else
        {
            // Sans corps, les mêmes paramètres sont acceptés dans l'adresse (pratique pour un simple webhook).
            string? durationText = request.Query["durationSeconds"];
            string? untilText = request.Query["until"];

            if (!string.IsNullOrEmpty(durationText))
            {
                if (!int.TryParse(durationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDuration))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_duration", "durationSeconds doit être un nombre entier de secondes.");
                }

                durationSeconds = parsedDuration;
            }

            if (!string.IsNullOrEmpty(untilText))
            {
                if (!DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedUntil))
                {
                    return Error(StatusCodes.Status400BadRequest, "invalid_until", "until doit être une date ISO 8601 (ex. 2026-01-01T08:00:00Z).");
                }

                until = parsedUntil;
            }
        }

        if (durationSeconds is not null && until is not null)
        {
            return Error(StatusCodes.Status400BadRequest, "ambiguous_request", "Indiquez durationSeconds ou until, pas les deux.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (durationSeconds is int seconds)
        {
            if (seconds < 1 || seconds > MaxDisableSeconds)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_duration", $"durationSeconds doit être compris entre 1 et {MaxDisableSeconds}.");
            }

            until = now.AddSeconds(seconds);
        }
        else if (until is DateTimeOffset requestedUntil)
        {
            if (requestedUntil <= now)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_until", "until doit être dans le futur.");
            }

            if (requestedUntil - now > TimeSpan.FromSeconds(MaxDisableSeconds))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_until", "until ne peut pas dépasser un an.");
            }
        }

        if (until is DateTimeOffset effectiveUntil)
        {
            protectionState.DisableUntil(effectiveUntil.UtcDateTime);
        }
        else
        {
            protectionState.Disable();
        }

        return Results.Ok(ToProtectionResponse(protectionState));
    }

    private static StatsResponse GetStats(IDnsStatisticsService statisticsService)
    {
        DnsStatisticsSnapshot snapshot = statisticsService.GetSnapshot();
        long allowed = Math.Max(0, snapshot.TotalQueries - snapshot.BlockedQueries);
        double ratio = snapshot.TotalQueries <= 0 ? 0 : Math.Round(Math.Min(1.0, snapshot.BlockedQueries / (double)snapshot.TotalQueries), 4);

        return new StatsResponse(
            GeneratedAt: DateTimeOffset.UtcNow,
            WindowStart: snapshot.HourlySeries.Count == 0 ? null : ToUtcOffset(snapshot.HourlySeries[0].HourStartUtc),
            TotalQueries: snapshot.TotalQueries,
            BlockedQueries: snapshot.BlockedQueries,
            AllowedQueries: allowed,
            BlockedRatio: ratio,
            AverageProcessingTimeMs: snapshot.AverageProcessingTimeMs,
            TopQueriedDomains: ToRanking(snapshot.TopSearchedDomains),
            TopBlockedDomains: ToRanking(snapshot.TopBlockedDomains),
            TopClients: ToRanking(snapshot.TopClients),
            Upstreams: snapshot.BuildUpstreamSummaries()
                .Select(summary => new UpstreamStats(summary.Upstream, summary.RequestCount, summary.AverageResponseTimeMs))
                .ToList(),
            Hourly: snapshot.HourlySeries
                .Select(point => new HourlyStats(ToUtcOffset(point.HourStartUtc), point.TotalQueries, point.BlockedQueries))
                .ToList());
    }

    private static async Task<IResult> GetQueryLogAsync(
        IDnsQueryLogService queryLogService,
        int? limit,
        int? offset,
        string? search,
        string? reason,
        CancellationToken cancellationToken)
    {
        int take = limit ?? DefaultQueryLogLimit;
        int skip = offset ?? 0;

        if (take < 1 || take > MaxQueryLogLimit)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_limit", $"limit doit être compris entre 1 et {MaxQueryLogLimit}.");
        }

        if (skip < 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_offset", "offset doit être positif ou nul.");
        }

        QueryLogReason? reasonFilter = null;

        if (!string.IsNullOrWhiteSpace(reason))
        {
            if (!Enum.TryParse(reason, ignoreCase: true, out QueryLogReason parsedReason) || !Enum.IsDefined(parsedReason))
            {
                string accepted = string.Join(", ", Enum.GetNames<QueryLogReason>());
                return Error(StatusCodes.Status400BadRequest, "invalid_reason", $"reason inconnu. Valeurs acceptées : {accepted}.");
            }

            reasonFilter = parsedReason;
        }

        string? searchText = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        // Une ligne de plus que demandé pour savoir s'il existe une page suivante.
        IReadOnlyList<DnsQueryLogRecord> records = await queryLogService.GetPageAsync(skip, take + 1, searchText, reasonFilter, cancellationToken);

        List<QueryLogItem> items = records
            .Take(take)
            .Select(record => new QueryLogItem(
                ToUtcOffset(record.TimestampUtc),
                record.Domain,
                record.QueryType,
                record.ClientKey,
                record.Reason.ToString(),
                record.Blocked,
                record.ReasonDetail,
                record.UpstreamServer,
                record.ProcessingTimeMs))
            .ToList();

        return Results.Ok(new QueryLogResponse(items, skip, take, records.Count > take));
    }

    private static UpdateStatusResponse GetUpdateStatus(IUpdateService updateService)
    {
        return ToUpdateResponse(updateService.Status);
    }

    private static async Task<UpdateStatusResponse> CheckForUpdateAsync(IUpdateService updateService, CancellationToken cancellationToken)
    {
        return ToUpdateResponse(await updateService.CheckAsync(force: true, cancellationToken));
    }

    // L'installation (téléchargement compris) peut durer : elle est lancée en arrière-plan et suivie via
    // GET /api/v1/update. Le serveur redémarre ensuite de lui-même.
    private static IResult InstallUpdate(IUpdateService updateService)
    {
        UpdateStatus status = updateService.Status;

        if (status.IsBusy)
        {
            return Error(StatusCodes.Status409Conflict, "update_in_progress", "Une vérification ou une installation est déjà en cours.");
        }

        if (!status.IsUpdateAvailable)
        {
            return Error(StatusCodes.Status409Conflict, "no_update", "Aucune mise à jour à installer (lancez d'abord POST /api/v1/update/check).");
        }

        if (!status.CanInstall)
        {
            return Error(StatusCodes.Status409Conflict, "install_unavailable", status.InstallBlocker ?? "Installation impossible sur cette machine.");
        }

        _ = Task.Run(() => updateService.InstallAsync(CancellationToken.None));
        return Results.Accepted("/api/v1/update", ToUpdateResponse(updateService.Status));
    }

    private static async Task<AccessListsResponse> GetAccessAsync(ILocalSettingsStore settingsStore)
    {
        AppLocalSettings settings = await settingsStore.LoadAsync();
        return ToAccessListsResponse(settings.Dns);
    }

    // Décision du serveur DNS en cours d'exécution pour cette adresse, avec la règle en cause.
    private static async Task<IResult> CheckAccessAsync(IDnsAccessControl accessControl, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return Error(StatusCodes.Status400BadRequest, "missing_ip", "Indiquez l'adresse à vérifier : ?ip=192.168.1.50.");
        }

        if (ip.Contains('/', StringComparison.Ordinal)
            || !ClientAccessRule.TryParseStrict(ip, out ClientAccessRule rule)
            || !IPAddress.TryParse(ip.Trim(), out IPAddress? address))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_ip", "ip doit être une adresse IPv4 ou IPv6 (ex. 192.168.1.50).");
        }

        await accessControl.InitializeAsync();
        ClientAccessDecision decision = accessControl.Evaluate(address);
        return Results.Ok(new ClientAccessCheckResponse(rule.ToString(), decision.Allowed, decision.Mode.ToString(), decision.Rule, decision.RuleList?.ToString()));
    }

    private static Task<IResult> BlockClientAsync(HttpRequest request, ILocalSettingsStore settingsStore, IDnsAccessControl accessControl)
    {
        return ChangeClientAccessAsync(request, settingsStore, accessControl, block: true);
    }

    private static Task<IResult> AllowClientAsync(HttpRequest request, ILocalSettingsStore settingsStore, IDnsAccessControl accessControl)
    {
        return ChangeClientAccessAsync(request, settingsStore, accessControl, block: false);
    }

    // Modifie les listes d'accès des paramètres DNS (clients autorisés, refusés, toujours autorisés) sans changer
    // de mode (voir ClientAccessLists). La modification est en vigueur pour le serveur DNS dès la réponse.
    private static async Task<IResult> ChangeClientAccessAsync(
        HttpRequest request,
        ILocalSettingsStore settingsStore,
        IDnsAccessControl accessControl,
        bool block)
    {
        string? client;

        if (request.ContentLength is > 0 || (request.ContentLength is null && request.HasJsonContentType()))
        {
            if (!request.HasJsonContentType())
            {
                return Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Le corps de la requête doit être du JSON (Content-Type: application/json).");
            }

            try
            {
                ClientAccessRequest? body = await request.ReadFromJsonAsync<ClientAccessRequest>();
                client = body?.Client;
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_json", "Corps JSON invalide. Attendu : {\"client\": \"192.168.1.50\"}.");
            }
        }
        else
        {
            // Sans corps, le client est accepté dans l'adresse (pratique pour un simple webhook).
            client = request.Query["client"];
        }

        if (string.IsNullOrWhiteSpace(client))
        {
            return Error(StatusCodes.Status400BadRequest, "missing_client", "Indiquez le client : {\"client\": \"192.168.1.50\"} ou ?client=192.168.1.50.");
        }

        if (!ClientAccessRule.TryParseStrict(client, out ClientAccessRule rule))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_client", "client doit être une adresse IP (ex. 192.168.1.50) ou un sous-réseau CIDR (ex. 192.168.1.0/24).");
        }

        // Vérification préalable, sans écriture : chaque enregistrement des réglages fait recharger tous les
        // services, inutile quand il n'y a rien à changer ou que l'opération est refusée.
        AppLocalSettings settings = await settingsStore.LoadAsync();
        ClientAccessChange change = ApplyClientAccess(settings.Dns, rule, block);

        if (change.Outcome == ClientAccessOutcome.Changed)
        {
            // Recalculé sous le verrou des réglages, sur leur dernière version.
            await settingsStore.UpdateAsync(latest =>
            {
                change = ApplyClientAccess(latest.Dns, rule, block);
                settings = latest;
            });

            await accessControl.ReloadAsync();
        }

        string canonical = rule.ToString();

        switch (change.Outcome)
        {
            case ClientAccessOutcome.Conflict:
                string rules = string.Join(", ", change.Conflicts.Select(conflict => $"« {conflict.Rule} » ({DescribeList(conflict.List)})"));
                string message = change.Conflicts.Count > 1
                    ? $"{canonical} reste autorisé par {rules}, plus larges. Pour n'en bloquer qu'une partie, modifiez ces règles depuis Paramètres DNS."
                    : $"{canonical} reste autorisé par {rules}, plus large. Pour n'en bloquer qu'une partie, modifiez cette règle depuis Paramètres DNS.";
                return Results.Json(new ClientAccessConflictError("covered_by_rule", message, ToRuleItems(change.Conflicts)), statusCode: StatusCodes.Status409Conflict);

            case ClientAccessOutcome.AllowlistWouldBeEmpty:
                return Results.Json(
                    new ClientAccessConflictError(
                        "allowlist_would_be_empty",
                        $"Bloquer {canonical} viderait la liste des clients autorisés : le DNS serait alors ouvert à tous les clients. Ajoutez d'abord un autre client autorisé, ou modifiez les listes depuis Paramètres DNS.",
                        ToRuleItems(change.Removed)),
                    statusCode: StatusCodes.Status409Conflict);

            default:
                AccessListsResponse lists = ToAccessListsResponse(settings.Dns);
                return Results.Ok(new ClientAccessChangeResponse(
                    canonical,
                    change.Outcome == ClientAccessOutcome.Changed,
                    ToRuleItems(change.Added),
                    ToRuleItems(change.Removed),
                    lists.Mode,
                    lists.AllowedClients,
                    lists.BlockedClients,
                    lists.AlwaysAllowedClients));
        }
    }

    private static ClientAccessChange ApplyClientAccess(DnsSettingsData dns, ClientAccessRule rule, bool block)
    {
        return block ? ClientAccessLists.Block(dns, rule) : ClientAccessLists.Allow(dns, rule);
    }

    private static AccessListsResponse ToAccessListsResponse(DnsSettingsData dns)
    {
        return new AccessListsResponse(
            ClientAccessLists.GetMode(dns).ToString(),
            ClientAccessLists.ValidRules(dns.AllowedClients).Select(rule => rule.Source).ToList(),
            ClientAccessLists.ValidRules(dns.DisallowedClients).Select(rule => rule.Source).ToList(),
            ClientAccessLists.ValidRules(dns.AlwaysAllowedClients).Select(rule => rule.Source).ToList());
    }

    private static IReadOnlyList<AccessRuleItem> ToRuleItems(IReadOnlyList<ClientAccessListEntry> entries)
    {
        return entries.Select(entry => new AccessRuleItem(entry.List.ToString(), entry.Rule)).ToList();
    }

    private static string DescribeList(ClientAccessList list)
    {
        return list switch
        {
            ClientAccessList.Allowed => "clients autorisés",
            ClientAccessList.Blocked => "clients refusés",
            _ => "clients toujours autorisés",
        };
    }

    private static UpdateStatusResponse ToUpdateResponse(UpdateStatus status)
    {
        ReleaseSummary? latest = status.LatestRelease is { } release
            ? new ReleaseSummary(release.Version.ToString(), release.Name, release.PublishedAt, release.HtmlUrl, release.IsPreRelease, release.Notes)
            : null;

        return new UpdateStatusResponse(
            status.CurrentVersion,
            status.State.ToString(),
            status.IsUpdateAvailable,
            latest,
            status.LastCheckedUtc,
            status.LastError,
            status.Progress,
            status.CanInstall,
            status.InstallBlocker);
    }

    private static ProtectionStatusResponse ToProtectionResponse(IDnsProtectionState protectionState)
    {
        bool enabled = protectionState.IsEnabled;
        DateTime? untilUtc = enabled ? null : protectionState.DisabledUntilUtc;

        if (untilUtc is null)
        {
            return new ProtectionStatusResponse(enabled, null, null);
        }

        long remaining = Math.Max(0, (long)Math.Ceiling((untilUtc.Value - DateTime.UtcNow).TotalSeconds));
        return new ProtectionStatusResponse(enabled, ToUtcOffset(untilUtc.Value), remaining);
    }

    private static IReadOnlyList<RankedItem> ToRanking(IReadOnlyList<DnsStatisticsEntry> entries)
    {
        return entries.Select(entry => new RankedItem(entry.Label, entry.Count)).ToList();
    }

    private static DateTimeOffset ToUtcOffset(DateTime utc)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private static IResult Error(int statusCode, string code, string message)
    {
        return Results.Json(new ApiError(code, message), statusCode: statusCode);
    }
}
