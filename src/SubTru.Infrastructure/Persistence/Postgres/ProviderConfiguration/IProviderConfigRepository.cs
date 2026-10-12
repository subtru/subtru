using SubTru.Infrastructure.Providers.Google;

namespace SubTru.Infrastructure.Persistence.Postgres.ProviderConfiguration;

public interface IProviderConfigRepository
{
    Task<AppleProviderConfigReadModel?> GetAppleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default);

    Task<AppleProviderConfigReadModel> UpsertAppleProviderConfigAsync(
        Guid projectId,
        AppleProviderConfigWriteModel writeModel,
        CancellationToken ct = default);

    Task<GoogleProviderConfigReadModel?> GetGoogleProviderConfigAsync(
        Guid projectId,
        CancellationToken ct = default);

    Task<GoogleProviderCredentialReadModel?> GetGoogleProviderCredentialAsync(
        Guid projectId,
        CancellationToken ct = default);

    Task<GoogleProviderConfigReadModel> UpsertGoogleProviderConfigAsync(
        Guid projectId,
        GoogleProviderConfigWriteModel writeModel,
        ValidatedGoogleServiceAccount? validatedServiceAccount,
        CancellationToken ct = default);
}
