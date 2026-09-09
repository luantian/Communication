namespace IndustrialCommunication;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState state, string? reason)
    {
        State = state;
        Reason = reason;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public ConnectionState State { get; }
    public string? Reason { get; }
    public DateTimeOffset Timestamp { get; }
}
