namespace SubTru.Infrastructure.Providers.Apple;

public sealed class AppleVerificationOptions
{
    public string? RootCertificateDirectory { get; set; }

    public bool EnableOnlineRevocationChecks { get; set; } = true;

    public TimeSpan OnlineRevocationTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
