using System.Text;
using Microsoft.AspNetCore.Http;
using SubTru.Api.Features.Webhooks.Shared;

namespace SubTru.Tests.Features.Webhooks.Shared;

public sealed class WebhookRequestBodyReaderTests
{
    [Fact]
    public async Task ReadIfWithinLimitAsync_ReturnsBodyWithinLimit()
    {
        const string body = "webhook body";
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = null;

        var result = await WebhookRequestBodyReader.ReadIfWithinLimitAsync(
            context.Request,
            CancellationToken.None);

        Assert.Equal(body, result);
    }

    [Fact]
    public async Task ReadIfWithinLimitAsync_RejectsOversizedBodyWithoutContentLength()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(
            new byte[WebhookRequestBodyReader.MaxBodySizeBytes + 1]);
        context.Request.ContentLength = null;

        var result = await WebhookRequestBodyReader.ReadIfWithinLimitAsync(
            context.Request,
            CancellationToken.None);

        Assert.Null(result);
    }
}
