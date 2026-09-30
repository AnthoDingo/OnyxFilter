using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.Encryption;

namespace OnyxFilter.Services.DnsForwarding;

// Service DNS-over-TLS (RFC 7858) : écoute en TCP sur le port configuré dans "Paramètres de
// chiffrement" (/settings/encryption, 853 par défaut), négocie TLS 1.2/1.3 avec le certificat
// configuré, puis échange des messages DNS avec le même cadrage qu'en TCP (préfixe de longueur sur
// 2 octets), plusieurs requêtes pouvant se succéder sur une même connexion. La résolution passe par
// IDnsQueryPipeline : mêmes filtres, cache, serveurs en amont et statistiques que le port 53.
// Le service ne démarre que si "Activer le chiffrement" est coché, et se reconfigure à chaud (port,
// certificat, activation) à chaque enregistrement de la page, sans redémarrage de l'application.
public sealed class DnsOverTlsService : BackgroundService
{
    private const int MaxTcpMessageSize = 4096;

    // Nombre maximal de connexions TLS simultanées : borne la mémoire (tampons + état TLS par
    // connexion) sur un serveur à 2 Go de RAM. Les connexions excédentaires sont refusées.
    private const int MaxConcurrentConnections = 128;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // Délai d'inactivité entre deux requêtes d'une même connexion : assez long pour que les clients
    // réutilisent la connexion (l'intérêt principal de DoT, la poignée de main étant coûteuse), assez
    // court pour ne pas immobiliser les emplacements de connexion.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ILocalSettingsStore settingsStore;
    private readonly IDnsQueryPipeline queryPipeline;
    private readonly IDnsAccessControl accessControl;
    private readonly IDnsRateLimiter rateLimiter;
    private readonly ILogger<DnsOverTlsService> logger;

    private readonly SemaphoreSlim connectionSlots = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);

    // Session en cours (une session = une configuration appliquée) : annulée pour forcer un
    // rechargement lorsque les paramètres de chiffrement changent.
    private readonly object sessionLock = new object();
    private CancellationTokenSource? activeSessionTokenSource;
    private string appliedSettingsSnapshot = string.Empty;

    public DnsOverTlsService(
        ILocalSettingsStore settingsStore,
        IDnsQueryPipeline queryPipeline,
        IDnsAccessControl accessControl,
        IDnsRateLimiter rateLimiter,
        ILogger<DnsOverTlsService> logger)
    {
        this.settingsStore = settingsStore;
        this.queryPipeline = queryPipeline;
        this.accessControl = accessControl;
        this.rateLimiter = rateLimiter;
        this.logger = logger;

        settingsStore.SettingsChanged += OnSettingsChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queryPipeline.InitializeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            using CancellationTokenSource sessionTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            lock (sessionLock)
            {
                activeSessionTokenSource = sessionTokenSource;
            }

            try
            {
                await RunSessionAsync(sessionTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Annulation de session (changement de paramètres) ou arrêt de l'application : la
                // boucle décide de la suite selon stoppingToken.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erreur inattendue du service DNS-over-TLS, nouvelle tentative dans 5 secondes.");
                await DelaySafelyAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            finally
            {
                lock (sessionLock)
                {
                    activeSessionTokenSource = null;
                }
            }
        }
    }

    // Applique la configuration courante : attente passive si le chiffrement est désactivé ou si le
    // certificat est invalide (jusqu'au prochain enregistrement des paramètres), écoute TLS sinon.
    private async Task RunSessionAsync(CancellationToken sessionToken)
    {
        EncryptionSettingsData encryptionSettings;

        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            encryptionSettings = settings.Encryption;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Impossible de lire les paramètres de chiffrement, nouvelle tentative dans 5 secondes.");
            await DelaySafelyAsync(TimeSpan.FromSeconds(5), sessionToken);
            return;
        }

        lock (sessionLock)
        {
            appliedSettingsSnapshot = BuildSettingsSnapshot(encryptionSettings);
        }

        if (!encryptionSettings.EnableEncryption)
        {
            logger.LogDebug("Service DNS-over-TLS inactif : le chiffrement est désactivé dans les paramètres.");
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        if (encryptionSettings.DnsOverTlsPort < 1 || encryptionSettings.DnsOverTlsPort > 65535)
        {
            logger.LogError("Port DNS-over-TLS invalide ({Port}) : service non démarré.", encryptionSettings.DnsOverTlsPort);
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        LoadedServerCertificate serverCertificate;

        try
        {
            serverCertificate = ServerCertificateLoader.Load(encryptionSettings);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(
                "Service DNS-over-TLS non démarré, certificat inutilisable : {Reason} Corrigez la page /settings/encryption.",
                ex.Message);
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        using (serverCertificate)
        {
            logger.LogInformation(
                "Certificat DNS-over-TLS chargé : {Names} (expire le {NotAfterUtc:yyyy-MM-dd} UTC).",
                serverCertificate.Summary.DisplayName,
                serverCertificate.Summary.NotAfterUtc);

            await RunListenerAsync(encryptionSettings.DnsOverTlsPort, serverCertificate, sessionToken);
        }
    }

    private async Task RunListenerAsync(int port, LoadedServerCertificate serverCertificate, CancellationToken sessionToken)
    {
        TcpListener? listener = CreateTcpListener(port);

        if (listener is null)
        {
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        logger.LogInformation("Service DNS-over-TLS en écoute sur le port {Port}.", port);

        // Options TLS partagées par toutes les connexions de la session : certificat pré-assemblé
        // (chaîne incluse) et protocoles restreints à TLS 1.2/1.3 (exigence de RFC 7858 : TLS >= 1.2).
        // Pas d'exigence ALPN : la RFC 7858 ne définit pas d'identifiant ALPN pour DNS-over-TLS,
        // et la plupart des clients (Android « DNS privé », iOS, systemd-resolved…) n'en envoient
        // pas. Exiger "dot" ferait échouer la poignée de main TLS avec ces clients.
        SslServerAuthenticationOptions tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateContext = serverCertificate.Context,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        };

        try
        {
            using (sessionToken.Register(() => listener.Stop()))
            {
                while (!sessionToken.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await listener.AcceptTcpClientAsync().WaitAsync(sessionToken);
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
                        logger.LogDebug(ex, "Erreur socket lors de l'acceptation d'une connexion DNS-over-TLS.");
                        continue;
                    }

                    // Limite de connexions simultanées atteinte : connexion refusée immédiatement,
                    // sans attente, pour ne pas accumuler de file sur un matériel modeste.
                    if (!connectionSlots.Wait(0))
                    {
                        logger.LogDebug("Connexion DNS-over-TLS refusée : limite de {Max} connexions simultanées atteinte.", MaxConcurrentConnections);
                        client.Dispose();
                        continue;
                    }

                    _ = HandleClientAsync(client, tlsOptions, sessionToken);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, SslServerAuthenticationOptions tlsOptions, CancellationToken sessionToken)
    {
        try
        {
            using (client)
            {
                IPAddress? clientAddress = null;

                if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                {
                    clientAddress = remoteEndPoint.Address;

                    // Client non autorisé : connexion fermée immédiatement, avant même la poignée de
                    // main TLS (aucune ressource cryptographique dépensée pour un client interdit).
                    if (!accessControl.IsClientAllowed(remoteEndPoint.Address))
                    {
                        logger.LogDebug("Connexion DNS-over-TLS ignorée : client non autorisé {RemoteEndPoint}.", remoteEndPoint);
                        return;
                    }

                    // Connexion au-delà de la limite de requêtes : fermée immédiatement sans réponse.
                    if (!rateLimiter.IsAllowed(remoteEndPoint.Address))
                    {
                        logger.LogDebug("Connexion DNS-over-TLS ignorée : limite de requêtes atteinte pour {RemoteEndPoint}.", remoteEndPoint);
                        return;
                    }
                }

                using SslStream tlsStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

                // Poignée de main bornée dans le temps : un client qui ouvre la connexion sans
                // négocier ne doit pas immobiliser un emplacement indéfiniment.
                using (CancellationTokenSource handshakeTokenSource = CancellationTokenSource.CreateLinkedTokenSource(sessionToken))
                {
                    handshakeTokenSource.CancelAfter(HandshakeTimeout);
                    await tlsStream.AuthenticateAsServerAsync(tlsOptions, handshakeTokenSource.Token);
                }

                await ServeQueriesAsync(tlsStream, clientAddress, sessionToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt de session, expiration de la poignée de main ou délai d'inactivité : fermeture
            // silencieuse, comportement normal.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec du traitement d'une connexion DNS-over-TLS.");
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    // Sert les requêtes successives d'une même connexion TLS (RFC 7858 §3.3 : les clients sont
    // encouragés à réutiliser la connexion), jusqu'à fermeture par le client ou inactivité prolongée.
    private async Task ServeQueriesAsync(SslStream tlsStream, IPAddress? clientAddress, CancellationToken sessionToken)
    {
        byte[] lengthBuffer = new byte[2];

        while (!sessionToken.IsCancellationRequested)
        {
            using CancellationTokenSource idleTokenSource = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            idleTokenSource.CancelAfter(IdleTimeout);

            // Fermeture propre par le client entre deux requêtes : fin normale de la connexion.
            if (!await TryReadExactAsync(tlsStream, lengthBuffer, idleTokenSource.Token))
            {
                return;
            }

            int messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

            if (messageLength <= 0 || messageLength > MaxTcpMessageSize)
            {
                return;
            }

            byte[] queryBuffer = new byte[messageLength];

            if (!await TryReadExactAsync(tlsStream, queryBuffer, idleTokenSource.Token))
            {
                throw new IOException("Connexion TLS fermée avant la fin de la requête DNS.");
            }

            // Limite de requêtes appliquée par requête (et pas seulement à la connexion) : une même
            // connexion TLS peut transporter de nombreuses requêtes successives.
            if (clientAddress is not null && !rateLimiter.IsAllowed(clientAddress))
            {
                logger.LogDebug("Requête DNS-over-TLS ignorée : limite de requêtes atteinte pour {ClientAddress}.", clientAddress);
                return;
            }

            // Domaine interdit : requête non traitée du tout, connexion fermée sans réponse.
            if (queryPipeline.IsQueryDisallowed(queryBuffer))
            {
                logger.LogDebug("Requête DNS-over-TLS ignorée : domaine interdit.");
                return;
            }

            byte[]? response = await queryPipeline.ResolveAsync(queryBuffer, clientAddress, sessionToken);

            if (response is null)
            {
                return;
            }

            byte[] responseLengthPrefix = { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) };
            await tlsStream.WriteAsync(responseLengthPrefix, sessionToken);
            await tlsStream.WriteAsync(response, sessionToken);
            await tlsStream.FlushAsync(sessionToken);
        }
    }

    // Lit exactement buffer.Length octets. Retourne faux si le flux se termine proprement avant le
    // premier octet (fermeture normale entre deux requêtes) ; lève IOException si le flux se termine
    // au milieu d'une lecture.
    private static async Task<bool> TryReadExactAsync(SslStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);

            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new IOException("Connexion TLS fermée avant la fin de la requête DNS.");
            }

            offset += read;
        }

        return true;
    }

    private TcpListener? CreateTcpListener(int port)
    {
        try
        {
            TcpListener dualStackListener = new TcpListener(IPAddress.IPv6Any, port);
            dualStackListener.Server.DualMode = true;
            dualStackListener.Start();
            return dualStackListener;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Écoute DNS-over-TLS en dual-stack IPv6/IPv4 indisponible, tentative en IPv4 uniquement.");
        }

        try
        {
            TcpListener ipv4Listener = new TcpListener(IPAddress.Any, port);
            ipv4Listener.Start();
            return ipv4Listener;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Impossible de démarrer le service DNS-over-TLS sur le port {Port}. Vérifiez qu'aucun autre service " +
                "n'écoute déjà sur ce port et, s'il est inférieur à 1024 sous Linux, que le binding sur un port " +
                "privilégié est autorisé (ex. 'sudo setcap cap_net_bind_service=+ep <chemin de l'exécutable>').",
                port);
            return null;
        }
    }

    // Ne redémarre la session que si le bloc "Encryption" a réellement changé : l'enregistrement des
    // autres pages de paramètres (DNS, filtres...) ne doit pas couper les connexions DoT en cours.
    private void OnSettingsChanged()
    {
        _ = ReloadIfEncryptionSettingsChangedAsync();
    }

    private async Task ReloadIfEncryptionSettingsChangedAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();
            string newSnapshot = BuildSettingsSnapshot(settings.Encryption);

            lock (sessionLock)
            {
                if (string.Equals(newSnapshot, appliedSettingsSnapshot, StringComparison.Ordinal))
                {
                    return;
                }

                activeSessionTokenSource?.Cancel();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Impossible de vérifier les paramètres de chiffrement après leur enregistrement.");
        }
    }

    private static string BuildSettingsSnapshot(EncryptionSettingsData settings)
    {
        return JsonSerializer.Serialize(settings);
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DelaySafelyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        connectionSlots.Dispose();
        base.Dispose();
    }
}
