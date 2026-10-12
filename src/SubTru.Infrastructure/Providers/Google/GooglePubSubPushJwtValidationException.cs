namespace SubTru.Infrastructure.Providers.Google;

public sealed class GooglePubSubPushJwtValidationException : Exception
{
    public GooglePubSubPushJwtValidationException(
        string errorCode,
        string message,
        Exception? innerException = null,
        bool isTransient = false)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
    }

    public string ErrorCode { get; }

    public bool IsTransient { get; }
}
