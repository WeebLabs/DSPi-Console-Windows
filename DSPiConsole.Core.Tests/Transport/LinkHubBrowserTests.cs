using System.Net;
using DSPiConsole.Core.Link;
using DSPiConsole.Usb.Transport;

namespace DSPiConsole.Core.Tests.Transport;

/// <summary>Hub discovery (spec 3) over a fake DNS-SD, and typed addresses
/// over a fake info probe.</summary>
public class LinkHubBrowserTests
{
    private const string MacHid = "6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b";
    private const string OwnHid = "0d6c41e2-93b7-4f0a-b5a8-3c2e7f19a4d0";

    private sealed class FakeDns : IDnsSdBrowser
    {
        public readonly Dictionary<string, DnsSdService> Services = new();
        public bool Started;
        public int Resolves;
        public event EventHandler<string>? Found;
        public event EventHandler<string>? Lost;
        public void Start(string serviceType) { Assert.Equal("_dspi._tcp", serviceType); Started = true; }
        public void Stop() => Started = false;
        /// <summary>When set, resolves wait for it (then answer as usual).</summary>
        public TaskCompletionSource? Gate;
        public async Task<DnsSdService?> ResolveAsync(string instance, TimeSpan timeout)
        {
            Interlocked.Increment(ref Resolves);
            if (Gate != null) await Gate.Task;
            lock (Services) return Services.GetValueOrDefault(instance);
        }
        public void Announce(string instance) => Found?.Invoke(this, instance);
        public void Goodbye(string instance) => Lost?.Invoke(this, instance);
        public void Dispose() { }
    }

    private static DnsSdService Service(string instance, string hid, string auth = "none", string? d = null, params IPAddress[] addresses)
    {
        var txt = new Dictionary<string, string> { ["v"] = "1", ["hid"] = hid, ["kind"] = "console", ["auth"] = auth, ["n"] = "1" };
        if (d != null) txt["d"] = d;
        return new DnsSdService(instance, "Studio-Mac.local.", 11915, txt, addresses, 12);
    }

    private static async Task Until(Func<bool> condition, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AnAnnouncedHubIsListedByItsHostName()
    {
        var dns = new FakeDns();
        dns.Services["Studio Mac._dspi._tcp.local"] = Service("Studio Mac._dspi._tcp.local", MacHid, "pin", "E46058388B1A2E2C");
        using var browser = new LinkHubBrowser(dns) { Enabled = true };
        Assert.True(dns.Started);
        dns.Announce("Studio Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);

        var hub = browser.Hubs[0];
        Assert.Equal(MacHid, hub.Id);
        Assert.Equal("Studio Mac", hub.Name);
        Assert.Equal(LinkAuthMode.Pin, hub.Auth);
        Assert.Equal(["E46058388B1A2E2C"], hub.Serials);
        Assert.Equal("Studio-Mac.local", hub.Endpoint.Host);
        Assert.Null(hub.Endpoint.Address);
        Assert.False(hub.IsManual);
    }

    [Fact]
    public async Task OurOwnHubAndMalformedRecordsAreNotListed()
    {
        var dns = new FakeDns();
        dns.Services["Me._dspi._tcp.local"] = Service("Me._dspi._tcp.local", OwnHid);
        dns.Services["Odd._dspi._tcp.local"] = new DnsSdService("Odd._dspi._tcp.local", "odd.local", 11915,
            new Dictionary<string, string> { ["v"] = "1" }, [], 0);
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { OwnHubId = OwnHid, Enabled = true };
        dns.Announce("Me._dspi._tcp.local");
        dns.Announce("Odd._dspi._tcp.local");
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => Volatile.Read(ref dns.Resolves) == 3);
        await Until(() => browser.Hubs.Count == 1);
        Assert.Equal(MacHid, browser.Hubs[0].Id);
    }

    [Fact]
    public async Task AGoodbyeRemovesTheHub()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { Enabled = true };
        var changes = 0;
        browser.HubsChanged += (_, _) => Interlocked.Increment(ref changes);
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        dns.Goodbye("Mac._dspi._tcp.local");
        Assert.Empty(browser.Hubs);
        Assert.True(Volatile.Read(ref changes) >= 2);
    }

    [Fact]
    public async Task AResolveThatEndsAfterTheGoodbyeDoesntBringTheHubBack()
    {
        var dns = new FakeDns { Gate = new TaskCompletionSource() };
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { Enabled = true };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => dns.Resolves == 1);
        dns.Goodbye("Mac._dspi._tcp.local");
        dns.Gate.SetResult();
        await Task.Delay(100);
        Assert.Empty(browser.Hubs);
    }

    [Fact]
    public async Task AFailedResolveIsTriedAgainWhileTheHubIsAnnounced()
    {
        var dns = new FakeDns();
        using var browser = new LinkHubBrowser(dns) { Enabled = true, ResolveRetryDelay = TimeSpan.FromMilliseconds(20) };
        dns.Announce("Mac._dspi._tcp.local");    // nothing answers yet
        await Until(() => dns.Resolves >= 2);
        lock (dns.Services) dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        await Until(() => browser.Hubs.Count == 1);
    }

    [Fact]
    public async Task AFailedResolveStopsAfterTheGoodbye()
    {
        var dns = new FakeDns();
        using var browser = new LinkHubBrowser(dns) { Enabled = true, ResolveRetryDelay = TimeSpan.FromMilliseconds(50) };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => dns.Resolves == 1);
        dns.Goodbye("Mac._dspi._tcp.local");
        await Task.Delay(300);
        Assert.Equal(1, dns.Resolves);
    }

    [Fact]
    public async Task ReannouncementsInsideTheIntervalShareOneLaterResolve()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { Enabled = true, ReresolveInterval = TimeSpan.FromMilliseconds(300) };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        dns.Announce("Mac._dspi._tcp.local");
        dns.Announce("Mac._dspi._tcp.local");
        dns.Announce("Mac._dspi._tcp.local");
        Assert.Equal(1, dns.Resolves);
        // Put off, not dropped: one more resolve at the end of the interval.
        await Until(() => Volatile.Read(ref dns.Resolves) == 2);
        await Task.Delay(400);
        Assert.Equal(2, dns.Resolves);
    }

    /// <summary>A bridge advertises n=0 as it starts and its device a moment
    /// later (bridge hub_task.c). The second record came inside the
    /// re-resolve interval and was dropped, so the device never appeared.</summary>
    [Fact]
    public async Task ABridgeThatNamesItsDeviceJustAfterStartingIsListedWithIt()
    {
        const string bridge = "DSPi Bridge 3F2A._dspi._tcp.local";
        static DnsSdService Bridge(string? serial)
        {
            var txt = new Dictionary<string, string> { ["v"] = "1", ["hid"] = MacHid, ["kind"] = "bridge", ["auth"] = "pin", ["n"] = serial == null ? "0" : "1", ["web"] = "1" };
            if (serial != null) txt["d"] = serial;
            return new DnsSdService(bridge, "dspi-bridge-3f2a.local.", 11915, txt, [], 12);
        }
        var dns = new FakeDns();
        dns.Services[bridge] = Bridge(null);
        using var browser = new LinkHubBrowser(dns, (_, _) => Task.FromResult<LinkInfo?>(null)) { Enabled = true, ReresolveInterval = TimeSpan.FromMilliseconds(500) };
        dns.Announce(bridge);
        await Until(() => browser.Hubs.Count == 1);
        Assert.Empty(browser.Hubs[0].Serials);

        await Task.Delay(100);
        lock (dns.Services) dns.Services[bridge] = Bridge("E46058388B1A2E2C");
        dns.Announce(bridge);    // the device's announcements, all inside the interval
        await Task.Delay(100);
        dns.Announce(bridge);
        await Until(() => browser.Hubs[0].Serials.Count == 1);
        Assert.Equal(["E46058388B1A2E2C"], browser.Hubs[0].Serials);
        Assert.Equal(1, browser.Hubs[0].DeviceCount);
    }

    [Fact]
    public async Task AnAnnouncementBookedForLaterIsDroppedAfterTheGoodbye()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { Enabled = true, ReresolveInterval = TimeSpan.FromMilliseconds(200) };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        dns.Announce("Mac._dspi._tcp.local");
        dns.Goodbye("Mac._dspi._tcp.local");
        await Task.Delay(400);
        Assert.Equal(1, dns.Resolves);
        Assert.Empty(browser.Hubs);
    }

    // ── Hubs that list no devices ──

    private static LinkInfo BridgeInfo(string hid, params string[] serials) =>
        new(new LinkProtoVersion(), new LinkHubInfo(hid, "DSPi Bridge 3F2A", LinkHubKind.Bridge, "1.0.0"), LinkAuthMode.Pin, "/dspi/v1",
            false, serials.Select(s => new LinkInfoDevice(s, "Living room")).ToArray());

    [Fact]
    public async Task AHubListingNoDevicesIsAskedForThemOnItsInfoPage()
    {
        var dns = new FakeDns();
        dns.Services["Bridge._dspi._tcp.local"] = Service("Bridge._dspi._tcp.local", MacHid, "pin");
        var probes = 0;
        using var browser = new LinkHubBrowser(dns, (e, _) =>
        {
            Interlocked.Increment(ref probes);
            Assert.Equal("Studio-Mac.local", e.Host);
            return Task.FromResult<LinkInfo?>(BridgeInfo(MacHid, "E46058388B1A2E2C"));
        }) { EmptyHubRefreshInterval = TimeSpan.FromMilliseconds(100), Enabled = true };
        dns.Announce("Bridge._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1 && browser.Hubs[0].Serials.Count == 1);
        Assert.Equal(["E46058388B1A2E2C"], browser.Hubs[0].Serials);
        Assert.Equal(1, browser.Hubs[0].DeviceCount);
    }

    /// <summary>Windows may keep answering with the record that lists no
    /// device: the info page's device stays until the info page drops it.</summary>
    [Fact]
    public async Task ADeviceFromTheInfoPageOutlastsAStaleRecord()
    {
        var dns = new FakeDns();
        dns.Services["Bridge._dspi._tcp.local"] = Service("Bridge._dspi._tcp.local", MacHid, "pin");
        var serials = new[] { "E46058388B1A2E2C" };
        var changes = 0;
        using var browser = new LinkHubBrowser(dns, (_, _) => Task.FromResult<LinkInfo?>(BridgeInfo(MacHid, Volatile.Read(ref serials))))
            { EmptyHubRefreshInterval = TimeSpan.FromMilliseconds(100), ReresolveInterval = TimeSpan.FromMilliseconds(50), Enabled = true };
        dns.Announce("Bridge._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1 && browser.Hubs[0].Serials.Count == 1);
        browser.HubsChanged += (_, _) => Interlocked.Increment(ref changes);

        // The stale record comes back again and again; the device stays, and
        // nothing is republished.
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(80);
            dns.Announce("Bridge._dspi._tcp.local");
        }
        await Task.Delay(250);
        Assert.Equal(["E46058388B1A2E2C"], browser.Hubs[0].Serials);
        Assert.Equal(0, Volatile.Read(ref changes));

        // The info page no longer lists it: gone.
        Volatile.Write(ref serials, []);
        await Until(() => browser.Hubs[0].Serials.Count == 0);
    }

    [Fact]
    public async Task AnInfoPageFromAnotherHubIsIgnored()
    {
        var dns = new FakeDns();
        dns.Services["Bridge._dspi._tcp.local"] = Service("Bridge._dspi._tcp.local", MacHid, "pin");
        var probes = 0;
        using var browser = new LinkHubBrowser(dns, (_, _) =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult<LinkInfo?>(BridgeInfo(OwnHid, "E46058388B1A2E2C"));
        }) { EmptyHubRefreshInterval = TimeSpan.FromMilliseconds(50), Enabled = true };
        dns.Announce("Bridge._dspi._tcp.local");
        await Until(() => Volatile.Read(ref probes) >= 2);
        Assert.Empty(browser.Hubs[0].Serials);
    }

    [Fact]
    public async Task AHubThatListsADeviceIsNotProbed()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid, d: "E46058388B1A2E2C");
        var probes = 0;
        using var browser = new LinkHubBrowser(dns, (_, _) =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult<LinkInfo?>(null);
        }) { EmptyHubRefreshInterval = TimeSpan.FromMilliseconds(50), Enabled = true };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref probes));
    }

    [Fact]
    public async Task TurningDiscoveryOffForgetsTheHubs()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns) { Enabled = true };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        browser.Enabled = false;
        Assert.False(dns.Started);
        Assert.Empty(browser.Hubs);
    }

    /// <summary>After the host name has failed, the resolved address is
    /// dialled, a LAN one in preference to a VPN's.</summary>
    [Fact]
    public async Task AFailedHubIsDialledByAddressNext()
    {
        var dns = new FakeDns();
        var tailscale = IPAddress.Parse("100.92.155.35");
        var lan = IPAddress.Parse("192.168.1.20");
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid, addresses: [tailscale, lan]);
        using var browser = new LinkHubBrowser(dns) { Enabled = true };
        dns.Announce("Mac._dspi._tcp.local");
        await Until(() => browser.Hubs.Count == 1);
        Assert.Null(browser.Hubs[0].Endpoint.Address);

        browser.Reresolve(MacHid);
        await Until(() => browser.Hubs[0].Endpoint.Address != null);
        Assert.Equal(lan, browser.Hubs[0].Endpoint.Address);
    }

    [Fact]
    public void TheBestAddressIsALanOne()
    {
        var v6ll = new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 12);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), LinkHubBrowser.BestAddress([IPAddress.Parse("100.64.0.1"), IPAddress.Parse("10.0.0.5")]));
        Assert.Equal(IPAddress.Parse("172.20.1.1"), LinkHubBrowser.BestAddress([v6ll, IPAddress.Parse("172.20.1.1")]));
        Assert.Equal(v6ll, LinkHubBrowser.BestAddress([IPAddress.Parse("100.64.0.1"), v6ll]));
        Assert.Equal(IPAddress.Parse("100.64.0.1"), LinkHubBrowser.BestAddress([IPAddress.Parse("100.64.0.1")]));
        Assert.Null(LinkHubBrowser.BestAddress([]));
    }

    [Fact]
    public void InstanceLabels()
    {
        Assert.Equal("Studio Mac", LinkHubBrowser.InstanceLabel("Studio Mac._dspi._tcp.local"));
        Assert.Equal("DSPi Bridge 3F2A", LinkHubBrowser.InstanceLabel("DSPi Bridge 3F2A._dspi._tcp.local."));
    }

    // ── Typed addresses ──

    private static LinkInfo Info(string hid, params string[] serials) =>
        new(new LinkProtoVersion(), new LinkHubInfo(hid, "Studio Mac", LinkHubKind.Console, "1.1.6"), LinkAuthMode.None, "/dspi/v1",
            false, serials.Select(s => new LinkInfoDevice(s, "Living room")).ToArray());

    [Fact]
    public async Task ATypedAddressIsProbedForItsHub()
    {
        var asked = new List<LinkEndpoint>();
        using var browser = new LinkHubBrowser(new FakeDns(), (e, _) =>
        {
            lock (asked) asked.Add(e);
            return Task.FromResult<LinkInfo?>(Info(MacHid, "E46058388B1A2E2C"));
        });
        browser.SetManual(["192.168.1.20:12000"]);
        await Until(() => browser.Hubs.Count == 1);
        var hub = browser.Hubs[0];
        Assert.True(hub.IsManual);
        Assert.Equal(MacHid, hub.Id);
        Assert.Equal(["E46058388B1A2E2C"], hub.Serials);
        Assert.Equal(12000, hub.Endpoint.Port);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), hub.Endpoint.Address);
        lock (asked) Assert.Equal("192.168.1.20", asked[0].Host);
    }

    [Fact]
    public async Task ATypedHubWithNoDevicesIsAskedAgain()
    {
        var serials = Array.Empty<string>();
        var changes = 0;
        using var browser = new LinkHubBrowser(new FakeDns(), (_, _) => Task.FromResult<LinkInfo?>(BridgeInfo(MacHid, Volatile.Read(ref serials))))
            { ManualRetryInterval = TimeSpan.FromMilliseconds(50) };
        browser.HubsChanged += (_, _) => Interlocked.Increment(ref changes);
        browser.SetManual(["192.168.1.40"]);
        await Until(() => browser.Hubs.Count == 1);
        await Task.Delay(200);
        // The same empty answer again and again is not a change.
        Assert.Equal(1, Volatile.Read(ref changes));
        Volatile.Write(ref serials, ["E46058388B1A2E2C"]);
        await Until(() => browser.Hubs[0].Serials.Count == 1);
    }

    [Fact]
    public async Task AHubFoundBothWaysIsListedOnce()
    {
        var dns = new FakeDns();
        dns.Services["Mac._dspi._tcp.local"] = Service("Mac._dspi._tcp.local", MacHid);
        using var browser = new LinkHubBrowser(dns, (_, _) => Task.FromResult<LinkInfo?>(Info(MacHid))) { Enabled = true };
        dns.Announce("Mac._dspi._tcp.local");
        browser.SetManual(["192.168.1.20"]);
        await Until(() => browser.Hubs.Count >= 1);
        await Task.Delay(100);
        var hub = Assert.Single(browser.Hubs);
        Assert.False(hub.IsManual);
    }

    [Fact]
    public async Task TypingOurOwnAddressListsNothing()
    {
        using var browser = new LinkHubBrowser(new FakeDns(), (_, _) => Task.FromResult<LinkInfo?>(Info(OwnHid))) { OwnHubId = OwnHid };
        var probed = await browser.ProbeAsync("127.0.0.1");
        Assert.NotNull(probed);
        Assert.True(browser.IsOwnHub(probed!));
        browser.SetManual(["127.0.0.1"]);
        await Task.Delay(100);
        Assert.Empty(browser.Hubs);
    }

    [Fact]
    public async Task AnAddressThatDoesntAnswerIsRetried()
    {
        int calls = 0;
        using var browser = new LinkHubBrowser(new FakeDns(), (_, _) =>
            Task.FromResult<LinkInfo?>(Interlocked.Increment(ref calls) < 2 ? null : Info(MacHid)))
        { ManualRetryInterval = TimeSpan.FromMilliseconds(100) };
        browser.SetManual(["studio.local"]);
        await Until(() => browser.Hubs.Count == 1);
        Assert.True(Volatile.Read(ref calls) >= 2);
    }

    [Fact]
    public async Task NonsenseIsNotProbed()
    {
        int calls = 0;
        using var browser = new LinkHubBrowser(new FakeDns(), (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<LinkInfo?>(null); });
        Assert.Null(await browser.ProbeAsync("host:abc"));
        Assert.Equal(0, calls);
    }
}
