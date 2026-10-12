namespace SubTru.Domain.Access;

public sealed record ProductAccessState(
    string ProductId,
    bool HasAccess,
    AccessReason AccessReason,
    DateTimeOffset? AccessEndsAt
);
