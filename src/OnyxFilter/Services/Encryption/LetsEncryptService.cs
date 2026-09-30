using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OnyxFilter.Models.Settings;
using OnyxFilter.Services.Encryption.Acme;

namespace OnyxFilter.Services.Encryption;

// Certificat Let's Encrypt automatique : au démarrage puis toutes les 12 heures, vérifie le certificat en place
// (présent, lisible, clé correspondante, noms attendus, même autorité) et en demande un nouveau dès qu'il entre
// dans les 15 jours précédant son expiration (voir LetsEncryptPolicy). Validation par défi HTTP-01. Un nouveau
// certificat est enregistré dans ManagedCertificateFiles, puis son empreinte dans les paramètres : le
// DNS-over-TLS et le DNS-over-QUIC le rechargent alors d'eux-mêmes.
// Après un échec, nouvel essai 1 h plus tard, puis 2 h, 4 h… jusqu'à 12 h, pour rester loin des limites
// d'émission de Let's Encrypt ; un changement de configuration ou une demande explicite relance aussitôt.
public sealed class LetsEncryptService : BackgroundService, ILetsEncryptService
{
    public const string HttpClientName = "letsencrypt";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan IssuanceTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ILocalSettingsStore settingsStore;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly AcmeHttpChallengeStore challengeStore;
    private readonly IOptions<LetsEncryptOptions> options;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<LetsEncryptService> logger;
    private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, 1);
    private readonly object statusLock = new object();

    private LetsEncryptStatus status = LetsEncryptStatus.Initial;
    private volatile string? appliedConfiguration;
    private int forceRenewalRequested;
    private int consecutiveFailures;
    private DateTime? retryNotBeforeUtc;

    public LetsEncryptService(
        ILocalSettingsStore settingsStore,
        IHttpClientFactory httpClientFactory,
        AcmeHttpChallengeStore challengeStore,
        IOptions<LetsEncryptOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<LetsEncryptService> logger)
    {
        this.settingsStore = settingsStore;
        this.httpClientFactory = httpClientFactory;
        this.challengeStore = challengeStore;
        this.options = options;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    public event Action? StatusChanged;

    public LetsEncryptStatus Status
    {
        get
        {
            lock (statusLock)
            {
                return status;
            }
        }
    }

    public void RequestCheck(bool forceRenewal)
    {
        if (forceRenewal)
        {
            Interlocked.Exchange(ref forceRenewalRequested, 1);
        }

        Wake();
    }

    public override void Dispose()
    {
        settingsStore.SettingsChanged -= OnSettingsChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsStore.SettingsChanged += OnSettingsChanged;

        // Les défis HTTP-01 peuvent passer par l'interface web : elle doit déjà écouter.
        if (!await WaitForApplicationStartedAsync(stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;

            try
            {
                delay = await RunCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Let's Encrypt : erreur inattendue lors de la vérification du certificat.");
                delay = FirstRetryDelay;
            }

            try
            {
                await wakeSignal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Une vérification ; renvoie le délai avant la suivante.
    private async Task<TimeSpan> RunCheckAsync(CancellationToken stoppingToken)
    {
        bool forced = Interlocked.Exchange(ref forceRenewalRequested, 0) == 1;
        AppLocalSettings settings = await settingsStore.LoadAsync();
        EncryptionSettingsData encryption = settings.Encryption;
        LetsEncryptSettingsData letsEncrypt = encryption.LetsEncrypt;
        appliedConfiguration = BuildConfigurationSnapshot(encryption);

        DateTime now = DateTime.UtcNow;
        ManagedCertificate? current = ManagedCertificateFiles.TryRead(out string? readProblem);
        DateTime? currentDueUtc = current is null ? null : LetsEncryptPolicy.GetRenewalDueUtc(current.Summary);

        if (!letsEncrypt.Enabled)
        {
            ResetFailures();
            SetStatus(new LetsEncryptStatus(LetsEncryptState.Disabled, null, current, currentDueUtc, null, now));
            return CheckInterval;
        }

        string? configurationProblem = LetsEncryptPolicy.ValidateConfiguration(encryption, out IReadOnlyList<string> domains);

        if (configurationProblem is not null)
        {
            // Nouvelle vérification dès l'enregistrement de la configuration corrigée (OnSettingsChanged).
            SetStatus(new LetsEncryptStatus(LetsEncryptState.NotConfigured, configurationProblem, current, currentDueUtc, null, now));
            return CheckInterval;
        }

        string directoryUrl = letsEncrypt.UseStaging ? options.Value.StagingDirectoryUrl : options.Value.ProductionDirectoryUrl;
        string? reason = forced ? "renouvellement demandé" : GetRenewalReason(current, readProblem, domains, directoryUrl, now);

        if (reason is null)
        {
            ResetFailures();
            return ScheduleValid(current!, now);
        }

        if (!forced && retryNotBeforeUtc is { } notBefore && now < notBefore)
        {
            // Attente après un échec : l'état « Error » affiché reste celui de la dernière tentative.
            return notBefore - now;
        }

        logger.LogInformation("Let's Encrypt : demande d'un certificat pour {Domains} ({Reason}).", string.Join(", ", domains), reason);
        SetStatus(new LetsEncryptStatus(LetsEncryptState.Issuing, "Préparation…", current, currentDueUtc, null, now));

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(IssuanceTimeout);
            await IssueAsync(letsEncrypt, domains, directoryUrl, timeout.Token);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            consecutiveFailures++;
            TimeSpan retryDelay = TimeSpan.FromTicks(Math.Min(CheckInterval.Ticks, FirstRetryDelay.Ticks << Math.Min(consecutiveFailures - 1, 4)));
            retryNotBeforeUtc = now + retryDelay;

            string message = DescribeFailure(ex, letsEncrypt.ChallengePort);
            logger.LogWarning("Let's Encrypt : échec de l'obtention du certificat ({Message}). Nouvel essai à {Retry:HH:mm} UTC.", message, retryNotBeforeUtc);
            SetStatus(new LetsEncryptStatus(LetsEncryptState.Error, message, current, currentDueUtc, retryNotBeforeUtc, now));
            return retryDelay;
        }

        ResetFailures();
        ManagedCertificate issued = ManagedCertificateFiles.TryRead(out string? issuedProblem)
            ?? throw new InvalidOperationException("Certificat enregistré illisible : " + issuedProblem);

        await settingsStore.UpdateAsync(latest => latest.Encryption.LetsEncrypt.CertificateThumbprint = issued.Summary.Thumbprint);
        logger.LogInformation(
            "Let's Encrypt : certificat obtenu pour {Domains}, valable jusqu'au {NotAfter:yyyy-MM-dd} UTC.",
            string.Join(", ", issued.Summary.DnsNames),
            issued.Summary.NotAfterUtc);

        return ScheduleValid(issued, DateTime.UtcNow);
    }

    private TimeSpan ScheduleValid(ManagedCertificate certificate, DateTime now)
    {
        DateTime dueUtc = LetsEncryptPolicy.GetRenewalDueUtc(certificate.Summary);
        TimeSpan untilDue = dueUtc - now;
        TimeSpan delay = untilDue > TimeSpan.Zero && untilDue < CheckInterval ? untilDue : CheckInterval;
        SetStatus(new LetsEncryptStatus(LetsEncryptState.Valid, null, certificate, dueUtc, now + delay, now));
        return delay;
    }

    // Null si le certificat en place convient, sinon la raison d'en demander un nouveau.
    private static string? GetRenewalReason(ManagedCertificate? current, string? readProblem, IReadOnlyList<string> domains, string directoryUrl, DateTime now)
    {
        if (current is null)
        {
            return readProblem ?? "aucun certificat";
        }

        if (!string.Equals(current.DirectoryUrl, directoryUrl, StringComparison.Ordinal))
        {
            return "changement d'environnement Let's Encrypt";
        }

        HashSet<string> wanted = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);

        if (!wanted.SetEquals(current.Summary.DnsNames))
        {
            return "noms du certificat modifiés";
        }

        DateTime dueUtc = LetsEncryptPolicy.GetRenewalDueUtc(current.Summary);
        return now >= dueUtc ? $"expire le {current.Summary.NotAfterUtc:yyyy-MM-dd}" : null;
    }

    private async Task IssueAsync(LetsEncryptSettingsData settings, IReadOnlyList<string> domains, string directoryUrl, CancellationToken cancellationToken)
    {
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        AcmeAccountData? account = ManagedCertificateFiles.LoadAccount(directoryUrl);
        using ECDsa accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (account is not null)
        {
            accountKey.ImportFromPem(account.PrivateKeyPem);
        }

        AcmeClient client = new AcmeClient(httpClient, directoryUrl, accountKey, account?.AccountUrl);

        if (account is null)
        {
            ReportProgress("Création du compte Let's Encrypt…");
            string accountUrl = await client.RegisterAccountAsync(settings.Email, cancellationToken);
            ManagedCertificateFiles.SaveAccount(new AcmeAccountData
            {
                DirectoryUrl = directoryUrl,
                AccountUrl = accountUrl,
                PrivateKeyPem = accountKey.ExportPkcs8PrivateKeyPem(),
            });
        }

        ReportProgress("Commande du certificat…");
        AcmeOrder order = await client.CreateOrderAsync(domains, cancellationToken);
        List<string> publishedTokens = new List<string>();
        AcmeHttpChallengeListener? listener = null;
        bool listenerAttempted = false;

        try
        {
            foreach (string authorizationUrl in order.Authorizations)
            {
                AcmeAuthorization authorization = await client.GetAuthorizationAsync(authorizationUrl, cancellationToken);

                // Autorisation encore valide d'une émission récente : pas de nouveau défi.
                if (authorization.Status == "valid")
                {
                    continue;
                }

                if (authorization.Status != "pending")
                {
                    throw new AcmeException($"validation de {authorization.Domain} impossible ({authorization.FailureDetail ?? authorization.Status})", "validation");
                }

                AcmeChallenge challenge = authorization.Challenges.FirstOrDefault(candidate => candidate.Type == "http-01")
                    ?? throw new AcmeException($"aucun défi HTTP-01 proposé pour {authorization.Domain}");

                challengeStore.Add(challenge.Token, client.GetKeyAuthorization(challenge.Token));
                publishedTokens.Add(challenge.Token);

                if (!listenerAttempted)
                {
                    listenerAttempted = true;
                    listener = AcmeHttpChallengeListener.TryStart(settings.ChallengePort, challengeStore, logger, out string? failure);

                    if (listener is null)
                    {
                        logger.LogInformation("Let's Encrypt : écoute de validation non ouverte ({Failure}) ; défis servis par l'interface web.", failure);
                    }
                }

                ReportProgress($"Validation de {authorization.Domain}…");
                await client.AcceptChallengeAsync(challenge.Url, cancellationToken);
                await WaitForAuthorizationAsync(client, authorizationUrl, cancellationToken);
            }

            ReportProgress("Émission du certificat…");
            using ECDsa certificateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            order = await WaitForOrderAsync(client, order, "ready", cancellationToken);

            if (order.Status == "ready")
            {
                order = await client.FinalizeAsync(order, BuildCertificateSigningRequest(certificateKey, domains), cancellationToken);
            }

            order = await WaitForOrderAsync(client, order, "valid", cancellationToken);
            string certificateChainPem = await client.DownloadCertificateAsync(
                order.Certificate ?? throw new AcmeException("commande validée sans certificat"),
                cancellationToken);
            string privateKeyPem = certificateKey.ExportPkcs8PrivateKeyPem();

            // Vérification avant de remplacer quoi que ce soit : chaîne lisible, clé correspondante, noms attendus.
            using (X509Certificate2 issued = X509Certificate2.CreateFromPem(certificateChainPem, privateKeyPem))
            {
                CertificateSummary summary = CertificateSummary.FromCertificate(issued);
                string[] missing = domains.Where(domain => !summary.Covers(domain)).ToArray();

                if (missing.Length > 0)
                {
                    throw new AcmeException("certificat reçu sans les noms " + string.Join(", ", missing));
                }
            }

            ManagedCertificateFiles.Save(certificateChainPem, privateKeyPem, new ManagedCertificateMetadata(directoryUrl, DateTime.UtcNow));
        }
        finally
        {
            foreach (string token in publishedTokens)
            {
                challengeStore.Remove(token);
            }

            if (listener is not null)
            {
                await listener.DisposeAsync();
            }
        }
    }

    private static async Task WaitForAuthorizationAsync(AcmeClient client, string authorizationUrl, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);
            AcmeAuthorization authorization = await client.GetAuthorizationAsync(authorizationUrl, cancellationToken);

            if (authorization.Status == "valid")
            {
                return;
            }

            if (authorization.Status != "pending")
            {
                throw new AcmeException($"validation de {authorization.Domain} refusée : {authorization.FailureDetail ?? authorization.Status}", "validation");
            }
        }
    }

    // Attend que la commande atteigne l'état voulu (« ready » : toutes les autorisations validées ; « valid » :
    // certificat émis). Une commande déjà plus avancée que « ready » est rendue telle quelle.
    private static async Task<AcmeOrder> WaitForOrderAsync(AcmeClient client, AcmeOrder order, string expected, CancellationToken cancellationToken)
    {
        while (order.Status != expected && !(expected == "ready" && order.Status is "processing" or "valid"))
        {
            if (order.Status == "invalid")
            {
                throw new AcmeException("commande refusée par Let's Encrypt : " + (order.Error ?? "raison inconnue"));
            }

            await Task.Delay(PollInterval, cancellationToken);
            order = await client.GetOrderAsync(order.Url, cancellationToken);
        }

        return order;
    }

    private static byte[] BuildCertificateSigningRequest(ECDsa certificateKey, IReadOnlyList<string> domains)
    {
        X500DistinguishedNameBuilder subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(domains[0]);

        CertificateRequest request = new CertificateRequest(subject.Build(), certificateKey, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder alternativeNames = new SubjectAlternativeNameBuilder();

        foreach (string domain in domains)
        {
            alternativeNames.AddDnsName(domain);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());
        return request.CreateSigningRequest();
    }

    private static string DescribeFailure(Exception exception, int challengePort)
    {
        switch (exception)
        {
            case AcmeException acme when acme.ProblemType is "validation"
                or "urn:ietf:params:acme:error:connection"
                or "urn:ietf:params:acme:error:unauthorized"
                or "urn:ietf:params:acme:error:incorrectResponse"
                or "urn:ietf:params:acme:error:dns":
                string portHint = challengePort == 80 ? "le port 80" : $"le port 80 (redirigé vers le port {challengePort} de cette machine)";
                return Capitalize(acme.Message) + $". Vérifiez que le nom pointe vers l'adresse publique de cette machine et que {portHint} est joignable depuis Internet.";
            case AcmeException acme:
                return Capitalize(acme.Message);
            case HttpRequestException http:
                return "Let's Encrypt injoignable : " + http.Message;
            case OperationCanceledException:
                return $"Délai dépassé ({IssuanceTimeout.TotalMinutes:0} minutes) : Let's Encrypt n'a pas terminé la validation.";
            default:
                return exception.Message;
        }
    }

    private static string Capitalize(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    // Réglages dont dépend le certificat (hors empreinte, que ce service écrit lui-même).
    private static string BuildConfigurationSnapshot(EncryptionSettingsData encryption)
    {
        LetsEncryptSettingsData letsEncrypt = encryption.LetsEncrypt;
        return JsonSerializer.Serialize(new
        {
            encryption.ServerName,
            letsEncrypt.Enabled,
            letsEncrypt.AdditionalDomains,
            letsEncrypt.Email,
            letsEncrypt.UseStaging,
            letsEncrypt.AcceptTermsOfService,
            letsEncrypt.ChallengePort,
        });
    }

    private void OnSettingsChanged()
    {
        _ = CheckConfigurationChangeAsync();
    }

    // Configuration du certificat modifiée : vérification immédiate, sans attendre l'échéance d'un échec.
    private async Task CheckConfigurationChangeAsync()
    {
        try
        {
            AppLocalSettings settings = await settingsStore.LoadAsync();

            if (!string.Equals(BuildConfigurationSnapshot(settings.Encryption), appliedConfiguration, StringComparison.Ordinal))
            {
                ResetFailures();
                Wake();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Let's Encrypt : relecture des paramètres impossible.");
        }
    }

    private void ResetFailures()
    {
        consecutiveFailures = 0;
        retryNotBeforeUtc = null;
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

    private void ReportProgress(string message)
    {
        lock (statusLock)
        {
            status = status with { Message = message };
        }

        StatusChanged?.Invoke();
    }

    private void SetStatus(LetsEncryptStatus newStatus)
    {
        lock (statusLock)
        {
            status = newStatus;
        }

        StatusChanged?.Invoke();
    }

    private async Task<bool> WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        using (stoppingToken.Register(() => started.TrySetCanceled()))
        {
            try
            {
                await started.Task;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
