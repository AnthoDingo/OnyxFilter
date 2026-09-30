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

public sealed record ReleaseSummary(
    string Version,
    string Name,
    DateTimeOffset? PublishedAt,
    string Url,
    bool PreRelease,
    string Notes);

// État des mises à jour (voir IUpdateService.Status).
public sealed record UpdateStatusResponse(
    string CurrentVersion,
    string State,
    bool UpdateAvailable,
    ReleaseSummary? Latest,
    DateTimeOffset? LastChecked,
    string? LastError,
    double? Progress,
    bool CanInstall,
    string? InstallBlocker);

// Accès des clients au DNS (voir ClientAccessLists) : mode en vigueur (« Blocklist » : tous les clients sauf
// les refusés ; « Allowlist » : seulement les autorisés) et règles valides des trois listes. Les clients
// toujours autorisés sont servis dans tous les cas.
public sealed record AccessListsResponse(
    string Mode,
    IReadOnlyList<string> AllowedClients,
    IReadOnlyList<string> BlockedClients,
    IReadOnlyList<string> AlwaysAllowedClients);

// Corps de POST /api/v1/access/block et /api/v1/access/allow : adresse IP ou sous-réseau CIDR.
public sealed record ClientAccessRequest(string? Client);

// Règle d'une liste d'accès : List vaut « Allowed », « Blocked » ou « AlwaysAllowed ».
public sealed record AccessRuleItem(string List, string Rule);

public sealed record ClientAccessChangeResponse(
    string Client,
    bool Changed,
    IReadOnlyList<AccessRuleItem> Added,
    IReadOnlyList<AccessRuleItem> Removed,
    string Mode,
    IReadOnlyList<string> AllowedClients,
    IReadOnlyList<string> BlockedClients,
    IReadOnlyList<string> AlwaysAllowedClients);

// Refus d'une opération d'accès (409) : Rules cite les règles des paramètres DNS qui s'y opposent.
public sealed record ClientAccessConflictError(string Error, string Message, IReadOnlyList<AccessRuleItem> Rules);

public sealed record ClientAccessCheckResponse(string Ip, bool Allowed, string Mode, string? Rule, string? List);
