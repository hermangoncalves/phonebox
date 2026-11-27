using PhoneBox.Agent;
using Xunit;

public sealed class ScreenLeaseTests
{
    [Fact]
    public void OldViewerCannotExpireOrCloseItsReplacement()
    {
        var lease = new ScreenLease("desk/screen", 1, 0);
        Assert.True(lease.Replace(1, 2, 2_000));
        Assert.False(lease.Close(1));
        Assert.True(lease.Heartbeat(2, 9_000));
        Assert.False(lease.Expired(18_999));
        Assert.True(lease.Expired(19_000));
    }

    [Fact]
    public void EpochIncrementsOnReplace()
    {
        var lease = new ScreenLease("k", 1, 0);
        var before = lease.Epoch;
        Assert.True(lease.Replace(1, 2, 100));
        Assert.Equal(before + 1, lease.Epoch);
    }

    [Fact]
    public void HeartbeatFromWrongViewerIsIgnored()
    {
        var lease = new ScreenLease("k", 1, 0);
        Assert.False(lease.Heartbeat(999, 5_000));
        Assert.True(lease.Expired(10_000));
    }

    [Fact]
    public void ZeroFramesStayLiveWithHeartbeats()
    {
        var lease = new ScreenLease("k", 1, 0);
        for (var i = 1; i <= 60; i++)
            Assert.True(lease.Heartbeat(1, i * 1000));
        Assert.False(lease.Expired(60_000));
    }

    [Fact]
    public void TenSecondsWithoutHeartbeatExpiresDespiteVideo()
    {
        var lease = new ScreenLease("k", 1, 0);
        // no heartbeats after t=0; video irrelevant
        Assert.True(lease.Expired(10_000));
        Assert.False(lease.Expired(9_999));
    }

    [Fact]
    public void CloseByOwnerSucceedsAndExpires()
    {
        var lease = new ScreenLease("k", 1, 0);
        Assert.True(lease.Close(1));
        Assert.True(lease.Expired(0));
        Assert.False(lease.Heartbeat(1, 1_000));
        Assert.False(lease.Close(1));
    }

    [Fact]
    public void ReplaceOnClosedLeaseFails()
    {
        var lease = new ScreenLease("k", 1, 0);
        Assert.True(lease.Close(1));
        Assert.False(lease.Replace(1, 2, 1_000));
    }
}
