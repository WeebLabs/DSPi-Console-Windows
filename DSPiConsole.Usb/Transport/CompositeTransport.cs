using DSPiConsole.Core.Link;

namespace DSPiConsole.Usb.Transport;

/// <summary>A hub found by discovery or typed in by the user.</summary>
public sealed record LinkHubRecord(
    string Id,
    string Name,
    LinkHubKind Kind,
    LinkAuthMode Auth,
    int DeviceCount,
    IReadOnlyList<string> Serials,
    LinkEndpoint Endpoint,
    bool IsManual = false)
{
    /// <summary>"host:port" for display.</summary>
    public string DisplayAddress => LinkHubAddress.Display(Endpoint.Address?.ToString() ?? Endpoint.Host, Endpoint.Port);
}

/// <summary>
/// The one transport <see cref="DspDevice"/> sits on: the boards plugged into
/// this PC and the devices other hubs share, as one list. Selecting a device
/// routes to whichever transport owns it, and the connection, selection,
/// error and notification streams follow the active one, so the view model
/// never learns whether its device is local or remote. A switch between
/// transports looks to the facade like a disconnect then a connect, which is
/// the order it has always handled. Port of the macOS Console's
/// CompositeTransport (plan L1 item 2, the "DeviceManager").
/// </summary>
public sealed class CompositeTransport : IDspTransport
{
    private readonly IDspTransport _local;
    private readonly LinkTokenStore _tokens;
    private readonly Func<string, LinkHubRecord, NetworkTransport> _makeRemote;
    private readonly object _lock = new();

    private readonly Dictionary<string, NetworkTransport> _remotes = new();
    private IReadOnlyList<LinkHubRecord> _hubs = Array.Empty<LinkHubRecord>();
    private IDspTransport _active;
    private IReadOnlyList<DSPiDeviceInfo> _devices = Array.Empty<DSPiDeviceInfo>();
    private bool _connected;
    private ulong _generation;
    private IDspTransport? _lockedTransport;

    /// <summary>True while the active remote was chosen automatically (no
    /// board here); a board that appears then takes over.</summary>
    private bool _remoteWasAutoSelected;
    private bool _autoSelecting;

    /// <summary>How long a hub must stay off the browse list with its session
    /// down before its transport is dropped: rides out a missed announcement
    /// and the first rungs of the reconnect ladder.</summary>
    public TimeSpan PruneGrace { get; init; } = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTime> _pruneSince = new();
    private int _pruneCheckBooked;

    /// <summary>Asked when a hub needs a PIN (see <see cref="NetworkTransport.PairingPrompt"/>).</summary>
    public Func<LinkPairingRequest, Task<string?>>? PairingPrompt { get; set; }

    /// <summary>A hub's connection failed: discovery may re-resolve it.</summary>
    public event EventHandler<string>? HubUnreachable;

    /// <param name="local">The USB transport.</param>
    /// <param name="makeRemote">Builds the transport for a hub (tests pass one
    /// over a fake hub).</param>
    public CompositeTransport(IDspTransport local, LinkTokenStore tokens, Func<string, LinkHubRecord, NetworkTransport> makeRemote)
    {
        _local = local;
        _tokens = tokens;
        _makeRemote = makeRemote;
        _active = local;
        Subscribe(local);
        local.AvailableDevicesChanged += (_, _) => Recompute();
        local.Connected += (_, _) => LocalOpened();
        local.SelectedDeviceChanged += (_, _) => { if (local.SelectedDevice == null) Recompute(); };
    }

    /// <summary>True while the device being driven is on another hub.</summary>
    public bool IsRemoteActive { get { lock (_lock) return _active != _local; } }

    /// <summary>The hubs currently known, as last given to <see cref="UpdateHubs"/>.</summary>
    public IReadOnlyList<LinkHubRecord> Hubs { get { lock (_lock) return _hubs; } }

    // ── Hubs ──

    /// <summary>
    /// Discovery's current view of the network. Devices a hub's TXT record
    /// lists appear before any connection. A hub with a transport already is
    /// told its address (one back on a new lease is dialled there); one that
    /// has gone takes its unconnected transport with it after the grace.
    /// </summary>
    public void UpdateHubs(IReadOnlyList<LinkHubRecord> hubs)
    {
        List<(NetworkTransport, LinkHubRecord, bool)> seen = new();
        lock (_lock)
        {
            var previous = _hubs.Select(h => h.Id).ToHashSet();
            _hubs = hubs;
            foreach (var hub in hubs)
                if (_remotes.TryGetValue(hub.Id, out var remote))
                    seen.Add((remote, hub, !previous.Contains(hub.Id)));
        }
        foreach (var (remote, hub, reappeared) in seen) remote.HubSeen(hub.Endpoint, reappeared, hub.Name);
        Prune();
        Recompute();
    }

    private NetworkTransport RemoteFor(LinkHubRecord hub)
    {
        NetworkTransport transport;
        lock (_lock)
        {
            if (_remotes.TryGetValue(hub.Id, out var existing)) return existing;
            transport = _makeRemote(hub.Id, hub);
            transport.PairingPrompt = PairingPrompt;
            _remotes[hub.Id] = transport;
        }
        var id = hub.Id;
        transport.AvailableDevicesChanged += (_, _) => Recompute();
        transport.ConnectionFailed += (_, _) =>
        {
            HubUnreachable?.Invoke(this, id);
            Prune();
            Recompute();
        };
        transport.Connect(hub.Endpoint);
        return transport;
    }

    /// <summary>Drops every transport whose hub has been off the list with no
    /// session up for the grace period; if it was driving the view model, the
    /// local transport takes over.</summary>
    private void Prune()
    {
        var now = DateTime.UtcNow;
        List<NetworkTransport> gone = new();
        bool waiting;
        bool fallBack = false;
        lock (_lock)
        {
            var listed = _hubs.Select(h => h.Id).ToHashSet();
            foreach (var (id, remote) in _remotes.ToList())
            {
                if (listed.Contains(id) || !remote.IsSessionDown)
                {
                    _pruneSince.Remove(id);
                    continue;
                }
                var since = _pruneSince.TryGetValue(id, out var s) ? s : now;
                _pruneSince[id] = since;
                if (now - since < PruneGrace) continue;
                _pruneSince.Remove(id);
                _remotes.Remove(id);
                gone.Add(remote);
                if (_active == remote) fallBack = true;
            }
            waiting = _pruneSince.Count > 0;
        }
        if (fallBack)
        {
            lock (_lock) _remoteWasAutoSelected = false;
            Attach(_local);
        }
        foreach (var remote in gone) remote.Dispose();
        if (waiting && Interlocked.Exchange(ref _pruneCheckBooked, 1) == 0)
        {
            // Nothing else is sure to call Prune once the grace runs out.
            _ = Task.Delay(PruneGrace + TimeSpan.FromMilliseconds(50)).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _pruneCheckBooked, 0);
                Prune();
                Recompute();
            }, TaskScheduler.Default);
        }
    }

    // ── The list ──

    private void Recompute()
    {
        List<DSPiDeviceInfo> merged;
        IReadOnlyList<LinkHubRecord> hubs;
        bool changed;
        lock (_lock)
        {
            merged = _local.AvailableDevices.ToList();
            var seen = merged.Select(d => d.Serial).ToHashSet();
            void Add(DSPiDeviceInfo d)
            {
                if (seen.Add(d.Serial)) merged.Add(d);
            }

            var listed = new HashSet<string>();
            foreach (var hub in _hubs)
            {
                if (_remotes.TryGetValue(hub.Id, out var t) && t.AvailableDevices.Count > 0)
                {
                    foreach (var d in t.AvailableDevices) Add(d);
                    listed.Add(hub.Id);
                }
                else
                {
                    // Not connected yet: what its TXT record advertises.
                    foreach (var serial in hub.Serials)
                        Add(new DSPiDeviceInfo(serial, $"dspilink://{hub.Id}/{NetworkTransport.PlaceholderHandle}")
                        {
                            Kind = DeviceLinkKind.Remote,
                            HubId = hub.Id,
                            HubName = hub.Name,
                        });
                }
            }
            // A session that is up lists its devices whether or not discovery
            // shows the hub right now.
            foreach (var (id, t) in _remotes.OrderBy(r => r.Key))
                if (!listed.Contains(id))
                    foreach (var d in t.AvailableDevices) Add(d);
            // The device being driven stays listed while its hub is unreachable.
            if (_active != _local && _active.SelectedDevice is { } selected) Add(selected);

            // The view model rebuilds the picker on every change: only a real one.
            changed = !_devices.SequenceEqual(merged);
            _devices = merged;
            hubs = _hubs;
        }
        if (changed) AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
        AutoSelectRemoteIfIdle(merged, hubs);
    }

    /// <summary>With no board here, a lone shared device is picked on its own,
    /// but only on a hub that won't stop to ask for a PIN: a pairing prompt
    /// nobody asked for at launch is worse than a click.</summary>
    private void AutoSelectRemoteIfIdle(List<DSPiDeviceInfo> merged, IReadOnlyList<LinkHubRecord> hubs)
    {
        DSPiDeviceInfo? candidate;
        lock (_lock)
        {
            if (_autoSelecting || _active != _local) return;
            if (_local.AvailableDevices.Count > 0 || _local.SelectedDevice != null) return;
            candidate = merged.FirstOrDefault(d =>
                d.Kind == DeviceLinkKind.Remote && hubs.FirstOrDefault(h => h.Id == d.HubId) is { } hub
                && (hub.Auth == LinkAuthMode.None || _tokens.Token(hub.Id) != null));
            if (candidate == null) return;
            _autoSelecting = true;
        }
        try
        {
            Select(candidate);
            lock (_lock) _remoteWasAutoSelected = _active != _local;
        }
        finally
        {
            lock (_lock) _autoSelecting = false;
        }
    }

    /// <summary>A board opened here takes back from a remote picked on its own
    /// (a USB firmware flash unplugs the board for a moment, and the installer
    /// verifies whichever device the view model is on).</summary>
    private void LocalOpened()
    {
        bool takeOver;
        lock (_lock)
        {
            takeOver = _remoteWasAutoSelected && _active != _local;
            if (takeOver) _remoteWasAutoSelected = false;
        }
        if (takeOver) Attach(_local);
    }

    // ── Switching ──

    private void Subscribe(IDspTransport t)
    {
        t.Connected += OnConnected;
        t.Disconnected += OnDisconnected;
        t.ErrorMessageChanged += OnError;
        t.SelectedDeviceChanged += OnSelected;
        t.NotificationReceived += OnNotification;
    }

    private bool IsActive(object? sender)
    {
        lock (_lock) return ReferenceEquals(sender, _active);
    }

    private void OnConnected(object? sender, DSPiDeviceInfo device)
    {
        if (!IsActive(sender)) return;
        SetConnected(true, device);
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        if (!IsActive(sender)) return;
        SetConnected(false, null);
    }

    private void OnError(object? sender, EventArgs e)
    {
        if (IsActive(sender)) ErrorMessageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSelected(object? sender, EventArgs e)
    {
        if (IsActive(sender)) SelectedDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotification(object? sender, LinkNotification n)
    {
        if (IsActive(sender)) NotificationReceived?.Invoke(this, n);
    }

    /// <summary>Raises Connected / Disconnected on a real change only, so the
    /// facade sees exactly one of each per connection.</summary>
    /// <summary>Held while a connection change is decided and raised, so
    /// changes from different threads reach the facade in order.</summary>
    private readonly object _connectedRaise = new();

    private void SetConnected(bool value, DSPiDeviceInfo? device)
    {
        lock (_connectedRaise)
        {
            lock (_lock)
            {
                if (_connected == value) return;
                _connected = value;
                if (value) _generation++;
            }
            if (value) Connected?.Invoke(this, device!);
            else Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Points everything at <paramref name="transport"/>: the old
    /// connection ends for the facade, and the new one begins if it is up.</summary>
    private void Attach(IDspTransport transport)
    {
        lock (_lock)
        {
            if (_active == transport) return;
            // A remote pruned meanwhile is not attached.
            if (transport != _local && !(transport is NetworkTransport n && _remotes.ContainsValue(n))) return;
            _active = transport;
        }
        LinkLog.Write($"devices: now driving {(transport is NetworkTransport n2 ? $"a device on {n2.HubName}" : "a board on USB")}");
        // Subscribing once per transport: the handlers filter on the active one.
        bool subscribe;
        lock (_lock) subscribe = transport != _local && _subscribed.Add(transport);
        if (subscribe) Subscribe(transport);
        SetConnected(false, null);
        ErrorMessageChanged?.Invoke(this, EventArgs.Empty);
        SelectedDeviceChanged?.Invoke(this, EventArgs.Empty);
        if (transport.IsConnected && transport.SelectedDevice is { } device) SetConnected(true, device);
        Recompute();
    }

    private readonly HashSet<IDspTransport> _subscribed = new(ReferenceEqualityComparer.Instance);

    // ── IDspTransport ──

    private IDspTransport Active { get { lock (_lock) return _active; } }

    public ushort Session => Active.Session;
    public ulong Generation { get { lock (_lock) return _generation; } }
    public bool IsConnected { get { lock (_lock) return _connected; } }
    public string? ErrorMessage => Active.ErrorMessage;
    public DSPiDeviceInfo? SelectedDevice => Active.SelectedDevice;
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices { get { lock (_lock) return _devices; } }

    public event EventHandler<DSPiDeviceInfo>? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? ErrorMessageChanged;
    public event EventHandler? SelectedDeviceChanged;
    public event EventHandler<LinkNotification>? NotificationReceived;

    public void Start() => _local.Start();

    public void Stop()
    {
        _local.Stop();
        List<NetworkTransport> remotes;
        lock (_lock) remotes = _remotes.Values.ToList();
        foreach (var r in remotes) r.Disconnect();
    }

    /// <summary>The user's choice: a remote picked here stays when a board appears.</summary>
    public void SelectDevice(DSPiDeviceInfo device)
    {
        lock (_lock) _remoteWasAutoSelected = false;
        Select(device);
    }

    private void Select(DSPiDeviceInfo device)
    {
        if (device.Kind != DeviceLinkKind.Remote || device.HubId == null)
        {
            Attach(_local);
            _local.SelectDevice(device);
            return;
        }
        NetworkTransport? transport;
        LinkHubRecord? hub;
        lock (_lock)
        {
            _remotes.TryGetValue(device.HubId, out transport);
            hub = _hubs.FirstOrDefault(h => h.Id == device.HubId);
        }
        if (transport == null)
        {
            if (hub == null) return; // gone from the network: nothing to dial
            transport = RemoteFor(hub);
        }
        transport.Bind(device.Serial);
        Attach(transport);
    }

    /// <summary>
    /// Opens a session to a hub without choosing one of its devices (the Pair
    /// button in Settings › Networking): a hub that asks for a PIN asks now,
    /// and once paired its devices are listed from the session rather than
    /// from its announcement. The device being driven doesn't change.
    /// </summary>
    public void OpenHub(LinkHubRecord hub)
    {
        NetworkTransport? existing;
        lock (_lock) _remotes.TryGetValue(hub.Id, out existing);
        if (existing == null) RemoteFor(hub);
        else if (existing.IsSessionDown) existing.Connect(hub.Endpoint);
    }

    public void Reconnect() => Active.Reconnect();
    public void Disconnect() => Active.Disconnect();
    public void MarkDisconnected() => Active.MarkDisconnected();

    public LinkStatus ControlOut(byte request, ushort value, ushort index, ReadOnlySpan<byte> data) =>
        Active.ControlOut(request, value, index, data);

    public LinkStatus ControlIn(byte request, ushort value, ushort index, int length, out byte[] data) =>
        Active.ControlIn(request, value, index, length, out data);

    public T Exclusive<T>(Func<T> body) => Active.Exclusive(body);

    public byte[]? ReadBulkParams() => Active.ReadBulkParams();

    /// <summary>The lock is released where it was granted, even if the device
    /// was switched mid-flow.</summary>
    public bool AcquireDeviceLock(string reason, TimeSpan timeout)
    {
        var t = Active;
        bool granted = t.AcquireDeviceLock(reason, timeout);
        lock (_lock)
        {
            _lastLockRefused = !granted && t.LastLockRefused;
            if (granted) _lockedTransport = t;
        }
        return granted;
    }

    private bool _lastLockRefused;
    public bool LastLockRefused { get { lock (_lock) return _lastLockRefused; } }

    public void ReleaseDeviceLock()
    {
        IDspTransport? t;
        lock (_lock)
        {
            t = _lockedTransport;
            _lockedTransport = null;
        }
        t?.ReleaseDeviceLock();
    }

    public void Dispose()
    {
        List<NetworkTransport> remotes;
        lock (_lock)
        {
            remotes = _remotes.Values.ToList();
            _remotes.Clear();
        }
        foreach (var r in remotes) r.Dispose();
        _local.Dispose();
    }
}
