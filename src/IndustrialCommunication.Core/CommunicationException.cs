namespace IndustrialCommunication;

/// <summary>
/// Thrown for startup and programming errors (bad configuration, unknown protocol, duplicate device names)
/// and by <c>EnsureSuccess()</c> bridges on runtime failures.
/// </summary>
public sealed class CommunicationException : Exception
{
    public CommErrorKind Kind { get; }
    public string? ErrorCode { get; }

    public CommunicationException(CommErrorKind kind, string? errorCode, string? message)
        : base(message is null
            ? (errorCode is null ? kind.ToString() : $"{kind} [{errorCode}]")
            : (errorCode is null ? message : $"{message} [{errorCode}]"))
    {
        Kind = kind;
        ErrorCode = errorCode;
    }

    public CommunicationException(string message)
        : base(message)
    {
        Kind = CommErrorKind.InvalidArgument;
    }
}
