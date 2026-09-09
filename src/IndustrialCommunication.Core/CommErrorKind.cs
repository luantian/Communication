namespace IndustrialCommunication;

/// <summary>Category of a communication failure.</summary>
public enum CommErrorKind
{
    /// <summary>Not an error — used by successful results only.</summary>
    None = 0,

    /// <summary>The client is not connected and auto-reconnect is disabled.</summary>
    NotConnected,

    /// <summary>The operation did not complete within the configured timeout. The underlying connection is closed afterwards.</summary>
    Timeout,

    /// <summary>The transport broke (I/O error, reset connection, disposed socket).</summary>
    ConnectionLost,

    /// <summary>A frame was malformed or could not be interpreted.</summary>
    ProtocolError,

    /// <summary>The device answered with a protocol-level rejection (end code / response code / Modbus exception).</summary>
    DeviceRejected,

    /// <summary>The address string could not be parsed for this driver.</summary>
    InvalidAddress,

    /// <summary>An argument (e.g. word/bit count) is out of range.</summary>
    InvalidArgument,

    /// <summary>The operation was cancelled through the caller's CancellationToken.</summary>
    Cancelled,

    /// <summary>The client has been disposed.</summary>
    Disposed,
}
