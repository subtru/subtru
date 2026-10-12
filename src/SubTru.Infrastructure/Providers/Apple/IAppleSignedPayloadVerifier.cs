using SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

namespace SubTru.Infrastructure.Providers.Apple;

public interface IAppleSignedPayloadVerifier
{
    Task<AppleTrustedNotification> VerifyAndDecode(
        string signedPayload,
        AppleProviderConfigReadModel config,
        CancellationToken ct = default);
}
