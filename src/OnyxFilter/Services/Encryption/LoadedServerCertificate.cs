using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace OnyxFilter.Services.Encryption;

// Certificat serveur prêt à l'emploi pour SslStream : le contexte pré-assemblé (certificat + clé
// privée + chaîne intermédiaire) évite de reconstruire la chaîne à chaque poignée de main TLS, ce qui
// compte sur un matériel modeste (Raspberry Pi 3+). À libérer via Dispose lors d'un rechargement de la
// configuration.
public sealed class LoadedServerCertificate : IDisposable
{
    private readonly X509Certificate2 leafCertificate;
    private readonly X509Certificate2Collection intermediateCertificates;

    public LoadedServerCertificate(X509Certificate2 leafCertificate, X509Certificate2Collection intermediateCertificates)
    {
        this.leafCertificate = leafCertificate;
        this.intermediateCertificates = intermediateCertificates;
        Context = SslStreamCertificateContext.Create(leafCertificate, intermediateCertificates, offline: true);
    }

    public SslStreamCertificateContext Context { get; }

    public string Subject
    {
        get
        {
            return leafCertificate.Subject;
        }
    }

    public DateTime NotAfterUtc
    {
        get
        {
            return leafCertificate.NotAfter.ToUniversalTime();
        }
    }

    public void Dispose()
    {
        leafCertificate.Dispose();

        foreach (X509Certificate2 intermediateCertificate in intermediateCertificates)
        {
            intermediateCertificate.Dispose();
        }
    }
}
