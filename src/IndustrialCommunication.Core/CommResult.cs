namespace IndustrialCommunication;

/// <summary>
/// Result of a communication operation that produces no value. Runtime failures are reported
/// through this result instead of exceptions; exceptions are reserved for startup/programming errors.
/// </summary>
public readonly struct CommResult
{
    public bool Success { get; }
    public CommErrorKind Kind { get; }
    public string? ErrorCode { get; }
    public string? Message { get; }

    private CommResult(CommErrorKind kind, string? errorCode, string? message)
    {
        Kind = kind;
        ErrorCode = errorCode;
        Message = message;
        Success = kind == CommErrorKind.None;
    }

    public static CommResult Ok() => new(CommErrorKind.None, null, null);

    public static CommResult Fail(CommErrorKind kind, string? errorCode = null, string? message = null)
    {
        if (kind == CommErrorKind.None)
            throw new ArgumentException("A failing result cannot use CommErrorKind.None.", nameof(kind));
        return new CommResult(kind, errorCode, message);
    }

    /// <summary>Throws a <see cref="CommunicationException"/> when the result represents a failure.</summary>
    public void EnsureSuccess()
    {
        if (!Success)
            throw new CommunicationException(Kind, ErrorCode, Message);
    }

    public override string ToString() => Success
        ? "OK"
        : string.IsNullOrEmpty(ErrorCode) ? $"{Kind}: {Message}" : $"{Kind} [{ErrorCode}]: {Message}";
}

/// <summary>Result of a communication operation that produces a value. <see cref="Value"/> is only meaningful when <see cref="Success"/> is true.</summary>
public readonly struct CommResult<T>
{
    public bool Success { get; }
    public CommErrorKind Kind { get; }
    public string? ErrorCode { get; }
    public string? Message { get; }
    public T Value { get; }

    private CommResult(CommErrorKind kind, string? errorCode, string? message, T value)
    {
        Kind = kind;
        ErrorCode = errorCode;
        Message = message;
        Value = value;
        Success = kind == CommErrorKind.None;
    }

    public static CommResult<T> Ok(T value) => new(CommErrorKind.None, null, null, value);

    public static CommResult<T> Fail(CommErrorKind kind, string? errorCode = null, string? message = null)
    {
        if (kind == CommErrorKind.None)
            throw new ArgumentException("A failing result cannot use CommErrorKind.None.", nameof(kind));
        return new CommResult<T>(kind, errorCode, message, default!);
    }

    /// <summary>Throws a <see cref="CommunicationException"/> on failure, returns the value on success.</summary>
    public T EnsureSuccess()
    {
        if (!Success)
            throw new CommunicationException(Kind, ErrorCode, Message);
        return Value;
    }

    /// <summary>Drops the value, keeping the failure information.</summary>
    public CommResult WithoutValue() => Success ? CommResult.Ok() : CommResult.Fail(Kind, ErrorCode, Message);

    public override string ToString() => Success
        ? $"OK: {Value}"
        : string.IsNullOrEmpty(ErrorCode) ? $"{Kind}: {Message}" : $"{Kind} [{ErrorCode}]: {Message}";
}
