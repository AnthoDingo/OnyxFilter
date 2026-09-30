using System;
using System.Collections.Generic;

namespace OnyxFilter.Models.Settings;

// Reflète les champs de la page "Paramètres DNS" (/settings/dns).
public sealed class DnsSettingsData
{
    public IReadOnlyList<string> UpstreamServers { get; set; } = new[] { "https://dns.cloudflare.com/dns-query", "https://dns.google/dns-query" };

    public IReadOnlyList<string> FallbackServers { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> BootstrapServers { get; set; } = new[] { "2606:4700:4700::1111", "2606:4700:4700::1001", "1.1.1.1", "1.0.0.1" };

    public string ReversePrivateServersText { get; set; } = string.Empty;

    public UpstreamResolutionMode ResolutionMode { get; set; } = UpstreamResolutionMode.LoadBalancing;

    public bool UsePrivateReverseResolvers { get; set; }

    public bool EnableClientIpReverseResolution { get; set; }

    public int RateLimitPerSecond { get; set; } = 20;

    public int RateLimitSubnetLengthIpv4 { get; set; } = 24;

    public int RateLimitSubnetLengthIpv6 { get; set; } = 56;

    public bool EnableEdnsClientSubnet { get; set; }

    public bool EnableDnssec { get; set; }

    public bool DisableIpv6Resolution { get; set; }

    public DnsBlockingMode BlockingMode { get; set; } = DnsBlockingMode.Default;

    public string CustomBlockingIpv4 { get; set; } = string.Empty;

    public string CustomBlockingIpv6 { get; set; } = string.Empty;

    public bool EnableCache { get; set; }

    public long CacheSizeBytes { get; set; }

    public int CacheTtlMinSeconds { get; set; }

    public int CacheTtlMaxSeconds { get; set; }

    public bool OptimisticCache { get; set; }

    public IReadOnlyList<string> AllowedClients { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> DisallowedClients { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> DisallowedDomains { get; set; } = Array.Empty<string>();
}
