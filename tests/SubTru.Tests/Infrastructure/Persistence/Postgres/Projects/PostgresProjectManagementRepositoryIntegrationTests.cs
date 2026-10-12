using Npgsql;
using SubTru.Infrastructure.Persistence.Postgres;
using SubTru.Infrastructure.Persistence.Postgres.Projects;
using SubTru.Tests.Support.Postgres;

namespace SubTru.Tests.Infrastructure.Persistence.Postgres.Projects;

public sealed class PostgresProjectManagementRepositoryIntegrationTests
{
    [Trait("Category", "Integration")]
    [RequiresPostgresFact]
    public async Task CreateAsync_AllowsDuplicateNameAndSlug()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var connectionString = Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION")
            ?? throw new InvalidOperationException("SUBTRU_TEST_DB_CONNECTION must be set for this test.");
        var factory = new NpgsqlConnectionFactory(connectionString);
        var repository = new PostgresProjectManagementRepository(factory);
        var label = $"duplicate-{Guid.NewGuid():N}";
        ProjectListItem? first = null;
        ProjectListItem? second = null;

        try
        {
            first = await repository.CreateAsync(
                new ProjectWriteModel(label, label, IsActive: true),
                ct);
            second = await repository.CreateAsync(
                new ProjectWriteModel(label, label, IsActive: true),
                ct);

            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(first.Name, second.Name);
            Assert.Equal(first.Slug, second.Slug);
        }
        finally
        {
            await using var conn = factory.Create();
            await conn.OpenAsync(ct);

            if (first is not null)
            {
                await PostgresIntegrationTestData.DeleteProjectAsync(conn, first.Id, ct);
            }

            if (second is not null)
            {
                await PostgresIntegrationTestData.DeleteProjectAsync(conn, second.Id, ct);
            }
        }
    }
}
