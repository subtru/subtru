namespace SubTru.Api.Platform.Authentication;

public static class HttpContextProjectContextExtensions
{
    public static ProjectContext GetRequiredProjectContext(this HttpContext context)
    {
        if (context.Items.TryGetValue(AuthConstants.ProjectContextItemKey, out var value) &&
            value is ProjectContext projectContext)
        {
            return projectContext;
        }

        throw new InvalidOperationException("Project context is missing from the current request.");
    }

    public static string GetRequiredUserEmail(this HttpContext context)
    {
        var ctx = context.GetRequiredProjectContext();
        return ctx.Email ?? throw new InvalidOperationException(
            "User email is not available for the current authenticated request.");
    }
}
