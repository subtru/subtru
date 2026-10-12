using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SubTru.Infrastructure.Providers.Apple;

public sealed class AppleTrustStore : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cer",
        ".crt",
        ".pem"
    };

    private readonly List<X509Certificate2> _rootCertificates;
    private bool _disposed;

    internal AppleTrustStore(IEnumerable<X509Certificate2> rootCertificates)
        : this(rootCertificates, cloneCertificates: true)
    {
    }

    private AppleTrustStore(IEnumerable<X509Certificate2> rootCertificates, bool cloneCertificates)
    {
        ArgumentNullException.ThrowIfNull(rootCertificates);

        _rootCertificates = rootCertificates
            .Select(certificate => cloneCertificates ? CloneCertificate(certificate) : certificate)
            .ToList();

        if (_rootCertificates.Count == 0)
        {
            throw new InvalidOperationException("At least one trusted Apple root certificate is required.");
        }

        RootFingerprints = new HashSet<string>(
            _rootCertificates.Select(GetSha256Fingerprint),
            StringComparer.OrdinalIgnoreCase);

        RootMetadata = _rootCertificates
            .Select(certificate => new AppleTrustRootMetadata(
                certificate.Subject,
                GetSha256Fingerprint(certificate),
                certificate.NotAfter.ToUniversalTime()))
            .ToArray();
    }

    public IReadOnlySet<string> RootFingerprints { get; }
    public IReadOnlyList<AppleTrustRootMetadata> RootMetadata { get; }

    public static AppleTrustStore LoadFromDirectory(AppleVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RootCertificateDirectory))
        {
            throw new InvalidOperationException("Missing required configuration: AppleVerification__RootCertificateDirectory");
        }

        var fullPath = Path.GetFullPath(options.RootCertificateDirectory);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Apple trust bundle directory does not exist: {fullPath}");
        }

        var certificateFiles = Directory.EnumerateFiles(fullPath)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (certificateFiles.Length == 0)
        {
            throw new InvalidOperationException($"Apple trust bundle directory '{fullPath}' did not contain any supported certificate files.");
        }

        var now = DateTimeOffset.UtcNow;
        var certificatesByFingerprint = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in certificateFiles)
            {
                var loadedCertificates = LoadCertificatesFromFile(file);
                var retainedCertificates = new HashSet<X509Certificate2>(ReferenceEqualityComparer.Instance);
                try
                {
                    foreach (var certificate in loadedCertificates)
                    {
                        ValidateRootCertificate(certificate, file, now);

                        var fingerprint = GetSha256Fingerprint(certificate);
                        if (certificatesByFingerprint.ContainsKey(fingerprint))
                        {
                            continue;
                        }

                        certificatesByFingerprint[fingerprint] = certificate;
                        retainedCertificates.Add(certificate);
                    }
                }
                finally
                {
                    foreach (var certificate in loadedCertificates)
                    {
                        if (!retainedCertificates.Contains(certificate))
                        {
                            certificate.Dispose();
                        }
                    }
                }
            }

            if (certificatesByFingerprint.Count == 0)
            {
                throw new InvalidOperationException($"Apple trust bundle directory '{fullPath}' did not contain any valid root certificates.");
            }

            return new AppleTrustStore(certificatesByFingerprint.Values, cloneCertificates: false);
        }
        catch
        {
            foreach (var certificate in certificatesByFingerprint.Values)
            {
                certificate.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var certificate in _rootCertificates)
        {
            certificate.Dispose();
        }

        _disposed = true;
    }

    internal static string GetSha256Fingerprint(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    internal void AddTrustedRootsTo(X509Certificate2Collection customTrustStore)
    {
        ArgumentNullException.ThrowIfNull(customTrustStore);
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var rootCertificate in _rootCertificates)
        {
            customTrustStore.Add(rootCertificate);
        }
    }

    private static IReadOnlyList<X509Certificate2> LoadCertificatesFromFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".pem", StringComparison.OrdinalIgnoreCase)
            ? LoadPemCertificates(path)
            : new[] { LoadDerCertificate(path) };
    }

    private static X509Certificate2 LoadDerCertificate(string path)
    {
        try
        {
            return X509CertificateLoader.LoadCertificateFromFile(path);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' could not be loaded as an X509 certificate.", ex);
        }
    }

    private static IReadOnlyList<X509Certificate2> LoadPemCertificates(string path)
    {
        string pemText;
        try
        {
            pemText = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' could not be read.", ex);
        }

        var certificates = new List<X509Certificate2>();
        var remaining = pemText.AsSpan();
        var foundPemBlock = false;

        try
        {
            while (!remaining.IsEmpty)
            {
                var trimmed = remaining.TrimStart();
                if (trimmed.IsEmpty)
                {
                    break;
                }

                if (!PemEncoding.TryFind(trimmed, out var fields))
                {
                    throw new InvalidOperationException($"Apple trust bundle file '{path}' contained unexpected non-PEM content.");
                }

                if (fields.Location.Start.Value != 0)
                {
                    throw new InvalidOperationException($"Apple trust bundle file '{path}' contained unexpected non-PEM content.");
                }

                foundPemBlock = true;

                var label = trimmed[fields.Label];
                if (!label.SequenceEqual("CERTIFICATE".AsSpan()))
                {
                    throw new InvalidOperationException($"Apple trust bundle file '{path}' contained an unexpected PEM block type '{label.ToString()}'.");
                }

                var body = RemoveWhitespace(trimmed[fields.Base64Data].ToString());

                try
                {
                    certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(body)));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    throw new InvalidOperationException($"Apple trust bundle file '{path}' contained an invalid PEM certificate block.", ex);
                }

                remaining = trimmed[fields.Location.End.Value..];
            }

            if (!foundPemBlock)
            {
                throw new InvalidOperationException($"Apple trust bundle file '{path}' did not contain any PEM blocks.");
            }
        }
        catch
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            throw;
        }

        return certificates;
    }

    private static void ValidateRootCertificate(
        X509Certificate2 certificate,
        string path,
        DateTimeOffset now)
    {
        if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' contained a certificate that was not a self-issued root CA certificate.");
        }

        if (!IsSelfSigned(certificate))
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' contained a certificate that was not a self-signed root CA certificate.");
        }

        var basicConstraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();

        if (basicConstraints is null || !basicConstraints.CertificateAuthority)
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' contained a certificate that was not a CA root.");
        }

        var keyUsage = certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();

        if (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' contained a certificate that did not permit certificate signing.");
        }

        var notBeforeUtc = certificate.NotBefore.ToUniversalTime();
        var notAfterUtc = certificate.NotAfter.ToUniversalTime();
        var nowUtc = now.UtcDateTime;

        if (nowUtc < notBeforeUtc || nowUtc > notAfterUtc)
        {
            throw new InvalidOperationException($"Apple trust bundle file '{path}' contained a certificate outside its validity window.");
        }
    }

    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(ch => !char.IsWhiteSpace(ch)));

    private static bool IsSelfSigned(X509Certificate2 certificate)
    {
        var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
        var certificateSequence = reader.ReadSequence();
        var tbsCertificate = certificateSequence.ReadEncodedValue().ToArray();

        var signatureAlgorithm = certificateSequence.ReadSequence();
        var signatureAlgorithmOid = signatureAlgorithm.ReadObjectIdentifier();

        var signature = certificateSequence.ReadBitString(out _);
        certificateSequence.ThrowIfNotEmpty();
        reader.ThrowIfNotEmpty();

        return VerifyCertificateSignature(certificate, signatureAlgorithmOid, tbsCertificate, signature);
    }

    private static bool VerifyCertificateSignature(
        X509Certificate2 certificate,
        string signatureAlgorithmOid,
        byte[] tbsCertificate,
        byte[] signature)
    {
        return signatureAlgorithmOid switch
        {
            "1.2.840.113549.1.1.5" => VerifyWithRsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA1),
            "1.2.840.10045.4.3.2" => VerifyWithEcdsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA256),
            "1.2.840.10045.4.3.3" => VerifyWithEcdsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA384),
            "1.2.840.10045.4.3.4" => VerifyWithEcdsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA512),
            "1.2.840.113549.1.1.11" => VerifyWithRsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA256),
            "1.2.840.113549.1.1.12" => VerifyWithRsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA384),
            "1.2.840.113549.1.1.13" => VerifyWithRsa(certificate, tbsCertificate, signature, HashAlgorithmName.SHA512),
            _ => throw new InvalidOperationException($"Apple trust bundle contained a certificate with unsupported signature algorithm OID '{signatureAlgorithmOid}'.")
        };
    }

    private static bool VerifyWithEcdsa(
        X509Certificate2 certificate,
        byte[] tbsCertificate,
        byte[] signature,
        HashAlgorithmName hashAlgorithm)
    {
        using var ecdsa = certificate.GetECDsaPublicKey();
        return ecdsa is not null
            && ecdsa.VerifyData(tbsCertificate, signature, hashAlgorithm, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool VerifyWithRsa(
        X509Certificate2 certificate,
        byte[] tbsCertificate,
        byte[] signature,
        HashAlgorithmName hashAlgorithm)
    {
        using var rsa = certificate.GetRSAPublicKey();
        return rsa is not null
            && rsa.VerifyData(tbsCertificate, signature, hashAlgorithm, RSASignaturePadding.Pkcs1);
    }

    private static X509Certificate2 CloneCertificate(X509Certificate2 certificate)
        => X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
}

public sealed record AppleTrustRootMetadata(
    string Subject,
    string Sha256Fingerprint,
    DateTime NotAfterUtc);
