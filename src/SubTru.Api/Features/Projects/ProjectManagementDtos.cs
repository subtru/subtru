namespace SubTru.Api.Features.Projects;

public sealed record CreateProjectRequest(
    string? Name,
    string? Slug);

public sealed record UpdateProjectRequest(
    string? Name,
    string? Slug,
    bool IsActive = true);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string? Slug,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectListResponse(
    IReadOnlyList<ProjectDto> Items);
