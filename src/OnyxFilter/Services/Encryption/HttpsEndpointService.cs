using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Encryption;

// État du port HTTPS de l'interface web et du DNS-over-HTTPS.
public sealed record HttpsEndpointStatus(bool Active, int Port, string? Reason, CertificateSummary? Certificate)
{
    public static HttpsEndpointStatus Initial { get; } = new HttpsEndpointStatus(false, 0, "chiffrement désactivé", null);
}

public interface IHttpsEndpointService
{
    HttpsEndpointStatus Status { get; }

    event Action? StatusChanged;

    // Certificat présenté aux connexions HTTPS (chaîne complète), relu à chaque connexion : un certificat
    // renouvelé est servi aussitôt, sans redémarrage.
    LoadedServerCertificate? Certificate { get; }

    // Port HTTPS vers lequel rediriger le HTTP (« Rediriger HTTP vers HTTPS »), null sinon.
    int? RedirectPort { get; }
}

// Ouvre le port HTTPS (« Chiffrement ») quand le chiffrement est activé et qu'un certificat valide est en place
// (Let's Encrypt ou fourni à la main), et le ferme sinon, sans redémarrage : la décision est revue à chaque
// enregistrement des paramètres, dont l'enregistrement d'un nouveau certificat par LetsEncryptService. Un port
// déjà occupé est détecté avant de l'ouvrir, pour qu'une erreur de liaison ne touche jamais le reste.
public sealed class HttpsEndpointService : BackgroundService, IHttpsEndpointService
{
    private static readonly TimeSpan BindCheckDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RetiredCertificateLifetime = TimeSpan.FromMinutes(5);

    private readonly ILocalSettingsStore settingsStore;
    private readonly KestrelEndpointsConfigurationProvider endpoints;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<HttpsEndpointService> logger;
    private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, 1);

    private volatile HttpsEndpointStatus status = HttpsEndpointStatus.Initial;
    private volatile LoadedServerCertificate? certificate;
    private volatile string? appliedSnapshot;
    // 0 : pas de redirection.
    private int redirectPort;

    public HttpsEndpointService(
        ILocalSettingsStore settingsStore,
        KestrelEndpointsConfigurationProvider endpoints,
        IHostApplicationLifetime lifetime,
        ILogger<HttpsEndpointService> logger)
    {
        this.settingsStore = settingsStore;
        this.endpoints = endpoints;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    public event Action? StatusChanged;

    public HttpsEndpointStatus Status => status;

    public LoadedServerCertificate? Certificate => certificate;

    public int? RedirectPort => Volatile.Read(ref redirectPort) is int port and > 0 ? port : null;

    public override void Dispose()
    {
        settingsStore.SettingsChanged -= Wake;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsStore.SettingsChanged += Wake;

        // Après le démarrage de Kestrel : une liaison qui échouerait au rechargement est seulement journalisée.
        TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled()))
        {
            try
            {
                await started.Task;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "HTTPS : erreur inattendue lors de l'application des paramètres de chiffrement.");
            }

            try
            {
                await wakeSignal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        EncryptionSettingsData encryption = (await settingsStore.LoadAsync()).Encryption;
        string snapshot = JsonSerializer.Serialize(encryption);

        // Autre page enregistrée : rien à revoir.
        if (string.Equals(snapshot, appliedSnapshot, StringComparison.Ordinal))
        {
            return;
        }

        appliedSnapshot = snapshot;
        int port = encryption.HttpsPort;

        if (!encryption.EnableEncryption)
        {
            Deactivate(port, "chiffrement désactivé");
            return;
        }

        if (port is < 1 or > 65535)
        {
            Deactivate(port, $"port HTTPS invalide ({port})");
            return;
        }

        LoadedServerCertificate loaded;

        try
        {
            loaded = ServerCertificateLoader.Load(encryption);
        }
        catch (InvalidOperationException ex)
        {
            Deactivate(port, "certificat inutilisable : " + ex.Message);
            return;
        }

        ReplaceCertificate(loaded);
        string url = "https://*:" + port;

        if (!string.Equals(endpoints.HttpsUrl, url, StringComparison.Ordinal))
        {
            // Libère d'abord un ancien port HTTPS (changement de port), puis vérifie que le nouveau est libre.
            endpoints.SetHttpsUrl(null);

            if (GetPortProblem(port) is { } problem)
            {
                Deactivate(port, problem);
                return;
            }

            endpoints.SetHttpsUrl(url);
            await Task.Delay(BindCheckDelay, cancellationToken);

            // Kestrel ouvre le port en arrière-plan : il doit maintenant être occupé.
            if (GetPortProblem(port) is null)
            {
                endpoints.SetHttpsUrl(null);
                Deactivate(port, "Kestrel n'a pas pu ouvrir le port (voir le journal)");
                return;
            }

            logger.LogInformation("HTTPS actif sur le port {Port} (certificat : {Names}).", port, loaded.Summary.DisplayName);
        }

        Volatile.Write(ref redirectPort, encryption.AutoRedirectToHttps ? port : 0);
        SetStatus(new HttpsEndpointStatus(true, port, null, loaded.Summary));
    }

    private void Deactivate(int port, string reason)
    {
        endpoints.SetHttpsUrl(null);
        Volatile.Write(ref redirectPort, 0);

        if (status.Active || !string.Equals(status.Reason, reason, StringComparison.Ordinal))
        {
            logger.LogInformation("HTTPS inactif : {Reason}.", reason);
        }

        SetStatus(new HttpsEndpointStatus(false, port, reason, null));
    }

    // Les connexions en cours peuvent encore utiliser l'ancien certificat : il est libéré plus tard.
    private void ReplaceCertificate(LoadedServerCertificate loaded)
    {
        LoadedServerCertificate? retired = Interlocked.Exchange(ref certificate, loaded);

        if (retired is not null)
        {
            _ = Task.Delay(RetiredCertificateLifetime).ContinueWith(_ => retired.Dispose(), TaskScheduler.Default);
        }
    }

    // Null si le port peut être ouvert, sinon la raison.
    private static string? GetPortProblem(int port)
    {
        TcpListener probe = Socket.OSSupportsIPv6 ? new TcpListener(IPAddress.IPv6Any, port) : new TcpListener(IPAddress.Any, port);

        try
        {
            if (Socket.OSSupportsIPv6)
            {
                probe.Server.DualMode = true;
            }

            probe.Start();
            return null;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return $"port {port} déjà utilisé";
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return $"droits insuffisants pour ouvrir le port {port}";
        }
        catch (SocketException ex)
        {
            return $"port {port} indisponible ({ex.Message})";
        }
        finally
        {
            probe.Stop();
        }
    }

    private void SetStatus(HttpsEndpointStatus newStatus)
    {
        status = newStatus;
        StatusChanged?.Invoke();
    }

    private void Wake()
    {
        try
        {
            if (wakeSignal.CurrentCount == 0)
            {
                wakeSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Réveil déjà demandé.
        }
    }
}
