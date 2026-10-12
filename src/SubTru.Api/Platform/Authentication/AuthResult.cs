namespace SubTru.Api.Platform.Authentication;

/// <summary>
/// The result of a successful authentication attempt.
/// Project API keys carry their project and environment. Installation-admin
/// authentication is represented explicitly by IsAdminAuth.
/// </summary>
public sealed record AuthResult(
    Guid? UserId,
    Guid? ProjectId,
    bool IsApiKeyAuth,
    string? Environment = null,
    string? Email = null,
    bool IsAdminAuth = false);
