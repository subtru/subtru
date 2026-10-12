namespace SubTru.Domain.Subscriptions;

public static class SubscriptionEnvironment
{
    public const string Production = "production";
    public const string Test = "test";

    public static bool IsValid(string? environment)
        => string.Equals(environment, Production, StringComparison.Ordinal)
            || string.Equals(environment, Test, StringComparison.Ordinal);

    public static string NormalizeOrDefault(string? environment)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return Production;
        }

        var normalized = environment.Trim().ToLowerInvariant();
        return IsValid(normalized)
            ? normalized
            : throw new ArgumentException("Environment must be production or test.", nameof(environment));
    }
}
