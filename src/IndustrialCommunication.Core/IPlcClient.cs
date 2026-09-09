namespace IndustrialCommunication;

/// <summary>
/// Protocol-independent PLC/device client. Drivers implement the four primitive operations
/// (bit and 16-bit-word read/write); the typed layer on top of them is provided by <see cref="PlcClientBase"/>.
/// </summary>
public interface IPlcClient : IAsyncDisposable
{
    string DeviceName { get; }
    bool IsConnected { get; }

    /// <summary>Raised on connection transitions. Handlers run on the thread pool and must not block.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>One cheap probe through the standard pipeline; used by the connection monitor.</summary>
    Task<CommResult> HeartbeatAsync(CancellationToken ct = default);

    // —— Primitives (driver-implemented) ——

    Task<CommResult<bool[]>> ReadBitsAsync(string address, ushort count, CancellationToken ct = default);
    Task<CommResult> WriteBitsAsync(string address, IReadOnlyList<bool> values, CancellationToken ct = default);
    Task<CommResult<ushort[]>> ReadWordsAsync(string address, ushort count, CancellationToken ct = default);
    Task<CommResult> WriteWordsAsync(string address, IReadOnlyList<ushort> values, CancellationToken ct = default);

    // —— Typed layer (default implementation, overridable by drivers) ——

    Task<CommResult<T>> ReadAsync<T>(string address, CancellationToken ct = default) where T : struct;
    Task<CommResult> WriteAsync<T>(string address, T value, CancellationToken ct = default) where T : struct;
    Task<CommResult<string>> ReadStringAsync(string address, ushort wordLength, CancellationToken ct = default);
    Task<CommResult> WriteStringAsync(string address, string value, ushort wordLength, CancellationToken ct = default);

    /// <summary>Reads multiple points sequentially; each point gets its own status, one bad point never fails the group.</summary>
    Task<GroupReadResult> ReadGroupAsync(IReadOnlyList<DevicePoint> points, CancellationToken ct = default);
}
