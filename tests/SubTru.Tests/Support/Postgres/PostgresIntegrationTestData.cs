using Npgsql;

namespace SubTru.Tests.Support.Postgres;

internal static class PostgresIntegrationTestData
{
    public static async Task<Guid> InsertProjectAsync(
        NpgsqlConnection conn,
        string slug,
        CancellationToken ct)
    {
        var projectId = Guid.NewGuid();
        await InsertProjectAsync(conn, projectId, slug, ct);
        return projectId;
    }

    public static async Task InsertProjectAsync(
        NpgsqlConnection conn,
        Guid projectId,
        string slug,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO projects (id, name, slug, is_active)
            VALUES (@project_id, @project_name, @project_slug, true);
            """,
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);
        cmd.Parameters.AddWithValue("project_name", $"Integration Test {slug}");
        cmd.Parameters.AddWithValue("project_slug", slug);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task DeleteProjectAsync(
        NpgsqlConnection conn,
        Guid projectId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM projects WHERE id = @project_id;",
            conn);

        cmd.Parameters.AddWithValue("project_id", projectId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
