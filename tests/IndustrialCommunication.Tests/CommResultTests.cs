using IndustrialCommunication;
using Xunit;

namespace IndustrialCommunication.Tests;

public class CommResultTests
{
    [Fact]
    public void Ok_succeeds_with_no_error()
    {
        var r = CommResult.Ok();

        Assert.True(r.Success);
        Assert.Equal(CommErrorKind.None, r.Kind);
        Assert.Null(r.ErrorCode);
        Assert.Null(r.Message);
        r.EnsureSuccess();
    }

    [Theory]
    [InlineData(CommErrorKind.Timeout)]
    [InlineData(CommErrorKind.DeviceRejected)]
    public void Fail_carries_kind_and_details(CommErrorKind kind)
    {
        var r = CommResult.Fail(kind, "C500", "station busy");

        Assert.False(r.Success);
        Assert.Equal(kind, r.Kind);
        Assert.Equal("C500", r.ErrorCode);
        Assert.Equal("station busy", r.Message);
        Assert.Contains("C500", r.ToString());
    }

    [Fact]
    public void Fail_with_None_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CommResult.Fail(CommErrorKind.None));
        Assert.Throws<ArgumentException>(() => CommResult<int>.Fail(CommErrorKind.None));
    }

    [Fact]
    public void EnsureSuccess_throws_CommunicationException_with_kind()
    {
        var r = CommResult.Fail(CommErrorKind.ConnectionLost, null, "reset by peer");

        var ex = Assert.Throws<CommunicationException>(() => r.EnsureSuccess());
        Assert.Equal(CommErrorKind.ConnectionLost, ex.Kind);
    }

    [Fact]
    public void Generic_result_value_only_on_success()
    {
        var ok = CommResult<int>.Ok(42);
        Assert.True(ok.Success);
        Assert.Equal(42, ok.Value);
        Assert.Equal(42, ok.EnsureSuccess());

        var fail = CommResult<int>.Fail(CommErrorKind.NotConnected);
        Assert.False(fail.Success);
        Assert.Equal(default, fail.Value);
        Assert.Throws<CommunicationException>(() => fail.EnsureSuccess());
    }

    [Fact]
    public void WithoutValue_preserves_failure()
    {
        var fail = CommResult<ushort[]>.Fail(CommErrorKind.ProtocolError, "0401", "bad frame");

        var plain = fail.WithoutValue();

        Assert.False(plain.Success);
        Assert.Equal(CommErrorKind.ProtocolError, plain.Kind);
        Assert.Equal("0401", plain.ErrorCode);
    }
}
