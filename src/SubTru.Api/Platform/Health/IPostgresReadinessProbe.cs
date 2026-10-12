namespace SubTru.Api.Platform.Health;

public interface IPostgresReadinessProbe
{
    Task<bool> IsReadyAsync(CancellationToken ct = default);
}
