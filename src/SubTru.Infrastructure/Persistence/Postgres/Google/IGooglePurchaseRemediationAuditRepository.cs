namespace SubTru.Infrastructure.Persistence.Postgres.Google;

public interface IGooglePurchaseRemediationAuditRepository
{
    Task InsertAsync(
        GooglePurchaseRemediationAuditWriteModel writeModel,
        CancellationToken ct = default);
}
