namespace SubTru.Infrastructure.Providers.Google;

public sealed class GoogleServiceAccountValidationException : Exception
{
    public GoogleServiceAccountValidationException(string errorCode, string message, bool isTransient = false)
        : base(message)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
    }

    public GoogleServiceAccountValidationException(string errorCode, string message, Exception innerException, bool isTransient = false)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        IsTransient = isTransient;
    }

    public string ErrorCode { get; }

    public bool IsTransient { get; }
}
