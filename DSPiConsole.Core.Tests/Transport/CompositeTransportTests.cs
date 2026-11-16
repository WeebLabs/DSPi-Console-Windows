using DSPiConsole.Core.Link;
using DSPiConsole.Core.Tests.Link;
using DSPiConsole.Usb;
using DSPiConsole.Usb.Transport;
using static DSPiConsole.Core.Tests.Transport.DspDeviceTransportTests;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>Local boards and other hubs' devices as one list behind one
/// facade (<see cref="CompositeTransport"/>). Ported in spirit from the
/// macOS Console's CompositeTransportTests.</summary>
public class CompositeTransportTests : IDisposable
{
    private const string HubId = "6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b";
    private const string RemoteSerial = "0123456789ABCDEF";
    private static readonly DSPiDeviceInfo LocalBoard = new("E46058388B1A2E2C", "vid_2E8B&pid_FEAA#E46058388B1A2E2C");
    private readonly string _tokenPath = Path.Combine(Path.GetTempPath(), $"dspi-ct-{Guid.NewGuid():N}.dat");

    public void Dispose()
    {
        try { File.Delete(_tokenPath); } catch { }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeTransport Local = new();
        public readonly FakeLinkHub Hub = new();
        public readonly LinkTokenStore Tokens;
        public readonly CompositeTransport Composite;
        public readonly DspDevice Device;
        public readonly List<string> Log = new();

        public Rig(string tokenPath, TimeSpan? prune = null)
        {
            Tokens = new LinkTokenStore(tokenPath);
            Composite = new CompositeTransport(Local, Tokens, (id, hub) =>
                new NetworkTransport(new LinkClient(Hub.OpenAsync, "1.1.6"), id, hub.Name, Tokens, "PC")
                { BackgroundWait = TimeSpan.FromSeconds(3) })
            { PruneGrace = prune ?? TimeSpan.FromSeconds(10) };
            Device = new DspDevice(Composite);
            Device.DeviceConnected += (_, _) => { lock (Log) Log.Add("connected:" + Device.SelectedDeviceInfo?.Serial); };
            Device.DeviceDisconnected += (_, _) => { lock (Log) Log.Add("disconnected"); };
        }

        public string[] Events() { lock (Log) return Log.ToArray(); }

        public async ValueTask DisposeAsync()
        {
            Device.Dispose();
            await Hub.DisposeAsync();
        }
    }

    private static LinkHubRecord Hub(LinkAuthMode auth = LinkAuthMode.None, params string[] serials) =>
        new(HubId, "Studio Mac", LinkHubKind.Console, auth, serials.Length, serials.Length > 0 ? serials : [RemoteSerial],
            new LinkEndpoint("studio.local"));

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting.");
            await Task.Delay(10);
        }
    }

    private static async Task AcceptOpen(FakeLinkHub hub, ushort session = 12) =>
        await hub.AcceptOpenAsync(session, FakeLinkHub.Board(0, RemoteSerial));

    [Fact]
    public async Task TheListMergesBoardsHereWithTheHubsAdvertisedDevices()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);

        var list = rig.Composite.AvailableDevices;
        Assert.Equal(2, list.Count);
        Assert.Equal(DeviceLinkKind.Usb, list[0].Kind);
        var remote = list[1];
        Assert.Equal(RemoteSerial, remote.Serial);
        Assert.Equal(DeviceLinkKind.Remote, remote.Kind);
        Assert.Equal(HubId, remote.HubId);
        Assert.Equal(0, rig.Hub.Connections); // nothing dialled until it's chosen
    }

    /// <summary>Switching boards looks to the facade like a disconnect then a
    /// connect, the order it has always handled.</summary>
    [Fact]
    public async Task SwitchingToARemoteDeviceAndBack()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);
        Assert.Equal(["connected:E46058388B1A2E2C"], rig.Events());

        var remote = rig.Composite.AvailableDevices.Single(d => d.Kind == DeviceLinkKind.Remote);
        rig.Device.SelectDevice(remote);
        Assert.True(rig.Composite.IsRemoteActive);
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected && rig.Events().Length == 3);
        Assert.Equal(["connected:E46058388B1A2E2C", "disconnected", "connected:" + RemoteSerial], rig.Events());
        Assert.Equal(12, rig.Device.Session);

        // The local board's notifications no longer reach the facade.
        int seen = 0;
        rig.Device.NotifyPacketReceived += (_, _) => Interlocked.Increment(ref seen);
        rig.Local.Notify([2, 3, 0, 0, 0], 1);
        Assert.Equal(0, seen);

        rig.Device.SelectDevice(LocalBoard);
        Assert.False(rig.Composite.IsRemoteActive);
        Assert.Equal(["connected:E46058388B1A2E2C", "disconnected", "connected:" + RemoteSerial, "disconnected", "connected:E46058388B1A2E2C"], rig.Events());
        Assert.Equal(1, rig.Device.Session);
    }

    [Fact]
    public async Task CommandsGoToTheActiveTransport()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);
        rig.Device.SelectDevice(rig.Composite.AvailableDevices.Single(d => d.Kind == DeviceLinkKind.Remote));
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);

        var name = Task.Run(() => rig.Device.GetChannelNameFromDevice(0));
        var req = await rig.Hub.ExpectAsync<LinkCmdRequest>();
        Assert.Equal(VendorCommands.GetChannelName, req.BRequest);
        await rig.Hub.SendAsync(new LinkCmdResponse(req.Tag, LinkStatus.Ok, "Remote L"u8.ToArray()));
        Assert.Equal("Remote L", await name);
        Assert.Empty(rig.Local.Ins);
    }

    /// <summary>With no board here, a lone device on an open hub is picked on
    /// its own; a board that appears later takes over.</summary>
    [Fact]
    public async Task ALoneRemoteIsPickedAndABoardHereTakesOver()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Composite.UpdateHubs([Hub()]);
        Assert.True(rig.Composite.IsRemoteActive);
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);

        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        Assert.False(rig.Composite.IsRemoteActive);
        await Until(() => rig.Device.SelectedDeviceInfo?.Serial == LocalBoard.Serial);
    }

    [Fact]
    public async Task APinHubWithNoTokenIsNotPickedOnItsOwn()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Composite.UpdateHubs([Hub(LinkAuthMode.Pin)]);
        Assert.False(rig.Composite.IsRemoteActive);
        Assert.Equal(0, rig.Hub.Connections);

        rig.Tokens.SetToken(HubId, "tok"); // paired before: now it may be
        rig.Composite.UpdateHubs([Hub(LinkAuthMode.Pin)]);
        Assert.True(rig.Composite.IsRemoteActive);
    }

    /// <summary>The Pair button: a PIN hub whose announcement lists nothing is
    /// paired without choosing a device, and its devices then come from the
    /// session. The device being driven doesn't change.</summary>
    [Fact]
    public async Task OpeningAPinHubPairsItAndListsItsDevices()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        var asked = 0;
        rig.Composite.PairingPrompt = _ => { Interlocked.Increment(ref asked); return Task.FromResult<string?>("482913"); };
        var empty = new LinkHubRecord(HubId, "DSPi Bridge 3F2A", LinkHubKind.Bridge, LinkAuthMode.Pin, 0, [], new LinkEndpoint("dspi-bridge-3f2a.local"));
        rig.Composite.UpdateHubs([empty]);
        Assert.DoesNotContain(rig.Composite.AvailableDevices, d => d.Kind == DeviceLinkKind.Remote);

        rig.Composite.OpenHub(empty);
        await rig.Hub.ExpectAsync<LinkHelloClient>();
        await rig.Hub.SendAsync(FakeLinkHub.Hello(LinkAuthMode.Pin, LinkPinSource.Fixed));
        var pair = await rig.Hub.ExpectAsync<LinkAuthPair>();
        Assert.Equal("482913", pair.Pin);
        await rig.Hub.SendAsync(LinkOk.WithBody(pair.Id, new LinkAuthOkBody(7, LinkRole.Admin, "tok")));
        var list = await rig.Hub.ExpectAsync<LinkDeviceListRequest>();
        await rig.Hub.SendAsync(LinkOk.WithBody(list.Id, new LinkDeviceListBody([FakeLinkHub.Board(0, RemoteSerial)])));

        await Until(() => rig.Composite.AvailableDevices.Any(d => d.Serial == RemoteSerial && d.HubId == HubId));
        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.Equal("tok", rig.Tokens.Token(HubId));
        Assert.False(rig.Composite.IsRemoteActive);
        Assert.Equal(LocalBoard.Serial, rig.Composite.SelectedDevice?.Serial);
    }

    /// <summary>One board on two hubs (a Mac's USB and a bridge's UART) is
    /// listed once; both routes are offered, and the one chosen is the one
    /// listed and driven.</summary>
    [Fact]
    public async Task OneBoardOnTwoHubsOffersBothRoutes()
    {
        const string BridgeId = "de9d702d-057a-4350-9457-46e85edd4088";
        await using var rig = new Rig(_tokenPath);
        var mac = Hub(LinkAuthMode.Pin, RemoteSerial);
        var bridge = new LinkHubRecord(BridgeId, "DSPi Bridge 1B34", LinkHubKind.Bridge, LinkAuthMode.Pin, 1, [RemoteSerial], new LinkEndpoint("dspi-bridge-1b34.local"));
        rig.Composite.UpdateHubs([mac, bridge]);

        var listed = Assert.Single(rig.Composite.AvailableDevices);
        Assert.Equal(HubId, listed.HubId);
        var routes = rig.Composite.RoutesFor(RemoteSerial);
        Assert.Equal([HubId, BridgeId], routes.Select(r => r.HubId));
        Assert.Equal("DSPi Bridge 1B34", routes[1].HubName);

        rig.Composite.SelectDevice(routes[1]);
        Assert.True(rig.Composite.IsRemoteActive);
        Assert.Equal(BridgeId, rig.Composite.SelectedDevice?.HubId);
        await Until(() => rig.Composite.AvailableDevices.Single().HubId == BridgeId);
        Assert.Equal(2, rig.Composite.RoutesFor(RemoteSerial).Count);
    }

    [Fact]
    public async Task ABoardPluggedInHereHasNoOtherRoutes()
    {
        await using var rig = new Rig(_tokenPath);
        var board = new DSPiDeviceInfo(RemoteSerial, "vid_2E8B&pid_FEAA#" + RemoteSerial);
        rig.Local.List(board);
        rig.Composite.UpdateHubs([Hub(LinkAuthMode.Pin, RemoteSerial)]);
        Assert.Equal(DeviceLinkKind.Usb, Assert.Single(rig.Composite.AvailableDevices).Kind);
        Assert.Empty(rig.Composite.RoutesFor(RemoteSerial));
        // One hub alone is no choice either.
        Assert.Empty(rig.Composite.RoutesFor("FFFFFFFFFFFFFFFF"));
    }

    /// <summary>A user's remote choice stays when a board appears.</summary>
    [Fact]
    public async Task AChosenRemoteStaysWhenABoardAppears()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);
        rig.Device.SelectDevice(rig.Composite.AvailableDevices.Single(d => d.Kind == DeviceLinkKind.Remote));
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);
        rig.Local.Drop();
        rig.Local.Connect(LocalBoard); // a replug
        Assert.True(rig.Composite.IsRemoteActive);
    }

    /// <summary>A hub gone from the list with its session down is dropped after
    /// the grace, and the view model goes back to the board here.</summary>
    [Fact]
    public async Task AVanishedHubIsPrunedAfterTheGrace()
    {
        await using var rig = new Rig(_tokenPath, prune: TimeSpan.FromMilliseconds(300));
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);
        rig.Device.SelectDevice(rig.Composite.AvailableDevices.Single(d => d.Kind == DeviceLinkKind.Remote));
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);

        rig.Composite.UpdateHubs([]);
        Assert.True(rig.Composite.IsRemoteActive); // a session that is up outlasts a missed announcement
        await rig.Hub.CloseAsync(LinkCloseCode.ShuttingDown);
        await Until(() => !rig.Composite.IsRemoteActive, 3000);
        await Until(() => rig.Device.SelectedDeviceInfo?.Serial == LocalBoard.Serial);
        Assert.DoesNotContain(rig.Composite.AvailableDevices, d => d.Kind == DeviceLinkKind.Remote);
    }

    [Fact]
    public async Task TheActiveDeviceStaysListedWhileItsHubIsUnreachable()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Composite.UpdateHubs([Hub()]);
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);
        rig.Composite.UpdateHubs([]);
        Assert.Contains(rig.Composite.AvailableDevices, d => d.Serial == RemoteSerial);
    }

    [Fact]
    public async Task ALockIsReleasedWhereItWasGranted()
    {
        await using var rig = new Rig(_tokenPath);
        rig.Local.List(LocalBoard);
        rig.Local.Connect(LocalBoard);
        rig.Composite.UpdateHubs([Hub()]);
        rig.Device.SelectDevice(rig.Composite.AvailableDevices.Single(d => d.Kind == DeviceLinkKind.Remote));
        await AcceptOpen(rig.Hub);
        await Until(() => rig.Device.IsConnected);

        var acquiring = Task.Run(() => rig.Composite.AcquireDeviceLock("Saving preset", TimeSpan.FromSeconds(5)));
        var req = await rig.Hub.ExpectAsync<LinkLockAcquire>();
        await rig.Hub.SendAsync(new LinkOk(req.Id));
        Assert.True(await acquiring);

        rig.Device.SelectDevice(LocalBoard); // switched mid-flow
        rig.Composite.ReleaseDeviceLock();
        Assert.Equal(req.Handle, (await rig.Hub.ExpectAsync<LinkLockRelease>()).Handle);
    }
}
