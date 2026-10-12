namespace SubTru.Api.Features.Access;

public sealed record AccessEntitlementDto(
    string ProductId,
    bool HasAccess,
    string AccessReason,
    DateTimeOffset? AccessEndsAt
);
