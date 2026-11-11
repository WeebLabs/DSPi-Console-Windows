namespace DSPiConsole.Core.Link.Hub;

/// <summary>A session on the hub: an id, a live role, and where its
/// notifications, polls and device events go. The local UI is session 1;
/// the server registers one per authenticated connection.</summary>
public sealed class LinkHubSession
{
    private readonly object _lock = new();
    private LinkRole _role;

    public LinkHubSession(ushort id, LinkRole role, int? clientId)
    {
        Id = id;
        _role = role;
        ClientId = clientId;
    }

    public ushort Id { get; }

    /// <summary>Changed by an admin on another thread; read on every command.</summary>
    public LinkRole Role
    {
        get { lock (_lock) return _role; }
        set { lock (_lock) _role = value; }
    }

    /// <summary>The paired client this session authenticated as, so a
    /// revocation or role change reaches it; null for the local UI and for
    /// open-access sessions.</summary>
    public int? ClientId { get; }

    /// <summary>The hub closed the session itself (a revocation): the
    /// connection closes with 4002.</summary>
    public Action? OnClosedByHub { get; set; }
    public Action<LinkNotifyFrame>? OnNotify { get; set; }
    public Action<LinkResyncFrame>? OnResync { get; set; }
    public Action<LinkPollFrame>? OnPoll { get; set; }
    public Action<DeviceRegistryEvent>? OnDeviceEvent { get; set; }
    public Action<LinkPollError>? OnPollError { get; set; }

    private int _pollTag;
    internal ushort NextPollTag() => (ushort)Interlocked.Increment(ref _pollTag);

    internal void Clear()
    {
        OnClosedByHub = null;
        OnNotify = null;
        OnResync = null;
        OnPoll = null;
        OnDeviceEvent = null;
        OnPollError = null;
    }
}

/// <summary>
/// The hub: the board this PC shares, driven through one
/// <see cref="CommandRouter"/>, plus the registry, snapshot cache, notify
/// relay and poll scheduler, and the sessions (the local UI and remote
/// clients) that use it. The local UI is a session like any other, so
/// ordering, attribution and locks are the same for everyone. Shares the one
/// board the local UI has open, at its registry handle (N8). Port of the
/// macOS Console's LinkHub, USB-free: the board arrives as an
/// <see cref="IHubDevice"/> and its notifications through <see cref="Ingest"/>.
/// </summary>
public sealed class LinkHub : IDisposable
{
    private readonly object _deviceLock = new();
    private readonly object _sessionLock = new();

    private IHubDevice? _device;
    private CommandRouter? _router;
    private PollScheduler? _scheduler;
    private byte _attachedHandle;

    private readonly Dictionary<ushort, LinkHubSession> _sessions = new();
    private ushort _nextSessionId = 2;

    private readonly SnapshotCache _snapshot = new();
    private readonly NotifyRelay _relay = new();
    private readonly Timer _reaper;
    private int _snapshotRefreshPending;

    /// <summary>How long after a write a host-sourced notification is
    /// attributed to its writer. USB round trips are milliseconds.</summary>
    public static readonly TimeSpan AttributionWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest lock a session may take (spec 7.7).</summary>
    public static readonly TimeSpan MaxLockTimeout = TimeSpan.FromSeconds(60);

    public LinkPolicy Policy { get; }
    public LinkAuthStore Auth { get; }
    public DeviceRegistry Registry { get; }

    /// <summary>Router timeouts; tests shorten them.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BulkTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The wait before re-reading the snapshot after a
    /// BULK_INVALIDATED, so the read doesn't race the firmware's own settle.</summary>
    public TimeSpan SnapshotRefreshDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>A device was attached or detached.</summary>
    public event EventHandler<bool>? AttachedChanged;
    /// <summary>Any device or session change, for the advertisement and the
    /// settings page's status line.</summary>
    public event EventHandler? Changed;

    public LinkHub(LinkPolicy policy, LinkAuthStore auth, DeviceRegistry registry)
    {
        Policy = policy;
        Auth = auth;
        Registry = registry;
        Registry.DeviceEvent += OnRegistryEvent;
        // A revoked client is cut off at once; a re-roled one is re-roled at
        // once (the router reads the role on every command).
        Auth.ClientRevoked += (_, cid) => CloseSessions(cid);
        Auth.ClientRoleChanged += (_, e) => SetRole(e.Cid, e.Role);
        Auth.AuthModeChanged += (_, mode) => AuthModeChanged(mode);
        // Devices past their offline grace leave the registry.
        // Devices past their offline grace leave the registry, and a lock that
        // ran out stops showing in locked_by (the router lets it lapse quietly).
        _reaper = new Timer(_ =>
        {
            Registry.ReapOffline();
            if (CurrentRouter is { } router) Registry.SetLockHolder(router.Handle, router.CurrentLockHolder);
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public bool IsDeviceAttached
    {
        get { lock (_deviceLock) return _device != null; }
    }

    /// <summary>The attached device's handle (0 with none): the handle
    /// clients and the local transport address.</summary>
    public byte CurrentHandle
    {
        get { lock (_deviceLock) return _attachedHandle; }
    }

    public int SessionCount
    {
        get { lock (_sessionLock) return _sessions.Count; }
    }

    private readonly DateTime _started = DateTime.UtcNow;

    /// <summary>For hub.stats.</summary>
    public TimeSpan Uptime => DateTime.UtcNow - _started;

    /// <summary>Paired clients with a live session (auth.list "online").</summary>
    public IReadOnlySet<int> OnlineClientIds()
    {
        lock (_sessionLock) return _sessions.Values.Where(s => s.ClientId != null).Select(s => s.ClientId!.Value).ToHashSet();
    }

    /// <summary>Remote sessions: everyone but the local UI.</summary>
    public int RemoteSessionCount
    {
        get { lock (_sessionLock) return _sessions.Keys.Count(id => id != LinkSessionIds.Local); }
    }

    // ── Device lifecycle ──

    /// <summary>Attaches the board: builds its router and scheduler, registers
    /// it, then warms the snapshot through the router. Replaces a board
    /// already attached (it goes offline, and every session gets a RESYNC).</summary>
    public void AttachDevice(IHubDevice device)
    {
        DetachDevice();
        var registered = Registry.DeviceOnline(device.Info);
        var router = new CommandRouter(registered.Handle, device, Policy) { CommandTimeout = CommandTimeout, BulkTimeout = BulkTimeout };
        // Polls take their turn in the router like any command, so one can
        // never land between the chunks of a bulk read (unlike the Mac).
        var scheduler = new PollScheduler(
            request => router.Run(request, InternalSession, BulkTimeout + TimeSpan.FromSeconds(1)),
            Policy,
            DeliverPoll,
            DeliverPollError);
        router.OtherSessionPolls = (codes, excluding) => scheduler.SessionsSubscribed(codes, excluding);
        lock (_deviceLock)
        {
            _device = device;
            _router = router;
            _scheduler = scheduler;
            _attachedHandle = registered.Handle;
            _lastSeq = null;
        }
        AttachedChanged?.Invoke(this, true);
        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(WarmSnapshot);

        // An accepted write retires the stored name at once, so adoption runs
        // at most once even if the confirming notify is lost.
        if (Registry.AdoptionName(device.Info) is { } name)
        {
            var serial = device.Info.Serial;
            WriteBoardName(name, null, status =>
            {
                if (status == LinkStatus.Ok) Registry.AdoptionAccepted(serial);
            });
        }
    }

    /// <summary>Detaches the board. A no-op with none attached, so a stray
    /// disconnect doesn't send every session a RESYNC for nothing.</summary>
    public void DetachDevice()
    {
        IHubDevice? device;
        CommandRouter? router;
        PollScheduler? scheduler;
        byte handle;
        lock (_deviceLock)
        {
            if (_device == null) return;
            device = _device;
            router = _router;
            scheduler = _scheduler;
            handle = _attachedHandle;
            _device = null;
            _router = null;
            _scheduler = null;
            _attachedHandle = 0;
        }
        scheduler?.Stop();
        // Queued commands answer NO_DEVICE, so a local GET blocked on one
        // returns instead of waiting on a router nobody will run.
        router?.Dispose();
        Registry.DeviceOffline(device.Info.Serial);
        _snapshot.Invalidate();
        AttachedChanged?.Invoke(this, false);
        Changed?.Invoke(this, EventArgs.Empty);
        _relay.ResyncAll(handle, (byte)LinkResyncReason.DeviceReattached);
    }

    private CommandRouter? CurrentRouter
    {
        get { lock (_deviceLock) return _router; }
    }

    private PollScheduler? CurrentScheduler
    {
        get { lock (_deviceLock) return _scheduler; }
    }

    private static readonly RouterSession InternalSession = new(LinkSessionIds.Internal, LinkRole.Admin, ExemptFromInflightCap: true);

    // ── Notifications ──

    /// <summary>One notification from the board: attributes it, patches the
    /// cache, relays it to the remote sessions. Returns the origin, for the
    /// local UI's own copy. Only v2 packets are relayed (spec 8.3); the
    /// local UI also gets keep-alives and v1 packets for the Interrupt
    /// Monitor.</summary>
    public ushort Ingest(LinkNotification note)
    {
        // The USB reader only knows a packet was host-written; which
        // session's write it was, only the router knows.
        ushort origin = LinkNotifyAttribution.IsHostSourced(note.Packet)
            ? CurrentRouter?.ConsumeAttribution(AttributionWindow) ?? 0
            : LinkNotification.Unattributed;
        if (note.Version != 0x02 || note.EventId == IdleEvent) return origin;

        // The firmware numbers every v2 event as it queues it; a jump means
        // its queue overflowed and dropped some, so the cache missed changes
        // (clients see the same gap in the relayed packets and re-read).
        bool lost = false;
        if (note.Packet.Length > 3)
        {
            byte seq = note.Packet[3];
            lock (_deviceLock)
            {
                lost = _lastSeq is { } last && seq != (byte)(last + 1);
                _lastSeq = seq;
            }
        }

        if (lost)
        {
            _snapshot.Invalidate();
            RefreshSnapshotSoon();
        }
        if (note.EventId == BulkInvalidated)
        {
            _snapshot.Invalidate();
            RefreshSnapshotSoon();
        }
        else if (note.EventId == DeviceNameEvent)
        {
            NoteBoardName(note.Packet);
        }
        else
        {
            _snapshot.ApplyParamChange(note.Packet);
        }
        _relay.Publish(CurrentHandle, note with { Origin = origin });
        return origin;
    }

    private const byte BulkInvalidated = 0x03;
    private const byte IdleEvent = 0x00;
    /// <summary>The attached board's last notification seq; null after attach.</summary>
    private byte? _lastSeq;
    private const byte DeviceNameEvent = 0x10;

    /// <summary>The 0x10 event carries the new name in a 32-byte field at 8.</summary>
    private void NoteBoardName(byte[] packet)
    {
        if (packet.Length < 8 + Models.DeviceNameText.FieldLength) return;
        var name = Models.DeviceNameText.Decode(packet.AsSpan(8, Models.DeviceNameText.FieldLength));
        string? serial;
        lock (_deviceLock) serial = _device?.Info.Serial;
        if (serial != null) Registry.BoardNameStored(serial, name);
    }

    // ── Device names (spec 7.4) ──

    /// <summary><c>device.rename</c>: on name-capable firmware, writes the
    /// board (its notify then updates the registry); otherwise keeps a
    /// hub-stored name. An unknown handle, or a name-capable board that isn't
    /// attached, answers NO_DEVICE: its own name would override a stored one
    /// when it came back.</summary>
    public void RenameDevice(byte handle, string name, ushort sessionId, Action<LinkStatus> completion)
    {
        if (Registry.Device(handle) is not { } known)
        {
            completion(LinkStatus.NoDevice);
            return;
        }
        bool attached;
        lock (_deviceLock) attached = _device != null && _attachedHandle == handle;
        if (known.Info.BoardName != null)
        {
            if (!attached) completion(LinkStatus.NoDevice);
            else WriteBoardName(Models.DeviceNameText.BoardName(name), sessionId, completion);
            return;
        }
        completion(Registry.Rename(handle, name) == DeviceRenameOutcome.Renamed ? LinkStatus.Ok : LinkStatus.NoDevice);
    }

    /// <summary>REQ_SET_DEVICE_NAME through the router, as a session or as
    /// the hub itself (null) for adoption.</summary>
    private void WriteBoardName(string ascii, ushort? sessionId, Action<LinkStatus> completion)
    {
        var request = new LinkCmdRequest(0, CurrentHandle, LinkDirection.Set, SetDeviceName, 0, 2,
            payload: Models.DeviceNameText.Encode(ascii));
        if (sessionId is { } id)
        {
            Submit(request, id, r => completion(r.Status));
            return;
        }
        if (CurrentRouter is not { } router)
        {
            completion(LinkStatus.NoDevice);
            return;
        }
        router.Submit(request, InternalSession, r => completion(r.Response.Status));
    }

    private const byte SetDeviceName = 0x56;
    private const byte GetAllParams = 0xA0;

    // ── Snapshot (spec 7.5) ──

    /// <summary>Reads the bulk blob through the router as the hub's own
    /// session, so it takes its turn and respects locks like any command.</summary>
    private void WarmSnapshot()
    {
        // Only clients read it: with none, the local UI isn't made to wait
        // behind a whole-blob read nobody will use.
        if (RemoteSessionCount == 0 || CurrentRouter is not { } router) return;
        var request = new LinkCmdRequest(0, CurrentHandle, LinkDirection.Get, GetAllParams, 0, 2, ushort.MaxValue);
        // Changes landing while the (chunked) read runs are replayed onto it;
        // only an invalidation in between throws it away.
        var started = _snapshot.BeginRead();
        router.Submit(request, InternalSession, r =>
        {
            if (r.Response.Status != LinkStatus.Ok || r.Response.Payload.Length == 0)
            {
                LinkLog.Write($"hub: snapshot read answered {r.Response.Status}");
                return;
            }
            // Refused because the blob was invalidated while it was read: read
            // again, or the cache would stay empty until the next change.
            if (!_snapshot.SetBulk(r.Response.Payload, started)) RefreshSnapshotSoon();
        });
    }

    /// <summary>A moment after an invalidation, read again; a burst of
    /// invalidations costs one read.</summary>
    private void RefreshSnapshotSoon()
    {
        if (Interlocked.Exchange(ref _snapshotRefreshPending, 1) == 1) return;
        _ = Task.Delay(SnapshotRefreshDelay).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _snapshotRefreshPending, 0);
            WarmSnapshot();
        }, TaskScheduler.Default);
    }

    public DeviceSnapshot? CurrentSnapshot() => _snapshot.Snapshot();

    // ── Sessions ──

    /// <summary>Registers a session. The local UI passes
    /// <see cref="LinkSessionIds.Local"/>; remote sessions take the next free id.</summary>
    public LinkHubSession OpenSession(LinkRole role, ushort? id = null, int? clientId = null)
    {
        LinkHubSession session;
        lock (_sessionLock)
        {
            ushort sid = id ?? AllocateSessionId();
            session = new LinkHubSession(sid, role, clientId);
            _sessions[sid] = session;
        }
        // The local UI gets its notifications straight from Ingest's caller.
        if (session.Id != LinkSessionIds.Local)
        {
            _relay.AddSession(session.Id, frame => session.OnNotify?.Invoke(frame), frame => session.OnResync?.Invoke(frame));
            // The first client: warm the snapshot it will likely ask for.
            if (_snapshot.Snapshot() == null) _ = Task.Run(WarmSnapshot);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return session;
    }

    /// <summary>The counter wraps and one socket can re-authenticate without
    /// end, so skip the reserved ids and any still open. Caller holds the lock.</summary>
    private ushort AllocateSessionId()
    {
        while (true)
        {
            ushort n = _nextSessionId;
            _nextSessionId = (ushort)(_nextSessionId + 1);
            if (n is LinkSessionIds.None or LinkSessionIds.Local or LinkSessionIds.Internal) continue;
            if (!_sessions.ContainsKey(n)) return n;
        }
    }

    /// <summary>Tests start the counter near the wrap.</summary>
    internal ushort NextSessionId
    {
        set { lock (_sessionLock) _nextSessionId = value; }
    }

    public void CloseSession(ushort id)
    {
        LinkHubSession? closing;
        lock (_sessionLock)
        {
            if (!_sessions.Remove(id, out closing)) return;
        }
        // The callbacks capture the connection; clearing them lets it go.
        closing.Clear();
        _relay.RemoveSession(id);
        CurrentScheduler?.UnsubscribeAll(id);
        var router = CurrentRouter;
        router?.SessionDidClose(id);
        Registry.SetLockHolder(CurrentHandle, router?.CurrentLockHolder);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes every session that authenticated as this client,
    /// telling each so its socket closes with the revocation code.</summary>
    public void CloseSessions(int clientId)
    {
        List<LinkHubSession> victims;
        lock (_sessionLock) victims = _sessions.Values.Where(s => s.ClientId == clientId).ToList();
        foreach (var s in victims)
        {
            var tell = s.OnClosedByHub;   // CloseSession clears it
            CloseSession(s.Id);
            tell?.Invoke();
        }
    }

    /// <summary>A role change applies from the next command. A session made a
    /// viewer gives up any lock it holds: it can't write, so its lock would
    /// only keep everyone else out. Under open access every session stays an
    /// admin (spec 6.1); the stored role takes effect when pairing is turned
    /// back on.</summary>
    public void SetRole(int clientId, LinkRole role)
    {
        if (Auth.AuthMode == LinkAuthMode.None) return;
        List<ushort> affected;
        lock (_sessionLock)
        {
            affected = _sessions.Values.Where(s => s.ClientId == clientId).Select(s => s.Id).ToList();
            foreach (var s in _sessions.Values.Where(s => s.ClientId == clientId)) s.Role = role;
        }
        if (role == LinkRole.Viewer)
            foreach (var id in affected) ReleaseLock(id);
    }

    /// <summary>
    /// Require Pairing turned on: the sessions that came in under open access
    /// (no paired client behind them) are closed with 4002, so they pair to
    /// come back; otherwise a stranger let in while it was open would stay an
    /// admin. A paired session goes back to its client's stored role, which
    /// open access had raised to admin. Turned off: every session is an admin
    /// again (spec 6.1).
    /// </summary>
    private void AuthModeChanged(LinkAuthMode mode)
    {
        List<LinkHubSession> sessions;
        lock (_sessionLock) sessions = _sessions.Values.Where(s => s.Id != LinkSessionIds.Local).ToList();
        if (mode == LinkAuthMode.None)
        {
            foreach (var s in sessions) s.Role = LinkRole.Admin;
            return;
        }
        foreach (var s in sessions)
        {
            if (s.ClientId is { } cid && Auth.Client(cid) is { } client)
            {
                s.Role = client.Role;
                if (client.Role == LinkRole.Viewer) ReleaseLock(s.Id);
                continue;
            }
            // Let in while open, or its client forgotten since.
            var tell = s.OnClosedByHub;   // CloseSession clears it
            CloseSession(s.Id);
            tell?.Invoke();
        }
    }

    private LinkHubSession? Session(ushort id)
    {
        lock (_sessionLock) return _sessions.GetValueOrDefault(id);
    }

    // ── Commands ──

    /// <summary>One command for a session. The completion runs on any thread.
    /// A command for a handle the hub doesn't have, or with no board
    /// attached, answers NO_DEVICE.</summary>
    public void Submit(LinkCmdRequest request, ushort sessionId, Action<LinkCmdResponse> completion)
    {
        CommandRouter? router;
        byte handle;
        lock (_deviceLock)
        {
            router = _router;
            handle = _attachedHandle;
        }
        if (Session(sessionId) is not { } session || router == null || request.Handle != handle)
        {
            if (sessionId != LinkSessionIds.Local && Interlocked.Increment(ref _noDeviceLogged) <= 10)
                LinkLog.Write($"hub: session {sessionId} 0x{request.BRequest:X2} for handle {request.Handle}: " +
                              (Session(sessionId) == null ? "no such session" : router == null ? "no board attached" : $"the attached board is handle {handle}"));
            completion(new LinkCmdResponse(request.Tag, LinkStatus.NoDevice));
            return;
        }
        // The local UI fires bursts (a preset apply is dozens of writes) and
        // can't retry a RATE_LIMITED, so it isn't capped.
        var routed = new RouterSession(session.Id, session.Role, ExemptFromInflightCap: session.Id == LinkSessionIds.Local);
        router.Submit(request, routed, r => completion(r.Response));
    }

    private int _noDeviceLogged;

    // ── Polls and locks ──

    public IReadOnlyList<LinkPollGrant> SubscribePolls(ushort sessionId, byte handle, IReadOnlyList<LinkPollSpec> polls)
    {
        if (Session(sessionId) is not { } session || CurrentScheduler is not { } scheduler || handle != CurrentHandle)
            return polls.Select(p => new LinkPollGrant(p.Slot, 0)).ToList();
        var requests = polls.Select(p => new PollScheduler.PollRequest(p.Slot,
            new PollScheduler.PollSpec(handle, (byte)p.Req, (ushort)p.Val, (ushort)p.Idx, (ushort)p.Len), p.Hz)).ToList();
        return scheduler.Subscribe(sessionId, session.Role, requests)
            .Select(g => new LinkPollGrant(g.Slot, (int)Math.Round(g.Hz))).ToList();
    }

    public void UnsubscribePolls(ushort sessionId, IReadOnlyList<int> slots) =>
        CurrentScheduler?.Unsubscribe(sessionId, slots);

    public bool AcquireLock(ushort sessionId, TimeSpan timeout)
    {
        if (CurrentRouter is not { } router) return false;
        if (timeout > MaxLockTimeout) timeout = MaxLockTimeout;
        bool granted = router.AcquireLock(sessionId, timeout);
        if (granted) Registry.SetLockHolder(router.Handle, sessionId);
        return granted;
    }

    public void ReleaseLock(ushort sessionId)
    {
        if (CurrentRouter is not { } router) return;
        router.ReleaseLock(sessionId);
        Registry.SetLockHolder(router.Handle, router.CurrentLockHolder);
    }

    // ── Fan-out ──

    /// <summary>A POLL frame's tag is the session's own poll sequence (spec 8.4).</summary>
    private void DeliverPoll(ushort sessionId, int slot, byte handle, byte[] payload)
    {
        if (Session(sessionId) is not { } session) return;
        session.OnPoll?.Invoke(new LinkPollFrame(session.NextPollTag(), handle, (byte)slot, payload));
    }

    private void DeliverPollError(ushort sessionId, int slot, byte handle, LinkStatus status) =>
        Session(sessionId)?.OnPollError?.Invoke(new LinkPollError(handle, slot, LinkErrorCode.Busy, $"Poll failing ({status})"));

    private void OnRegistryEvent(object? sender, DeviceRegistryEvent e)
    {
        List<LinkHubSession> all;
        lock (_sessionLock) all = _sessions.Values.ToList();
        foreach (var s in all)
        {
            try { s.OnDeviceEvent?.Invoke(e); }
            catch (Exception ex) { LinkLog.Write($"hub: device event threw {ex.GetType().Name}"); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _reaper.Dispose();
        DetachDevice();
        _relay.Dispose();
    }
}
