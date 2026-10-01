using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.ClientLocation;

// Pays et fournisseur d'accès (opérateur du système autonome) d'un client, pour l'interface web et l'API.
// "Provider" : nom lisible (voir ProviderNames) ; "Description" : description brute de l'ASN.
// "CountryCode" vide si le pays n'est pas connu.
public sealed record ClientLocation(string CountryCode, int Asn, string Provider, string Description)
{
    // Drapeau emoji construit à partir du code pays ISO 3166-1 alpha-2 (indicateurs régionaux Unicode).
    public string Flag => CountryCode.Length == 2 && char.IsAsciiLetter(CountryCode[0]) && char.IsAsciiLetter(CountryCode[1])
        ? char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(CountryCode[0]) - 'A') + char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(CountryCode[1]) - 'A')
        : string.Empty;
}

public interface IClientLocationService
{
    // "clientKey" : adresse telle qu'affichée dans le journal et les statistiques (éventuellement
    // anonymisée, ce qui ne change rien en pratique : les plages sont au moins des /24 ou des /48).
    // Null si l'adresse est inconnue, privée, si la base n'est pas encore chargée, ou si l'affichage est
    // désactivé ("Afficher le pays et le fournisseur des clients").
    ClientLocation? Lookup(string clientKey);

    // Pays d'un client pour le filtrage par pays (indépendant du réglage d'affichage). Null si inconnu.
    string? LookupCountry(IPAddress address);

    // Base chargée : sans elle, le filtrage par pays laisse passer tous les clients.
    bool IsLoaded { get; }

    // Codes pays présents dans la base (triés), pour la page "Filtrage par pays".
    IReadOnlyList<string> Countries { get; }
}

// Base « IP vers ASN » publique d'iptoasn.com (domaine public, PDDL) : une plage d'adresses par ligne,
// avec le numéro d'ASN, le pays et le nom de l'opérateur. Fichier téléchargé dans le dossier des données,
// chargé au démarrage depuis ce cache (sans réseau), puis retéléchargé une fois par semaine. Désactivable
// ("Afficher le pays et le fournisseur des clients", /settings/general) : tant que le filtrage par pays
// est lui aussi désactivé, plus aucun téléchargement, et la base est libérée de la mémoire.
// Données gardées en tableaux triés (recherche dichotomique) plutôt qu'en objets par plage : environ
// 25 Mo de mémoire pour ~650 000 plages, acceptable sur un Raspberry Pi à 2 Go.
public sealed class ClientLocationService : BackgroundService, IClientLocationService
{
    private const string SourceUrl = "https://iptoasn.com/data/ip2asn-combined.tsv.gz";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    // Délai entre deux vérifications (et entre deux tentatives après un échec de téléchargement). Un
    // changement du réglage réveille le service immédiatement.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly string cacheFilePath;
    private readonly ILocalSettingsStore settingsStore;
    private readonly ILogger<ClientLocationService> logger;
    private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0);
    private readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    private volatile LocationTable table = LocationTable.Empty;
    private volatile bool displayEnabled;

    public ClientLocationService(IHostEnvironment hostEnvironment, ILocalSettingsStore settingsStore, ILogger<ClientLocationService> logger)
    {
        cacheFilePath = Path.Combine(hostEnvironment.ContentRootPath, "ip2asn-combined.tsv.gz");
        this.settingsStore = settingsStore;
        this.logger = logger;

        settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public bool IsLoaded => table.RangeCount > 0;

    public IReadOnlyList<string> Countries => table.Countries;

    public ClientLocation? Lookup(string clientKey)
    {
        return displayEnabled && IPAddress.TryParse(clientKey, out IPAddress? address) ? LookupAddress(address) : null;
    }

    public string? LookupCountry(IPAddress address)
    {
        string? countryCode = LookupAddress(address)?.CountryCode;
        return string.IsNullOrEmpty(countryCode) ? null : countryCode;
    }

    private ClientLocation? LookupAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return table.Lookup(address);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool enabled;

            try
            {
                AppLocalSettings settings = await settingsStore.LoadAsync();
                displayEnabled = settings.General.ShowClientLocation;
                enabled = displayEnabled || settings.CountryFilter.Mode != CountryFilterMode.Disabled;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Impossible de lire le réglage de localisation des clients.");
                enabled = false;
            }

            if (!enabled)
            {
                table = LocationTable.Empty;
            }
            else
            {
                if (table.RangeCount == 0 && File.Exists(cacheFilePath))
                {
                    await LoadAsync(stoppingToken);
                }

                if (!File.Exists(cacheFilePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFilePath) > MaxAge)
                {
                    try
                    {
                        await DownloadAsync(stoppingToken);
                        await LoadAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Échec du téléchargement de la base de localisation des clients ({Url}).", SourceUrl);
                    }
                }
            }

            try
            {
                await wakeSignal.WaitAsync(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Écriture atomique : le cache précédent n'est remplacé qu'une fois le téléchargement complet.
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        string temporaryFilePath = cacheFilePath + ".tmp";

        using (HttpResponseMessage response = await httpClient.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();

            using Stream networkStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using FileStream fileStream = new FileStream(temporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await networkStream.CopyToAsync(fileStream, cancellationToken);
        }

        File.Move(temporaryFilePath, cacheFilePath, overwrite: true);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using FileStream fileStream = new FileStream(cacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            using StreamReader reader = new StreamReader(gzipStream);

            table = await LocationTable.ParseAsync(reader, cancellationToken);
            logger.LogInformation("Base de localisation des clients chargée : {RangeCount} plage(s) d'adresses.", table.RangeCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Impossible de lire la base de localisation des clients ({Path}).", cacheFilePath);
        }
    }

    private void OnSettingsChanged()
    {
        wakeSignal.Release();
    }

    public override void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        wakeSignal.Dispose();
        httpClient.Dispose();
        base.Dispose();
    }

    // Plages IPv4 et IPv6 dans des tableaux séparés (uint / UInt128), triés par début de plage comme dans
    // le fichier source. Les informations (pays, opérateur) sont dédupliquées : beaucoup de plages
    // partagent le même opérateur.
    internal sealed class LocationTable
    {
        public static readonly LocationTable Empty = new LocationTable();

        private readonly List<uint> v4Starts = new List<uint>();
        private readonly List<uint> v4Ends = new List<uint>();
        private readonly List<int> v4Infos = new List<int>();
        private readonly List<UInt128> v6Starts = new List<UInt128>();
        private readonly List<UInt128> v6Ends = new List<UInt128>();
        private readonly List<int> v6Infos = new List<int>();
        private readonly List<ClientLocation> infos = new List<ClientLocation>();

        public int RangeCount => v4Starts.Count + v6Starts.Count;

        public IReadOnlyList<string> Countries { get; private set; } = Array.Empty<string>();

        public static async Task<LocationTable> ParseAsync(TextReader reader, CancellationToken cancellationToken)
        {
            LocationTable result = new LocationTable();
            Dictionary<(string, string, string), int> infoIndexes = new Dictionary<(string, string, string), int>();
            string? line;

            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                string[] fields = line.Split('\t');

                // Plages non annoncées : ASN 0, pays "None". Rien à afficher pour ces adresses (dont
                // les adresses privées du réseau local).
                if (fields.Length < 5
                    || fields[2] == "0"
                    || !IPAddress.TryParse(fields[0], out IPAddress? start)
                    || !IPAddress.TryParse(fields[1], out IPAddress? end)
                    || start.AddressFamily != end.AddressFamily)
                {
                    continue;
                }

                (string, string, string) key = (fields[2], fields[3], fields[4]);

                if (!infoIndexes.TryGetValue(key, out int infoIndex))
                {
                    infoIndex = result.infos.Count;
                    int asn = int.TryParse(fields[2], out int parsedAsn) ? parsedAsn : 0;

                    // Pays inconnu : "None" ou "Unknown" dans le fichier source.
                    string countryCode = fields[3].Length == 2 ? fields[3] : string.Empty;
                    result.infos.Add(new ClientLocation(countryCode, asn, ProviderNames.Resolve(asn, fields[4]), fields[4]));
                    infoIndexes.Add(key, infoIndex);
                }

                if (start.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.v4Starts.Add(ToUInt32(start));
                    result.v4Ends.Add(ToUInt32(end));
                    result.v4Infos.Add(infoIndex);
                }
                else
                {
                    result.v6Starts.Add(ToUInt128(start));
                    result.v6Ends.Add(ToUInt128(end));
                    result.v6Infos.Add(infoIndex);
                }
            }

            result.v4Starts.TrimExcess();
            result.v4Ends.TrimExcess();
            result.v4Infos.TrimExcess();
            result.v6Starts.TrimExcess();
            result.v6Ends.TrimExcess();
            result.v6Infos.TrimExcess();

            SortedSet<string> countries = new SortedSet<string>(StringComparer.Ordinal);

            foreach (ClientLocation info in result.infos)
            {
                if (info.CountryCode.Length != 0)
                {
                    countries.Add(info.CountryCode);
                }
            }

            result.Countries = new List<string>(countries);
            return result;
        }

        public ClientLocation? Lookup(IPAddress address)
        {
            int infoIndex = address.AddressFamily == AddressFamily.InterNetwork
                ? Find(v4Starts, v4Ends, v4Infos, ToUInt32(address))
                : Find(v6Starts, v6Ends, v6Infos, ToUInt128(address));

            return infoIndex < 0 ? null : infos[infoIndex];
        }

        // Dernière plage dont le début est <= adresse, retenue si l'adresse ne dépasse pas sa fin.
        private static int Find<T>(List<T> starts, List<T> ends, List<int> infoIndexes, T value) where T : IComparable<T>
        {
            int index = starts.BinarySearch(value);

            if (index < 0)
            {
                index = ~index - 1;
            }

            return index >= 0 && value.CompareTo(ends[index]) <= 0 ? infoIndexes[index] : -1;
        }

        private static uint ToUInt32(IPAddress address)
        {
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private static UInt128 ToUInt128(IPAddress address)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(bytes);
        }
    }
}
