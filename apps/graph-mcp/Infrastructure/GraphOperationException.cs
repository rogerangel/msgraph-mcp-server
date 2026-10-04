using GraphMcp.Models;

namespace GraphMcp.Infrastructure;

public sealed class GraphOperationException(string code, string message, int? retryAfterSeconds = null, string? requestId = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public string? RequestId { get; } = requestId;
    public ErrorDto ToError() => new(Code, Message, RetryAfterSeconds, RequestId);
    public static GraphOperationException Invalid(string message) => new("invalid_input", message);
}
