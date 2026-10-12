using SubTru.Api.Features.Webhooks.Shared;

namespace SubTru.Tests.Features.Webhooks.Shared;

public sealed class WebhookUrlBuilderTests
{
    [Theory]
    [InlineData("https://api.subtru.com", "https://api.subtru.com")]
    [InlineData(" https://api.subtru.com/ ", "https://api.subtru.com")]
    [InlineData("https://api.subtru.com:8443/", "https://api.subtru.com:8443")]
    public void NormalizeConfiguredBaseUrl_ValidHttpsOrigin_ReturnsOrigin(string value, string expected)
    {
        var normalized = WebhookUrlBuilder.NormalizeConfiguredBaseUrl(value);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://api.subtru.com")]
    [InlineData("https://api.subtru.com/webhooks")]
    [InlineData("https://api.subtru.com?environment=test")]
    [InlineData("https://api.subtru.com#webhooks")]
    public void NormalizeConfiguredBaseUrl_InvalidValue_Throws(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlBuilder.NormalizeConfiguredBaseUrl(value));
    }

    [Fact]
    public void Build_MissingPublicBaseUrl_Throws()
    {
        var options = new WebhookUrlOptions();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebhookUrlBuilder.Build(options, "google", "subtru_wh_google.secret"));

        Assert.Contains("WebhookUrls__PublicBaseUrl", exception.Message);
    }

    [Fact]
    public void Build_UsesConfiguredPublicBaseUrl()
    {
        var options = new WebhookUrlOptions
        {
            PublicBaseUrl = "https://api.subtru.com"
        };

        var url = WebhookUrlBuilder.Build(options, "google", "subtru_wh_google.secret");

        Assert.Equal("https://api.subtru.com/webhooks/google/subtru_wh_google.secret", url);
    }
}
