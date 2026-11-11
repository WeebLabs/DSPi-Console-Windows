using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DSPiConsole.Core.Link;
using DSPiConsole.Core.Link.Hub;

namespace DSPiConsole.Core.Tests.Link.Hub;

/// <summary>The hub and its per-connection protocol handler together, over a
/// fake device: what a client sees for each message (spec 5-8).</summary>
public sealed class LinkSessionHandlerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dspi-hub-" + Guid.NewGuid().ToString("N"));
    private readonly LinkAuthStore _auth;
    private readonly LinkHub _hub;
    private readonly FakeHubDevice _device = new();

    public LinkSessionHandlerTests()
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

    /// <summary>One connection: everything it emitted, in order.</summary>
    private sealed class Conn
    {
        public readonly BlockingCollection<LinkOutbound> Out = new();
        public LinkSessionHandler Handler = null!;

        public LinkOutbound Next(int ms = 3000)
        {
            Assert.True(Out.TryTake(out var o, ms), "nothing was emitted");
            return o!;
        }

        public LinkMessage NextMessage(int ms = 3000)
        {
            while (true)
            {
                if (Next(ms) is LinkOutbound.Text t) return LinkMessage.Decode(t.Json);
            }
        }

        public T NextMessage<T>(int ms = 3000) where T : LinkMessage
        {
            while (true)
            {
                var m = NextMessage(ms);
                if (m is T typed) return typed;
            }
        }

        public T NextFrame<T>(int ms = 3000) where T : LinkFrame
        {
            while (true)
            {
                if (Next(ms) is LinkOutbound.Binary b && LinkFrame.Decode(b.Data) is T f) return f;
            }
        }

        public ushort Close(int ms = 3000)
        {
            while (true)
            {
                if (Next(ms) is LinkOutbound.Close c) return c.Code;
            }
        }

        public void Send(LinkMessage m) => Handler.ReceiveText(m.Encode());
        public void Send(LinkFrame f) => Handler.ReceiveBinary(f.Encode());
    }

    private Conn Connect(string peer = "192.168.1.50")
    {
        var c = new Conn();
        c.Handler = new LinkSessionHandler(_hub, peer, o => c.Out.Add(o));
        return c;
    }

    private Conn Hello(string peer = "192.168.1.50")
    {
        var c = Connect(peer);
        c.Send(new LinkHelloClient(new LinkClientInfo("Test")));
        Assert.IsType<LinkHelloHub>(c.NextMessage());
        return c;
    }

    private static LinkCmdRequest Volume(ushort tag, byte handle = 0) =>
        new(tag, handle, LinkDirection.Set, 0xD2, 0, 2, payload: new byte[] { 0, 0, 0xA0, 0xC1 });

    // ── hello and auth ──

    [Fact]
    public void HelloSaysWhatTheHubIsAndOpenModeOpensAnAdminSession()
    {
        var c = Connect();
        c.Send(new LinkHelloClient(new LinkClientInfo("Test")));
        var hello = Assert.IsType<LinkHelloHub>(c.NextMessage());
        Assert.Equal(LinkAuthMode.None, hello.Auth);
        Assert.Equal(_auth.HubId.ToString().ToLowerInvariant(), hello.Hub.Id);
        Assert.Equal(LinkHubKind.Console, hello.Hub.Kind);
        Assert.Null(hello.PinSourceWire);
        Assert.Contains(LinkCapability.Cmd, hello.Caps);
        Assert.Contains(LinkCapability.Lock, hello.Caps);
        Assert.Equal(8192, hello.Limits!.MaxPayload);
        Assert.Equal(2, hello.Version.Minor);
        Assert.True(c.Handler.IsAuthenticated);
    }

    [Fact]
    public void TheCourtesyPairInOpenModeAnswersTheSessionId()
    {
        var c = Hello();
        c.Send(new LinkAuthPair(1, "000000", "Test"));
        var ok = Assert.IsType<LinkOk>(c.NextMessage());
        var body = ok.DecodeBody<LinkAuthOkBody>();
        Assert.Equal(LinkRole.Admin, body.Role);
        Assert.True(body.Session >= 2);
    }

    [Fact]
    public void PinModeWantsAuthFirst()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var c = Connect();
        c.Send(new LinkHelloClient(new LinkClientInfo("Test")));
        var hello = Assert.IsType<LinkHelloHub>(c.NextMessage());
        Assert.Equal(LinkAuthMode.Pin, hello.Auth);
        Assert.Equal(LinkPinSource.Window, hello.PinSourceWire);
        Assert.False(c.Handler.IsAuthenticated);
        c.Send(new LinkDeviceListRequest(5));
        var err = Assert.IsType<LinkErr>(c.NextMessage());
        Assert.Equal(LinkErrorCode.Unauthenticated, err.Code);
        Assert.Equal(5, err.Id);
        // Binary before auth is dropped.
        c.Send(Volume(1));
        Assert.False(c.Out.TryTake(out _, 100));
    }

    [Fact]
    public void PairingGivesATokenThatWorksNextTime()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Studio PC", LinkRole.Control));
        var body = Assert.IsType<LinkOk>(c.NextMessage()).DecodeBody<LinkAuthOkBody>();
        Assert.Equal(LinkRole.Control, body.Role);
        Assert.NotNull(body.Token);
        Assert.NotNull(body.Policy);

        var again = Hello("192.168.1.51");
        again.Send(new LinkAuthToken(2, body.Token!));
        var second = Assert.IsType<LinkOk>(again.NextMessage()).DecodeBody<LinkAuthOkBody>();
        Assert.Equal(LinkRole.Control, second.Role);
        Assert.NotEqual(body.Session, second.Session);
    }

    [Fact]
    public void ATokenUnderOpenAccessGivesAnAdminSession()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Phone", LinkRole.Viewer));
        var token = c.NextMessage<LinkOk>().DecodeBody<LinkAuthOkBody>().Token!;

        _auth.AuthMode = LinkAuthMode.None;
        var again = Hello("192.168.1.51");
        again.Send(new LinkAuthToken(2, token));
        Assert.Equal(LinkRole.Admin, again.NextMessage<LinkOk>().DecodeBody<LinkAuthOkBody>().Role);
    }

    [Fact]
    public void AWrongTokenIsUnauthenticatedAndTheLimitCloses4001()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var c = Hello();
        for (int i = 0; i < LinkAuthStore.FailureLimit; i++)
        {
            c.Send(new LinkAuthToken(i, "bm90LWEtdG9rZW4"));
            var err = Assert.IsType<LinkErr>(c.NextMessage());
            Assert.Contains(err.Code, new[] { LinkErrorCode.Unauthenticated, LinkErrorCode.RateLimited });
        }
        Assert.Equal(LinkCloseCode.AuthFailed, c.Close());
    }

    [Fact]
    public void RevokingAClientClosesItsConnection4002()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Studio PC"));
        c.NextMessage<LinkOk>();
        var cid = _auth.Clients.Single().Cid;
        _auth.Revoke(cid);
        Assert.Equal(LinkCloseCode.TokenRevoked, c.Close());
        Assert.False(c.Handler.IsAuthenticated);
    }

    [Fact]
    public void ARoleChangeAppliesToTheNextCommand()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Studio PC"));
        c.NextMessage<LinkOk>();
        _auth.SetRole(_auth.Clients.Single().Cid, LinkRole.Viewer);
        SpinWait.SpinUntil(() => false, 50);   // the role event is queued
        c.Send(Volume(9));
        var r = c.NextFrame<LinkCmdResponse>();
        Assert.Equal(LinkStatus.Denied, r.Status);
    }

    // ── Commands ──

    [Fact]
    public void ACommandRunsAndAnswersUnderItsTag()
    {
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(Volume(42));
        var r = c.NextFrame<LinkCmdResponse>();
        Assert.Equal(42, r.Tag);
        Assert.Equal(LinkStatus.Ok, r.Status);
        Assert.Contains(_device.Executed, e => e.BRequest == 0xD2);
    }

    [Fact]
    public void NoDeviceAndAWrongHandleAnswerNoDevice()
    {
        var c = Hello();
        c.Send(Volume(1));
        Assert.Equal(LinkStatus.NoDevice, c.NextFrame<LinkCmdResponse>().Status);
        _hub.AttachDevice(_device);
        c.Send(Volume(2, handle: 7));
        Assert.Equal(LinkStatus.NoDevice, c.NextFrame<LinkCmdResponse>().Status);
    }

    [Fact]
    public void AnOversizedCommandIsTooLarge()
    {
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkCmdRequest(3, 0, LinkDirection.Get, 0x7F, 0, 2, 9000));
        Assert.Equal(LinkStatus.TooLarge, c.NextFrame<LinkCmdResponse>().Status);
    }

    [Fact]
    public void AnUnknownRequestTypeIsUnsupportedUnderItsTag()
    {
        var c = Hello();
        c.Handler.ReceiveBinary(new byte[] { 0x10, 0, 0x34, 0x12, 0, 0 });
        var r = c.NextFrame<LinkCmdResponse>();
        Assert.Equal(0x1234, r.Tag);
        Assert.Equal(LinkStatus.Unsupported, r.Status);
    }

    [Fact]
    public void FirmwareDataIsUnsupported()
    {
        var c = Hello();
        c.Send(new LinkFwDataFrame(5, 0, new byte[] { 1, 2, 3 }));
        var ack = c.NextFrame<LinkFwDataAck>();
        Assert.Equal(LinkStatus.Unsupported, ack.Status);
    }

    // ── Control-plane errors ──

    [Fact]
    public void MalformedJsonCloses4000()
    {
        var c = Connect();
        c.Handler.ReceiveText("{not json");
        Assert.Equal(LinkCloseCode.ProtocolError, c.Close());
    }

    [Fact]
    public void ABadFieldIsBadRequestUnderItsId()
    {
        var c = Hello();
        c.Handler.ReceiveText("{\"t\":\"device.rename\",\"id\":8,\"handle\":\"zero\",\"name\":\"x\"}");
        var err = Assert.IsType<LinkErr>(c.NextMessage());
        Assert.Equal(LinkErrorCode.BadRequest, err.Code);
        Assert.Equal(8, err.Id);
    }

    [Fact]
    public void AnUnknownTypeAndTheUnofferedAdminMessagesAreUnknownType()
    {
        var c = Hello();
        c.Handler.ReceiveText("{\"t\":\"device.frobnicate\",\"id\":3}");
        Assert.Equal(LinkErrorCode.UnknownType, Assert.IsType<LinkErr>(c.NextMessage()).Code);
        c.Send(new LinkHubConfigGet(4));
        Assert.Equal(LinkErrorCode.UnknownType, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    // ── Inventory, snapshot ──

    [Fact]
    public void TheDeviceListAndItsEvents()
    {
        var c = Hello();
        _hub.AttachDevice(_device);
        var added = c.NextMessage<LinkDeviceAdded>();
        Assert.Equal(_device.Info.Serial, added.Device.Serial);
        c.Send(new LinkDeviceListRequest(2));
        var list = c.NextMessage<LinkOk>().DecodeBody<LinkDeviceListBody>();
        var d = Assert.Single(list.Devices);
        Assert.Equal(0, d.Handle);
        Assert.Equal(LinkDeviceState.Online, d.State);
        Assert.Equal("1.1.6", d.Fw);
        _hub.DetachDevice();
        var changed = c.NextMessage<LinkDeviceChanged>();
        Assert.Equal(LinkDeviceState.Offline, changed.Device.State);
        Assert.Equal(LinkResyncReason.DeviceReattached, (LinkResyncReason)c.NextFrame<LinkResyncFrame>().Reason);
    }

    [Fact]
    public void TheSnapshotIsServedFromTheCache()
    {
        var blob = new byte[64];
        blob[0] = 0x44; blob[4] = 32; blob[6] = 64;   // a header: wire version 32, length 64
        _device.Answer = r => r.BRequest == 0xA0 ? new LinkCmdResponse(r.Tag, LinkStatus.Ok, blob) : new LinkCmdResponse(r.Tag, LinkStatus.Ok);
        // Warmed once there's a client to read it.
        var c = Hello();
        _hub.AttachDevice(_device);
        Assert.True(SpinWait.SpinUntil(() => _hub.CurrentSnapshot() != null, 3000));
        c.Send(new LinkDeviceSnapshotRequest(6, 0));
        var body = c.NextMessage<LinkOk>().DecodeBody<LinkSnapshotBody>();
        Assert.Equal(blob, body.Bulk);
    }

    [Fact]
    public void NoClientNoSnapshotRead()
    {
        _hub.AttachDevice(_device);
        Thread.Sleep(100);
        Assert.DoesNotContain(_device.Executed, r => r.BRequest == 0xA0);
    }

    [Fact]
    public void NoSnapshotWithoutADevice()
    {
        var c = Hello();
        c.Send(new LinkDeviceSnapshotRequest(6, 0));
        Assert.Equal(LinkErrorCode.NoDevice, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    // ── Notifications ──

    private static byte[] ParamChanged(byte source) =>
        new byte[] { 0x02, 0x02, 0, 1, 0x10, 0x00, 0x04, 0x00, source, 0, 0, 0, 0, 0, 0, 0 };

    [Fact]
    public void ARemoteWritesNotifyIsAttributedToItAndRelayedToOthers()
    {
        _hub.AttachDevice(_device);
        var a = Hello();
        a.Send(new LinkAuthPair(1, "0", "A"));
        var sa = a.NextMessage<LinkOk>().DecodeBody<LinkAuthOkBody>().Session;
        var b = Hello("192.168.1.60");
        b.Send(new LinkAuthPair(1, "0", "B"));
        b.NextMessage<LinkOk>();

        a.Send(Volume(1));
        a.NextFrame<LinkCmdResponse>();
        ushort localOrigin = _hub.Ingest(new LinkNotification(ParamChanged(1), 1, DateTime.Now));
        Assert.Equal(sa, localOrigin);
        var na = b.NextFrame<LinkNotifyFrame>();
        Assert.Equal(sa, na.Origin);
    }

    [Fact]
    public void AKnobTurnIsUnattributed()
    {
        _hub.AttachDevice(_device);
        var a = Hello();
        a.Send(Volume(1));
        a.NextFrame<LinkCmdResponse>();
        Assert.Equal(0, _hub.Ingest(new LinkNotification(ParamChanged(0), 0, DateTime.Now)));
    }

    [Fact]
    public void TheLocalUisWritesAreAttributedToSessionOne()
    {
        _hub.AttachDevice(_device);
        var done = new TaskCompletionSource<LinkCmdResponse>();
        _hub.Submit(Volume(0), LinkSessionIds.Local, r => done.SetResult(r));
        Assert.Equal(LinkStatus.Ok, done.Task.Result.Status);
        Assert.Equal(LinkSessionIds.Local, _hub.Ingest(new LinkNotification(ParamChanged(1), 1, DateTime.Now)));
    }

    // ── Locks (spec 7.7) ──

    [Fact]
    public void ALockIsCappedAndKeepsOthersOut()
    {
        _hub.AttachDevice(_device);
        var a = Hello();
        var b = Hello("192.168.1.60");
        a.Send(new LinkLockAcquire(3, 0, "Applying configuration", 120000));
        var ok = a.NextMessage<LinkOk>().DecodeBody<LinkLockOkBody>();
        Assert.Equal(60000, ok.TimeoutMs);
        b.Send(new LinkLockAcquire(4, 0, "x", 1000));
        Assert.Equal(LinkErrorCode.Locked, b.NextMessage<LinkErr>().Code);
        b.Send(Volume(5));
        Assert.Equal(LinkStatus.Locked, b.NextFrame<LinkCmdResponse>().Status);
        Assert.NotNull(_hub.Registry.Devices.Single().LockedBy);

        // Closing the holder's connection releases it.
        a.Handler.ConnectionClosed();
        b.Send(Volume(6));
        Assert.Equal(LinkStatus.Ok, b.NextFrame<LinkCmdResponse>().Status);
    }

    [Fact]
    public void ANegativeLockTimeoutIsABadRequest()
    {
        _hub.AttachDevice(_device);
        var a = Hello();
        a.Send(new LinkLockAcquire(3, 0, null, -1));
        Assert.Equal(LinkErrorCode.BadRequest, Assert.IsType<LinkErr>(a.NextMessage()).Code);
    }

    [Fact]
    public void AViewerCantLock()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Phone", LinkRole.Viewer));
        Assert.Equal(LinkRole.Viewer, c.NextMessage<LinkOk>().DecodeBody<LinkAuthOkBody>().Role);
        c.Send(new LinkLockAcquire(2, 0));
        Assert.Equal(LinkErrorCode.Denied, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    // ── Rename (spec 7.4) ──

    [Fact]
    public void RenamingANameCapableBoardWritesIt()
    {
        _device.Info = _device.Info with { BoardName = "" };
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkDeviceRename(7, 0, "Living room"));
        c.NextMessage<LinkOk>();
        var write = _device.Executed.Last(r => r.BRequest == 0x56);
        Assert.Equal("Living room", Models.DeviceNameText.Decode(write.Payload));
    }

    [Fact]
    public void RenamingAnOlderBoardKeepsAHubName()
    {
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkDeviceRename(7, 0, "Kitchen"));
        c.NextMessage<LinkOk>();
        Assert.Equal("Kitchen", _hub.Registry.Devices.Single().Name);
        Assert.DoesNotContain(_device.Executed, r => r.BRequest == 0x56);
    }

    [Fact]
    public void RenamingAnUnknownHandleIsNoDevice()
    {
        var c = Hello();
        c.Send(new LinkDeviceRename(7, 3, "x"));
        Assert.Equal(LinkErrorCode.NoDevice, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    // ── Admin ──

    [Fact]
    public void AdminMessagesNeedAdmin()
    {
        _auth.AuthMode = LinkAuthMode.Pin;
        var pin = _auth.BeginPairing();
        var c = Hello();
        c.Send(new LinkAuthPair(1, pin, "Phone", LinkRole.Control));
        c.NextMessage<LinkOk>();
        c.Send(new LinkAuthList(2));
        Assert.Equal(LinkErrorCode.Denied, Assert.IsType<LinkErr>(c.NextMessage()).Code);
        c.Send(new LinkHubRename(3, "New name"));
        Assert.Equal(LinkErrorCode.Denied, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    [Fact]
    public void AnAdminCanListClientsAndRenameTheHub()
    {
        var c = Hello();   // open mode: admin
        c.Send(new LinkAuthList(2));
        c.NextMessage<LinkOk>().DecodeBody<LinkAuthListBody>();
        c.Send(new LinkHubRename(3, "Studio PC"));
        c.NextMessage<LinkOk>();
        Assert.Equal("Studio PC", _auth.HubName);
    }

    // ── Polls (spec 7.6) ──

    [Fact]
    public void APollIsGrantedAndDelivered()
    {
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkPollSubscribe(4, 0, new[] { new LinkPollSpec(0, 0x7F, 0, 2, 7, 10) }));
        var granted = c.NextMessage<LinkOk>().DecodeBody<LinkPollSubscribeBody>();
        Assert.Equal(10, Assert.Single(granted.Granted).Hz);
        var poll = c.NextFrame<LinkPollFrame>();
        Assert.Equal(0, poll.Slot);
    }

    [Fact]
    public void ABadSlotIsABadRequest()
    {
        _hub.AttachDevice(_device);
        var c = Hello();
        c.Send(new LinkPollSubscribe(4, 0, new[] { new LinkPollSpec(16, 0x7F, 0, 2, 7, 10) }));
        Assert.Equal(LinkErrorCode.BadRequest, Assert.IsType<LinkErr>(c.NextMessage()).Code);
    }

    // ── Sessions ──

    [Fact]
    public void SessionIdsSkipTheReservedOnesWhenTheyWrap()
    {
        _hub.NextSessionId = 0xFFFE;
        var a = _hub.OpenSession(LinkRole.Admin);
        var b = _hub.OpenSession(LinkRole.Admin);
        Assert.Equal(0xFFFE, a.Id);
        Assert.Equal(2, b.Id);   // 0xFFFF, 0 and 1 skipped
    }

    [Fact]
    public void ASecondAuthReplacesTheFirstSession()
    {
        var c = Hello();
        c.Send(new LinkAuthPair(1, "0", "A"));
        c.NextMessage<LinkOk>();
        int before = _hub.SessionCount;
        c.Send(new LinkAuthPair(2, "0", "A"));
        c.NextMessage<LinkOk>();
        Assert.Equal(before, _hub.SessionCount);
        c.Handler.ConnectionClosed();
        Assert.Equal(1, _hub.SessionCount);   // just the local UI
    }
}
