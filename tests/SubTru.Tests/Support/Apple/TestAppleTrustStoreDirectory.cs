using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SubTru.Tests.Support.Apple;

internal static class TestAppleTrustStoreDirectory
{
    private static readonly Lazy<string> DirectoryPathValue = new(CreateDirectory, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object CleanupLock = new();
    private static string? _createdDirectoryPath;

    static TestAppleTrustStoreDirectory()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory();
        AppDomain.CurrentDomain.DomainUnload += (_, _) => TryDeleteDirectory();
    }

    public static string DirectoryPath => DirectoryPathValue.Value;

    private static string CreateDirectory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"subtru-apple-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);

        using var root = TestAppleCertificateFactory.CreateRootCertificate();
        File.WriteAllBytes(Path.Combine(directoryPath, "apple-root.cer"), root.Export(X509ContentType.Cert));

        _createdDirectoryPath = directoryPath;
        return directoryPath;
    }

    private static void TryDeleteDirectory()
    {
        lock (CleanupLock)
        {
            if (string.IsNullOrWhiteSpace(_createdDirectoryPath) || !Directory.Exists(_createdDirectoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(_createdDirectoryPath, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test-only temp artifacts.
            }
        }
    }
}

internal static class TestAppleCertificateFactory
{
    public static X509Certificate2 CreateRootCertificate(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string subject = "CN=Apple Root CA - G3, O=Apple Inc., C=US",
        X509KeyUsageFlags? keyUsages = null)
    {
        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var end = notAfter ?? DateTimeOffset.UtcNow.AddDays(30);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            subject,
            rootKey,
            HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 1, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        if (keyUsages.HasValue)
        {
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsages.Value, critical: true));
        }

        return rootRequest.CreateSelfSigned(start, end);
    }
}
