using System;
using System.Collections.Generic;

namespace OnyxFilter.Services.Api;

// Contrats JSON de l'API HTTP (/api/v1, voir ApiEndpoints). Sérialisés en camelCase ; les dates sont en
// UTC au format ISO 8601.

public sealed record ApiError(string Error, string Message);

// État de la protection (arrêt temporaire du filtrage, voir IDnsProtectionState).
public sealed record ProtectionStatusResponse(
    bool Enabled,
    DateTimeOffset? DisabledUntil,
    long? RemainingSeconds);

// Corps optionnel de POST /api/v1/protection/disable : au plus l'un des deux champs. Sans aucun des deux,
// le filtrage est coupé jusqu'à réactivation manuelle.
public sealed record DisableProtectionRequest(int? DurationSeconds, DateTimeOffset? Until);

public sealed record RankedItem(string Name, long Count);

public sealed record UpstreamStats(string Server, long? Queries, int? AverageResponseTimeMs);

public sealed record HourlyStats(DateTimeOffset HourStart, long TotalQueries, long BlockedQueries);

public sealed record StatsResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? WindowStart,
    long TotalQueries,
    long BlockedQueries,
    long AllowedQueries,
    double BlockedRatio,
    int AverageProcessingTimeMs,
    IReadOnlyList<RankedItem> TopQueriedDomains,
    IReadOnlyList<RankedItem> TopBlockedDomains,
    IReadOnlyList<RankedItem> TopClients,
    IReadOnlyList<UpstreamStats> Upstreams,
    IReadOnlyList<HourlyStats> Hourly);

public sealed record QueryLogItem(
    DateTimeOffset Time,
    string Domain,
    string Type,
    string Client,
    string Reason,
    bool Blocked,
    string? ReasonDetail,
    string? Upstream,
    long ProcessingTimeMs);

public sealed record QueryLogResponse(
    IReadOnlyList<QueryLogItem> Items,
    int Offset,
    int Limit,
    bool HasMore);
