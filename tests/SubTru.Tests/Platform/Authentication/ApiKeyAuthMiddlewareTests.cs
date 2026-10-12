using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SubTru.Api.Platform.Authentication;

namespace SubTru.Tests.Platform.Authentication;

public sealed class ApiKeyAuthMiddlewareTests
{
    [Fact]
    public async Task HealthEndpoint_DoesNotRequireAuthentication()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = CreateContext("/health/live");

        await middleware.InvokeAsync(
            context,
            Array.Empty<IProjectResolver>(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutBearerToken_ReturnsUnauthorized()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext("/v1/projects");

        await middleware.InvokeAsync(
            context,
            Array.Empty<IProjectResolver>(),
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminAuthentication_CanAddressAnyProject()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = CreateContext("/v1/projects/ignored");
        context.Request.Headers.Authorization = "Bearer subtru_admin_test";
        context.Request.RouteValues["projectId"] = Guid.NewGuid().ToString("D");
        var resolver = new FakeProjectResolver(new AuthResult(
            UserId: Guid.Empty,
            ProjectId: null,
            IsApiKeyAuth: false,
            IsAdminAuth: true));

        await middleware.InvokeAsync(
            context,
            [resolver],
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.True(nextCalled);
        Assert.True(context.GetRequiredProjectContext().IsAdminAuth);
    }

    [Fact]
    public async Task ProjectApiKey_CanAddressItsOwnProject()
    {
        var projectId = Guid.NewGuid();
        var nextCalled = false;
        var middleware = CreateMiddleware(() => nextCalled = true);
        var context = CreateContext("/v1/projects/ignored");
        context.Request.Headers.Authorization = "Bearer subtru_test_key.secret";
        context.Request.RouteValues["projectId"] = projectId.ToString("D");
        var resolver = new FakeProjectResolver(new AuthResult(
            UserId: null,
            ProjectId: projectId,
            IsApiKeyAuth: true,
            Environment: "test"));

        await middleware.InvokeAsync(
            context,
            [resolver],
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.True(nextCalled);
        var projectContext = context.GetRequiredProjectContext();
        Assert.True(projectContext.IsApiKeyAuth);
        Assert.False(projectContext.IsAdminAuth);
        Assert.Equal("test", projectContext.Environment);
    }

    [Fact]
    public async Task ProjectApiKey_CannotAddressAnotherProject()
    {
        var middleware = CreateMiddleware();
        var context = CreateContext("/v1/projects/ignored");
        context.Request.Headers.Authorization = "Bearer subtru_test_key.secret";
        context.Request.RouteValues["projectId"] = Guid.NewGuid().ToString("D");
        var resolver = new FakeProjectResolver(new AuthResult(
            UserId: null,
            ProjectId: Guid.NewGuid(),
            IsApiKeyAuth: true,
            Environment: "test"));

        await middleware.InvokeAsync(
            context,
            [resolver],
            NullLogger<ApiKeyAuthMiddleware>.Instance);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminResolver_UsesExactFixedToken()
    {
        const string expectedToken = "subtru_admin_0123456789abcdef0123456789abcdef";
        var resolver = new AdminProjectResolver(expectedToken);

        var valid = await resolver.ResolveAsync(expectedToken);
        var invalid = await resolver.ResolveAsync("subtru_admin_fedcba9876543210fedcba9876543210");

        Assert.NotNull(valid);
        Assert.True(valid.IsAdminAuth);
        Assert.Null(invalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wrong_prefix_0123456789abcdef0123456789abcdef")]
    [InlineData("subtru_admin_too-short")]
    public void AdminResolver_WithMissingOrInvalidToken_Throws(string? configuredToken)
    {
        Assert.Throws<InvalidOperationException>(() => new AdminProjectResolver(configuredToken));
    }

    private static ApiKeyAuthMiddleware CreateMiddleware(Action? onNext = null)
        => new(_ =>
        {
            onNext?.Invoke();
            return Task.CompletedTask;
        });

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeProjectResolver(AuthResult? result) : IProjectResolver
    {
        public Task<AuthResult?> ResolveAsync(string bearerToken, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
