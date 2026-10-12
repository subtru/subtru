using Npgsql;

namespace SubTru.Tests.Support.Postgres;

public sealed class RuntimeDatabaseSecurityIntegrationTests
{
    private const string RuntimeRole = "subtru_api_app";

    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task SubtruApiApp_CanUseRuntimeTablesAndCannotEscalatePrivileges()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var adminConnectionString = Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("SUBTRU_TEST_DB_CONNECTION must be set for this test.");

        var suffix = Guid.NewGuid().ToString("N");
        var runtimePassword = $"rt_{suffix}";
        Guid? projectId = null;

        await using var adminConn = new NpgsqlConnection(adminConnectionString);
        await adminConn.OpenAsync(ct);
        await ExecuteAsync(
            adminConn,
            $"ALTER ROLE {RuntimeRole} WITH PASSWORD '{runtimePassword}';",
            ct);

        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = RuntimeRole,
            Password = runtimePassword
        }.ConnectionString;

        await using var runtimeConn = new NpgsqlConnection(runtimeConnectionString);
        await runtimeConn.OpenAsync(ct);

        try
        {
            projectId = await ScalarAsync<Guid>(
                runtimeConn,
                """
                INSERT INTO projects (name, slug)
                VALUES (@name, @slug)
                RETURNING id;
                """,
                ct,
                ("name", $"Runtime Project {suffix}"),
                ("slug", $"runtime-project-{suffix}"));

            await ExecuteAsync(
                runtimeConn,
                "UPDATE projects SET name = @name WHERE id = @project_id;",
                ct,
                ("name", $"Updated Runtime Project {suffix}"),
                ("project_id", projectId.Value));

            await ExecuteAsync(
                runtimeConn,
                """
                INSERT INTO api_keys (
                    project_id,
                    key_id,
                    api_key_hash,
                    name,
                    environment
                )
                VALUES (
                    @project_id,
                    @key_id,
                    @api_key_hash,
                    @name,
                    'test'
                );
                """,
                ct,
                ("project_id", projectId.Value),
                ("key_id", $"subtru_test_{suffix}"),
                ("api_key_hash", new byte[32]),
                ("name", "Runtime security test"));

            var projectName = await ScalarAsync<string>(
                runtimeConn,
                "SELECT name FROM projects WHERE id = @project_id;",
                ct,
                ("project_id", projectId.Value));
            Assert.Equal($"Updated Runtime Project {suffix}", projectName);

            var deleteDenied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                runtimeConn,
                "DELETE FROM projects WHERE id = @project_id;",
                ct,
                ("project_id", projectId.Value)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteDenied.SqlState);

            var createDenied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                runtimeConn,
                $"CREATE TABLE runtime_escalation_{suffix} (id integer);",
                ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, createDenied.SqlState);
        }
        finally
        {
            if (projectId.HasValue)
            {
                await ExecuteAsync(
                    adminConn,
                    "DELETE FROM projects WHERE id = @project_id;",
                    ct,
                    ("project_id", projectId.Value));
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection conn,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddParameters(cmd, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection conn,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddParameters(cmd, parameters);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Assert.IsType<T>(result);
    }

    private static void AddParameters(
        NpgsqlCommand cmd,
        params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}
