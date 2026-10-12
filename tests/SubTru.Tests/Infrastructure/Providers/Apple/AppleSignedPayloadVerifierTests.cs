using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SubTru.Infrastructure.Providers.Apple;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;
using SubTru.Infrastructure.Persistence.Postgres.WebhookEvents;
using SubTru.Infrastructure.Persistence.Postgres.WebhookKeys;

namespace SubTru.Tests.Infrastructure.Providers.Apple;

public sealed class AppleSignedPayloadVerifierTests
{
    private static readonly AppleProviderConfigReadModel TestConfig = new(
        BundleId: "com.example.app",
        AppAppleId: 1234567890,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task VerifyAndDecode_InvalidCompactJws_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create();
        var verifier = CreateVerifier(fixture.CreateTrustStore());

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode("not-a-jws", TestConfig));

        Assert.Equal("invalid_apple_signed_payload", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_InvalidBase64Url_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create();
        var verifier = CreateVerifier(fixture.CreateTrustStore());

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode("header.!.signature", TestConfig));

        Assert.Equal("invalid_apple_signed_payload", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_InvalidJson_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create();
        var verifier = CreateVerifier(fixture.CreateTrustStore());
        var invalidJson = Base64Url("not-json");

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode($"header.{invalidJson}.signature", TestConfig));

        Assert.Equal("invalid_apple_signed_payload", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_OutOfRangeTimestamp_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create(
            payloadJson: """
                {
                  "notificationType": "DID_RENEW",
                  "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
                  "signedDate": 9223372036854775807
                }
                """);
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig));

        Assert.Equal("invalid_apple_timestamp", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_MissingCertificateChain_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create();
        var verifier = CreateVerifier(fixture.CreateTrustStore());
        var header = Base64Url("{\"alg\":\"ES256\"}");
        var payload = Base64Url("{\"notificationType\":\"TEST\",\"notificationUUID\":\"123e4567-e89b-12d3-a456-426614174000\",\"signedDate\":1741262400000}");
        var jws = $"{header}.{payload}.signature";

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(jws, TestConfig));

        Assert.Equal("missing_apple_certificate_chain", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_UnsupportedCriticalHeader_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create(includeUnsupportedCriticalHeader: true);
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig));

        Assert.Equal("unsupported_apple_jws_critical_header", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_TrustedLocalRootAndExtensions_ReturnsNotification()
    {
        using var fixture = SignedPayloadFixture.Create();
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var notification = await verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig);

        Assert.Equal("DID_RENEW", notification.NotificationType);
        Assert.NotNull(notification.Data);
        Assert.Equal("com.example.app", notification.Data.BundleId);
    }

    [Fact]
    public async Task VerifyAndDecode_UnknownLocalTrustRoot_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create(includePayloadRoot: true);
        using var otherRoot = TestAppleCertificateFactory.CreateRootCertificate();
        using var trustStore = new AppleTrustStore(new[] { otherRoot });
        var verifier = CreateVerifier(trustStore, enableOnlineRevocationChecks: false);

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig));

        Assert.Equal("invalid_apple_certificate_chain", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_MissingReceiptSigningExtension_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create(includeLeafReceiptSigningOid: false);
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig));

        Assert.Equal("invalid_apple_certificate_chain", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_IntermediatesMissingWwdrExtension_ThrowsExpectedError()
    {
        using var fixture = SignedPayloadFixture.Create(includeIntermediateWwdrOid: false);
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var ex = await Assert.ThrowsAsync<AppleSignedPayloadVerificationException>(() =>
            verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig));

        Assert.Equal("invalid_apple_certificate_chain", ex.ErrorCode);
    }

    [Fact]
    public async Task VerifyAndDecode_VariableChainDepth_BuildsToTrustedRoot()
    {
        using var fixture = SignedPayloadFixture.Create(additionalIntermediateCount: 1, includePayloadRoot: false);
        var verifier = CreateVerifier(fixture.CreateTrustStore(), enableOnlineRevocationChecks: false);

        var notification = await verifier.VerifyAndDecode(fixture.SignedPayload, TestConfig);

        Assert.Equal("DID_RENEW", notification.NotificationType);
    }

    [Fact]
    public void ConfigureChainPolicy_OnlineRevocation_AppliesExpectedSettings()
    {
        using var fixture = SignedPayloadFixture.Create();
        using var chain = new X509Chain();
        var effectiveDate = new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        var settings = AppleSignedPayloadVerifier.CreateChainPolicySettings(
            enableOnlineRevocationChecks: true,
            onlineRevocationTimeout: TimeSpan.FromSeconds(7),
            effectiveDate);

        using var trustStore = fixture.CreateTrustStore();
        AppleSignedPayloadVerifier.ConfigureChainPolicy(
            chain.ChainPolicy,
            trustStore,
            settings,
            fixture.PayloadIntermediates);

        Assert.True(chain.ChainPolicy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.Online, chain.ChainPolicy.RevocationMode);
        Assert.Equal(TimeSpan.FromSeconds(7), chain.ChainPolicy.UrlRetrievalTimeout);
        Assert.Equal(effectiveDate.UtcDateTime, chain.ChainPolicy.VerificationTime);
        Assert.False(chain.ChainPolicy.VerificationTimeIgnored);
    }

    [Fact]
    public void ConfigureChainPolicy_OfflineRevocation_AppliesExpectedSettings()
    {
        using var fixture = SignedPayloadFixture.Create();
        using var chain = new X509Chain();
        var signedDate = new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        var settings = AppleSignedPayloadVerifier.CreateChainPolicySettings(
            enableOnlineRevocationChecks: false,
            onlineRevocationTimeout: TimeSpan.FromSeconds(7),
            signedDate);

        using var trustStore = fixture.CreateTrustStore();
        AppleSignedPayloadVerifier.ConfigureChainPolicy(
            chain.ChainPolicy,
            trustStore,
            settings,
            fixture.PayloadIntermediates);

        Assert.True(chain.ChainPolicy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, chain.ChainPolicy.RevocationMode);
        Assert.Equal(signedDate.UtcDateTime, chain.ChainPolicy.VerificationTime);
        Assert.False(chain.ChainPolicy.VerificationTimeIgnored);
    }

    [Fact]
    public void GetEffectiveValidationTime_OfflineUsesPayloadSignedDate()
    {
        using var payload = JsonDocument.Parse("""
            {
              "signedDate": 1741262400000
            }
            """);

        var effectiveDate = AppleSignedPayloadVerifier.GetEffectiveValidationTime(payload.RootElement, enableOnlineRevocationChecks: false);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1741262400000), effectiveDate);
    }

    [Fact]
    public void IsRetryableRevocationFailure_RecognizesTransientStatuses()
    {
        var statuses = new[]
        {
            new X509ChainStatus
            {
                Status = X509ChainStatusFlags.RevocationStatusUnknown,
                StatusInformation = "unknown"
            }
        };

        Assert.True(AppleSignedPayloadVerifier.IsRetryableRevocationFailure(statuses));
    }

    [Fact]
    public void ParseVerifiedPayloads_ParsesNestedTransactionAndRenewalPayloads()
    {
        using var topLevelJson = JsonDocument.Parse("""
            {
              "notificationType": "DID_RENEW",
              "subtype": "BILLING_RECOVERY",
              "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
              "signedDate": 1741262400000,
              "data": {
                "bundleId": "com.example.app",
                "appAppleId": 1234567890,
                "environment": "production",
                "status": 1
              }
            }
            """);

        using var transactionJson = JsonDocument.Parse("""
            {
              "transactionId": "2000000123456789",
              "originalTransactionId": "1000000123456789",
              "productId": "com.example.premium.monthly",
              "appAccountToken": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "bundleId": "com.example.app",
              "environment": "production",
              "type": "Auto-Renewable Subscription",
              "expiresDate": "1743854400000",
              "offerDiscountType": "FREE_TRIAL",
              "signedDate": 1741262400000
            }
            """);

        using var renewalJson = JsonDocument.Parse("""
            {
              "originalTransactionId": "1000000123456789",
              "productId": "com.example.premium.current",
              "autoRenewProductId": "com.example.premium.future",
              "appAccountToken": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "environment": "production",
              "autoRenewStatus": "1",
              "isInBillingRetryPeriod": 0,
              "renewalDate": 1743854400000,
              "signedDate": 1741262400000
            }
            """);

        var notification = AppleSignedPayloadVerifier.ParseVerifiedPayloads(topLevelJson, transactionJson, renewalJson, TestConfig);

        Assert.Equal("DID_RENEW", notification.NotificationType);
        Assert.Equal("BILLING_RECOVERY", notification.Subtype);
        Assert.NotNull(notification.Data);
        Assert.NotNull(notification.Data.TransactionInfo);
        Assert.NotNull(notification.Data.RenewalInfo);
        Assert.Equal("com.example.premium.monthly", notification.Data.TransactionInfo.ProductId);
        Assert.Equal("FREE_TRIAL", notification.Data.TransactionInfo.OfferDiscountType);
        Assert.Equal("com.example.premium.current", notification.Data.RenewalInfo.ProductId);
        Assert.Equal("com.example.premium.future", notification.Data.RenewalInfo.AutoRenewProductId);
        Assert.True(notification.Data.RenewalInfo.AutoRenewStatus);
        Assert.False(notification.Data.RenewalInfo.IsInBillingRetryPeriod);
    }

    [Fact]
    public void ParseVerifiedPayloads_BundleMismatch_ThrowsExpectedError()
    {
        using var topLevelJson = JsonDocument.Parse("""
            {
              "notificationType": "DID_RENEW",
              "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
              "signedDate": 1741262400000,
              "data": {
                "bundleId": "com.other.app",
                "appAppleId": 1234567890,
                "environment": "production"
              }
            }
            """);

        var ex = Assert.Throws<AppleSignedPayloadVerificationException>(() =>
            AppleSignedPayloadVerifier.ParseVerifiedPayloads(topLevelJson, null, null, TestConfig));

        Assert.Equal("bundle_id_mismatch", ex.ErrorCode);
    }

    [Fact]
    public void ParseVerifiedPayloads_ProductionMissingAppAppleId_ThrowsExpectedError()
    {
        using var topLevelJson = JsonDocument.Parse("""
            {
              "notificationType": "DID_RENEW",
              "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
              "signedDate": 1741262400000,
              "data": {
                "bundleId": "com.example.app",
                "environment": "production"
              }
            }
            """);

        var ex = Assert.Throws<AppleSignedPayloadVerificationException>(() =>
            AppleSignedPayloadVerifier.ParseVerifiedPayloads(topLevelJson, null, null, TestConfig));

        Assert.Equal("missing_app_apple_id", ex.ErrorCode);
    }

    [Fact]
    public void ParseVerifiedPayloads_BothEnvironment_AllowsSandboxNotification()
    {
        using var topLevelJson = JsonDocument.Parse("""
            {
              "notificationType": "SUBSCRIBED",
              "subtype": "INITIAL_BUY",
              "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
              "signedDate": 1741262400000,
              "data": {
                "bundleId": "com.example.app",
                "environment": "sandbox"
              }
            }
            """);

        var notification = AppleSignedPayloadVerifier.ParseVerifiedPayloads(topLevelJson, null, null, TestConfig);

        Assert.Equal("sandbox", notification.Data!.Environment);
    }

    private static AppleSignedPayloadVerifier CreateVerifier(
        AppleTrustStore trustStore,
        bool enableOnlineRevocationChecks = false,
        TimeSpan? onlineRevocationTimeout = null)
        => new(
            new AppleVerificationOptions
            {
                RootCertificateDirectory = "unused-for-unit-tests",
                EnableOnlineRevocationChecks = enableOnlineRevocationChecks,
                OnlineRevocationTimeout = onlineRevocationTimeout ?? TimeSpan.FromSeconds(5)
            },
            trustStore);

    private static string Base64Url(string value)
        => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(value));

    private sealed class SignedPayloadFixture : IDisposable
    {
        private const string ReceiptSignerOid = "1.2.840.113635.100.6.11.1";
        private const string WwdrIntermediateOid = "1.2.840.113635.100.6.2.1";

        private SignedPayloadFixture(
            string signedPayload,
            X509Certificate2 root,
            IReadOnlyList<X509Certificate2> payloadIntermediates,
            ECDsa leafKey,
            IReadOnlyList<X509Certificate2> ownedCertificates)
        {
            SignedPayload = signedPayload;
            _root = root;
            PayloadIntermediates = payloadIntermediates;
            _leafKey = leafKey;
            _ownedCertificates = ownedCertificates;
        }

        private readonly X509Certificate2 _root;
        private readonly ECDsa _leafKey;
        private readonly IReadOnlyList<X509Certificate2> _ownedCertificates;

        public string SignedPayload { get; }

        public IReadOnlyList<X509Certificate2> PayloadIntermediates { get; }

        public static SignedPayloadFixture Create(
            bool includeLeafReceiptSigningOid = true,
            bool includeIntermediateWwdrOid = true,
            int additionalIntermediateCount = 0,
            bool includePayloadRoot = true,
            bool includeUnsupportedCriticalHeader = false,
            string? payloadJson = null)
        {
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddDays(30);
            var signedDate = DateTimeOffset.UtcNow;
            var ownedCertificates = new List<X509Certificate2>();

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest(
                "CN=Apple Root CA - G3, O=Apple Inc., C=US",
                rootKey,
                HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            var root = rootRequest.CreateSelfSigned(notBefore, notAfter);
            ownedCertificates.Add(root);

            var topDownIntermediates = new List<X509Certificate2>();
            var parent = root;

            for (var index = additionalIntermediateCount; index >= 0; index--)
            {
                var isWwdrIntermediate = index == 0;
                var subject = isWwdrIntermediate
                    ? "CN=Apple Worldwide Developer Relations Certification Authority, O=Apple Inc., C=US"
                    : $"CN=Apple Extra Intermediate {index}, O=Apple Inc., C=US";

                var intermediate = CreateIntermediateCertificate(
                    parent,
                    subject,
                    includeWwdrOid: isWwdrIntermediate && includeIntermediateWwdrOid,
                    notBefore,
                    notAfter);

                ownedCertificates.Add(intermediate);
                topDownIntermediates.Add(intermediate);
                parent = intermediate;
            }

            var payloadIntermediates = topDownIntermediates
                .AsEnumerable()
                .Reverse()
                .ToList();

            var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafRequest = new CertificateRequest(
                "CN=App Store Receipt Signing, O=Apple Inc., C=US",
                leafKey,
                HashAlgorithmName.SHA256);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
            if (includeLeafReceiptSigningOid)
            {
                leafRequest.CertificateExtensions.Add(new X509Extension(new Oid(ReceiptSignerOid), new byte[] { 0x05, 0x00 }, critical: false));
            }

            var leafPublic = leafRequest.Create(parent, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            var leaf = leafPublic.CopyWithPrivateKey(leafKey);
            leafPublic.Dispose();
            ownedCertificates.Add(leaf);

            payloadJson ??= $$"""
                {
                  "notificationType": "DID_RENEW",
                  "notificationUUID": "123e4567-e89b-12d3-a456-426614174000",
                  "signedDate": {{signedDate.ToUnixTimeMilliseconds()}},
                  "data": {
                    "bundleId": "com.example.app",
                    "appAppleId": 1234567890,
                    "environment": "production",
                    "status": 1
                  }
                }
                """;

            var signedPayload = SignCompactJws(
                payloadJson,
                leaf,
                payloadIntermediates,
                includePayloadRoot ? root : null,
                leafKey,
                includeUnsupportedCriticalHeader ? new[] { "test-header" } : null);

            return new SignedPayloadFixture(signedPayload, root, payloadIntermediates, leafKey, ownedCertificates);
        }

        public AppleTrustStore CreateTrustStore()
            => new(new[] { _root });

        public void Dispose()
        {
            _leafKey.Dispose();

            foreach (var certificate in _ownedCertificates)
            {
                certificate.Dispose();
            }
        }

        private static X509Certificate2 CreateIntermediateCertificate(
            X509Certificate2 issuer,
            string subject,
            bool includeWwdrOid,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter)
        {
            using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(
                subject,
                intermediateKey,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            if (includeWwdrOid)
            {
                request.CertificateExtensions.Add(new X509Extension(new Oid(WwdrIntermediateOid), new byte[] { 0x05, 0x00 }, critical: false));
            }

            var publicCertificate = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            var certificate = publicCertificate.CopyWithPrivateKey(intermediateKey);
            publicCertificate.Dispose();
            return certificate;
        }

        private static string SignCompactJws(
            string payloadJson,
            X509Certificate2 leaf,
            IReadOnlyList<X509Certificate2> payloadIntermediates,
            X509Certificate2? payloadRoot,
            ECDsa leafKey,
            IReadOnlyList<string>? criticalHeaders = null)
        {
            var x5c = new List<string> { Convert.ToBase64String(leaf.RawData) };
            x5c.AddRange(payloadIntermediates.Select(certificate => Convert.ToBase64String(certificate.RawData)));

            if (payloadRoot is not null)
            {
                x5c.Add(Convert.ToBase64String(payloadRoot.RawData));
            }

            var signingCredentials = new SigningCredentials(
                new ECDsaSecurityKey(leafKey),
                SecurityAlgorithms.EcdsaSha256);

            var headers = new Dictionary<string, object>
            {
                ["x5c"] = x5c
            };

            if (criticalHeaders is not null)
            {
                headers["crit"] = criticalHeaders;
                headers["test-header"] = "unsupported";
            }

            return new JsonWebTokenHandler().CreateToken(
                payloadJson,
                signingCredentials,
                headers);
        }
    }
}
