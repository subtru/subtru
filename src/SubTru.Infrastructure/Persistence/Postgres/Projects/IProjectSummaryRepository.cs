namespace SubTru.Infrastructure.Persistence.Postgres.Projects;

public interface IProjectSummaryRepository
{
    Task<ProjectSummaryReadModel> GetSummaryAsync(
        Guid projectId,
        string environment,
        CancellationToken ct
    );
}
