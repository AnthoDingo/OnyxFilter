using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Services.BrowsingSecurity;
using OnyxFilter.Services.DnsForwarding;
using OnyxFilter.Services.Filtering;
using OnyxFilter.Services.ParentalControl;
using OnyxFilter.Services.Rewrites;
using OnyxFilter.Services.SafeSearch;

namespace OnyxFilter.Components.Pages;

public partial class FilteringCheck : ComponentBase
{
    // Types d'enregistrement les plus courants : le blocage/l'autorisation par domaine (listes de
    // blocage/autorisation, règles personnalisées) est indépendant du type demandé, mais les
    // Réécritures DNS et la Recherche Sécurisée ne s'appliquent, comme chez AdGuard Home, qu'aux
    // questions A/AAAA.
    private static readonly (string Label, ushort Type)[] RecordTypeOptions =
    {
        ("A", DnsMessageParser.TypeA),
        ("AAAA", DnsMessageParser.TypeAaaa),
        ("CNAME", 5),
        ("MX", 15),
        ("TXT", 16),
        ("NS", 2),
        ("SOA", 6),
        ("PTR", 12),
        ("SRV", 33),
    };

    [Inject]
    public IDnsRewriteService RewriteService { get; set; } = default!;

    [Inject]
    public ICustomFilterRulesService CustomFilterRulesService { get; set; } = default!;

    [Inject]
    public IDnsAllowlistService AllowlistService { get; set; } = default!;

    [Inject]
    public IDnsFilterService FilterService { get; set; } = default!;

    [Inject]
    public IBrowsingSecurityService BrowsingSecurityService { get; set; } = default!;

    [Inject]
    public IParentalControlService ParentalControlService { get; set; } = default!;

    [Inject]
    public ISafeSearchService SafeSearchService { get; set; } = default!;

    private string HostName { get; set; } = string.Empty;

    private string ClientIdentifier { get; set; } = string.Empty;

    private ushort SelectedRecordType { get; set; } = DnsMessageParser.TypeA;

    private bool IsChecking { get; set; }

    private string? ErrorMessage { get; set; }

    private string? ResultMessage { get; set; }

    private string ResultCssClass { get; set; } = "alert-secondary";

    private void OnRecordTypeChanged(ChangeEventArgs e)
    {
        if (e.Value is string text && ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort parsedType))
        {
            SelectedRecordType = parsedType;
        }
    }

    // Rejoue, sur une requête synthétique construite pour "HostName", le même enchaînement de
    // vérifications que DnsQueryPipeline.ResolveAsync (Réécritures DNS, Règles de filtrage
    // personnalisées, Listes d'autorisation/blocage DNS, Sécurité de navigation, Contrôle parental,
    // Recherche Sécurisée), sans jamais consulter le cache DNS ni les serveurs en amont : ce diagnostic ne
    // doit avoir aucun effet de bord sur la résolution réelle.
    private async Task CheckAsync()
    {
        ErrorMessage = null;
        ResultMessage = null;

        string hostName = HostName.Trim().TrimEnd('.').ToLowerInvariant();

        if (hostName.Length == 0)
        {
            ErrorMessage = "Le nom d'hôte est obligatoire.";
            return;
        }

        // "Identifiant du client" n'est utilisé ici que s'il s'agit d'une adresse IP valide (transmise
        // aux services qui en tiennent compte, comme le ferait le pipeline réel pour l'EDNS Client
        // Subnet) : ce diagnostic ne reproduit pas les réglages par ClientID, non pris en charge ailleurs
        // dans OnyxFilter.
        IPAddress? clientAddress = IPAddress.TryParse(ClientIdentifier.Trim(), out IPAddress? parsedClient) ? parsedClient : null;

        IsChecking = true;

        try
        {
            byte[] query = DnsQueryBuilder.BuildQuery(hostName, SelectedRecordType, out _);

            byte[]? rewriteResponse = await RewriteService.TryBuildRewriteResponseAsync(query, clientAddress, CancellationToken.None);

            if (rewriteResponse is not null)
            {
                SetResult("Réécrite par une réécriture DNS (Réécritures DNS) : la réponse configurée est renvoyée sans consulter les serveurs en amont.", "alert-warning");
                return;
            }

            if (CustomFilterRulesService.IsExcepted(hostName))
            {
                SetResult("Autorisée par une exception des règles de filtrage personnalisées : ne sera jamais bloquée, y compris par les listes de blocage abonnées.", "alert-success");
                return;
            }

            if (CustomFilterRulesService.TryBuildBlockResponse(query, out _, out _))
            {
                SetResult("Bloquée par les règles de filtrage personnalisées.", "alert-danger");
                return;
            }

            if (AllowlistService.IsAllowed(hostName))
            {
                SetResult("Autorisée par une liste d'autorisation DNS : ne sera pas bloquée par les listes de blocage abonnées.", "alert-success");
                return;
            }

            if (FilterService.TryBuildBlockResponse(query, out _, out _))
            {
                SetResult("Bloquée par les listes de blocage DNS abonnées.", "alert-danger");
                return;
            }

            byte[]? browsingSecurityResponse = await BrowsingSecurityService.TryBuildBlockResponseAsync(query, CancellationToken.None);

            if (browsingSecurityResponse is not null)
            {
                SetResult("Bloquée par la Sécurité de navigation d'OnyxFilter.", "alert-danger");
                return;
            }

            byte[]? parentalControlResponse = await ParentalControlService.TryBuildBlockResponseAsync(query, CancellationToken.None);

            if (parentalControlResponse is not null)
            {
                SetResult("Bloquée par le Contrôle parental d'OnyxFilter.", "alert-danger");
                return;
            }

            byte[]? safeSearchResponse = await SafeSearchService.TryBuildRewriteResponseAsync(query, clientAddress, CancellationToken.None);

            if (safeSearchResponse is not null)
            {
                SetResult("Réécrite par la Recherche Sécurisée (redirection vers la variante sécurisée du moteur concerné).", "alert-warning");
                return;
            }

            SetResult("Non filtrée : ce nom d'hôte sera résolu normalement auprès des serveurs en amont configurés.", "alert-success");
        }
        catch (Exception ex)
        {
            ErrorMessage = "Erreur lors de la vérification : " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void SetResult(string message, string cssClass)
    {
        ResultMessage = message;
        ResultCssClass = cssClass;
    }
}
