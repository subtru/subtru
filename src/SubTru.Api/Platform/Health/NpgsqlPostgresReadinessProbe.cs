using SubTru.Infrastructure.Persistence.Postgres;

namespace SubTru.Api.Platform.Health;

public sealed class NpgsqlPostgresReadinessProbe(
    NpgsqlConnectionFactory connectionFactory,
    ILogger<NpgsqlPostgresReadinessProbe> logger) : IPostgresReadinessProbe
{
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(2);

    private readonly NpgsqlConnectionFactory _connectionFactory = connectionFactory;
    private readonly ILogger<NpgsqlPostgresReadinessProbe> _logger = logger;

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ReadinessTimeout);

            await using var connection = _connectionFactory.Create();
            await connection.OpenAsync(timeoutCts.Token);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = (int)ReadinessTimeout.TotalSeconds;

            await command.ExecuteScalarAsync(timeoutCts.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "PostgreSQL readiness check failed. exception_type={ExceptionType}",
                ex.GetType().Name);

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
