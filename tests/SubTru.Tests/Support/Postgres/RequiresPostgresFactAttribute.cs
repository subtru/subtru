namespace SubTru.Tests.Support.Postgres;

public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        var hasConnectionString = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("SUBTRU_TEST_DB_CONNECTION"));

        if (!hasConnectionString && !System.Diagnostics.Debugger.IsAttached)
        {
            Skip = "Set SUBTRU_TEST_DB_CONNECTION to run integration tests.";
        }
    }
}
