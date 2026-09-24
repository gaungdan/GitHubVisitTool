using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GitHubTurbo.Proxy;

public sealed class CertificateManager : IDisposable
{
    const string CaPassword = "GitHubTurbo";
    const string CaSubject = "CN=GitHubTurbo Root CA, O=GitHubTurbo, OU=Network Acceleration";

    readonly ConcurrentDictionary<string, X509Certificate2> _domainCerts = new(StringComparer.OrdinalIgnoreCase);
    readonly string _pfxPath;
    readonly string _cerPath;

    public X509Certificate2 CaCert { get; }

    public string CerFilePath => _cerPath;

    public CertificateManager(string? certificateDirectory = null)
    {
        var directory = certificateDirectory ?? AppPaths.CertificateDirectory;
        Directory.CreateDirectory(directory);
        _pfxPath = Path.Combine(directory, "githubturbo-ca.pfx");
        _cerPath = Path.Combine(directory, "githubturbo-ca.cer");
        CaCert = LoadOrCreateCa();
    }

    X509Certificate2 LoadOrCreateCa()
    {
        if (File.Exists(_pfxPath))
        {
            try
            {
                var existing = OpenPfx(File.ReadAllBytes(_pfxPath), CaPassword);
                if (existing.HasPrivateKey && existing.NotAfter > DateTime.UtcNow.AddDays(30))
                    return existing;
            }
            catch
            {
                // Corrupted pfx — fall through and regenerate.
            }
        }

        using var caKey = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(CaSubject),
            caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pkcs12, CaPassword));
        File.WriteAllBytes(_cerPath, cert.Export(X509ContentType.Cert));

        return OpenPfx(File.ReadAllBytes(_pfxPath), CaPassword);
    }

    /// <summary>
    /// Import a PFX with a persistent key container. Ephemeral keys cannot be used
    /// for Windows Schannel server-side TLS (AcquireCredentialsHandle fails).
    /// </summary>
    static X509Certificate2 OpenPfx(byte[] pfxBytes, string? password)
        => new(pfxBytes, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    public X509Certificate2 GetOrCreateServerCert(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            domain = "github.com";

        return _domainCerts.GetOrAdd(domain, CreateServerCert);
    }

    X509Certificate2 CreateServerCert(string domain)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName($"CN={domain}"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;

        using var signed = request.Create(CaCert, notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return OpenPfx(withKey.Export(X509ContentType.Pkcs12), password: null);
    }

    public bool IsRootTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, CaCert.Thumbprint, false)
                .Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TrustRootCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, CaCert.Thumbprint, false).Count == 0)
                store.Add(CaCert);
            return IsRootTrusted();
        }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync($"Trust root certificate failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void RemoveRootCertificate()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, CaCert.Thumbprint, false);
            foreach (var cert in found)
                store.Remove(cert);
        }
        catch (Exception ex)
        {
            _ = AppLogger.ErrorAsync($"Remove root certificate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var cert in _domainCerts.Values)
            cert.Dispose();
        _domainCerts.Clear();
        CaCert.Dispose();
    }
}
