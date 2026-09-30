using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.DnsForwarding;

// Service DNS "en clair" : écoute sur le port 53 (UDP et TCP), applique les paramètres d'accès (clients
// autorisés/interdits, domaines interdits) et la limite de requêtes, puis délègue la résolution à
// IDnsQueryPipeline (filtres de blocage, cache DNS, serveurs en amont, statistiques). La même logique
// de résolution est partagée avec le service DNS-over-TLS (DnsOverTlsService).
public sealed class DnsProxyService : BackgroundService
{
    private const int DnsPort = 53;
    private const int MaxTcpMessageSize = 4096;

    private readonly IDnsQueryPipeline queryPipeline;
    private readonly IDnsRateLimiter rateLimiter;
    private readonly IDnsAccessControl accessControl;
    private readonly ILogger<DnsProxyService> logger;

    public DnsProxyService(
        IDnsQueryPipeline queryPipeline,
        IDnsRateLimiter rateLimiter,
        IDnsAccessControl accessControl,
        ILogger<DnsProxyService> logger)
    {
        this.queryPipeline = queryPipeline;
        this.rateLimiter = rateLimiter;
        this.accessControl = accessControl;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queryPipeline.InitializeAsync(stoppingToken);

        Task udpTask = RunUdpListenerAsync(stoppingToken);
        Task tcpTask = RunTcpListenerAsync(stoppingToken);

        await Task.WhenAll(udpTask, tcpTask);
    }

    private async Task RunUdpListenerAsync(CancellationToken stoppingToken)
    {
        UdpClient? listener = CreateUdpListener();

        if (listener is null)
        {
            return;
        }

        logger.LogInformation("Service DNS (UDP) en écoute sur le port {Port}.", DnsPort);

        using (listener)
        using (stoppingToken.Register(() => listener.Close()))
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult request;

                try
                {
                    request = await listener.ReceiveAsync().WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    logger.LogDebug(ex, "Erreur socket UDP lors de la réception d'une requête DNS.");
                    continue;
                }

                // Client non autorisé : requête ignorée silencieusement (pas de réponse, pour ne pas
                // divulguer la présence du serveur à un client interdit).
                if (!accessControl.IsClientAllowed(request.RemoteEndPoint.Address))
                {
                    logger.LogDebug("Requête DNS (UDP) ignorée : client non autorisé {RemoteEndPoint}.", request.RemoteEndPoint);
                    continue;
                }

                // Requête au-delà de la limite : ignorée silencieusement (comportement usuel en UDP,
                // pour ne pas amplifier le trafic vers un client potentiellement usurpé).
                if (!rateLimiter.IsAllowed(request.RemoteEndPoint.Address))
                {
                    logger.LogDebug("Requête DNS (UDP) ignorée : limite de requêtes atteinte pour {RemoteEndPoint}.", request.RemoteEndPoint);
                    continue;
                }

                // Domaine interdit : requête non traitée du tout.
                if (queryPipeline.IsQueryDisallowed(request.Buffer))
                {
                    logger.LogDebug("Requête DNS (UDP) ignorée : domaine interdit demandé par {RemoteEndPoint}.", request.RemoteEndPoint);
                    continue;
                }

                _ = HandleUdpQueryAsync(listener, request, stoppingToken);
            }
        }
    }

    private async Task HandleUdpQueryAsync(UdpClient listener, UdpReceiveResult request, CancellationToken stoppingToken)
    {
        try
        {
            byte[]? response = await queryPipeline.ResolveAsync(request.Buffer, request.RemoteEndPoint.Address, stoppingToken);

            if (response is not null)
            {
                await listener.SendAsync(response, response.Length, request.RemoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec du traitement d'une requête DNS (UDP) depuis {RemoteEndPoint}.", request.RemoteEndPoint);
        }
    }

    private UdpClient? CreateUdpListener()
    {
        try
        {
            UdpClient dualStackListener = new UdpClient(AddressFamily.InterNetworkV6);
            dualStackListener.Client.DualMode = true;
            dualStackListener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, DnsPort));
            return dualStackListener;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Écoute DNS (UDP) en dual-stack IPv6/IPv4 indisponible, tentative en IPv4 uniquement.");
        }

        try
        {
            return new UdpClient(new IPEndPoint(IPAddress.Any, DnsPort));
        }
        catch (Exception ex)
        {
            LogBindFailure(ex, "UDP");
            return null;
        }
    }

    private async Task RunTcpListenerAsync(CancellationToken stoppingToken)
    {
        TcpListener? listener = CreateTcpListener();

        if (listener is null)
        {
            return;
        }

        logger.LogInformation("Service DNS (TCP) en écoute sur le port {Port}.", DnsPort);

        try
        {
            using (stoppingToken.Register(() => listener.Stop()))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await listener.AcceptTcpClientAsync().WaitAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        logger.LogDebug(ex, "Erreur socket TCP lors de l'acceptation d'une connexion DNS.");
                        continue;
                    }

                    _ = HandleTcpClientAsync(client, stoppingToken);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                IPAddress? clientAddress = null;

                if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                {
                    clientAddress = remoteEndPoint.Address;

                    // Client non autorisé : connexion fermée immédiatement sans réponse.
                    if (!accessControl.IsClientAllowed(remoteEndPoint.Address))
                    {
                        logger.LogDebug("Requête DNS (TCP) ignorée : client non autorisé {RemoteEndPoint}.", remoteEndPoint);
                        return;
                    }

                    // Connexion au-delà de la limite : fermée immédiatement sans réponse.
                    if (!rateLimiter.IsAllowed(remoteEndPoint.Address))
                    {
                        logger.LogDebug("Requête DNS (TCP) ignorée : limite de requêtes atteinte pour {RemoteEndPoint}.", remoteEndPoint);
                        return;
                    }
                }

                using NetworkStream stream = client.GetStream();

                byte[] lengthBuffer = new byte[2];
                await ReadExactAsync(stream, lengthBuffer, stoppingToken);
                int messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

                if (messageLength <= 0 || messageLength > MaxTcpMessageSize)
                {
                    return;
                }

                byte[] queryBuffer = new byte[messageLength];
                await ReadExactAsync(stream, queryBuffer, stoppingToken);

                // Domaine interdit : requête non traitée du tout, connexion fermée sans réponse.
                if (queryPipeline.IsQueryDisallowed(queryBuffer))
                {
                    logger.LogDebug("Requête DNS (TCP) ignorée : domaine interdit.");
                    return;
                }

                byte[]? response = await queryPipeline.ResolveAsync(queryBuffer, clientAddress, stoppingToken);

                if (response is null)
                {
                    return;
                }

                byte[] responseLengthPrefix = { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) };
                await stream.WriteAsync(responseLengthPrefix, stoppingToken);
                await stream.WriteAsync(response, stoppingToken);
                await stream.FlushAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Échec du traitement d'une requête DNS (TCP).");
            }
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);

            if (read == 0)
            {
                throw new IOException("Connexion TCP fermée avant la fin de la requête DNS.");
            }

            offset += read;
        }
    }

    private TcpListener? CreateTcpListener()
    {
        try
        {
            TcpListener dualStackListener = new TcpListener(IPAddress.IPv6Any, DnsPort);
            dualStackListener.Server.DualMode = true;
            dualStackListener.Start();
            return dualStackListener;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Écoute DNS (TCP) en dual-stack IPv6/IPv4 indisponible, tentative en IPv4 uniquement.");
        }

        try
        {
            TcpListener ipv4Listener = new TcpListener(IPAddress.Any, DnsPort);
            ipv4Listener.Start();
            return ipv4Listener;
        }
        catch (Exception ex)
        {
            LogBindFailure(ex, "TCP");
            return null;
        }
    }

    private void LogBindFailure(Exception ex, string protocol)
    {
        logger.LogError(
            ex,
            "Impossible de démarrer le service DNS ({Protocol}) sur le port {Port}. Sur Linux, autorisez le " +
            "binding sur un port privilégié (ex. 'sudo setcap cap_net_bind_service=+ep <chemin de l'exécutable>', " +
            "ou exécutez le service en tant que root), et vérifiez qu'aucun autre service (ex. systemd-resolved) " +
            "n'écoute déjà sur le port {Port}. Sur Windows, exécutez le service en tant qu'administrateur.",
            protocol,
            DnsPort,
            DnsPort);
    }
}
