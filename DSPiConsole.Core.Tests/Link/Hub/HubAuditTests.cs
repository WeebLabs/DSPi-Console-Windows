using System.Collections.Concurrent;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>Regressions for the L3 audit's findings.</summary>
public sealed class HubAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-audit-" + Guid.NewGuid().ToString("N"));
    private readonly LinkAuthStore _auth;
    private readonly LinkHub _hub;
    private readonly FakeHubDevice _device = new();

    public HubAuditTests()
    {
        Directory.CreateDirectory(_dir);
        _auth = new LinkAuthStore(Path.Combine(_dir, "auth.json"));
        _hub = new LinkHub(LinkPolicy.Embedded!, _auth, new DeviceRegistry(null)) { SnapshotRefreshDelay = TimeSpan.FromMilliseconds(10) };
        _hub.OpenSession(LinkRole.Admin, LinkSessionIds.Local);
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private (LinkSessionHandler Handler, BlockingCollection<LinkOutbound> Out) Open(string peer = "192.168.1.50")
    {
        var output = new BlockingCollection<LinkOutbound>();
        var h = new LinkSessionHandler(_hub, peer, o => output.Add(o));
        h.ReceiveText(new LinkHelloClient(new LinkClientInfo("Test")).Encode());
        return (h, output);
    }

    private static ushort? CloseCode(BlockingCollection<LinkOutbound> output, int ms = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
            if (output.TryTake(out var o, 50) && o is LinkOutbound.Close c) return c.Code;
        return null;
    }

    [Fact]
    public void TurningOnPairingClosesSessionsLetInWhileOpen()
    {
        var (h, output) = Open();
        Assert.True(h.IsAuthenticated);
        _auth.AuthMode = LinkAuthMode.Pin;
        Assert.Equal((ushort?)LinkCloseCode.TokenRevoked, CloseCode(output));
        Assert.False(h.IsAuthenticated);
    }

    [Fact]
    public void APairedSessionSurvivesTurningPairingOnAndIsAdminWhenOpen()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        var (h, output) = Open();
        h.ReceiveText(new LinkAuthPair(1, pin, "Phone", LinkRole.Viewer).Encode());
        Assert.True(SpinWait.SpinUntil(() => h.IsAuthenticated, 2000));
        _auth.AuthMode = LinkAuthMode.None;
        _auth.AuthMode = LinkAuthMode.Pin;
        Assert.Null(CloseCode(output, 300));
    }

    [Theory]
    [InlineData(LinkRole.Viewer)]
    [InlineData(LinkRole.Control)]
    public void APairedSessionGetsItsRoleBackWhenPairingReturns(LinkRole role)
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var paired = _auth.Pair(_auth.BeginPairing(), "Phone", role, "192.168.1.50");
        var cid = paired.Client!.Cid;
        var session = _hub.OpenSession(paired.Client.Role, clientId: cid);
        Assert.Equal(role, session.Role);

        _auth.AuthMode = LinkAuthMode.None;
        Assert.Equal(LinkRole.Admin, session.Role);
        _auth.AuthMode = LinkAuthMode.Pin;
        Assert.Equal(role, session.Role);
    }

    [Fact]
    public void ARoleChangedWhileOpenAppliesWhenPairingReturns()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var paired = _auth.Pair(_auth.BeginPairing(), "Phone", LinkRole.Admin, "192.168.1.50");
        var cid = paired.Client!.Cid;
        var session = _hub.OpenSession(paired.Client.Role, clientId: cid);

        _auth.AuthMode = LinkAuthMode.None;
        _auth.SetRole(cid, LinkRole.Viewer);
        Assert.Equal(LinkRole.Admin, session.Role);   // open access: still an admin
        _auth.AuthMode = LinkAuthMode.Pin;
        Assert.Equal(LinkRole.Viewer, session.Role);
    }

    [Fact]
    public void AViewerGivesUpItsLock()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        _hub.AttachDevice(_device);
        var (h, _) = Open();
        h.ReceiveText(new LinkAuthPair(1, pin, "Phone", LinkRole.Control).Encode());
        Assert.True(SpinWait.SpinUntil(() => h.IsAuthenticated, 2000));
        h.ReceiveText(new LinkLockAcquire(2, 0, "x", 30000).Encode());
        Assert.True(SpinWait.SpinUntil(() => _hub.Registry.Devices.Single().LockedBy != null, 2000));
        _auth.SetRole(_auth.Clients.Single().Cid, LinkRole.Viewer);
        Assert.True(SpinWait.SpinUntil(() => _hub.Registry.Devices.Single().LockedBy == null, 2000));
    }

    [Fact]
    public void HubNamesAreCleanedAndCapped()
    {
        _auth.HubName = "Studio\u0007 PC\r\n";
        Assert.Equal("Studio PC", _auth.HubName);
        _auth.HubName = new string('x', 500);
        Assert.Equal(LinkAuthStore.MaxHubNameBytes, System.Text.Encoding.UTF8.GetByteCount(_auth.HubName));
        _auth.HubName = "   ";
        Assert.Equal(LinkAuthStore.DefaultHubName(), _auth.HubName);
    }

    // ── Snapshot ──

    private static byte[] ParamChanged(byte seq, ushort offset, byte value) =>
        new byte[] { 0x02, 0x02, 0, seq, (byte)offset, (byte)(offset >> 8), 1, 0, 1, 0, 0, 0, value };

    [Fact]
    public void ChangesDuringARead_AreReplayedOntoIt()
    {
        var cache = new SnapshotCache();
        var token = cache.BeginRead();
        cache.ApplyParamChange(ParamChanged(1, 20, 0x7A));
        var blob = new byte[64];
        Assert.True(cache.SetBulk(blob, token));
        Assert.Equal(0x7A, cache.Snapshot()!.Bulk[20]);
    }

    [Fact]
    public void AnInvalidationDuringAReadThrowsItAway()
    {
        var cache = new SnapshotCache();
        var token = cache.BeginRead();
        cache.Invalidate();
        Assert.False(cache.SetBulk(new byte[64], token));
        Assert.Null(cache.Snapshot());
    }

    [Fact]
    public void AFirmwareSeqGapDropsTheSnapshot()
    {
        var blob = new byte[64];
        _device.Answer = r => r.BRequest == 0xA0 ? new LinkCmdResponse(r.Tag, LinkStatus.Ok, blob) : new LinkCmdResponse(r.Tag, LinkStatus.Ok);
        var (_, _) = Open();
        _hub.AttachDevice(_device);
        Assert.True(SpinWait.SpinUntil(() => _hub.CurrentSnapshot() != null, 3000));
        int reads = _device.Executed.Count(r => r.BRequest == 0xA0);
        _hub.Ingest(new LinkNotification(ParamChanged(1, 4, 1), 0, DateTime.Now));
        _hub.Ingest(new LinkNotification(ParamChanged(5, 4, 2), 0, DateTime.Now));   // 2-4 lost
        Assert.True(SpinWait.SpinUntil(() => _device.Executed.Count(r => r.BRequest == 0xA0) > reads, 3000));
    }

    // ── Polls ──

    [Fact]
    public void TheBulkBlobCantBePolled()
    {
        _hub.AttachDevice(_device);
        var (h, output) = Open();
        h.ReceiveText(new LinkPollSubscribe(3, 0, new[] { new LinkPollSpec(0, 0xA0, 0, 2, 8192, 10) }).Encode());
        LinkOk? ok = null;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (ok == null && DateTime.UtcNow < deadline)
            if (output.TryTake(out var o, 50) && o is LinkOutbound.Text t && LinkMessage.Decode(t.Json) is LinkOk k) ok = k;
        Assert.Equal(0, Assert.Single(ok!.DecodeBody<LinkPollSubscribeBody>().Granted).Hz);
    }

    // ── Router ──

    [Fact]
    public void ACallQueuedBehindAWedgedOneIsntRunLate()
    {
        var device = new FakeHubDevice { Gate = new ManualResetEventSlim(false) };
        using var router = new CommandRouter(0, device, LinkPolicy.Embedded!) { CommandTimeout = TimeSpan.FromMilliseconds(100) };
        var session = new RouterSession(2, LinkRole.Admin);
        var first = new TaskCompletionSource<RoutedResult>();
        var second = new TaskCompletionSource<RoutedResult>();
        router.Submit(new LinkCmdRequest(1, 0, LinkDirection.Get, 0x7F, 0, 2, 7), session, r => first.TrySetResult(r));
        router.Submit(new LinkCmdRequest(2, 0, LinkDirection.Set, 0xD2, 0, 2, payload: new byte[] { 1, 2, 3, 4 }), session, r => second.TrySetResult(r));
        Assert.Equal(LinkStatus.Timeout, first.Task.Result.Response.Status);
        Assert.Equal(LinkStatus.Timeout, second.Task.Result.Response.Status);
        device.Gate.Set();
        Thread.Sleep(200);
        Assert.DoesNotContain(device.Executed, r => r.BRequest == 0xD2);
    }
}
