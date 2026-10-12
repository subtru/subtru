using Microsoft.AspNetCore.Hosting;

namespace SubTru.Tests.Support;

internal static class TestWebHostConfiguration
{
    private const string AdminApiKey =
        "subtru_admin_0123456789abcdef0123456789abcdef";

    public static void UseAdminApiKey(IWebHostBuilder builder)
        => builder.UseSetting("SUBTRU_ADMIN_API_KEY", AdminApiKey);
}
