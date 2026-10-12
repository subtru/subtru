using Npgsql;
using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Infrastructure.Persistence.Postgres.Projects;

public sealed class PostgresProjectManagementRepository(
    NpgsqlConnectionFactory factory) : IProjectManagementRepository
{
    public async Task<IReadOnlyList<ProjectListItem>> ListAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, name, slug, is_active, created_at, updated_at
            FROM projects
            ORDER BY created_at ASC, id ASC;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var projects = new List<ProjectListItem>();
        while (await reader.ReadAsync(ct))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<ProjectListItem> CreateAsync(
        ProjectWriteModel project,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO projects (name, slug, is_active)
            VALUES (@name, @slug, @is_active)
            RETURNING id, name, slug, is_active, created_at, updated_at;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddWriteParameters(cmd, project);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadProject(reader);
    }

    public async Task<ProjectListItem?> UpdateAsync(
        Guid projectId,
        ProjectWriteModel project,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE projects
            SET name = @name,
                slug = @slug,
                is_active = @is_active
            WHERE id = @project_id
            RETURNING id, name, slug, is_active, created_at, updated_at;
            """;

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project_id", projectId);
        AddWriteParameters(cmd, project);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProject(reader) : null;
    }

    private static void AddWriteParameters(NpgsqlCommand cmd, ProjectWriteModel project)
    {
        cmd.Parameters.AddWithValue("name", project.Name);
        cmd.Parameters.AddWithValue("slug", (object?)project.Slug ?? DBNull.Value);
        cmd.Parameters.AddWithValue("is_active", project.IsActive);
    }

    private static ProjectListItem ReadProject(NpgsqlDataReader reader)
        => new(
            Id: reader.GetGuid(0),
            Name: reader.GetString(1),
            Slug: reader.IsDBNull(2) ? null : reader.GetString(2),
            IsActive: reader.GetBoolean(3),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(4),
            UpdatedAt: reader.GetFieldValue<DateTimeOffset>(5));
}
