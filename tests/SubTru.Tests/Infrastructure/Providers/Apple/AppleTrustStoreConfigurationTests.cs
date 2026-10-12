using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SubTru.Infrastructure.Providers.Apple;

namespace SubTru.Tests.Infrastructure.Providers.Apple;

public sealed class AppleTrustStoreConfigurationTests
{
    [Theory]
    [InlineData("AppleIncRootCertificate.cer", "1.2.840.113549.1.1.5")]
    [InlineData("AppleRootCA-G2.cer", "1.2.840.113549.1.1.12")]
    [InlineData("AppleRootCA-G3.cer", "1.2.840.10045.4.3.3")]
    public void LoadFromDirectory_OfficialAppleRoot_IsAccepted(string certificateFileName, string expectedSignatureAlgorithmOid)
    {
        using var tempDirectory = new TemporaryDirectory();
        var sourcePath = Path.Combine(GetOfficialAppleRootsDirectory(), certificateFileName);
        var destinationPath = Path.Combine(tempDirectory.DirectoryPath, certificateFileName);
        File.Copy(sourcePath, destinationPath);

        using var expectedCertificate = X509CertificateLoader.LoadCertificateFromFile(sourcePath);
        using var trustStore = AppleTrustStore.LoadFromDirectory(
            new AppleVerificationOptions
            {
                RootCertificateDirectory = tempDirectory.DirectoryPath
            });

        Assert.Single(trustStore.RootMetadata);
        Assert.Single(trustStore.RootFingerprints);
        Assert.Contains(AppleTrustStore.GetSha256Fingerprint(expectedCertificate), trustStore.RootFingerprints);
        Assert.Equal(expectedSignatureAlgorithmOid, expectedCertificate.SignatureAlgorithm.Value);
    }

    [Fact]
    public void LoadFromDirectory_OfficialAppleRootBundle_IsAccepted()
    {
        using var tempDirectory = new TemporaryDirectory();

        foreach (var sourcePath in Directory.EnumerateFiles(GetOfficialAppleRootsDirectory(), "*.cer"))
        {
            File.Copy(sourcePath, Path.Combine(tempDirectory.DirectoryPath, Path.GetFileName(sourcePath)));
        }

        using var trustStore = AppleTrustStore.LoadFromDirectory(
            new AppleVerificationOptions
            {
                RootCertificateDirectory = tempDirectory.DirectoryPath
            });

        Assert.Equal(3, trustStore.RootMetadata.Count);
        Assert.Equal(3, trustStore.RootFingerprints.Count);
    }

    [Fact]
    public void Startup_MissingAppleTrustDirectory_ThrowsConfigurationError()
    {
        using var factory = new AppleVerificationWebApplicationFactory(rootCertificateDirectory: null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.Contains("AppleVerification__RootCertificateDirectory", exception.Message);
    }

    [Fact]
    public void Startup_NonexistentAppleTrustDirectory_ThrowsConfigurationError()
    {
        using var factory = new AppleVerificationWebApplicationFactory(
            rootCertificateDirectory: Path.Combine(Path.GetTempPath(), $"missing-apple-trust-{Guid.NewGuid():N}"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_EmptyDirectory_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("did not contain any supported certificate files", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_MalformedCertificateFile_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "broken.cer"), new byte[] { 1, 2, 3, 4 });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("could not be loaded", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_PemWithNonCertificateBlock_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(tempDirectory.DirectoryPath, "apple.pem"),
            """
            -----BEGIN PRIVATE KEY-----
            ZmFrZQ==
            -----END PRIVATE KEY-----
            """);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("unexpected PEM block type", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_NonSelfSignedIntermediate_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var root = TestAppleCertificateFactory.CreateRootCertificate();
        using var intermediate = CreateIntermediateCertificate(root);
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "intermediate.cer"), intermediate.Export(X509ContentType.Cert));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("not a self-issued root CA certificate", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_SelfIssuedButNotSelfSignedCertificate_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var issuer = TestAppleCertificateFactory.CreateRootCertificate(subject: "CN=Apple Root CA - G3, O=Apple Inc., C=US");
        using var selfIssuedButNotSelfSigned = CreateSelfIssuedButNotSelfSignedCertificate(issuer);
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "self-issued-not-self-signed.cer"), selfIssuedButNotSelfSigned.Export(X509ContentType.Cert));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("not a self-signed root CA certificate", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_DuplicateRoots_AreCollapsedByFingerprint()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var root = TestAppleCertificateFactory.CreateRootCertificate();

        var bytes = root.Export(X509ContentType.Cert);
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "apple-root-a.cer"), bytes);
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "apple-root-b.cer"), bytes);

        using var trustStore = AppleTrustStore.LoadFromDirectory(
            new AppleVerificationOptions
            {
                RootCertificateDirectory = tempDirectory.DirectoryPath
            });

        Assert.Single(trustStore.RootMetadata);
        Assert.Single(trustStore.RootFingerprints);
    }

    [Fact]
    public void LoadFromDirectory_ExpiredRoot_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var root = TestAppleCertificateFactory.CreateRootCertificate(
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "expired.cer"), root.Export(X509ContentType.Cert));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("outside its validity window", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_NotYetValidRoot_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var root = TestAppleCertificateFactory.CreateRootCertificate(
            notBefore: DateTimeOffset.UtcNow.AddDays(1),
            notAfter: DateTimeOffset.UtcNow.AddDays(10));
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "future.cer"), root.Export(X509ContentType.Cert));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("outside its validity window", exception.Message);
    }

    [Fact]
    public void LoadFromDirectory_RootWithInvalidKeyUsage_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();
        using var root = TestAppleCertificateFactory.CreateRootCertificate(
            keyUsages: X509KeyUsageFlags.DigitalSignature);
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "invalid-key-usage.cer"), root.Export(X509ContentType.Cert));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("did not permit certificate signing", exception.Message);
    }

    [Fact]
    public void AddTrustedRootsTo_DisposedTrustStore_ThrowsObjectDisposedException()
    {
        using var root = TestAppleCertificateFactory.CreateRootCertificate();
        var trustStore = new AppleTrustStore(new[] { root });
        trustStore.Dispose();

        var customTrustStore = new X509Certificate2Collection();
        var exception = Assert.Throws<ObjectDisposedException>(() => trustStore.AddTrustedRootsTo(customTrustStore));

        Assert.Contains(nameof(AppleTrustStore), exception.ObjectName);
    }

    [Fact]
    public void LoadFromDirectory_UnsupportedSignatureAlgorithmOid_ThrowsConfigurationError()
    {
        using var tempDirectory = new TemporaryDirectory();

        var sourcePath = Path.Combine(GetOfficialAppleRootsDirectory(), "AppleRootCA-G2.cer");
        var unsupportedOidBytes = ReplaceOid(
            File.ReadAllBytes(sourcePath),
            oldOid: new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x0C },
            newOid: new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x0E });
        File.WriteAllBytes(Path.Combine(tempDirectory.DirectoryPath, "unsupported-oid.cer"), unsupportedOidBytes);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppleTrustStore.LoadFromDirectory(
                new AppleVerificationOptions
                {
                    RootCertificateDirectory = tempDirectory.DirectoryPath
                }));

        Assert.Contains("unsupported signature algorithm OID '1.2.840.113549.1.1.14'", exception.Message);
    }

    private static X509Certificate2 CreateIntermediateCertificate(X509Certificate2 issuer)
    {
        var notBefore = new DateTimeOffset(issuer.NotBefore).AddSeconds(1);
        var notAfter = new DateTimeOffset(issuer.NotAfter).AddSeconds(-1);
        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Apple Intermediate CA, O=Apple Inc., C=US",
            intermediateKey,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var publicCertificate = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        var certificate = publicCertificate.CopyWithPrivateKey(intermediateKey);
        publicCertificate.Dispose();
        return certificate;
    }

    private static X509Certificate2 CreateSelfIssuedButNotSelfSignedCertificate(X509Certificate2 issuer)
    {
        var notBefore = new DateTimeOffset(issuer.NotBefore).AddSeconds(1);
        var notAfter = new DateTimeOffset(issuer.NotAfter).AddSeconds(-1);
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            issuer.Subject,
            leafKey,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, critical: true));

        var publicCertificate = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        var certificate = publicCertificate.CopyWithPrivateKey(leafKey);
        publicCertificate.Dispose();
        return certificate;
    }

    private static string GetOfficialAppleRootsDirectory()
        => Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Providers",
            "Apple",
            "TestData",
            "AppleRoots");

    private static byte[] ReplaceOid(byte[] certificateBytes, byte[] oldOid, byte[] newOid)
    {
        var replaced = 0;
        var result = (byte[])certificateBytes.Clone();

        for (var i = 0; i <= result.Length - oldOid.Length; i++)
        {
            if (!result.AsSpan(i, oldOid.Length).SequenceEqual(oldOid))
            {
                continue;
            }

            newOid.CopyTo(result.AsSpan(i, newOid.Length));
            replaced++;
            i += oldOid.Length - 1;
        }

        Assert.True(replaced >= 2, "Expected to replace both certificate signature algorithm OID instances.");
        return result;
    }
}

public sealed class AppleVerificationWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string? _rootCertificateDirectory;

    public AppleVerificationWebApplicationFactory(string? rootCertificateDirectory)
    {
        _rootCertificateDirectory = rootCertificateDirectory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestWebHostConfiguration.UseAdminApiKey(builder);
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Port=5432;Database=subtru_test;Username=test;Password=test",
                ["API_KEY_PEPPER"] = "test-pepper",
                ["SUBTRU_ENCRYPTION_KEY"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["WebhookUrls:PublicBaseUrl"] = "https://api.test.subtru.com",
                ["AppleVerification:RootCertificateDirectory"] = _rootCertificateDirectory ?? string.Empty,
                ["SUBTRU_ADMIN_API_KEY"] = "subtru_admin_0123456789abcdef0123456789abcdef"
            };

            configBuilder.AddInMemoryCollection(config);
        });
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), $"subtru-apple-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
