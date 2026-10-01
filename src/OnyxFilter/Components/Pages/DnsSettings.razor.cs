using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.DnsForwarding;

namespace OnyxFilter.Components.Pages;

public partial class DnsSettings : ComponentBase
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IDnsCache DnsCache { get; set; } = default!;

    private string UpstreamServersText { get; set; } = string.Empty;

    private string FallbackServersText { get; set; } = string.Empty;

    private string BootstrapServersText { get; set; } = string.Empty;

    private string ReversePrivateServersText { get; set; } = string.Empty;

    private UpstreamResolutionMode ResolutionMode { get; set; } = UpstreamResolutionMode.LoadBalancing;

    private bool UsePrivateReverseResolvers { get; set; }

    private bool EnableClientIpReverseResolution { get; set; }

    // Panneau "Configuration du serveur DNS".
    private int RateLimitPerSecond { get; set; } = 20;

    private int RateLimitSubnetLengthIpv4 { get; set; } = 24;

    private int RateLimitSubnetLengthIpv6 { get; set; } = 56;

    private bool EnableEdnsClientSubnet { get; set; }

    private bool EnableDnssec { get; set; }

    private bool DisableIpv6Resolution { get; set; }

    private DnsBlockingMode BlockingMode { get; set; } = DnsBlockingMode.Default;

    private string CustomBlockingIpv4 { get; set; } = string.Empty;

    private string CustomBlockingIpv6 { get; set; } = string.Empty;

    // Panneau "Configuration du cache DNS".
    private bool EnableCache { get; set; }

    private long CacheSizeBytes { get; set; }

    private int CacheTtlMinSeconds { get; set; }

    private int CacheTtlMaxSeconds { get; set; }

    private bool OptimisticCache { get; set; }

    // Panneau "Paramètres d'accès".
    private string AllowedClientsText { get; set; } = string.Empty;

    private string DisallowedClientsText { get; set; } = string.Empty;

    private string AlwaysAllowedClientsText { get; set; } = string.Empty;

    private string DisallowedDomainsText { get; set; } = string.Empty;

    private string? DnsServersStatusMessage { get; set; }

    private string? ServerConfigStatusMessage { get; set; }

    private string? CacheStatusMessage { get; set; }

    private string? AccessStatusMessage { get; set; }

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        ApplyData(settings.Dns);
    }

    private void ApplyData(DnsSettingsData data)
    {
        UpstreamServersText = JoinLines(data.UpstreamServers);
        FallbackServersText = JoinLines(data.FallbackServers);
        BootstrapServersText = JoinLines(data.BootstrapServers);
        ReversePrivateServersText = data.ReversePrivateServersText;
        ResolutionMode = data.ResolutionMode;
        UsePrivateReverseResolvers = data.UsePrivateReverseResolvers;
        EnableClientIpReverseResolution = data.EnableClientIpReverseResolution;
        RateLimitPerSecond = data.RateLimitPerSecond;
        RateLimitSubnetLengthIpv4 = data.RateLimitSubnetLengthIpv4;
        RateLimitSubnetLengthIpv6 = data.RateLimitSubnetLengthIpv6;
        EnableEdnsClientSubnet = data.EnableEdnsClientSubnet;
        EnableDnssec = data.EnableDnssec;
        DisableIpv6Resolution = data.DisableIpv6Resolution;
        BlockingMode = data.BlockingMode;
        CustomBlockingIpv4 = data.CustomBlockingIpv4;
        CustomBlockingIpv6 = data.CustomBlockingIpv6;
        EnableCache = data.EnableCache;
        CacheSizeBytes = data.CacheSizeBytes;
        CacheTtlMinSeconds = data.CacheTtlMinSeconds;
        CacheTtlMaxSeconds = data.CacheTtlMaxSeconds;
        OptimisticCache = data.OptimisticCache;
        AllowedClientsText = JoinLines(data.AllowedClients);
        DisallowedClientsText = JoinLines(data.DisallowedClients);
        AlwaysAllowedClientsText = JoinLines(data.AlwaysAllowedClients);
        DisallowedDomainsText = JoinLines(data.DisallowedDomains);
    }

    private DnsSettingsData BuildData()
    {
        return new DnsSettingsData
        {
            UpstreamServers = SplitLines(UpstreamServersText),
            FallbackServers = SplitLines(FallbackServersText),
            BootstrapServers = SplitLines(BootstrapServersText),
            ReversePrivateServersText = ReversePrivateServersText,
            ResolutionMode = ResolutionMode,
            UsePrivateReverseResolvers = UsePrivateReverseResolvers,
            EnableClientIpReverseResolution = EnableClientIpReverseResolution,
            RateLimitPerSecond = RateLimitPerSecond,
            RateLimitSubnetLengthIpv4 = RateLimitSubnetLengthIpv4,
            RateLimitSubnetLengthIpv6 = RateLimitSubnetLengthIpv6,
            EnableEdnsClientSubnet = EnableEdnsClientSubnet,
            EnableDnssec = EnableDnssec,
            DisableIpv6Resolution = DisableIpv6Resolution,
            BlockingMode = BlockingMode,
            CustomBlockingIpv4 = CustomBlockingIpv4,
            CustomBlockingIpv6 = CustomBlockingIpv6,
            EnableCache = EnableCache,
            CacheSizeBytes = CacheSizeBytes,
            CacheTtlMinSeconds = CacheTtlMinSeconds,
            CacheTtlMaxSeconds = CacheTtlMaxSeconds,
            OptimisticCache = OptimisticCache,
            AllowedClients = SplitLines(AllowedClientsText),
            DisallowedClients = SplitLines(DisallowedClientsText),
            AlwaysAllowedClients = SplitLines(AlwaysAllowedClientsText),
            DisallowedDomains = SplitLines(DisallowedDomainsText),
        };
    }

    private static string[] SplitLines(string text)
    {
        return text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static string JoinLines(IReadOnlyList<string> lines)
    {
        return string.Join('\n', lines);
    }

    private async Task<string?> PersistAsync()
    {
        try
        {
            DnsSettingsData data = BuildData();
            await SettingsStore.UpdateAsync(settings => settings.Dns = data);
            return L["Paramètres enregistrés."];
        }
        catch (Exception ex)
        {
            return L["Erreur lors de l'enregistrement : {0}", ex.Message];
        }
    }

    private void SetResolutionMode(UpstreamResolutionMode mode)
    {
        ResolutionMode = mode;
    }

    private void OnUsePrivateReverseResolversChanged(ChangeEventArgs e)
    {
        UsePrivateReverseResolvers = e.Value is bool value && value;
    }

    private void OnEnableClientIpReverseResolutionChanged(ChangeEventArgs e)
    {
        EnableClientIpReverseResolution = e.Value is bool value && value;
    }

    private void OnEnableEdnsClientSubnetChanged(ChangeEventArgs e)
    {
        EnableEdnsClientSubnet = e.Value is bool value && value;
    }

    private void OnEnableDnssecChanged(ChangeEventArgs e)
    {
        EnableDnssec = e.Value is bool value && value;
    }

    private void OnDisableIpv6ResolutionChanged(ChangeEventArgs e)
    {
        DisableIpv6Resolution = e.Value is bool value && value;
    }

    private void SetBlockingMode(DnsBlockingMode mode)
    {
        BlockingMode = mode;
    }

    private void OnEnableCacheChanged(ChangeEventArgs e)
    {
        EnableCache = e.Value is bool value && value;
    }

    private void OnOptimisticCacheChanged(ChangeEventArgs e)
    {
        OptimisticCache = e.Value is bool value && value;
    }

    private async Task ValidateDnsServerSettingsAsync()
    {
        // TODO : valider les serveurs en amont/repli/amorçage/inverses une fois le moteur DNS disponible.
        DnsServersStatusMessage = await PersistAsync();
    }

    private Task ResetDnsServerSettingsAsync()
    {
        DnsSettingsData defaults = new DnsSettingsData();
        UpstreamServersText = JoinLines(defaults.UpstreamServers);
        FallbackServersText = JoinLines(defaults.FallbackServers);
        BootstrapServersText = JoinLines(defaults.BootstrapServers);
        ReversePrivateServersText = defaults.ReversePrivateServersText;
        ResolutionMode = defaults.ResolutionMode;
        UsePrivateReverseResolvers = defaults.UsePrivateReverseResolvers;
        EnableClientIpReverseResolution = defaults.EnableClientIpReverseResolution;

        return Task.CompletedTask;
    }

    private async Task SaveDnsServerConfigurationAsync()
    {
        // TODO : appliquer la configuration au moteur DNS une fois disponible.
        ServerConfigStatusMessage = await PersistAsync();
    }

    private async Task SaveCacheConfigurationAsync()
    {
        // TODO : appliquer la configuration du cache au moteur DNS une fois disponible.
        CacheStatusMessage = await PersistAsync();
    }

    private Task ClearCacheAsync()
    {
        DnsCache.Clear();
        CacheStatusMessage = L["Cache vidé."];
        return Task.CompletedTask;
    }

    private async Task SaveAccessSettingsAsync()
    {
        // L'enregistrement déclenche SettingsChanged : DnsAccessControl recharge aussitôt les règles.
        AccessStatusMessage = await PersistAsync();
    }
}
