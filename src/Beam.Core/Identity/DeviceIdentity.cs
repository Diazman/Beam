using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Beam.Core.Diagnostics;
using Beam.Core.Storage;

namespace Beam.Core.Identity;

/// <summary>
/// The cryptographic identity of this installation: a stable device id plus a self-signed
/// certificate used for mutual TLS. Other devices recognise us by the certificate fingerprint,
/// which cannot be forged without the private key.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private DeviceIdentity(string deviceId, X509Certificate2 certificate)
    {
        DeviceId = deviceId;
        Certificate = certificate;
        Fingerprint = ComputeFingerprint(certificate);
    }

    public string DeviceId { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>Lower-case hex SHA-256 of the certificate.</summary>
    public string Fingerprint { get; }

    public static DeviceIdentity LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.StoredIdentity);
                if (stored != null && !string.IsNullOrEmpty(stored.DeviceId) && !string.IsNullOrEmpty(stored.Certificate))
                {
                    var pfx = SecretProtector.Unprotect(Convert.FromBase64String(stored.Certificate), stored.Protected);
                    var certificate = LoadCertificate(pfx);
                    if (certificate.HasPrivateKey && certificate.NotAfter > DateTime.Now.AddDays(1))
                        return new DeviceIdentity(stored.DeviceId, certificate);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Device identity could not be loaded; creating a new one", ex);
        }

        return Create(path);
    }

    public static string ComputeFingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    /// <summary>Human-friendly short form of a fingerprint, e.g. "4F2A-91C3".</summary>
    public static string ShortCode(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length < 8) return fingerprint.ToUpperInvariant();
        return $"{fingerprint[..4]}-{fingerprint[4..8]}".ToUpperInvariant();
    }

    public void Dispose() => Certificate.Dispose();

    private static DeviceIdentity Create(string path)
    {
        var deviceId = Guid.NewGuid().ToString("N");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={AppInfo.ProductName} device {deviceId[..8]}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(25));
        var pfx = generated.Export(X509ContentType.Pkcs12);

        try
        {
            var stored = new StoredIdentity
            {
                DeviceId = deviceId,
                Protected = SecretProtector.CanProtect,
                Certificate = Convert.ToBase64String(SecretProtector.Protect(pfx)),
            };
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(stored, StorageJson.Default.StoredIdentity));
            SecretProtector.RestrictToCurrentUser(path);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save device identity; it will be regenerated next time", ex);
        }

        Log.Info($"Created new device identity {deviceId}");
        return new DeviceIdentity(deviceId, LoadCertificate(pfx));
    }

    private static X509Certificate2 LoadCertificate(byte[] pfx)
    {
        // Windows' TLS stack (SChannel) cannot use ephemeral keys, so the key is imported into the
        // user key store for the lifetime of the process. Elsewhere an ephemeral key is fine.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+
        return new X509Certificate2(pfx, (string?)null, flags);
#pragma warning restore SYSLIB0057
    }
}

internal sealed class StoredIdentity
{
    public string DeviceId { get; set; } = "";

    /// <summary>Base64 PKCS#12, DPAPI-protected when <see cref="Protected"/> is true.</summary>
    public string Certificate { get; set; } = "";

    public bool Protected { get; set; }
}
