using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DSPiConsole.Core.Link;

namespace DSPiConsole.Usb.Transport;

/// <summary>
/// Finds DSPi Link hubs: by DNS-SD (spec 3.1) while "Find hubs automatically"
/// is on, and at addresses the user typed, by probing /dspi/v1/info (spec
/// 3.2). This PC's own hub is never listed (it is recognised by its hid).
///
/// A hub is dialled by its .local host name first, which Windows resolves
/// over mDNS on every interface. A single DNS-SD resolve answers for one
/// interface only, and it may be the wrong one (a VPN adapter, L0 spike a), so
/// the resolved address is used only after the name has failed to connect,
/// via <see cref="Reresolve"/>. Port of the macOS Console's HubBrowser.
/// </summary>
public sealed class LinkHubBrowser : IDisposable
{
    private readonly IDnsSdBrowser _dns;
    private readonly Func<LinkEndpoint, CancellationToken, Task<LinkInfo?>> _probe;
    private readonly object _lock = new();

    private readonly Dictionary<string, LinkHubRecord> _discovered = new(StringComparer.OrdinalIgnoreCase); // by instance
    private readonly Dictionary<string, DnsSdService> _resolved = new(StringComparer.OrdinalIgnoreCase);    // by instance
    private readonly HashSet<string> _useAddress = new(StringComparer.OrdinalIgnoreCase);                   // hub ids
    private readonly Dictionary<string, LinkHubRecord?> _manual = new(StringComparer.OrdinalIgnoreCase);    // by typed address
    /// <summary>Instances announced and not said goodbye: a resolve that
    /// finishes after the goodbye mustn't bring the hub back.</summary>
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastResolve = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Instances with a re-resolve booked for the end of the interval.</summary>
    private readonly HashSet<string> _resolveBooked = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private bool _disposed;
    private Timer? _manualRetry;
    private Timer? _emptyRefresh;
    private int _refreshing;

    /// <summary>The first wait before a failed resolve is tried again (it
    /// doubles, to 30 s, while the hub is still announced).</summary>
    public TimeSpan ResolveRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Re-announcements re-resolve (a TXT change) at most this often.
    /// One that comes sooner is put off to the end of the interval, never
    /// dropped: a bridge advertises n=0 as it starts and its device a moment
    /// later, and that second record must not be lost.</summary>
    public TimeSpan ReresolveInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a hub that lists no devices is asked again through
    /// its info page, in case an announcement went missing. Hubs that list a
    /// device are left alone: a bridge has few sockets.</summary>
    public TimeSpan EmptyHubRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>This PC's own hub id, never listed.</summary>
    public string? OwnHubId { get; set; }

    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ManualRetryInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Every hub now known (discovered first, then typed), raised on
    /// a background thread whenever it changes.</summary>
    public event EventHandler<IReadOnlyList<LinkHubRecord>>? HubsChanged;

    public LinkHubBrowser(IDnsSdBrowser dns, Func<LinkEndpoint, CancellationToken, Task<LinkInfo?>>? probe = null)
    {
        _dns = dns;
        _probe = probe ?? ProbeInfoAsync;
        _dns.Found += (_, instance) => Announced(instance);
        _dns.Lost += (_, instance) =>
        {
            lock (_lock)
            {
                _present.Remove(instance);
                _lastResolve.Remove(instance);
                _discovered.Remove(instance);
                _resolved.Remove(instance);
            }
            Publish();
        };
    }

    /// <summary>An announcement: resolve now, or, inside the interval, once
    /// at its end (several announcements in a row share that one).</summary>
    private void Announced(string instance)
    {
        TimeSpan? wait = null;
        lock (_lock)
        {
            if (_disposed) return;
            _present.Add(instance);
            var now = DateTime.UtcNow;
            if (_lastResolve.TryGetValue(instance, out var last) && now - last < ReresolveInterval)
            {
                if (!_resolveBooked.Add(instance)) return;
                wait = last + ReresolveInterval - now;
            }
            else
            {
                _lastResolve[instance] = now;
            }
        }
        if (wait == null)
        {
            _ = ResolveAsync(instance);
            return;
        }
        _ = Task.Delay(wait.Value).ContinueWith(_ =>
        {
            lock (_lock)
            {
                _resolveBooked.Remove(instance);
                if (!_enabled || _disposed || !_present.Contains(instance)) return;
                _lastResolve[instance] = DateTime.UtcNow;
            }
            _ = ResolveAsync(instance);
        }, TaskScheduler.Default);
    }

    public IReadOnlyList<LinkHubRecord> Hubs
    {
        get
        {
            lock (_lock)
            {
                var list = _discovered.Values.ToList();
                var ids = list.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // A typed hub that discovery also found is listed once, as
                // discovered; one typed twice (by two spellings) once too.
                foreach (var typed in _manual.Values.OfType<LinkHubRecord>())
                    if (ids.Add(typed.Id)) list.Add(typed);
                return list.Where(h => !IsOwn(h.Id)).ToArray();
            }
        }
    }

    private bool IsOwn(string id) => OwnHubId != null && string.Equals(id, OwnHubId, StringComparison.OrdinalIgnoreCase);

    // ── Automatic discovery ──

    /// <summary>"Find hubs automatically".</summary>
    public bool Enabled
    {
        get { lock (_lock) return _enabled; }
        set
        {
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value)
                {
                    _discovered.Clear();
                    _resolved.Clear();
                    _present.Clear();
                    _lastResolve.Clear();
                }
                else
                {
                    _emptyRefresh ??= new Timer(_ => _ = RefreshEmptyHubsAsync(), null, EmptyHubRefreshInterval, EmptyHubRefreshInterval);
                }
            }
            if (value) _dns.Start(LinkService.ServiceType);
            else _dns.Stop();
            Publish();
        }
    }

    private async Task ResolveAsync(string instance, int attempt = 0)
    {
        if (attempt > 0)
        {
            // Said goodbye (or discovery went off) during the wait.
            lock (_lock) if (!_enabled || _disposed || !_present.Contains(instance)) return;
        }
        var service = await _dns.ResolveAsync(instance, ResolveTimeout).ConfigureAwait(false);
        var txt = service != null ? LinkTxtRecord.TryParse(service.Txt) : null;
        if (service == null || txt == null)
        {
            LinkLog.Write(service == null
                ? $"hubs: {InstanceLabel(instance)} did not resolve (attempt {attempt + 1})"
                : $"hubs: {InstanceLabel(instance)} has an unusable TXT record ({string.Join(" ", service.Txt.Select(p => $"{p.Key}={p.Value}"))}) (attempt {attempt + 1})");
            // A lost answer mustn't hide the hub until it says goodbye: try
            // again while it is still announced, backing off.
            bool again;
            lock (_lock) again = _enabled && !_disposed && _present.Contains(instance) && attempt < 6;
            if (!again) return;
            await Task.Delay(TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks, ResolveRetryDelay.Ticks << attempt))).ConfigureAwait(false);
            await ResolveAsync(instance, attempt + 1).ConfigureAwait(false);
            return;
        }
        lock (_lock)
        {
            // Said goodbye while this was resolving: stay gone.
            if (!_enabled || !_present.Contains(instance)) return;
            _resolved[instance] = service;
            _discovered[instance] = Record(instance, service, txt);
        }
        Publish();
    }

    /// <summary>
    /// Asks each discovered hub that lists no devices for its info page. A
    /// device that arrived in an announcement Windows didn't pass on (or one
    /// this browser missed) shows up here instead of staying hidden until the
    /// hub changes again; for a hub that asks for a PIN, that device is the
    /// only way to reach it. The next TXT record still wins.
    /// </summary>
    private async Task RefreshEmptyHubsAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            List<(string Instance, LinkHubRecord Hub)> empty;
            lock (_lock)
            {
                if (!_enabled || _disposed) return;
                empty = _discovered.Where(p => p.Value.Serials.Count == 0).Select(p => (p.Key, p.Value)).ToList();
            }
            foreach (var (instance, hub) in empty)
            {
                LinkInfo? info;
                using (var cts = new CancellationTokenSource(ResolveTimeout))
                {
                    try { info = await _probe(hub.Endpoint, cts.Token).ConfigureAwait(false); }
                    catch (Exception) { continue; }
                }
                if (info?.Devices is not { Count: > 0 } devices
                    || !string.Equals(info.Hub.Id, hub.Id, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_lock)
                {
                    // Gone, or a newer record already lists devices.
                    if (!_discovered.TryGetValue(instance, out var current) || current.Serials.Count > 0) continue;
                    _discovered[instance] = current with { DeviceCount = devices.Count, Serials = devices.Select(d => d.Serial).ToArray() };
                }
                LinkLog.Write($"hubs: {hub.Name} lists {devices.Count} device(s) on its info page; its announcement did not");
                Publish();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private LinkHubRecord Record(string instance, DnsSdService service, LinkTxtRecord txt)
    {
        var host = service.Host.TrimEnd('.');
        var address = _useAddress.Contains(txt.HubId) ? BestAddress(service.Addresses) : null;
        return new LinkHubRecord(txt.HubId, InstanceLabel(instance), txt.Kind, txt.Auth, txt.DeviceCount,
            txt.Serials ?? Array.Empty<string>(),
            new LinkEndpoint(address?.ToString() ?? host, service.Port, txt.WebSocketPath, txt.Tls, address));
    }

    /// <summary>"Studio Mac._dspi._tcp.local" → "Studio Mac".</summary>
    public static string InstanceLabel(string instance)
    {
        int i = instance.IndexOf("._dspi._tcp", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? instance[..i] : instance;
    }

    /// <summary>
    /// A LAN address first (private IPv4, then link-local IPv6 with its scope),
    /// then anything: a VPN adapter's address may not reach the hub.
    /// </summary>
    public static IPAddress? BestAddress(IReadOnlyList<IPAddress> addresses)
    {
        static bool PrivateV4(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        }
        return addresses.FirstOrDefault(PrivateV4)
               ?? addresses.FirstOrDefault(a => a.IsIPv6LinkLocal)
               ?? addresses.FirstOrDefault();
    }

    /// <summary>A hub's connection failed: look it up again and, from now on,
    /// dial the address the resolve gives rather than its host name.</summary>
    public void Reresolve(string hubId)
    {
        string? instance;
        lock (_lock)
        {
            _useAddress.Add(hubId);
            instance = _discovered.FirstOrDefault(p => string.Equals(p.Value.Id, hubId, StringComparison.OrdinalIgnoreCase)).Key;
        }
        if (instance != null) _ = ResolveAsync(instance);
        else _ = ProbeManualAsync();
    }

    // ── Typed addresses ──

    /// <summary>The addresses the user added (Settings › Networking). Each is
    /// probed for its hub id; one that doesn't answer, or answers with no
    /// devices, is asked again now and then.</summary>
    public void SetManual(IEnumerable<string> addresses)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var wanted = addresses.Select(a => a.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _manual.Keys.Where(k => !wanted.Contains(k)).ToList()) _manual.Remove(gone);
            foreach (var a in wanted) _manual.TryAdd(a, null);
            _manualRetry ??= new Timer(_ => _ = ProbeManualAsync(onlyMissing: true), null, ManualRetryInterval, ManualRetryInterval);
        }
        _ = ProbeManualAsync();
    }

    private async Task ProbeManualAsync(bool onlyMissing = false)
    {
        List<string> addresses;
        lock (_lock)
        {
            if (_disposed) return;
            // A hub with no devices yet may gain one; one with a device is
            // left alone (its sessions say when that changes).
            addresses = _manual.Where(p => !onlyMissing || p.Value == null || p.Value.Serials.Count == 0).Select(p => p.Key).ToList();
        }
        var probes = addresses.Select(async address => (address, record: await ProbeAsync(address).ConfigureAwait(false)));
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        bool changed = false;
        lock (_lock)
        {
            foreach (var (address, record) in results)
            {
                if (!_manual.ContainsKey(address)) continue;
                if (record == null && onlyMissing) continue;
                // Records compare their lists by reference: compare what shows.
                if (!SameListing(_manual[address], record)) changed = true;
                _manual[address] = record;
            }
        }
        if (changed) Publish();
    }

    private static bool SameListing(LinkHubRecord? a, LinkHubRecord? b) =>
        a == null || b == null
            ? a == b
            : a with { Serials = Array.Empty<string>() } == b with { Serials = Array.Empty<string>() } && a.Serials.SequenceEqual(b.Serials);

    /// <summary>What a typed address leads to: the hub's own description, or
    /// null when nothing answers. Also used to check an address before adding it.</summary>
    public async Task<LinkHubRecord?> ProbeAsync(string address)
    {
        if (!LinkHubAddress.TryParse(address, out var host, out var port)) return null;
        var endpoint = LinkHubAddress.Endpoint(host, port);
        using var cts = new CancellationTokenSource(ResolveTimeout);
        LinkInfo? info;
        try { info = await _probe(endpoint, cts.Token).ConfigureAwait(false); }
        catch (Exception) { return null; }
        if (info == null) return null;
        return new LinkHubRecord(info.Hub.Id.ToLowerInvariant(), info.Hub.Name, info.Hub.Kind, info.Auth,
            info.Devices?.Count ?? 0, info.Devices?.Select(d => d.Serial).ToArray() ?? Array.Empty<string>(),
            endpoint with { Path = info.Ws, Tls = info.Tls }, IsManual: true);
    }

    /// <summary>The id of the hub typed in as <paramref name="address"/>
    /// once it has answered (whether or not discovery also lists it); null
    /// while it hasn't.</summary>
    public string? AnsweredId(string address)
    {
        lock (_lock) return _manual.TryGetValue(address.Trim(), out var record) ? record?.Id : null;
    }

    /// <summary>Whether <paramref name="record"/> is this PC's own hub.</summary>
    public bool IsOwnHub(LinkHubRecord record) => IsOwn(record.Id);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>GET /dspi/v1/info (spec 3.2).</summary>
    public static async Task<LinkInfo?> ProbeInfoAsync(LinkEndpoint endpoint, CancellationToken token)
    {
        var uri = new UriBuilder(endpoint.Tls ? "https" : "http", endpoint.WebSocketUri.Host, endpoint.Port, LinkService.InfoPath).Uri;
        HttpResponseMessage response;
        if (endpoint.Address is { } address)
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (ctx, ct) =>
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            response = await client.GetAsync(uri, token).ConfigureAwait(false);
        }
        else
        {
            response = await Http.GetAsync(uri, token).ConfigureAwait(false);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return text.Length > 64 * 1024 ? null : LinkInfo.Decode(text);
        }
    }

    private void Publish()
    {
        var hubs = Hubs;
        LinkLog.Write("hubs: " + (hubs.Count == 0 ? "none" : string.Join("; ", hubs.Select(h =>
            $"{h.Name} [{h.Kind}, {h.Auth}, {h.DeviceCount} dev] at {h.DisplayAddress}{(h.IsManual ? " (typed)" : "")}"))));
        HubsChanged?.Invoke(this, hubs);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
        }
        _manualRetry?.Dispose();
        _emptyRefresh?.Dispose();
        _dns.Dispose();
    }
}
