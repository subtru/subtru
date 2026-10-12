using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

namespace SubTru.Infrastructure.Providers.Apple;

public sealed class AppleSignedPayloadVerifier : IAppleSignedPayloadVerifier
{
    private const string ReceiptSignerOid = "1.2.840.113635.100.6.11.1";
    private const string WwdrIntermediateOid = "1.2.840.113635.100.6.2.1";
    public const string RetryableCertificateChainErrorCode = "retryable_apple_certificate_chain_verification";

    private readonly AppleVerificationOptions _options;
    private readonly AppleTrustStore _trustStore;

    public AppleSignedPayloadVerifier(
        AppleVerificationOptions options,
        AppleTrustStore trustStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public Task<AppleTrustedNotification> VerifyAndDecode(
        string signedPayload,
        AppleProviderConfigReadModel config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(signedPayload))
        {
            throw new AppleSignedPayloadVerificationException("missing_signed_payload", "Field 'signedPayload' is required.");
        }

        ct.ThrowIfCancellationRequested();

        using var topLevelJson = ValidateAndDecodePayload(signedPayload, ct);
        JsonDocument? transactionJson = null;
        JsonDocument? renewalJson = null;

        try
        {
            if (topLevelJson.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("signedTransactionInfo", out var signedTransactionElement)
                && signedTransactionElement.ValueKind == JsonValueKind.String
                && signedTransactionElement.GetString() is string signedTransaction
                && !string.IsNullOrWhiteSpace(signedTransaction))
            {
                transactionJson = ValidateAndDecodePayload(signedTransaction, ct);
            }

            if (topLevelJson.RootElement.TryGetProperty("data", out data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("signedRenewalInfo", out var signedRenewalElement)
                && signedRenewalElement.ValueKind == JsonValueKind.String
                && signedRenewalElement.GetString() is string signedRenewal
                && !string.IsNullOrWhiteSpace(signedRenewal))
            {
                renewalJson = ValidateAndDecodePayload(signedRenewal, ct);
            }

            return Task.FromResult(ParseVerifiedPayloads(topLevelJson, transactionJson, renewalJson, config));
        }
        finally
        {
            transactionJson?.Dispose();
            renewalJson?.Dispose();
        }
    }

    internal static AppleTrustedNotification ParseVerifiedPayloads(
        JsonDocument topLevelJson,
        JsonDocument? transactionJson,
        JsonDocument? renewalJson,
        AppleProviderConfigReadModel config)
    {
        var notification = ParseNotification(topLevelJson.RootElement);

        if (notification.Data is null)
        {
            return notification;
        }

        ValidateProjectIdentity(notification.Data, config);

        var transactionInfo = transactionJson is null
            ? null
            : ParseTransactionInfo(transactionJson.RootElement);

        var renewalInfo = renewalJson is null
            ? null
            : ParseRenewalInfo(renewalJson.RootElement);

        if (transactionInfo is not null)
        {
            ValidateTransactionIdentity(transactionInfo, config);
        }

        if (transactionInfo is not null
            && renewalInfo is not null
            && !string.Equals(transactionInfo.OriginalTransactionId, renewalInfo.OriginalTransactionId, StringComparison.Ordinal))
        {
            throw new AppleSignedPayloadVerificationException(
                "original_transaction_id_mismatch",
                "Apple signed transaction and renewal payloads did not reference the same originalTransactionId.");
        }

        return notification with
        {
            Data = notification.Data with
            {
                TransactionInfo = transactionInfo,
                RenewalInfo = renewalInfo
            }
        };
    }

    private JsonDocument ValidateAndDecodePayload(string jws, CancellationToken ct)
    {
        var payload = DecodePayload(jws);

        try
        {
            var certificates = ReadCertificates(jws);
            try
            {
                var effectiveDate = GetEffectiveValidationTime(payload.RootElement, _options.EnableOnlineRevocationChecks);
                var chainPolicySettings = CreateChainPolicySettings(
                    _options.EnableOnlineRevocationChecks,
                    _options.OnlineRevocationTimeout,
                    effectiveDate);

                ValidateCertificateChain(
                    certificates,
                    _trustStore,
                    chainPolicySettings);

                ValidateSignature(jws, certificates[0], ct);
                return payload;
            }
            finally
            {
                foreach (var certificate in certificates)
                {
                    certificate.Dispose();
                }
            }
        }
        catch
        {
            payload.Dispose();
            throw;
        }
    }

    private static List<X509Certificate2> ReadCertificates(string jws)
    {
        var parts = SplitCompactJws(jws);
        using var headerJson = ParseJson(Base64UrlDecode(parts[0]));

        var algorithm = GetOptionalString(headerJson.RootElement, "alg");
        if (!string.Equals(algorithm, "ES256", StringComparison.Ordinal))
        {
            throw new AppleSignedPayloadVerificationException("unsupported_apple_jws_algorithm", "Apple signed payload must use ES256.");
        }

        if (headerJson.RootElement.TryGetProperty("crit", out var critElement))
        {
            throw new AppleSignedPayloadVerificationException(
                "unsupported_apple_jws_critical_header",
                critElement.ValueKind == JsonValueKind.Array
                    ? "Apple signed payload used unsupported critical JWS header parameters."
                    : "Apple signed payload contained an invalid critical JWS header.");
        }

        if (!headerJson.RootElement.TryGetProperty("x5c", out var x5cElement)
            || x5cElement.ValueKind != JsonValueKind.Array
            || x5cElement.GetArrayLength() == 0)
        {
            throw new AppleSignedPayloadVerificationException("missing_apple_certificate_chain", "Apple signed payload is missing x5c certificate headers.");
        }

        var certificates = new List<X509Certificate2>();
        try
        {
            foreach (var item in x5cElement.EnumerateArray())
            {
                var certificateBase64 = item.GetString();

                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(certificateBase64))
                {
                    throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple certificate chain contains an invalid certificate entry.");
                }

                try
                {
                    certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificateBase64)));
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple certificate chain contains an invalid certificate entry.");
                }
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

    internal static void ValidateCertificateChain(
        IReadOnlyList<X509Certificate2> certificates,
        AppleTrustStore trustStore,
        AppleChainPolicySettings chainPolicySettings)
    {
        ArgumentNullException.ThrowIfNull(trustStore);

        if (certificates.Count == 0)
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload certificate chain length was invalid.");
        }

        var leaf = certificates[0];
        var intermediates = certificates.Skip(1).ToArray();
        ValidateAppleCertificateExtensions(leaf, intermediates);

        using var chain = new X509Chain();
        ConfigureChainPolicy(chain.ChainPolicy, trustStore, chainPolicySettings, intermediates);

        if (!chain.Build(leaf))
        {
            if (chainPolicySettings.RevocationMode == X509RevocationMode.Online
                && IsRetryableRevocationFailure(chain.ChainStatus))
            {
                throw new AppleSignedPayloadVerificationException(
                    RetryableCertificateChainErrorCode,
                    "Apple signed payload certificate chain revocation status could not be determined.");
            }

            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload certificate chain validation failed.");
        }

        if (chain.ChainElements.Count < 2)
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload certificate path depth was invalid.");
        }

        var builtRoot = chain.ChainElements[^1].Certificate;
        if (!trustStore.RootFingerprints.Contains(AppleTrustStore.GetSha256Fingerprint(builtRoot)))
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload certificate chain terminated at an unexpected root.");
        }
    }

    private static void ValidateSignature(string jws, X509Certificate2 certificate, CancellationToken ct)
    {
        var parts = SplitCompactJws(jws);
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecodeBytes(parts[2]);

        using var ecdsa = certificate.GetECDsaPublicKey();
        ct.ThrowIfCancellationRequested();

        if (ecdsa is null)
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_signature", "Apple signed payload signing certificate did not contain an ECDSA public key.");
        }

        if (!ecdsa.VerifyData(
                signingInput,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_signature", "Apple signed payload signature validation failed.");
        }
    }

    private static JsonDocument DecodePayload(string jws)
    {
        var parts = SplitCompactJws(jws);
        var payload = Base64UrlDecode(parts[1]);
        return ParseJson(payload);
    }

    private static AppleTrustedNotification ParseNotification(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? ParseNotificationData(dataElement)
            : null;

        return new AppleTrustedNotification(
            NotificationType: GetRequiredString(root, "notificationType", "missing_notification_type"),
            Subtype: GetOptionalString(root, "subtype"),
            NotificationUuid: GetRequiredString(root, "notificationUUID", "missing_notification_uuid"),
            SignedDate: GetRequiredUnixMilliseconds(root, "signedDate", "missing_signed_date"),
            Data: data
        );
    }

    private static AppleTrustedNotificationData ParseNotificationData(JsonElement root)
    {
        return new AppleTrustedNotificationData(
            BundleId: GetRequiredString(root, "bundleId", "missing_bundle_id"),
            AppAppleId: GetOptionalLong(root, "appAppleId"),
            Environment: GetRequiredString(root, "environment", "missing_environment"),
            Status: GetOptionalInt(root, "status"),
            TransactionInfo: null,
            RenewalInfo: null
        );
    }

    private static AppleTrustedTransactionInfo ParseTransactionInfo(JsonElement root)
    {
        return new AppleTrustedTransactionInfo(
            TransactionId: GetRequiredString(root, "transactionId", "missing_transaction_id"),
            OriginalTransactionId: GetRequiredString(root, "originalTransactionId", "missing_original_transaction_id"),
            ProductId: GetRequiredString(root, "productId", "missing_product_id"),
            AppAccountToken: GetOptionalString(root, "appAccountToken"),
            BundleId: GetRequiredString(root, "bundleId", "missing_bundle_id"),
            Environment: GetRequiredString(root, "environment", "missing_environment"),
            Type: GetOptionalString(root, "type"),
            ExpiresDate: GetOptionalUnixMilliseconds(root, "expiresDate"),
            RevocationDate: GetOptionalUnixMilliseconds(root, "revocationDate"),
            SignedDate: GetRequiredUnixMilliseconds(root, "signedDate", "missing_signed_date"),
            OfferDiscountType: GetOptionalString(root, "offerDiscountType")
        );
    }

    private static AppleTrustedRenewalInfo ParseRenewalInfo(JsonElement root)
    {
        return new AppleTrustedRenewalInfo(
            OriginalTransactionId: GetRequiredString(root, "originalTransactionId", "missing_original_transaction_id"),
            ProductId: GetOptionalString(root, "productId"),
            AutoRenewProductId: GetOptionalString(root, "autoRenewProductId"),
            AppAccountToken: GetOptionalString(root, "appAccountToken"),
            Environment: GetRequiredString(root, "environment", "missing_environment"),
            AutoRenewStatus: GetOptionalBooleanLike(root, "autoRenewStatus"),
            GracePeriodExpiresDate: GetOptionalUnixMilliseconds(root, "gracePeriodExpiresDate"),
            RenewalDate: GetOptionalUnixMilliseconds(root, "renewalDate"),
            IsInBillingRetryPeriod: GetOptionalBooleanLike(root, "isInBillingRetryPeriod"),
            ExpirationIntent: GetOptionalInt(root, "expirationIntent"),
            SignedDate: GetRequiredUnixMilliseconds(root, "signedDate", "missing_signed_date")
        );
    }

    private static void ValidateTransactionIdentity(AppleTrustedTransactionInfo transaction, AppleProviderConfigReadModel config)
    {
        if (!string.Equals(transaction.BundleId, config.BundleId, StringComparison.Ordinal))
        {
            throw new AppleSignedPayloadVerificationException("bundle_id_mismatch", "Apple transaction bundleId did not match the configured project bundleId.");
        }

    }

    private static void ValidateProjectIdentity(AppleTrustedNotificationData data, AppleProviderConfigReadModel config)
    {
        if (!string.Equals(data.BundleId, config.BundleId, StringComparison.Ordinal))
        {
            throw new AppleSignedPayloadVerificationException("bundle_id_mismatch", "Apple notification bundleId did not match the configured project bundleId.");
        }

        if (string.Equals(data.Environment, "production", StringComparison.OrdinalIgnoreCase))
        {
            if (!data.AppAppleId.HasValue)
            {
                throw new AppleSignedPayloadVerificationException("missing_app_apple_id", "Apple notification did not include appAppleId for a production notification.");
            }

            if (data.AppAppleId.Value != config.AppAppleId)
            {
                throw new AppleSignedPayloadVerificationException("app_apple_id_mismatch", "Apple notification appAppleId did not match the configured project appAppleId.");
            }
        }
    }

    internal static DateTimeOffset GetEffectiveValidationTime(JsonElement payloadRoot, bool enableOnlineRevocationChecks)
    {
        if (enableOnlineRevocationChecks)
        {
            return DateTimeOffset.UtcNow;
        }

        // Offline validation replays historical notifications against their signedDate,
        // but the payload still must pass signature and chain validation immediately after.
        return GetOptionalUnixMilliseconds(payloadRoot, "signedDate") ?? DateTimeOffset.UtcNow;
    }

    private static string[] SplitCompactJws(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_signed_payload", "Apple signed payload must be a compact JWS with three segments.");
        }

        return parts;
    }

    private static string Base64UrlDecode(string input)
    {
        var bytes = Base64UrlDecodeBytes(input);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] Base64UrlDecodeBytes(string input)
    {
        try
        {
            return Base64UrlEncoder.DecodeBytes(input);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new AppleSignedPayloadVerificationException(
                "invalid_apple_signed_payload",
                "Apple signed payload contains invalid Base64URL data.",
                ex);
        }
    }

    private static JsonDocument ParseJson(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException ex)
        {
            throw new AppleSignedPayloadVerificationException(
                "invalid_apple_signed_payload",
                "Apple signed payload contains invalid JSON.",
                ex);
        }
    }

    internal static AppleChainPolicySettings CreateChainPolicySettings(
        bool enableOnlineRevocationChecks,
        TimeSpan onlineRevocationTimeout,
        DateTimeOffset effectiveDate)
    {
        return new AppleChainPolicySettings(
            RevocationMode: enableOnlineRevocationChecks ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
            UrlRetrievalTimeout: onlineRevocationTimeout,
            VerificationTime: effectiveDate,
            DisableCertificateDownloads: true);
    }

    internal static void ConfigureChainPolicy(
        X509ChainPolicy chainPolicy,
        AppleTrustStore trustStore,
        AppleChainPolicySettings chainPolicySettings,
        IEnumerable<X509Certificate2> intermediateCertificates)
    {
        ArgumentNullException.ThrowIfNull(chainPolicy);
        ArgumentNullException.ThrowIfNull(trustStore);
        ArgumentNullException.ThrowIfNull(intermediateCertificates);

        chainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chainPolicy.RevocationMode = chainPolicySettings.RevocationMode;
        chainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chainPolicy.DisableCertificateDownloads = chainPolicySettings.DisableCertificateDownloads;
        chainPolicy.VerificationTime = chainPolicySettings.VerificationTime.UtcDateTime;

        if (chainPolicySettings.RevocationMode == X509RevocationMode.Online)
        {
            chainPolicy.UrlRetrievalTimeout = chainPolicySettings.UrlRetrievalTimeout;
        }

        trustStore.AddTrustedRootsTo(chainPolicy.CustomTrustStore);

        foreach (var intermediateCertificate in intermediateCertificates)
        {
            chainPolicy.ExtraStore.Add(intermediateCertificate);
        }
    }

    private static void ValidateAppleCertificateExtensions(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> intermediates)
    {
        if (!HasNonCriticalExtension(leaf, ReceiptSignerOid))
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload leaf certificate did not contain the receipt signing extension.");
        }

        if (intermediates.Count == 0 || !intermediates.Any(intermediate => HasNonCriticalExtension(intermediate, WwdrIntermediateOid)))
        {
            throw new AppleSignedPayloadVerificationException("invalid_apple_certificate_chain", "Apple signed payload intermediates did not contain the WWDR extension.");
        }
    }

    private static bool HasNonCriticalExtension(X509Certificate2 certificate, string oid)
        => certificate.Extensions
            .OfType<X509Extension>()
            .Any(extension => string.Equals(extension.Oid?.Value, oid, StringComparison.Ordinal) && !extension.Critical);

    internal static bool IsRetryableRevocationFailure(X509ChainStatus[] statuses)
        => statuses.Any(status =>
            status.Status.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown)
            || status.Status.HasFlag(X509ChainStatusFlags.OfflineRevocation));

    private static string GetRequiredString(JsonElement root, string propertyName, string errorCode)
    {
        var value = GetOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppleSignedPayloadVerificationException(errorCode, $"Apple signed payload is missing '{propertyName}'.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static long? GetOptionalLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static bool? GetOptionalBooleanLike(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt32(out var number) => number switch
            {
                0 => false,
                1 => true,
                _ => null
            },
            JsonValueKind.String => property.GetString() switch
            {
                "0" => false,
                "1" => true,
                "true" => true,
                "false" => false,
                _ => null
            },
            _ => null
        };
    }

    private static DateTimeOffset GetRequiredUnixMilliseconds(JsonElement root, string propertyName, string errorCode)
    {
        var value = GetOptionalUnixMilliseconds(root, propertyName);
        if (!value.HasValue)
        {
            throw new AppleSignedPayloadVerificationException(errorCode, $"Apple signed payload is missing '{propertyName}'.");
        }

        return value.Value;
    }

    private static DateTimeOffset? GetOptionalUnixMilliseconds(JsonElement root, string propertyName)
    {
        var ms = GetOptionalLong(root, propertyName);
        if (!ms.HasValue)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new AppleSignedPayloadVerificationException(
                "invalid_apple_timestamp",
                $"Apple signed payload contains an out-of-range '{propertyName}' timestamp.",
                ex);
        }
    }
}

internal sealed record AppleChainPolicySettings(
    X509RevocationMode RevocationMode,
    TimeSpan UrlRetrievalTimeout,
    DateTimeOffset VerificationTime,
    bool DisableCertificateDownloads);
