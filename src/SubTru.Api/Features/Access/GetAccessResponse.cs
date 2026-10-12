namespace SubTru.Api.Features.Access;

public sealed record GetAccessResponse(
    IReadOnlyList<AccessEntitlementDto> Entitlements
);
