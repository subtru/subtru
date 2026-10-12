namespace SubTru.Api.Platform.Authentication;

public interface IProjectResolver
{
    Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default);
}