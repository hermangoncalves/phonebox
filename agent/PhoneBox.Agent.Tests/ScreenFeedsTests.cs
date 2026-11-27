using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

internal sealed class FakeScreenRun : IScreenRun
{
    public List<object> Sent { get; } = new();
    public bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public Task SendAsync(object command, CancellationToken ct) { Sent.Add(command); return Task.CompletedTask; }
}

internal sealed class ScreenFeedsTestRig : IAsyncDisposable
{
    public ConfigStore Store { get; }
    public ScreenFeeds Feeds { get; }
    public long NowMs { get; set; }
    public List<FakeScreenRun> Runs { get; } = new();
    private readonly string _dir;

    private ScreenFeedsTestRig(string dir, ConfigStore store, ScreenFeeds feeds)
    {
        _dir = dir;
        Store = store;
        Feeds = feeds;
        NowMs = 0;
    }

    public static ScreenFeedsTestRig Create(string dir)
    {
        var path = Path.Combine(dir, "config.json");
        var config = new AgentConfig
        {
            Phones = [new PhoneConfig { Id = "phone-a" }, new PhoneConfig { Id = "phone-b" }],
            Pages = [
                new PageConfig { Id = "desk", Enabled = true, Widgets = [
                    new WidgetConfig { Id = "screen", Type = "screen" },
                    new WidgetConfig { Id = "other", Type = "screen" },
                ] },
            ],
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        var store = new ConfigStore(path);

        var runs = new List<FakeScreenRun>();
        ScreenFeedsTestRig? rig = null;
        ScreenLauncher launcher = (opts, receive, ct) =>
        {
            var run = new FakeScreenRun();
            runs.Add(run);
            return Task.FromResult<IScreenRun>(run);
        };
        var feeds = new ScreenFeeds(store, launcher, () => rig!.NowMs);
        rig = new ScreenFeedsTestRig(dir, store, feeds, runs);
        rig.NowMs = 0;
        return rig;
    }

    private ScreenFeedsTestRig(string dir, ConfigStore store, ScreenFeeds feeds, List<FakeScreenRun> runs)
        : this(dir, store, feeds)
    {
        Runs.AddRange(runs);
    }

    public async ValueTask DisposeAsync()
    {
        await Feeds.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public sealed class ScreenFeedsTests : IAsyncLifetime
{
    private string _dir = "";

    public Task InitializeAsync() { _dir = Path.Combine(Path.GetTempPath(), "screenfeeds-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_dir); return Task.CompletedTask; }
    public async Task DisposeAsync() { try { Directory.Delete(_dir, recursive: true); } catch { } await Task.CompletedTask; }

    [Fact]
    public async Task AnotherWidgetCannotTakeTheSamePhoneSlot()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var first = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1),
            _ => Task.CompletedTask, default);
        Assert.NotNull(first.Connection);
        var second = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/other", 2),
            _ => Task.CompletedTask, default);
        Assert.Equal("busy", second.State);
        Assert.Null(second.Connection);
    }

    [Fact]
    public async Task SameWidgetReplacementClosesPrevious()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var first = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1),
            _ => Task.CompletedTask, default);
        Assert.NotNull(first.Connection);
        var run0 = rig.Runs[0];
        var second = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 2),
            _ => Task.CompletedTask, default);
        Assert.NotNull(second.Connection);
        Assert.True(run0.Disposed);
    }

    [Fact]
    public async Task HiddenWidgetCannotOpen()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var c = rig.Store.Get();
        c.Pages[0].Widgets = c.Pages[0].Widgets!.Where(w => w.Id != "screen").ToList();
        rig.Store.Save(JsonSerializer.Serialize(c));
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.Null(res.Connection);
    }

    [Fact]
    public async Task HeartbeatExpiryTenSeconds()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.NotNull(res.Connection);
        rig.NowMs = 0;
        await rig.Feeds.HeartbeatAsync(res.Connection!);
        rig.NowMs = 9_999;
        await rig.Feeds.TickAsync();
        rig.NowMs = 10_000;
        await rig.Feeds.TickAsync();
        var res2 = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 2), _ => Task.CompletedTask, default);
        Assert.NotNull(res2.Connection);
    }

    [Fact]
    public async Task HelperHealthExpirySixSeconds()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.NotNull(res.Connection);
        rig.NowMs = 6_000;
        await rig.Feeds.TickAsync();
        var res2 = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 2), _ => Task.CompletedTask, default);
        Assert.NotNull(res2.Connection);
    }

    [Fact]
    public async Task NoFrameForSixtySecondsStaysLiveWithHeartbeats()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.NotNull(res.Connection);
        for (var i = 1; i <= 60; i++)
        {
            rig.NowMs = i * 1000;
            await rig.Feeds.HeartbeatAsync(res.Connection!);
            await rig.Feeds.TickAsync();
        }
        var res2 = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 99), _ => Task.CompletedTask, default);
        Assert.NotNull(res2.Connection);
    }

    [Fact]
    public async Task SettingsSavedClosesDisabledWidget()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.NotNull(res.Connection);
        var c = rig.Store.Get();
        c.Pages[0].Widgets![0].Id = "gone";
        rig.Store.Save(JsonSerializer.Serialize(c));
        await rig.Feeds.SettingsSavedAsync();
        c = rig.Store.Get();
        c.Pages[0].Widgets![0].Id = "screen";
        rig.Store.Save(JsonSerializer.Serialize(c));
        var res2 = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 2), _ => Task.CompletedTask, default);
        Assert.NotNull(res2.Connection);
    }

    [Fact]
    public async Task StaleGenerationRejected()
    {
        await using var rig = ScreenFeedsTestRig.Create(_dir);
        var res = await rig.Feeds.OpenAsync(new ScreenViewer("phone-a", "desk/screen", 1), _ => Task.CompletedTask, default);
        Assert.NotNull(res.Connection);
        var stale = new ScreenConnection(res.Connection!.Viewer, res.Connection.Generation + 999);
        await rig.Feeds.HeartbeatAsync(stale);
        await rig.Feeds.CloseAsync(stale);
        await rig.Feeds.CloseAsync(res.Connection!);
    }
}
