using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OnyxFilter.Services.Encryption.Acme;

// Petit serveur HTTP ouvert le temps d'une validation Let's Encrypt, sur le port 80 par défaut (celui que
// Let's Encrypt contacte toujours) : il ne répond qu'aux défis HTTP-01 en cours, 404 pour tout le reste.
// En IPv4 et IPv6 à la fois, Let's Encrypt privilégiant l'IPv6 quand le domaine en a une adresse. Si le port
// est déjà pris (ex. l'interface web elle-même, ou un proxy qui redirige vers elle), rien n'est ouvert : les
// défis restent servis par l'interface web (voir AcmeHttpChallengeExtensions).
public sealed class AcmeHttpChallengeListener : IAsyncDisposable
{
    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener listener;
    private readonly AcmeHttpChallengeStore store;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stopSource = new CancellationTokenSource();
    private readonly Task acceptLoop;

    private AcmeHttpChallengeListener(TcpListener listener, AcmeHttpChallengeStore store, ILogger logger)
    {
        this.listener = listener;
        this.store = store;
        this.logger = logger;
        acceptLoop = AcceptLoopAsync();
    }

    // Null si le port n'a pas pu être ouvert ; failure en donne la raison.
    public static AcmeHttpChallengeListener? TryStart(int port, AcmeHttpChallengeStore store, ILogger logger, out string? failure)
    {
        failure = null;
        TcpListener listener;

        try
        {
            if (Socket.OSSupportsIPv6)
            {
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
            }
            else
            {
                listener = new TcpListener(IPAddress.Any, port);
            }

            listener.Start();
        }
        catch (SocketException ex)
        {
            failure = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => $"port {port} déjà utilisé",
                SocketError.AccessDenied => $"droits insuffisants pour ouvrir le port {port}",
                _ => $"port {port} indisponible ({ex.Message})",
            };
            return null;
        }

        return new AcmeHttpChallengeListener(listener, store, logger);
    }

    public async ValueTask DisposeAsync()
    {
        stopSource.Cancel();
        listener.Stop();

        try
        {
            await acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Arrêt de l'écouteur : attendu.
        }

        stopSource.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopSource.IsCancellationRequested)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(stopSource.Token);
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopSource.Token);
                timeout.CancelAfter(RequestTimeout);

                NetworkStream stream = client.GetStream();
                string? requestLine = await ReadRequestLineAsync(stream, timeout.Token);
                string[] parts = requestLine?.Split(' ') ?? Array.Empty<string>();
                string path = parts.Length >= 2 ? parts[1].Split('?')[0] : string.Empty;

                string keyAuthorization = string.Empty;
                bool found = parts.Length >= 2
                    && (parts[0] == "GET" || parts[0] == "HEAD")
                    && store.TryGetResponse(path, out keyAuthorization);

                string body = found ? keyAuthorization : "Not found";
                byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
                string head = (found ? "HTTP/1.1 200 OK" : "HTTP/1.1 404 Not Found")
                    + "\r\nContent-Type: text/plain\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);

                if (parts.Length == 0 || parts[0] != "HEAD")
                {
                    await stream.WriteAsync(bodyBytes, timeout.Token);
                }

                logger.LogInformation("Défi Let's Encrypt : {Path} demandé par {Remote} ({Result}).", path, client.Client.RemoteEndPoint, found ? "servi" : "inconnu");
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.IO.IOException or SocketException or ObjectDisposedException)
            {
                // Client lent ou déconnecté : sans conséquence, le serveur ACME réessaie.
            }
        }
    }

    // Lit l'en-tête de la requête (jusqu'à la ligne vide) et renvoie sa première ligne.
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[MaxRequestBytes];
        int length = 0;

        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);

            if (read == 0)
            {
                break;
            }

            length += read;
            string received = Encoding.ASCII.GetString(buffer, 0, length);

            if (received.Contains("\r\n\r\n", StringComparison.Ordinal) || received.Contains("\n\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        string text = Encoding.ASCII.GetString(buffer, 0, length);
        int endOfLine = text.IndexOf('\n');
        return endOfLine < 0 ? null : text.Substring(0, endOfLine).TrimEnd('\r');
    }
}
