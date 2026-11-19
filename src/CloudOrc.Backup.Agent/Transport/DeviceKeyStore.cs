using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CloudOrc.Backup.Agent.Platform;

namespace CloudOrc.Backup.Agent.Transport;

/// <summary>
/// The Backup Agent's device identity key (machine identity proof, mTLS). ECDSA P-256.
/// <list type="bullet">
/// <item>Windows: Windows machine key store (CNG), created NON-EXPORTABLE - the private key never
/// leaves CNG and does not survive a reimage (which correctly forces re-activation).</item>
/// <item>Linux / macOS: PKCS#8 file <c>{data}/keys/device-key.pem</c>, mode 0600 inside a 0700
/// root-owned directory - readable by root only, the same protection SSH host keys get.</item>
/// </list>
/// The key name differs from the CloudOrc Server Agent's on purpose: on a VPS where both agents are
/// installed they are two separate identities.
/// Used to sign session-renewal proofs and to create the CSR for an mTLS client certificate.
/// </summary>
public sealed class DeviceKeyStore
{
    public const string DefaultKeyName = "CloudOrc.Backup.DeviceKey.v1";
    private const string UnixKeyFile = "device-key.pem";
    private const string UnixCertFile = "client-cert.pem";

    private readonly string _keyName;
    private readonly string _keyDirectory;

    public DeviceKeyStore(string keyName = DefaultKeyName, string? keyDirectory = null)
    {
        _keyName = keyName;
        _keyDirectory = keyDirectory ?? DefaultKeyDirectory;
    }

    /// <summary>Where the Unix key and client certificate live (ignored on Windows).</summary>
    public static string DefaultKeyDirectory { get; set; } = Path.Combine(AgentPlatform.DefaultDataDirectory, "keys");

    public bool Exists() => OperatingSystem.IsWindows()
        ? CngKey.Exists(_keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)
        : File.Exists(Path.Combine(_keyDirectory, UnixKeyFile));

    /// <summary>Opens the key, creating it on first use.</summary>
    public ECDsa OpenOrCreate()
    {
        if (OperatingSystem.IsWindows())
        {
            var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
            var key = CngKey.Exists(_keyName, provider, CngKeyOpenOptions.MachineKey)
                ? CngKey.Open(_keyName, provider, CngKeyOpenOptions.MachineKey)
                : CngKey.Create(CngAlgorithm.ECDsaP256, _keyName, new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                });
            return new ECDsaCng(key);
        }

        var path = Path.Combine(_keyDirectory, UnixKeyFile);
        var ecdsa = ECDsa.Create();
        if (File.Exists(path))
        {
            ecdsa.ImportFromPem(File.ReadAllText(path));
            return ecdsa;
        }

        ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
        SecureFile.WriteAllText(path, ecdsa.ExportPkcs8PrivateKeyPem());
        return ecdsa;
    }

    /// <summary>Base64 SubjectPublicKeyInfo of the device key (safe to send/store anywhere).</summary>
    public string GetPublicKeySpki()
    {
        using var key = OpenOrCreate();
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>DER (RFC 3279) ECDSA-SHA256 signature - the format Node's crypto.verify and OpenSSL expect.</summary>
    public string Sign(string payload)
    {
        using var key = OpenOrCreate();
        var signature = key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    /// <summary>PKCS#10 CSR for an mTLS client certificate bound to the device key.</summary>
    public string CreateCsrPem(string subjectCommonName)
    {
        using var key = OpenOrCreate();
        var request = new CertificateRequest(new X500DistinguishedName($"CN={Sanitize(subjectCommonName)}"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false)); // clientAuth
        return request.CreateSigningRequestPem();
    }

    /// <summary>
    /// Binds a backend-issued client certificate to the device key and stores it (Windows:
    /// LocalMachine\My; Linux/macOS: client-cert.pem next to the key). Returns its thumbprint.
    /// </summary>
    public string InstallClientCertificate(string certificatePem)
    {
        using var publicOnly = X509Certificate2.CreateFromPem(certificatePem);
        using var key = OpenOrCreate();
        using var withKey = publicOnly.CopyWithPrivateKey(key);

        if (OperatingSystem.IsWindows())
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(withKey);
        }
        else
        {
            SecureFile.WriteAllText(Path.Combine(_keyDirectory, UnixCertFile), certificatePem);
        }

        return withKey.Thumbprint;
    }

    public static X509Certificate2? FindClientCertificate(string? thumbprint) =>
        new DeviceKeyStore().FindInstalledClientCertificate(thumbprint);

    public X509Certificate2? FindInstalledClientCertificate(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return null;
        }

        X509Certificate2? cert = null;
        if (OperatingSystem.IsWindows())
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            cert = matches.Count > 0 ? matches[0] : null;
        }
        else
        {
            var certPath = Path.Combine(_keyDirectory, UnixCertFile);
            if (!File.Exists(certPath) || !Exists())
            {
                return null;
            }

            try
            {
                using var publicOnly = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
                if (!string.Equals(publicOnly.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                using var key = OpenOrCreate();
                cert = publicOnly.CopyWithPrivateKey(key);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                return null;
            }
        }

        return cert is { HasPrivateKey: true } && cert.NotAfter > DateTime.Now ? cert : null;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_').ToArray();
        return chars.Length == 0 ? "cloudorc-backup-agent" : new string(chars);
    }
}
