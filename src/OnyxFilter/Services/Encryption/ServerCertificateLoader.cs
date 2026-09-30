using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.Encryption;

// Charge le certificat serveur configuré dans la page "Paramètres de chiffrement"
// (/settings/encryption), pour les services chiffrés (DNS-over-TLS, et à terme HTTPS/DNS-over-QUIC).
// Formats acceptés :
//  - PEM : certificat (fichier ou contenu collé) + clé privée (fichier ou contenu collé). Si le champ
//    de clé privée est vide, la clé est recherchée dans le PEM du certificat (fichier combiné, ex.
//    "fullchain + clé"). Les certificats supplémentaires du PEM sont envoyés comme chaîne intermédiaire.
//  - PKCS#12 (.pfx/.p12, sans mot de passe) : uniquement via "Chemin du fichier" du certificat ; les
//    champs de clé privée sont alors ignorés (la clé est déjà dans le fichier).
// Lève InvalidOperationException avec un message exploitable dans les journaux si la configuration est
// incomplète ou invalide.
public static class ServerCertificateLoader
{
    public static LoadedServerCertificate Load(EncryptionSettingsData settings)
    {
        // Windows (SChannel) ne sait pas utiliser une clé privée éphémère côté serveur : la clé doit
        // être persistée (magasin utilisateur temporaire). Ailleurs (Linux/OpenSSL), la clé reste en
        // mémoire, sans écriture sur le disque.
        X509KeyStorageFlags storageFlags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        if (IsPkcs12File(settings))
        {
            return LoadFromPkcs12File(settings.CertificateFilePath, storageFlags);
        }

        return LoadFromPem(settings, storageFlags);
    }

    private static bool IsPkcs12File(EncryptionSettingsData settings)
    {
        if (settings.CertificateSource != CertificateInputSource.FilePath)
        {
            return false;
        }

        string extension = Path.GetExtension(settings.CertificateFilePath);

        return string.Equals(extension, ".pfx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".p12", StringComparison.OrdinalIgnoreCase);
    }

    private static LoadedServerCertificate LoadFromPkcs12File(string filePath, X509KeyStorageFlags storageFlags)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Fichier de certificat introuvable : {filePath}");
        }

        X509Certificate2Collection collection;

        try
        {
            collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(filePath, password: null, storageFlags);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Impossible de lire le fichier PKCS#12 '{filePath}' (seuls les fichiers sans mot de passe sont pris en charge) : {ex.Message}",
                ex);
        }

        X509Certificate2? leafCertificate = null;
        X509Certificate2Collection intermediateCertificates = new X509Certificate2Collection();

        foreach (X509Certificate2 certificate in collection)
        {
            if (leafCertificate is null && certificate.HasPrivateKey)
            {
                leafCertificate = certificate;
            }
            else
            {
                intermediateCertificates.Add(certificate);
            }
        }

        if (leafCertificate is null)
        {
            throw new InvalidOperationException($"Le fichier PKCS#12 '{filePath}' ne contient aucun certificat avec sa clé privée.");
        }

        return new LoadedServerCertificate(leafCertificate, intermediateCertificates);
    }

    private static LoadedServerCertificate LoadFromPem(EncryptionSettingsData settings, X509KeyStorageFlags storageFlags)
    {
        string certificatePem = ReadSource(
            settings.CertificateSource,
            settings.CertificateFilePath,
            settings.CertificateContent,
            "certificat");

        // Clé privée facultative dans sa propre source : si les deux champs sont vides, elle est
        // recherchée dans le PEM du certificat (fichier combiné certificat + clé).
        string privateKeyPem;

        if (HasValue(settings.PrivateKeySource, settings.PrivateKeyFilePath, settings.PrivateKeyContent))
        {
            privateKeyPem = ReadSource(
                settings.PrivateKeySource,
                settings.PrivateKeyFilePath,
                settings.PrivateKeyContent,
                "clé privée");
        }
        else if (certificatePem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            privateKeyPem = certificatePem;
        }
        else
        {
            throw new InvalidOperationException(
                "Aucune clé privée configurée : renseignez le chemin ou le contenu de la clé privée dans les paramètres de chiffrement.");
        }

        X509Certificate2 leafWithKey;

        try
        {
            using X509Certificate2 parsedLeaf = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);

            // CreateFromPem produit une clé éphémère : ré-import PKCS#12 avec les indicateurs de
            // stockage adaptés à la plateforme (voir Load), indispensable pour SslStream sous Windows.
            leafWithKey = X509CertificateLoader.LoadPkcs12(
                parsedLeaf.Export(X509ContentType.Pkcs12),
                password: null,
                storageFlags);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Certificat ou clé privée PEM invalide : {ex.Message}", ex);
        }

        // Certificats supplémentaires du PEM (après le premier) : chaîne intermédiaire envoyée aux
        // clients TLS (ex. fichier "fullchain.pem" de Let's Encrypt).
        X509Certificate2Collection allCertificates = new X509Certificate2Collection();
        allCertificates.ImportFromPem(certificatePem);

        X509Certificate2Collection intermediateCertificates = new X509Certificate2Collection();

        for (int index = 0; index < allCertificates.Count; index++)
        {
            if (index == 0)
            {
                allCertificates[index].Dispose();
                continue;
            }

            intermediateCertificates.Add(allCertificates[index]);
        }

        return new LoadedServerCertificate(leafWithKey, intermediateCertificates);
    }

    private static bool HasValue(CertificateInputSource source, string filePath, string content)
    {
        return source == CertificateInputSource.FilePath
            ? !string.IsNullOrWhiteSpace(filePath)
            : !string.IsNullOrWhiteSpace(content);
    }

    private static string ReadSource(CertificateInputSource source, string filePath, string content, string label)
    {
        if (source == CertificateInputSource.FilePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new InvalidOperationException($"Aucun chemin de fichier configuré pour le {label}.");
            }

            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Fichier de {label} introuvable : {filePath}");
            }

            return File.ReadAllText(filePath);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Aucun contenu collé pour le {label}.");
        }

        return content;
    }
}
