using System.Collections.Concurrent;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.Tests.TestSupport;
using Xunit;

namespace IndustrialCommunication.Tests;

public class PollingEngineTests
{
    private static async Task<T> UntilAsync<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not met in time.");
    }

    private static CommunicationHost CreateHost(out FakePlcClient client)
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse("""
                {
                  "defaults": { "connectRetries": 0 },
                  "devices": [ { "name": "fake", "protocol": "Fake", "connection": {} } ],
                  "pollGroups": [
                    { "name": "main", "device": "fake", "intervalMs": 10,
                      "points": [
                        { "name": "a", "address": "W:10", "valueType": "UInt16" },
                        { "name": "b", "address": "W:11", "valueType": "UInt16" }
                      ] }
                  ]
                }
                """),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));
        client = (FakePlcClient)host.GetClient("fake");
        return host;
    }

    [Fact]
    public async Task First_poll_reports_all_points_then_only_changes()
    {
        await using var host = CreateHost(out var client);
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)7] : [(ushort)8];

        var events = new ConcurrentQueue<PollEventArgs>();
        await using var engine = host.CreatePollingEngine();
        engine.PollCompleted += (_, e) => events.Enqueue(e);
        await engine.StartAsync();

        var first = await UntilAsync(() => events.IsEmpty ? null : events.ToArray()[0]);
        Assert.Equal(["a", "b"], first.Changed.Keys.OrderBy(k => k).ToArray());
        Assert.Equal((ushort)7, first.Changed["a"]);

        // Same values again → new events, but empty Changed.
        var steady = await UntilAsync(() => events.Count >= 3 ? events.ToArray()[2] : null);
        Assert.Empty(steady.Changed);
        Assert.True(steady.Result.AllSuccess);

        // One value changes → exactly that point is reported (b flips to 1 on the next poll).
        var bump = 0;
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)7] : [(ushort)++bump];
        var changed = await UntilAsync(() => events.ToArray().LastOrDefault(e =>
            e.Changed.TryGetValue("b", out var v) && v is (ushort)1));
        Assert.Equal(["b"], changed.Changed.Keys);
        Assert.Equal((ushort)1, changed.Changed["b"]);
    }

    [Fact]
    public async Task Subscribe_fires_only_for_the_subscribed_point()
    {
        await using var host = CreateHost(out var client);
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)7] : [(ushort)8];

        var received = new ConcurrentQueue<object?>();
        await using var engine = host.CreatePollingEngine();
        using var subscription = engine.Subscribe("b", v => received.Enqueue(v));
        await engine.StartAsync();

        var first = await UntilAsync(() => received.IsEmpty ? null : received.ToArray()[0]);
        Assert.Equal((ushort)8, first);

        // 'a' changes → no callback (subscribed point unchanged).
        var version = 0;
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)++version] : [(ushort)8];
        await Task.Delay(150);
        Assert.Single(received);

        // 'b' changes → callback.
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)version] : [(ushort)99];
        var second = await UntilAsync(() => received.Count >= 2 ? received.ToArray()[1] : null);
        Assert.Equal((ushort)99, second);
    }

    [Fact]
    public async Task StopAsync_halts_the_loops()
    {
        await using var host = CreateHost(out var client);
        client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)1] : [(ushort)2];

        var counts = new int[1];
        await using var engine = host.CreatePollingEngine();
        engine.PollCompleted += (_, _) => Interlocked.Increment(ref counts[0]);
        await engine.StartAsync();

        await UntilAsync(() => Volatile.Read(ref counts[0]) > 0 ? new object() : null);
        await engine.StopAsync();
        var after = Volatile.Read(ref counts[0]);
        await Task.Delay(100);
        Assert.Equal(after, Volatile.Read(ref counts[0]));
    }

    [Fact]
    public async Task Failing_reads_keep_the_last_good_value_and_do_not_crash_the_loop()
    {
        await using var host = CreateHost(out var client);
        var broken = false;
        client.WordValues = (addr, c) => broken
            ? throw new IOException("poll boom")
            : addr.Offset == 10 ? [(ushort)5] : [(ushort)6];

        var events = new ConcurrentQueue<PollEventArgs>();
        await using var engine = host.CreatePollingEngine();
        engine.PollCompleted += (_, e) => events.Enqueue(e);
        await engine.StartAsync();

        await UntilAsync(() => events.IsEmpty ? null : new object());

        broken = true;
        var failedCycle = await UntilAsync(() =>
            events.ToArray().FirstOrDefault(e => !e.Result.AllSuccess) is { } fe && fe.Result.Statuses.Values.Any(s => !s.Success) ? fe : null);
        Assert.Empty(failedCycle.Changed);

        broken = false;
        var recovered = await UntilAsync(() =>
            events.ToArray().LastOrDefault(e => e.Changed.ContainsKey("a") && (ushort)e.Changed["a"]! == 5));
        Assert.NotNull(recovered);
    }

    [Fact]
    public void Loader_rejects_duplicate_group_names()
    {
        Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Parse("""
            { "devices": [ { "name": "d", "protocol": "F" } ],
              "pollGroups": [
                { "name": "g", "device": "d", "intervalMs": 50, "points": [ { "name": "p", "address": "W:0" } ] },
                { "name": "G", "device": "d", "intervalMs": 50, "points": [ { "name": "p", "address": "W:1" } ] }
              ] }
            """));
    }
}
