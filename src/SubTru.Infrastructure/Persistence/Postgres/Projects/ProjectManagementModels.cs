namespace SubTru.Infrastructure.Persistence.Postgres.Projects;

public sealed record ProjectListItem(
    Guid Id,
    string Name,
    string? Slug,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectWriteModel(
    string Name,
    string? Slug,
    bool IsActive);

public interface IProjectManagementRepository
{
    Task<IReadOnlyList<ProjectListItem>> ListAsync(CancellationToken ct = default);

    Task<ProjectListItem> CreateAsync(
        ProjectWriteModel project,
        CancellationToken ct = default);

    Task<ProjectListItem?> UpdateAsync(
        Guid projectId,
        ProjectWriteModel project,
        CancellationToken ct = default);
}
