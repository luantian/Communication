using IndustrialCommunication;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Tests.TestSupport;

public sealed class FakeOptions : ConnectionOptions
{
    public string? Token { get; set; }
    public bool FailConnect { get; set; }
}

/// <summary>In-memory driver used to test the host/registry/config plumbing without any transport.</summary>
public sealed class FakePlcClient : PlcClientBase
{
    public FakeOptions ResolvedOptions { get; }
    public DeviceRuntimeOptions ResolvedRuntime { get; }
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }

    /// <summary>Optional test hook: returns the words for a read (null = fall through to written/default values).</summary>
    public Func<DeviceAddress, int, ushort[]?>? WordValues { get; set; }

    /// <summary>Values written through DoWriteWordsAsync, keyed by word offset.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<int, ushort> WrittenWords { get; } = new();

    public FakePlcClient(FakeOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        ResolvedOptions = options;
        ResolvedRuntime = context.Runtime;
    }

    protected override Task DoConnectAsync(CancellationToken ct)
    {
        ConnectCount++;
        if (ResolvedOptions.FailConnect)
            throw new IOException("fake connect failure");
        return Task.CompletedTask;
    }

    protected override Task DoDisconnectAsync()
    {
        DisconnectCount++;
        return Task.CompletedTask;
    }

    protected override DeviceAddress ParseAddress(string address)
    {
        var parts = address.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var offset))
            throw new FormatException($"Address '{address}' must look like 'AREA:123'.");
        return new DeviceAddress { Area = parts[0], Offset = offset, IsBit = parts[0] == "B" };
    }

    protected override Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct) =>
        Task.FromResult(CommResult<bool[]>.Ok(Enumerable.Range(0, count).Select(i => (address.Offset + i) % 2 == 0).ToArray()));

    protected override Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct) =>
        Task.FromResult(CommResult.Ok());

    protected override Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        ushort[]? words = WordValues?.Invoke(address, count);
        if (words is null)
        {
            words = new ushort[count];
            for (int i = 0; i < count; i++)
                words[i] = WrittenWords.TryGetValue(address.Offset + i, out var stored) ? stored : (ushort)0;
        }

        return Task.FromResult(CommResult<ushort[]>.Ok(words));
    }

    protected override Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        for (int i = 0; i < values.Count; i++)
            WrittenWords[address.Offset + i] = values[i];
        return Task.FromResult(CommResult.Ok());
    }
}
