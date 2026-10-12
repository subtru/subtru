namespace SubTru.Infrastructure.Providers.Google;

public interface IGoogleServiceAccountValidator
{
    Task<ValidatedGoogleServiceAccount> ValidateAsync(
        string serviceAccountJson,
        CancellationToken ct = default);
}
