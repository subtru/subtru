namespace SubTru.Domain.Access;

public sealed record AccessState(
    bool HasAccess,
    AccessReason? AccessReason,
    DateTimeOffset? AccessEndsAt
);
