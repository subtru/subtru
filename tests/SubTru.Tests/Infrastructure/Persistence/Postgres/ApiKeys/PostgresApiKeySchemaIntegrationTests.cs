using Npgsql;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.ApiKeys;

public sealed class PostgresApiKeySchemaIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task NameColumn_IsNotNullable()
    {
        var connectionString = Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("SUBTRU_TEST_DB_CONNECTION must be set for this test.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'api_keys'
              AND column_name = 'name';
            """,
            connection);

        var isNullable = Assert.IsType<string>(await command.ExecuteScalarAsync());

        Assert.Equal("NO", isNullable);
    }
}
