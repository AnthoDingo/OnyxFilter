using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.QueryLog;
using OnyxFilter.Services.Statistics;

namespace OnyxFilter.Services.Api;

// API HTTP d'OnyxFilter, pour l'automatisation (domotique, scripts, supervision) :
//   GET  /api/v1/protection           état du filtrage
//   POST /api/v1/protection/disable   coupe le filtrage, durée ou échéance optionnelle
//   POST /api/v1/protection/enable    réactive le filtrage
//   GET  /api/v1/stats                statistiques d'usage (24 dernières heures)
//   GET  /api/v1/querylog             journal des requêtes, paginé
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
