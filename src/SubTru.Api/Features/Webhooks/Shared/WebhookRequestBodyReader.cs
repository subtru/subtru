using System.Buffers;
using System.Text;

namespace SubTru.Api.Features.Webhooks.Shared;

internal static class WebhookRequestBodyReader
{
    internal const int MaxBodySizeBytes = 1024 * 1024;
    private const int BufferSizeBytes = 16 * 1024;

    internal static async Task<string?> ReadIfWithinLimitAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > MaxBodySizeBytes)
        {
            return null;
        }

        var initialCapacity = request.ContentLength is > 0 and <= MaxBodySizeBytes
            ? (int)request.ContentLength.Value
            : 0;
        using var body = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSizeBytes);

        try
        {
            var totalBytesRead = 0;

            while (true)
            {
                var remainingBytes = MaxBodySizeBytes - totalBytesRead;
                var bytesRead = await request.Body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingBytes + 1)),
                    ct);

                if (bytesRead == 0)
                {
                    return Encoding.UTF8.GetString(body.GetBuffer(), 0, totalBytesRead);
                }

                totalBytesRead += bytesRead;
                if (totalBytesRead > MaxBodySizeBytes)
                {
                    return null;
                }

                body.Write(buffer, 0, bytesRead);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
