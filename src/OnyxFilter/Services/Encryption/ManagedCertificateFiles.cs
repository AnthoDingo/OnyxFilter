using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace OnyxFilter.Services.Encryption;

// Fichiers du certificat Let's Encrypt, dans le dossier « letsencrypt » du dossier des données (le répertoire
// courant du processus, voir DataDirectory) :
//   certificate.pem     chaîne complète (certificat puis intermédiaires), réutilisable ailleurs au besoin ;
//   private-key.pem     clé privée du certificat (lisible par le seul compte du service) ;
//   certificate.json    annuaire ACME d'origine et date d'émission ;
//   account-<hôte>.json compte ACME (clé et adresse), un par autorité.
// Les écritures passent par un fichier temporaire puis un renommage : jamais de fichier à moitié écrit.
public static class ManagedCertificateFiles
{
    public const string DirectoryName = "letsencrypt";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    public static string Root => Path.GetFullPath(DirectoryName);

    public static string CertificatePath => Path.Combine(Root, "certificate.pem");

    public static string PrivateKeyPath => Path.Combine(Root, "private-key.pem");

    private static string MetadataPath => Path.Combine(Root, "certificate.json");

    // Certificat en place, vérifié (lisible, clé correspondante). Null s'il n'y en a pas encore, ou s'il est
    // inutilisable : problem en donne alors la raison.
    public static ManagedCertificate? TryRead(out string? problem)
    {
        problem = null;

        if (!File.Exists(CertificatePath) || !File.Exists(PrivateKeyPath))
        {
            return null;
        }

        CertificateSummary summary;

        try
        {
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(File.ReadAllText(CertificatePath), File.ReadAllText(PrivateKeyPath));
            summary = CertificateSummary.FromCertificate(certificate);
        }
        catch (Exception ex)
        {
            problem = $"certificat Let's Encrypt illisible ({ex.Message})";
            return null;
        }

        ManagedCertificateMetadata? metadata = null;

        try
        {
            if (File.Exists(MetadataPath))
            {
                metadata = JsonSerializer.Deserialize<ManagedCertificateMetadata>(File.ReadAllText(MetadataPath));
            }
        }
        catch (JsonException)
        {
            // Métadonnées abîmées : l'origine du certificat devient inconnue, il sera simplement renouvelé.
        }

        return new ManagedCertificate(summary, metadata?.DirectoryUrl, metadata?.IssuedAtUtc);
    }

    public static void Save(string certificateChainPem, string privateKeyPem, ManagedCertificateMetadata metadata)
    {
        EnsureRoot();

        // La clé d'abord : si l'écriture s'interrompait entre les deux, le couple incohérent serait détecté
        // (TryRead) et le certificat redemandé.
        WriteAtomically(PrivateKeyPath, privateKeyPem, secret: true);
        WriteAtomically(CertificatePath, certificateChainPem, secret: false);
        WriteAtomically(MetadataPath, JsonSerializer.Serialize(metadata, JsonOptions), secret: false);
    }

    public static AcmeAccountData? LoadAccount(string directoryUrl)
    {
        string path = AccountPath(directoryUrl);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            AcmeAccountData? account = JsonSerializer.Deserialize<AcmeAccountData>(File.ReadAllText(path));
            return account is not null && string.Equals(account.DirectoryUrl, directoryUrl, StringComparison.Ordinal) ? account : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void SaveAccount(AcmeAccountData account)
    {
        EnsureRoot();
        WriteAtomically(AccountPath(account.DirectoryUrl), JsonSerializer.Serialize(account, JsonOptions), secret: true);
    }

    // Un compte par autorité : « account-acme-v02.api.letsencrypt.org.json ».
    private static string AccountPath(string directoryUrl)
    {
        string name = Uri.TryCreate(directoryUrl, UriKind.Absolute, out Uri? uri)
            ? (uri.IsDefaultPort ? uri.Host : uri.Host + "-" + uri.Port)
            : "default";

        StringBuilder safe = new StringBuilder(name.Length);

        foreach (char character in name)
        {
            safe.Append(char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_');
        }

        return Path.Combine(Root, "account-" + safe + ".json");
    }

    private static void EnsureRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Root);
        }
        else
        {
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void WriteAtomically(string path, string content, bool secret)
    {
        string temporaryPath = path + ".tmp";
        File.Delete(temporaryPath);

        FileStreamOptions options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (secret && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (StreamWriter writer = new StreamWriter(temporaryPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), options))
        {
            writer.Write(content);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed record ManagedCertificateMetadata(string DirectoryUrl, DateTime IssuedAtUtc);

public sealed record ManagedCertificate(CertificateSummary Summary, string? DirectoryUrl, DateTime? IssuedAtUtc);

// Compte ACME enregistré : clé ECDSA P-256 (PEM PKCS#8) et adresse du compte chez l'autorité.
public sealed class AcmeAccountData
{
    public string DirectoryUrl { get; set; } = string.Empty;

    public string AccountUrl { get; set; } = string.Empty;

    public string PrivateKeyPem { get; set; } = string.Empty;
}
