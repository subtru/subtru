using Npgsql;
using SubTru.Domain.Subscriptions;
using SubTru.Infrastructure.Persistence.Postgres.ApiKeys;
using SubTru.Infrastructure.Persistence.Postgres.Authentication;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.Authentication;

public sealed class PostgresProjectAuthServiceIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task ValidateApiKeyAsync_ValidKey_ReturnsProjectIdAndUpdatesLastUsedAt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var connectionString = GetTestConnectionString();
        if (connectionString is null)
        {
            throw new InvalidOperationException("SUBTRU_TEST_DB_CONNECTION must be set for this test.");
        }

        var factory = new NpgsqlConnectionFactory(connectionString);

        if (!await HasRequiredSchemaAsync(factory, ct))
        {
            throw new InvalidOperationException("Integration test DB is missing required schema (api_keys).");
        }

        var pepper = "test-pepper-2026";
        var repository = new PostgresApiKeyManagementRepository(factory, pepper);
        var authService = new PostgresProjectAuthService(factory, pepper);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var projectId = await PostgresIntegrationTestData.InsertProjectAsync(conn, "it-api-key-auth", ct);

        try
        {
            var created = await repository.CreateAsync(
                projectId,
                "Integration Test Key",
                SubscriptionEnvironment.Production,
                null,
                ct);

            var initialLastUsedAt = await GetLastUsedAtAsync(conn, created.Id, projectId, ct);
            Assert.Null(initialLastUsedAt);

            var validated = await authService.ValidateApiKeyAsync(created.ApiKey, ct);
            Assert.NotNull(validated);
            Assert.Equal(projectId, validated.ProjectId);
            Assert.Equal("production", validated.Environment);

            var touchedLastUsedAt = await GetLastUsedAtAsync(conn, created.Id, projectId, ct);
            Assert.NotNull(touchedLastUsedAt);
        }
        finally
        {
            await PostgresIntegrationTestData.DeleteProjectAsync(conn, projectId, ct);
        }
    }

    private static async Task<bool> HasRequiredSchemaAsync(NpgsqlConnectionFactory factory, CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT to_regclass('public.api_keys') IS NOT NULL;",
            conn);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    private static async Task<DateTimeOffset?> GetLastUsedAtAsync(
        NpgsqlConnection conn,
        Guid apiKeyId,
        Guid projectId,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT last_used_at FROM api_keys WHERE id = @id AND project_id = @project_id;",
            conn);
        cmd.Parameters.AddWithValue("id", apiKeyId);
        cmd.Parameters.AddWithValue("project_id", projectId);

        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is DateTimeOffset dateTimeOffsetValue)
        {
            return dateTimeOffsetValue;
        }

        if (value is DateTime dateTimeValue)
        {
            var utcDateTime = dateTimeValue.Kind == DateTimeKind.Utc
                ? dateTimeValue
                : DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Utc);

            return new DateTimeOffset(utcDateTime);
        }

        throw new InvalidOperationException(
            $"Unexpected type for last_used_at: {value.GetType().FullName}");
    }

    private static string? GetTestConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION");
        return string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
    }
}
