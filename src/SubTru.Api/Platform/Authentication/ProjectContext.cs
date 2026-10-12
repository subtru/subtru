namespace SubTru.Api.Platform.Authentication;

public sealed record ProjectContext(
    Guid? UserId,
    bool IsApiKeyAuth,
    string? Environment = null,
    string? Email = null,
    bool IsAdminAuth = false
);
