namespace SubTru.Api.Platform.Health;

public interface IApiReadinessCheck
{
    Task<ReadinessResult> CheckAsync(CancellationToken ct = default);
}
