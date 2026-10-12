using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using SubTru.Api.Platform.Authentication;
using SubTru.Api.Platform.Http;
using SubTru.Api.Platform.Observability;
using SubTru.Infrastructure.Persistence.Postgres.Projects;

namespace SubTru.Api.Features.Projects;

public static partial class ProjectManagementEndpoints
{
    [GeneratedRegex(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public static void MapProjectManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/projects");
        group.MapGet("", ListProjectsAsync);
        group.MapPost("", CreateProjectAsync);
        group.MapPut("/{projectId}", UpdateProjectAsync);
    }

    private static async Task<Results<Ok<ProjectListResponse>, JsonHttpResult<ApiErrorResponse>>> ListProjectsAsync(
        HttpContext context,
        IProjectManagementRepository repository,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(context, "Project management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        var projects = await repository.ListAsync(ct);
        return TypedResults.Ok(new ProjectListResponse(projects.Select(ToDto).ToList()));
    }

    private static async Task<Results<Created<ProjectDto>, BadRequest<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> CreateProjectAsync(
        CreateProjectRequest? request,
        HttpContext context,
        IProjectManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(context, "Project management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!TryNormalize(request?.Name, request?.Slug, out var name, out var slug, out var error))
        {
            return TypedResults.BadRequest(error!);
        }

        var project = await repository.CreateAsync(
            new ProjectWriteModel(name!, slug, IsActive: true),
            ct);
        ProductEventLogging.LogProjectCreated(loggerFactory, project.Id);
        return TypedResults.Created($"/v1/projects/{project.Id}", ToDto(project));
    }

    private static async Task<Results<Ok<ProjectDto>, BadRequest<ApiErrorResponse>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> UpdateProjectAsync(
        Guid projectId,
        UpdateProjectRequest? request,
        HttpContext context,
        IProjectManagementRepository repository,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var permissionError = ManagementAuthorization.RequireAdmin(context, "Project management");
        if (permissionError is not null)
        {
            return permissionError;
        }

        if (!TryNormalize(request?.Name, request?.Slug, out var name, out var slug, out var error))
        {
            return TypedResults.BadRequest(error!);
        }

        var project = await repository.UpdateAsync(
            projectId,
            new ProjectWriteModel(name!, slug, request!.IsActive),
            ct);
        if (project is null)
        {
            return TypedResults.NotFound(new ApiErrorResponse(
                "project_not_found",
                "Project not found."));
        }

        ProductEventLogging.LogProjectUpdated(loggerFactory, project.Id, project.IsActive);
        return TypedResults.Ok(ToDto(project));
    }

    private static bool TryNormalize(
        string? requestedName,
        string? requestedSlug,
        out string? name,
        out string? slug,
        out ApiErrorResponse? error)
    {
        name = requestedName?.Trim();
        slug = string.IsNullOrWhiteSpace(requestedSlug)
            ? null
            : requestedSlug.Trim().ToLowerInvariant();
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = new ApiErrorResponse("missing_name", "Field 'name' is required.");
            return false;
        }

        if (name.Length > 200)
        {
            error = new ApiErrorResponse("invalid_name", "Field 'name' must be 200 characters or fewer.");
            return false;
        }

        if (slug is not null && (slug.Length > 100 || !SlugPattern().IsMatch(slug)))
        {
            error = new ApiErrorResponse(
                "invalid_slug",
                "Field 'slug' must contain lowercase letters, numbers, and single hyphens only.");
            return false;
        }

        return true;
    }

    private static ProjectDto ToDto(ProjectListItem project)
        => new(
            project.Id,
            project.Name,
            project.Slug,
            project.IsActive,
            project.CreatedAt,
            project.UpdatedAt);
}
