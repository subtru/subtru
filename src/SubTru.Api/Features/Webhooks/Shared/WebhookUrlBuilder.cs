namespace SubTru.Api.Features.Webhooks.Shared;

public static class WebhookUrlBuilder
{
    public static string Build(WebhookUrlOptions options, string provider, string webhookKey)
    {
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            throw new InvalidOperationException("Missing required configuration: WebhookUrls__PublicBaseUrl");
        }

        var baseUrl = options.PublicBaseUrl;
        return $"{baseUrl}/webhooks/{provider}/{webhookKey}";
    }

    public static string NormalizeConfiguredBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Missing required configuration: WebhookUrls__PublicBaseUrl");
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.Query)
            || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            throw new InvalidOperationException(
                "WebhookUrls__PublicBaseUrl must be an absolute HTTPS URL without a query string or fragment.");
        }

        if (uri.AbsolutePath is not "" and not "/")
        {
            throw new InvalidOperationException(
                "WebhookUrls__PublicBaseUrl must not include a path.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
