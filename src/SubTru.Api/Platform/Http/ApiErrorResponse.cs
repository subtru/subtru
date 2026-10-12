namespace SubTru.Api.Platform.Http;

public sealed record ApiErrorResponse(
    string Error,
    string Message
);
