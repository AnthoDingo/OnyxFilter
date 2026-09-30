using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.Encryption;

namespace OnyxFilter.Services.DnsForwarding;

// Service DNS-over-QUIC (RFC 9250) : écoute en UDP/QUIC sur le port configuré dans "Paramètres de
// chiffrement" (/settings/encryption, 784 par défaut), avec le même certificat que le DNS-over-TLS.
// Chaque requête arrive sur son propre flux bidirectionnel ouvert par le client, avec le même cadrage
// qu'en TCP (préfixe de longueur sur 2 octets) ; une même connexion QUIC transporte de nombreux flux
// en parallèle. La résolution passe par IDnsQueryPipeline : mêmes filtres, cache, serveurs en amont et
// statistiques que le port 53. Le service ne démarre que si "Activer le chiffrement" est coché, et se
// reconfigure à chaud (port, certificat, activation) à chaque enregistrement de la page, sans
// redémarrage de l'application. Structure volontairement calquée sur DnsOverTlsService.
// Prérequis plateforme : QUIC s'appuie sur msquic (fourni avec .NET sous Windows ; paquet "libmsquic"
// à installer sous Linux/Raspberry Pi). Si msquic est absent, le service se met en attente avec un
// message explicite au lieu de faire échouer l'application.
public sealed class DnsOverQuicService : BackgroundService
{
    private const int MaxMessageSize = 4096;

    // Nombre maximal de connexions QUIC simultanées : borne la mémoire sur un serveur à 2 Go de RAM.
    // Les connexions excédentaires sont fermées avec DOQ_EXCESSIVE_LOAD.
    private const int MaxConcurrentConnections = 128;

    // Flux entrants simultanés par connexion (une requête DNS = un flux, RFC 9250 §4.2).
    private const int MaxStreamsPerConnection = 64;

    // Codes d'erreur applicatifs DoQ (RFC 9250 §5.3).
    private const long DoqNoError = 0x0;
    private const long DoqInternalError = 0x1;
    private const long DoqRequestCancelled = 0x3;
    private const long DoqExcessiveLoad = 0x4;

    // Délai d'inactivité d'un flux : temps maximal accordé au client pour envoyer sa requête complète
    // une fois le flux ouvert (la réutilisation de la connexion, elle, est gérée par QUIC).
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ILocalSettingsStore settingsStore;
    private readonly IDnsQueryPipeline queryPipeline;
    private readonly IDnsAccessControl accessControl;
    private readonly IDnsRateLimiter rateLimiter;
    private readonly ILogger<DnsOverQuicService> logger;

    private readonly SemaphoreSlim connectionSlots = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);

    // Session en cours (une session = une configuration appliquée) : annulée pour forcer un
    // rechargement lorsque les paramètres de chiffrement changent.
    private readonly object sessionLock = new object();
    private CancellationTokenSource? activeSessionTokenSource;
    private string appliedSettingsSnapshot = string.Empty;

    public DnsOverQuicService(
        ILocalSettingsStore settingsStore,
        IDnsQueryPipeline queryPipeline,
        IDnsAccessControl accessControl,
        IDnsRateLimiter rateLimiter,
        ILogger<DnsOverQuicService> logger)
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
                logger.LogError(ex, "Erreur inattendue du service DNS-over-QUIC, nouvelle tentative dans 5 secondes.");
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

    // Applique la configuration courante : attente passive si le chiffrement est désactivé, si QUIC
    // n'est pas disponible sur la plateforme ou si le certificat est invalide (jusqu'au prochain
    // enregistrement des paramètres), écoute QUIC sinon.
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
            logger.LogDebug("Service DNS-over-QUIC inactif : le chiffrement est désactivé dans les paramètres.");
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        if (!QuicListener.IsSupported)
        {
            logger.LogWarning(
                "Service DNS-over-QUIC non démarré : QUIC n'est pas disponible sur cette plateforme. " +
                "Sous Linux/Raspberry Pi, installez le paquet 'libmsquic' (dépôt packages.microsoft.com) " +
                "puis redémarrez l'application. Les services DNS (port 53) et DNS-over-TLS ne sont pas affectés.");
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        if (encryptionSettings.DnsOverQuicPort < 1 || encryptionSettings.DnsOverQuicPort > 65535)
        {
            logger.LogError("Port DNS-over-QUIC invalide ({Port}) : service non démarré.", encryptionSettings.DnsOverQuicPort);
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
                "Service DNS-over-QUIC non démarré, certificat inutilisable : {Reason} Corrigez la page /settings/encryption.",
                ex.Message);
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        using (serverCertificate)
        {
            await RunListenerAsync(encryptionSettings.DnsOverQuicPort, serverCertificate, sessionToken);
        }
    }

    private async Task RunListenerAsync(int port, LoadedServerCertificate serverCertificate, CancellationToken sessionToken)
    {
        List<SslApplicationProtocol> doqProtocol = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("doq"),
        };

        // Options communes à toutes les connexions de la session : certificat pré-assemblé (chaîne
        // incluse) et TLS 1.3 (seule version possible avec QUIC).
        QuicServerConnectionOptions connectionOptions = new QuicServerConnectionOptions
        {
            DefaultCloseErrorCode = DoqNoError,
            DefaultStreamErrorCode = DoqRequestCancelled,
            MaxInboundBidirectionalStreams = MaxStreamsPerConnection,
            MaxInboundUnidirectionalStreams = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = serverCertificate.Context,
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                ApplicationProtocols = doqProtocol,
            },
        };

        QuicListener? listener = await CreateListenerAsync(port, doqProtocol, connectionOptions, sessionToken);

        if (listener is null)
        {
            await WaitUntilCancelledAsync(sessionToken);
            return;
        }

        logger.LogInformation(
            "Service DNS-over-QUIC en écoute sur le port {Port} (certificat : {Names}, expire le {NotAfterUtc:yyyy-MM-dd} UTC).",
            port,
            serverCertificate.Summary.DisplayName,
            serverCertificate.Summary.NotAfterUtc);

        try
        {
            while (!sessionToken.IsCancellationRequested)
            {
                QuicConnection connection;

                try
                {
                    connection = await listener.AcceptConnectionAsync(sessionToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (QuicException ex)
                {
                    // Poignée de main échouée (client incompatible, ALPN inconnu...) : la connexion
                    // fautive est rejetée, l'écoute continue.
                    logger.LogDebug(ex, "Échec de l'acceptation d'une connexion DNS-over-QUIC.");
                    continue;
                }

                // Limite de connexions simultanées atteinte : connexion fermée immédiatement avec
                // DOQ_EXCESSIVE_LOAD (RFC 9250), sans attente.
                if (!connectionSlots.Wait(0))
                {
                    logger.LogDebug("Connexion DNS-over-QUIC refusée : limite de {Max} connexions simultanées atteinte.", MaxConcurrentConnections);
                    _ = CloseConnectionSilentlyAsync(connection, DoqExcessiveLoad);
                    continue;
                }

                _ = HandleConnectionAsync(connection, sessionToken);
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    private async Task<QuicListener?> CreateListenerAsync(
        int port,
        List<SslApplicationProtocol> doqProtocol,
        QuicServerConnectionOptions connectionOptions,
        CancellationToken sessionToken)
    {
        // Même stratégie que les autres points d'écoute : dual-stack IPv6/IPv4 d'abord, repli IPv4.
        try
        {
            return await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),
                    ApplicationProtocols = doqProtocol,
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connectionOptions),
                },
                sessionToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Écoute DNS-over-QUIC en dual-stack IPv6/IPv4 indisponible, tentative en IPv4 uniquement.");
        }

        try
        {
            return await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
                    ApplicationProtocols = doqProtocol,
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(connectionOptions),
                },
                sessionToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Impossible de démarrer le service DNS-over-QUIC sur le port {Port} (UDP). Vérifiez qu'aucun autre " +
                "service n'écoute déjà sur ce port et, s'il est inférieur à 1024 sous Linux, que le binding sur un " +
                "port privilégié est autorisé (ex. 'sudo setcap cap_net_bind_service=+ep <chemin de l'exécutable>').",
                port);
            return null;
        }
    }

    private async Task HandleConnectionAsync(QuicConnection connection, CancellationToken sessionToken)
    {
        try
        {
            await using (connection)
            {
                IPAddress clientAddress = connection.RemoteEndPoint.Address;

                // Client non autorisé : connexion fermée sans réponse (la poignée de main a déjà eu
                // lieu — msquic l'effectue avant de livrer la connexion — mais aucune requête n'est
                // servie à un client interdit).
                if (!accessControl.IsClientAllowed(clientAddress))
                {
                    logger.LogDebug("Connexion DNS-over-QUIC ignorée : client non autorisé {ClientAddress}.", clientAddress);
                    await connection.CloseAsync(DoqNoError, sessionToken);
                    return;
                }

                // Connexion au-delà de la limite de requêtes : fermée immédiatement sans réponse.
                if (!rateLimiter.IsAllowed(clientAddress))
                {
                    logger.LogDebug("Connexion DNS-over-QUIC ignorée : limite de requêtes atteinte pour {ClientAddress}.", clientAddress);
                    await connection.CloseAsync(DoqExcessiveLoad, sessionToken);
                    return;
                }

                // Une requête DNS par flux bidirectionnel (RFC 9250 §4.2) : les flux d'une même
                // connexion sont servis en parallèle.
                while (!sessionToken.IsCancellationRequested)
                {
                    QuicStream stream;

                    try
                    {
                        stream = await connection.AcceptInboundStreamAsync(sessionToken);
                    }
                    catch (QuicException)
                    {
                        // Connexion fermée par le client ou expirée (inactivité) : fin normale.
                        break;
                    }

                    _ = HandleStreamAsync(stream, clientAddress, sessionToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt de session : fermeture silencieuse, comportement normal.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec du traitement d'une connexion DNS-over-QUIC.");
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    // Sert une requête DNS sur son flux : cadrage identique au TCP (préfixe de longueur sur 2 octets,
    // RFC 9250 §4.2.1). Un flux abandonné sans réponse est fermé avec DOQ_REQUEST_CANCELLED
    // (DefaultStreamErrorCode) lors du Dispose.
    private async Task HandleStreamAsync(QuicStream stream, IPAddress clientAddress, CancellationToken sessionToken)
    {
        try
        {
            await using (stream)
            {
                using CancellationTokenSource idleTokenSource = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                idleTokenSource.CancelAfter(StreamIdleTimeout);

                byte[] lengthBuffer = new byte[2];

                if (!await TryReadExactAsync(stream, lengthBuffer, idleTokenSource.Token))
                {
                    return;
                }

                int messageLength = (lengthBuffer[0] << 8) | lengthBuffer[1];

                if (messageLength <= 0 || messageLength > MaxMessageSize)
                {
                    return;
                }

                byte[] queryBuffer = new byte[messageLength];

                if (!await TryReadExactAsync(stream, queryBuffer, idleTokenSource.Token))
                {
                    throw new IOException("Flux QUIC fermé avant la fin de la requête DNS.");
                }

                // Limite de requêtes appliquée par requête (et pas seulement à la connexion) : une
                // même connexion QUIC peut transporter de nombreux flux.
                if (!rateLimiter.IsAllowed(clientAddress))
                {
                    logger.LogDebug("Requête DNS-over-QUIC ignorée : limite de requêtes atteinte pour {ClientAddress}.", clientAddress);
                    return;
                }

                // Domaine interdit : requête non traitée du tout, flux fermé sans réponse.
                if (queryPipeline.IsQueryDisallowed(queryBuffer))
                {
                    logger.LogDebug("Requête DNS-over-QUIC ignorée : domaine interdit.");
                    return;
                }

                byte[]? response = await queryPipeline.ResolveAsync(queryBuffer, clientAddress, sessionToken);

                if (response is null)
                {
                    return;
                }

                byte[] responseLengthPrefix = { (byte)(response.Length >> 8), (byte)(response.Length & 0xFF) };
                await stream.WriteAsync(responseLengthPrefix, sessionToken);

                // completeWrites : signale au client que la réponse est complète (fermeture propre du
                // sens serveur→client du flux, attendue par RFC 9250).
                await stream.WriteAsync(response, completeWrites: true, sessionToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt de session ou délai d'inactivité du flux : fermeture silencieuse.
        }
        catch (QuicException ex)
        {
            logger.LogDebug(ex, "Échec du traitement d'un flux DNS-over-QUIC.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec du traitement d'une requête DNS-over-QUIC.");
        }
    }

    // Lit exactement buffer.Length octets. Retourne faux si le flux se termine proprement avant le
    // premier octet ; lève IOException si le flux se termine au milieu d'une lecture.
    private static async Task<bool> TryReadExactAsync(QuicStream stream, byte[] buffer, CancellationToken cancellationToken)
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

                throw new IOException("Flux QUIC fermé avant la fin de la requête DNS.");
            }

            offset += read;
        }

        return true;
    }

    private async Task CloseConnectionSilentlyAsync(QuicConnection connection, long errorCode)
    {
        try
        {
            await using (connection)
            {
                await connection.CloseAsync(errorCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Échec de la fermeture d'une connexion DNS-over-QUIC excédentaire.");
        }
    }

    // Ne redémarre la session que si le bloc "Encryption" a réellement changé : l'enregistrement des
    // autres pages de paramètres (DNS, filtres...) ne doit pas couper les connexions DoQ en cours.
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
