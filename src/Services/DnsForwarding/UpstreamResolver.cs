using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Transmet les requêtes DNS reçues par DnsProxyService aux serveurs en amont configurés sur la page
// "Paramètres DNS", en respectant (de façon simplifiée pour le moment) le mode de résolution choisi.
// La résolution des noms d'hôte des serveurs en amont (ex. "dns.cloudflare.com") passe systématiquement
// par IBootstrapResolver (Serveurs DNS d'amorçage), jamais par le résolveur du système d'exploitation.
public sealed class UpstreamResolver : IUpstreamResolver, IDisposable
{
    // Délai maximal accordé à un serveur en amont pour répondre. TODO : rendre configurable.
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    private readonly ILocalSettingsStore settingsStore;
    private readonly IBootstrapResolver bootstrapResolver;
    private readonly ILogger<UpstreamResolver> logger;
    private readonly HttpClient httpClient;
    private readonly object syncRoot = new object();

    // Taille de tampon UDP annoncée dans les enregistrements OPT que nous construisons (EDNS, RFC 6891) :
    // valeur usuelle et sûre pour éviter la fragmentation IP.
    private const ushort EdnsUdpPayloadSize = 1232;

    private IReadOnlyList<UpstreamServer> upstreamServers = Array.Empty<UpstreamServer>();
    private UpstreamResolutionMode resolutionMode = UpstreamResolutionMode.LoadBalancing;
    private int roundRobinCounter;

    private bool disableIpv6Resolution;
    private bool enableDnssec;
    private bool enableEdnsClientSubnet;
    private int ecsSubnetLengthIpv4 = 24;
    private int ecsSubnetLengthIpv6 = 56;

    public UpstreamResolver(ILocalSettingsStore settingsStore, IBootstrapResolver bootstrapResolver, ILogger<UpstreamResolver> logger)
    {
        this.settingsStore = settingsStore;
        this.bootstrapResolver = bootstrapResolver;
        this.logger = logger;

        // ConnectCallback prend le contrôle de l'établissement de la connexion TCP sous-jacente : l'hôte
        // (context.DnsEndPoint.Host) est résolu via les serveurs d'amorçage plutôt que par le résolveur
        // système, tout en conservant le nom d'origine pour le SNI/la validation du certificat TLS (gérés
        // par SocketsHttpHandler lui-même par-dessus le flux retourné ici).
        SocketsHttpHandler socketsHandler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress? address = await bootstrapResolver.ResolveAsync(context.DnsEndPoint.Host, cancellationToken);

                if (address is null)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        httpClient = new HttpClient(socketsHandler);
        this.settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await bootstrapResolver.InitializeAsync(cancellationToken);
        await ReloadAsync();
    }

    public bool IsClientSubnetEnabled
    {
        get
        {
            lock (syncRoot)
            {
                return enableEdnsClientSubnet;
            }
        }
    }

    public async Task<UpstreamResolutionResult> ResolveAsync(byte[] query, IPAddress? clientAddress, CancellationToken cancellationToken)
    {
        IReadOnlyList<UpstreamServer> servers;
        UpstreamResolutionMode mode;
        bool disableIpv6;
        bool dnssec;
        bool ecs;
        int prefixIpv4;
        int prefixIpv6;

        lock (syncRoot)
        {
            servers = upstreamServers;
            mode = resolutionMode;
            disableIpv6 = disableIpv6Resolution;
            dnssec = enableDnssec;
            ecs = enableEdnsClientSubnet;
            prefixIpv4 = ecsSubnetLengthIpv4;
            prefixIpv6 = ecsSubnetLengthIpv6;
        }

        // "Désactiver la résolution IPv6" : toute requête AAAA reçoit une réponse vide (NOERROR, sans
        // enregistrement), sans jamais être transmise aux serveurs en amont (donc sans serveur/temps de
        // réponse à rapporter aux statistiques).
        if (disableIpv6 && DnsMessageParser.TryReadQuestionType(query, out ushort queryType) && queryType == DnsMessageParser.TypeAaaa)
        {
            return new UpstreamResolutionResult(DnsMessageParser.BuildEmptyResponse(query), upstreamServer: null, responseTimeMs: 0);
        }

        if (servers.Count == 0)
        {
            logger.LogWarning("Aucun serveur DNS en amont configuré : requête abandonnée.");
            return UpstreamResolutionResult.Empty;
        }

        byte[] preparedQuery = PrepareQuery(query, clientAddress, dnssec, ecs, prefixIpv4, prefixIpv6);

        if (mode == UpstreamResolutionMode.LoadBalancing)
        {
            return await ResolveWithFailoverAsync(servers, preparedQuery, cancellationToken);
        }

        return await ResolveWithRaceAsync(servers, preparedQuery, cancellationToken);
    }

    // Applique "Activer DNSSEC" (bit DO) et "Activer le sous-réseau client (EDNS)" (option ECS, RFC
    // 7871) à la requête envoyée en amont, via le pseudo-enregistrement OPT. La vérification DNSSEC
    // elle-même (validation cryptographique des signatures RRSIG) est déléguée aux serveurs en amont :
    // OnyxFilter demande la validation (DO) et relaie tel quel le bit AD (Authenticated Data) de leur
    // réponse au client, sans revalider la chaîne de confiance localement.
    private static byte[] PrepareQuery(byte[] query, IPAddress? clientAddress, bool dnssec, bool ecs, int prefixIpv4, int prefixIpv6)
    {
        if (!dnssec && !ecs)
        {
            return query;
        }

        byte[]? ecsOption = ecs && clientAddress is not null
            ? BuildEcsOption(clientAddress, prefixIpv4, prefixIpv6)
            : null;

        if (DnsMessageParser.TryFindOptRecord(query, out int rrOffset, out int rdataLength))
        {
            byte[] result = (byte[])query.Clone();

            if (dnssec)
            {
                // Bit DO (DNSSEC OK) : bit de poids fort du premier octet du champ "Z" (RFC 6891 §6.1.3).
                result[rrOffset + 6] |= 0x80;
            }

            bool optIsLastInMessage = rrOffset + 10 + rdataLength == query.Length;

            if (ecsOption is not null && optIsLastInMessage)
            {
                int newRdataLength = rdataLength + ecsOption.Length;
                byte[] extended = new byte[result.Length + ecsOption.Length];
                Array.Copy(result, extended, result.Length);
                Array.Copy(ecsOption, 0, extended, result.Length, ecsOption.Length);
                extended[rrOffset + 8] = (byte)(newRdataLength >> 8);
                extended[rrOffset + 9] = (byte)(newRdataLength & 0xFF);
                return extended;
            }

            // Un enregistrement OPT existant mais non terminal (suivi d'autres données) ne peut pas être
            // étendu sans décaler le reste du message : le sous-réseau client n'est alors pas transmis
            // pour cette requête, mais le bit DO ci-dessus reste appliqué.
            return result;
        }

        if (!dnssec && ecsOption is null)
        {
            return query;
        }

        byte[] optRecord = BuildOptRecord(dnssec, ecsOption);
        byte[] queryWithOpt = new byte[query.Length + optRecord.Length];
        Array.Copy(query, queryWithOpt, query.Length);
        Array.Copy(optRecord, 0, queryWithOpt, query.Length, optRecord.Length);

        int additionalCount = (queryWithOpt[10] << 8) | queryWithOpt[11];
        additionalCount += 1;
        queryWithOpt[10] = (byte)(additionalCount >> 8);
        queryWithOpt[11] = (byte)(additionalCount & 0xFF);

        return queryWithOpt;
    }

    // Construit un nouveau pseudo-enregistrement OPT complet (nom racine + TYPE=41 + CLASS=taille de
    // tampon UDP + TTL portant le bit DO + RDATA optionnelle).
    private static byte[] BuildOptRecord(bool setDoBit, byte[]? ecsOption)
    {
        int rdataLength = ecsOption?.Length ?? 0;
        byte[] record = new byte[1 + 2 + 2 + 4 + 2 + rdataLength];
        int index = 0;

        record[index++] = 0x00; // NAME = racine.
        record[index++] = 0x00;
        record[index++] = 0x29; // TYPE = 41 (OPT).
        record[index++] = (byte)(EdnsUdpPayloadSize >> 8);
        record[index++] = (byte)(EdnsUdpPayloadSize & 0xFF); // CLASS = taille de tampon UDP.
        record[index++] = 0x00; // Extended RCODE.
        record[index++] = 0x00; // Version EDNS.
        record[index++] = (byte)(setDoBit ? 0x80 : 0x00); // Bit DO (poids fort du champ Z).
        record[index++] = 0x00; // Reste du champ Z.
        record[index++] = (byte)(rdataLength >> 8);
        record[index++] = (byte)(rdataLength & 0xFF);

        if (ecsOption is not null)
        {
            Array.Copy(ecsOption, 0, record, index, ecsOption.Length);
        }

        return record;
    }

    // Construit l'option EDNS Client Subnet (RFC 7871) : famille, longueur de préfixe et adresse
    // tronquée au sous-réseau (jamais l'adresse IP complète du client), en réutilisant les longueurs de
    // préfixe déjà configurées pour la limite de requêtes (RateLimitSubnetLengthIpv4/Ipv6) plutôt que
    // d'introduire un réglage dédié.
    private static byte[]? BuildEcsOption(IPAddress clientAddress, int prefixIpv4, int prefixIpv6)
    {
        bool isIpv4 = clientAddress.AddressFamily == AddressFamily.InterNetwork || clientAddress.IsIPv4MappedToIPv6;
        IPAddress effectiveAddress = isIpv4 && clientAddress.IsIPv4MappedToIPv6 ? clientAddress.MapToIPv4() : clientAddress;
        byte[] fullAddressBytes = effectiveAddress.GetAddressBytes();

        ushort family = isIpv4 ? (ushort)1 : (ushort)2;
        int maxPrefixLength = fullAddressBytes.Length * 8;
        int prefixLength = Math.Clamp(isIpv4 ? prefixIpv4 : prefixIpv6, 0, maxPrefixLength);
        int addressByteCount = (prefixLength + 7) / 8;

        byte[] maskedAddress = new byte[addressByteCount];
        Array.Copy(fullAddressBytes, maskedAddress, addressByteCount);

        int remainderBits = prefixLength % 8;

        if (remainderBits != 0 && addressByteCount > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainderBits));
            maskedAddress[addressByteCount - 1] &= mask;
        }

        int optionDataLength = 2 + 1 + 1 + addressByteCount; // FAMILY + SOURCE PREFIX + SCOPE PREFIX + ADDRESS
        byte[] option = new byte[4 + optionDataLength]; // OPTION-CODE (2) + OPTION-LENGTH (2) + données ci-dessus.

        option[0] = 0x00;
        option[1] = 0x08; // OPTION-CODE = 8 (edns-client-subnet).
        option[2] = (byte)(optionDataLength >> 8);
        option[3] = (byte)(optionDataLength & 0xFF);
        option[4] = (byte)(family >> 8);
        option[5] = (byte)(family & 0xFF);
        option[6] = (byte)prefixLength; // SOURCE PREFIX-LENGTH.
        option[7] = 0x00; // SCOPE PREFIX-LENGTH = 0 (imposé au demandeur, RFC 7871 §6.1).
        Array.Copy(maskedAddress, 0, option, 8, addressByteCount);

        return option;
    }

    private void OnSettingsChanged()
    {
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            List<UpstreamServer> parsed = new List<UpstreamServer>();

            foreach (string line in settings.Dns.UpstreamServers)
            {
                UpstreamServer? server = UpstreamServerParser.TryParse(line, out string? warning);

                if (server is not null)
                {
                    parsed.Add(server);
                }
                else if (!string.IsNullOrEmpty(warning))
                {
                    logger.LogWarning("Serveur DNS en amont ignoré ({Line}) : {Warning}", line, warning);
                }
            }

            lock (syncRoot)
            {
                upstreamServers = parsed;
                resolutionMode = settings.Dns.ResolutionMode;
                disableIpv6Resolution = settings.Dns.DisableIpv6Resolution;
                enableDnssec = settings.Dns.EnableDnssec;
                enableEdnsClientSubnet = settings.Dns.EnableEdnsClientSubnet;
                ecsSubnetLengthIpv4 = Math.Clamp(settings.Dns.RateLimitSubnetLengthIpv4, 0, 32);
                ecsSubnetLengthIpv6 = Math.Clamp(settings.Dns.RateLimitSubnetLengthIpv6, 0, 128);
            }

            logger.LogInformation(
                "{Count} serveur(s) DNS en amont chargé(s) (mode : {Mode}, DNSSEC={Dnssec}, EDNS Client Subnet={Ecs}, IPv6 désactivé={Ipv6Disabled}).",
                parsed.Count,
                settings.Dns.ResolutionMode,
                settings.Dns.EnableDnssec,
                settings.Dns.EnableEdnsClientSubnet,
                settings.Dns.DisableIpv6Resolution);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de charger les serveurs DNS en amont depuis appsettings.local.json.");
        }
    }

    // Mode "Équilibrage de charge" : un seul serveur interrogé à la fois, avec repli sur le suivant
    // en cas d'échec. La rotation démarre à un point différent à chaque requête.
    private async Task<UpstreamResolutionResult> ResolveWithFailoverAsync(IReadOnlyList<UpstreamServer> servers, byte[] query, CancellationToken cancellationToken)
    {
        int startIndex = Interlocked.Increment(ref roundRobinCounter);

        for (int attempt = 0; attempt < servers.Count; attempt++)
        {
            UpstreamServer server = servers[(startIndex + attempt) % servers.Count];

            try
            {
                using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(QueryTimeout);

                Stopwatch stopwatch = Stopwatch.StartNew();
                byte[]? response = await QueryServerAsync(server, query, timeoutSource.Token);
                stopwatch.Stop();

                if (response is not null)
                {
                    return new UpstreamResolutionResult(response, server.ToString(), stopwatch.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec de la requête DNS vers le serveur en amont {Server}.", server);
            }
        }

        return UpstreamResolutionResult.Empty;
    }

    // Modes "Requêtes en parallèle" et "Adresse IP la plus rapide" : tous les serveurs sont interrogés
    // simultanément, la première réponse valide est utilisée.
    // TODO : pour "Adresse IP la plus rapide", AdGuard Home mesure explicitement le temps de connexion
    // TCP de chaque serveur plutôt que de ne retenir que la première réponse arrivée ; cette nuance n'est
    // pas encore implémentée.
    private async Task<UpstreamResolutionResult> ResolveWithRaceAsync(IReadOnlyList<UpstreamServer> servers, byte[] query, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(QueryTimeout);

        List<Task<RaceAttempt>> pending = servers
            .Select(server => QueryServerSafeAsync(server, query, timeoutSource.Token))
            .ToList();

        while (pending.Count > 0)
        {
            Task<RaceAttempt> completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            RaceAttempt result = await completed;

            if (result.Response is not null)
            {
                return new UpstreamResolutionResult(result.Response, result.Server.ToString(), result.ElapsedMilliseconds);
            }
        }

        return UpstreamResolutionResult.Empty;
    }

    // Résultat interne d'une tentative en mode "course" : conserve le serveur interrogé pour pouvoir
    // l'attribuer à la réponse gagnante (statistiques "Top amonts"/temps de réponse).
    private readonly record struct RaceAttempt(UpstreamServer Server, byte[]? Response, long ElapsedMilliseconds);

    private async Task<RaceAttempt> QueryServerSafeAsync(UpstreamServer server, byte[] query, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            byte[]? response = await QueryServerAsync(server, query, cancellationToken);
            stopwatch.Stop();
            return new RaceAttempt(server, response, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec de la requête DNS vers le serveur en amont {Server}.", server);
            return new RaceAttempt(server, null, stopwatch.ElapsedMilliseconds);
        }
    }

    private Task<byte[]?> QueryServerAsync(UpstreamServer server, byte[] query, CancellationToken cancellationToken)
    {
        return server.Transport switch
        {
            UpstreamTransport.Udp => QueryUdpAsync(server, query, cancellationToken),
            UpstreamTransport.Tcp => QueryTcpAsync(server, query, cancellationToken, useTls: false),
            UpstreamTransport.Tls => QueryTcpAsync(server, query, cancellationToken, useTls: true),
            UpstreamTransport.Https => QueryHttpsAsync(server, query, cancellationToken),
            _ => Task.FromResult<byte[]?>(null),
        };
    }

    private async Task<byte[]?> QueryUdpAsync(UpstreamServer server, byte[] query, CancellationToken cancellationToken)
    {
        IPAddress? address = await bootstrapResolver.ResolveAsync(server.Host, cancellationToken);

        if (address is null)
        {
            return null;
        }

        using UdpClient client = new UdpClient(address.AddressFamily);

        await client.SendAsync(query, query.Length, new IPEndPoint(address, server.Port)).WaitAsync(cancellationToken);
        UdpReceiveResult result = await client.ReceiveAsync().WaitAsync(cancellationToken);

        return result.Buffer;
    }

    private async Task<byte[]?> QueryTcpAsync(UpstreamServer server, byte[] query, CancellationToken cancellationToken, bool useTls)
    {
        int port = server.Port > 0 ? server.Port : (useTls ? 853 : 53);

        IPAddress? address = await bootstrapResolver.ResolveAsync(server.Host, cancellationToken);

        if (address is null)
        {
            return null;
        }

        using TcpClient client = new TcpClient();
        await client.ConnectAsync(address, port).WaitAsync(cancellationToken);

        Stream stream = client.GetStream();
        SslStream? sslStream = null;

        try
        {
            if (useTls)
            {
                sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = server.Host,
                    EnabledSslProtocols = SslProtocols.None,
                }, cancellationToken);
                stream = sslStream;
            }

            byte[] lengthPrefix = { (byte)(query.Length >> 8), (byte)(query.Length & 0xFF) };

            await stream.WriteAsync(lengthPrefix, cancellationToken);
            await stream.WriteAsync(query, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            byte[] responseLengthBuffer = new byte[2];
            await ReadExactAsync(stream, responseLengthBuffer, cancellationToken);
            int responseLength = (responseLengthBuffer[0] << 8) | responseLengthBuffer[1];

            byte[] response = new byte[responseLength];
            await ReadExactAsync(stream, response, cancellationToken);

            return response;
        }
        finally
        {
            sslStream?.Dispose();
        }
    }

    private async Task<byte[]?> QueryHttpsAsync(UpstreamServer server, byte[] query, CancellationToken cancellationToken)
    {
        string path = string.IsNullOrEmpty(server.Path) ? "/dns-query" : server.Path;
        int port = server.Port > 0 ? server.Port : 443;

        UriBuilder uriBuilder = new UriBuilder("https", server.Host, port, path);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uriBuilder.Uri)
        {
            Content = new ByteArrayContent(query),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);

            if (read == 0)
            {
                throw new IOException("Connexion fermée par le serveur en amont avant la fin de la réponse.");
            }

            offset += read;
        }
    }

    public void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        httpClient.Dispose();
    }
}
