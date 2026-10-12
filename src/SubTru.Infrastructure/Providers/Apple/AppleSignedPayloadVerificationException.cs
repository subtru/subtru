namespace SubTru.Infrastructure.Providers.Apple;

public sealed class AppleSignedPayloadVerificationException : Exception
{
    public AppleSignedPayloadVerificationException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
