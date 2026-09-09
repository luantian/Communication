using IndustrialCommunication.OpcUa;
using Xunit;

namespace IndustrialCommunication.Tests;

public class OpcUaOptionsTests
{
    [Fact]
    public void Validate_requires_opc_tcp_endpoint()
    {
        var missing = new OpcUaOptions();
        Assert.Contains("endpointUrl", missing.Validate()[0]);

        var wrongScheme = new OpcUaOptions { EndpointUrl = "http://192.168.1.50:4840" };
        Assert.Single(wrongScheme.Validate());

        var good = new OpcUaOptions { EndpointUrl = "opc.tcp://192.168.1.50:4840" };
        Assert.Empty(good.Validate());
    }

    [Fact]
    public void Timeouts_have_minimums()
    {
        var options = new OpcUaOptions
        {
            EndpointUrl = "opc.tcp://192.168.1.50:4840",
            SessionTimeoutMs = 100,
            KeepAliveIntervalMs = 10,
        };

        Assert.Equal(2, options.Validate().Count);
    }
}

public class OpcUaValueTests
{
    [Fact]
    public void Convert_handles_matching_and_widening_types()
    {
        Assert.True(OpcUaValue.TryConvert<short>((short)-5, out var s));
        Assert.Equal((short)-5, s);

        Assert.True(OpcUaValue.TryConvert<int>(42, out var i));
        Assert.Equal(42, i);

        Assert.True(OpcUaValue.TryConvert<double>(3.5f, out var d));
        Assert.Equal(3.5, d, 3);

        Assert.True(OpcUaValue.TryConvert<ushort>("1234", out var us));
        Assert.Equal((ushort)1234, us);

        Assert.True(OpcUaValue.TryConvert<ushort>(true, out var bit));
        Assert.Equal((ushort)1, bit);
    }

    [Fact]
    public void Convert_rejects_unconvertible_values()
    {
        Assert.False(OpcUaValue.TryConvert<ushort>(70000, out _));
        Assert.False(OpcUaValue.TryConvert<float>("not-a-number", out _));
        Assert.False(OpcUaValue.TryConvert<int>(new object(), out _));
    }

    [Fact]
    public void ToWords_and_ToBits_slice_arrays_and_reject_short_values()
    {
        Assert.Equal(new ushort[] { 1, 2 }, OpcUaValue.ToWords(new ushort[] { 1, 2, 3 }, 2).Value);
        Assert.Equal(new ushort[] { 7 }, OpcUaValue.ToWords((short)7, 1).Value);
        Assert.False(OpcUaValue.ToWords("text", 1).Success);
        Assert.False(OpcUaValue.ToWords(new ushort[] { 1 }, 2).Success);

        Assert.Equal(new[] { true, false }, OpcUaValue.ToBits(new[] { true, false, true }, 2).Value);
        Assert.False(OpcUaValue.ToBits((ushort)1, 1).Success);
    }
}
