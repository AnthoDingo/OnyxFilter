using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services;
using OnyxFilter.Services.Encryption;

namespace OnyxFilter.Components.Pages;

public partial class SetupGuide : ComponentBase
{
    [Inject]
    public ILocalSettingsStore SettingsStore { get; set; } = default!;

    [Inject]
    public IHttpsEndpointService HttpsEndpointService { get; set; } = default!;

    [Inject]
    public NavigationManager Navigation { get; set; } = default!;

    private IReadOnlyList<string> Ipv4Addresses { get; set; } = Array.Empty<string>();

    private IReadOnlyList<string> Ipv6Addresses { get; set; } = Array.Empty<string>();

    private EncryptionSettingsData Encryption { get; set; } = new EncryptionSettingsData();

    // Adresse reprise dans les étapes : celle de la page si elle en est une (c'est celle que le réseau
    // utilise pour joindre le serveur), sinon la première adresse IPv4 trouvée.
    private string ServerAddress { get; set; } = string.Empty;

    // DNS chiffré utilisable par les appareils : certificat valide en place (HTTPS actif) et nom du
    // serveur renseigné, puisque les appareils vérifient ce nom dans le certificat.
    private bool EncryptedDnsAvailable { get; set; }

    private string DohUrl => HttpsEndpointService.Status.Port == 443
        ? $"https://{Encryption.ServerName}/dns-query"
        : $"https://{Encryption.ServerName}:{HttpsEndpointService.Status.Port}/dns-query";

    protected override async Task OnInitializedAsync()
    {
        AppLocalSettings settings = await SettingsStore.LoadAsync();
        Encryption = settings.Encryption;
        EncryptedDnsAvailable = HttpsEndpointService.Status.Active && !string.IsNullOrWhiteSpace(Encryption.ServerName);

        List<IPAddress> addresses = GetLocalAddresses();
        Ipv4Addresses = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Select(address => address.ToString()).ToList();
        Ipv6Addresses = addresses.Where(address => address.AddressFamily == AddressFamily.InterNetworkV6).Select(address => address.ToString()).ToList();

        string pageHost = new Uri(Navigation.BaseUri).Host;
        ServerAddress = Ipv4Addresses.Contains(pageHost) ? pageHost : Ipv4Addresses.FirstOrDefault() ?? L["adresse IP du serveur"];
    }

    // Adresses des interfaces actives, hors boucle locale et adresses de lien local (169.254.x.x, fe80::),
    // que les autres appareils du réseau ne peuvent pas utiliser comme serveur DNS. Seules les interfaces
    // dotées d'une passerelle (le vrai réseau local) sont retenues, ce qui écarte les ponts virtuels
    // (Docker, machines virtuelles) ; à défaut de passerelle, toutes les interfaces.
    private static List<IPAddress> GetLocalAddresses()
    {
        try
        {
            List<NetworkInterface> interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            List<NetworkInterface> withGateway = interfaces.Where(nic => nic.GetIPProperties().GatewayAddresses.Count > 0).ToList();

            return (withGateway.Count > 0 ? withGateway : interfaces)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(address => !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal && !address.IsIPv4MappedToIPv6)
                .Where(address => address.AddressFamily != AddressFamily.InterNetwork || !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return new List<IPAddress>();
        }
    }

    // Texte traduit contenant du balisage (<strong>, <code>) : seules les valeurs insérées sont encodées.
    private MarkupString Html(string key, params string[] values)
    {
        return new MarkupString(L[key, values.Select(value => (object)WebUtility.HtmlEncode(value)).ToArray()].Value);
    }
}
