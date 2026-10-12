using System.Net;

namespace SubTru.Infrastructure.Providers.Google;

public sealed class GooglePlayApiException : Exception
{
    public GooglePlayApiException(
        string errorCode,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null,
        bool isTransient = false)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public string ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }

    public bool IsTransient { get; }
}
