using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSPiConsole.Core.Link.Hub;

/// <summary>
/// The hub's side of one connection: takes the messages the server hands it
/// (a JSON text frame, or a binary data-plane frame), authenticates, then
/// drives the <see cref="LinkHub"/> as one session and turns hub events back
/// into outbound frames. No socket in it, so it's unit-tested by feeding
/// input and reading what it emits (spec 5-8). Port of the macOS Console's
/// LinkSessionHandler.
/// </summary>
public sealed class LinkSessionHandler : ILinkConnection
{
    private readonly LinkHub _hub;
    private readonly string _peer;
    private readonly Action<LinkOutbound> _emit;
    private readonly object _lock = new();
    private LinkHubSession? _session;

    /// <summary>The largest command payload accepted (at least 8192 so the
    /// bulk blob fits), and the largest reassembled WebSocket message; both
    /// advertised in hello.</summary>
    public const int MaxPayload = 8192;
    public const int MaxFrame = 65536;
    public const int MaxInflight = 8;
    public const int PollMaxHz = 20;
    public const int PollBudgetBps = 200000;

    /// <summary>The app's version, for hello.</summary>
    public static string AppVersion { get; set; } = "0";

    /// <summary>Whether the web UI ships (the "web" capability).</summary>
    public static bool WebBundlePresent { get; set; }

    public LinkSessionHandler(LinkHub hub, string peer, Action<LinkOutbound> emit)
    {
        _hub = hub;
        _peer = peer;
        _emit = emit;
    }

    /// <summary>The factory the server uses for each connection.</summary>
    public static LinkConnectionFactory Factory(LinkHub hub) => (peer, emit) => new LinkSessionHandler(hub, peer, emit);

    public bool IsAuthenticated
    {
        get { lock (_lock) return _session != null; }
    }

    private LinkHubSession? Current
    {
        get { lock (_lock) return _session; }
    }

    /// <summary>The live role, read from the hub session so a re-role takes
    /// effect at once; viewer until a session exists.</summary>
    private LinkRole Role => Current?.Role ?? LinkRole.Viewer;

    // ── Inbound ──

    /// <summary>A text frame. Malformed JSON closes with 4000; a known type
    /// with a bad field answers bad_request under its id.</summary>
    public void ReceiveText(string json)
    {
        LinkMessage message;
        try
        {
            message = LinkMessage.Decode(json);
        }
        catch (LinkMessageException e) when (e.RequestId != null && e.Type != "hello")
        {
            Send(new LinkErr(LinkErrorCode.BadRequest, e.RequestId, "Malformed message"));
            return;
        }
        catch (JsonException)
        {
            _emit(new LinkOutbound.Close(LinkCloseCode.ProtocolError));
            return;
        }
        Dispatch(message);
    }

    /// <summary>A binary frame. Nothing before auth (dropped, spec 5); an
    /// unknown request type gets UNSUPPORTED under its own tag (spec 8).</summary>
    public void ReceiveBinary(byte[] data)
    {
        if (Current is not { } session) return;
        LinkFrame frame;
        try
        {
            frame = LinkFrame.Decode(data);
        }
        catch (LinkFrameException e) when (e.Kind == LinkFrameErrorKind.UnknownType && e.Value < 0x80 && e.Tag is { } tag)
        {
            EmitFrame(new LinkCmdResponse(tag, LinkStatus.Unsupported));
            return;
        }
        catch (LinkFrameException e) when (e.FrameType == (byte)LinkFrameType.CmdRequest && e.Tag is { } tag)
        {
            EmitFrame(new LinkCmdResponse(tag, LinkStatus.BadFrame));
            return;
        }
        catch (LinkFrameException)
        {
            return;   // undecodable and unanswerable: dropped
        }

        switch (frame)
        {
            case LinkCmdRequest request:
                HandleCommand(request, session);
                break;
            case LinkFwDataFrame chunk:
                // No fw.install here (N6), so no transfer can be open.
                EmitFrame(new LinkFwDataAck(chunk.Tag, chunk.Offset, LinkStatus.Unsupported));
                break;
            default:
                break;   // hub-to-client types from a client: ignored
        }
    }

    /// <summary>The connection closed: the hub session and everything it
    /// held (lock, polls) go with it.</summary>
    public void ConnectionClosed()
    {
        LinkHubSession? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
        }
        if (session != null) _hub.CloseSession(session.Id);
    }

    // ── Dispatch ──

    private void Dispatch(LinkMessage message)
    {
        switch (message)
        {
            case LinkHelloClient hello:
                HandleHello(hello);
                return;
            case LinkAuthToken m:
                HandleAuthToken(m);
                return;
            case LinkAuthPair m:
                HandleAuthPair(m);
                return;
        }
        if (!IsAuthenticated)
        {
            // Events carry no id and want no answer.
            if (message.RequestId is { } id) Send(new LinkErr(LinkErrorCode.Unauthenticated, id));
            return;
        }
        switch (message)
        {
            case LinkDeviceListRequest m: HandleDeviceList(m); break;
            case LinkDeviceSnapshotRequest m: HandleSnapshot(m); break;
            case LinkDeviceRename m: HandleRename(m); break;
            case LinkPollSubscribe m: HandlePollSubscribe(m); break;
            case LinkPollUnsubscribe m: HandlePollUnsubscribe(m); break;
            case LinkLockAcquire m: HandleLockAcquire(m); break;
            case LinkLockRelease m: HandleLockRelease(m); break;
            case LinkAuthList m: HandleAuthList(m); break;
            case LinkAuthRevoke m: HandleAuthRevoke(m); break;
            case LinkAuthSetRole m: HandleAuthSetRole(m); break;
            case LinkHubStatsRequest m: HandleHubStats(m); break;
            case LinkHubRename m: HandleHubRename(m); break;
            default:
                // Unknown types, and the 1.2 admin messages this hub doesn't
                // implement (no hub_config etc. in its caps).
                if (message.RequestId is { } id) Send(new LinkErr(LinkErrorCode.UnknownType, id));
                break;
        }
    }

    private void HandleHello(LinkHelloClient hello)
    {
        LinkLog.Write($"hub: hello from {hello.Client.Name} ({hello.Client.App} {hello.Client.Version}, proto {hello.Version.Major}.{hello.Version.Minor}) at {_peer}");
        var auth = _hub.Auth;
        var mode = auth.AuthMode;
        Send(new LinkHelloHub(
            new LinkHubInfo(auth.HubId.ToString().ToLowerInvariant(), auth.HubName, LinkHubKind.Console, AppVersion),
            mode,
            Capabilities,
            Limits: new LinkLimits(MaxFrame, MaxPayload, MaxInflight, PollMaxHz, PollBudgetBps),
            PinSourceWire: mode == LinkAuthMode.Pin ? auth.PinSource : null));
        // Open access: every connection is an admin session, opened with the
        // hello (spec 6.1). A client that wants its session id may still send
        // auth.pair and gets the usual ok.
        if (mode == LinkAuthMode.None && !IsAuthenticated)
            OpenSession(null, LinkRole.Admin, null, null);
    }

    private static IReadOnlyList<string> Capabilities
    {
        get
        {
            var caps = new List<string>
            {
                LinkCapability.Cmd, LinkCapability.Notify, LinkCapability.Poll,
                LinkCapability.Snapshot, LinkCapability.Lock, LinkCapability.Rename,
            };
            if (WebBundlePresent) caps.Add(LinkCapability.Web);
            return caps;
        }
    }

    // ── Auth (spec 6, 7.3) ──

    private void HandleAuthToken(LinkAuthToken m)
    {
        var result = _hub.Auth.Authenticate(m.Token, _peer);
        if (result.Client is { } client) OpenSession(client.Cid, client.Role, m.Id, null);
        else AuthFailed(result.Failure, m.Id);
    }

    private void HandleAuthPair(LinkAuthPair m)
    {
        // Open access: no PIN, every connection is admin (spec 6.1).
        if (_hub.Auth.AuthMode == LinkAuthMode.None)
        {
            OpenSession(null, LinkRole.Admin, m.Id, null);
            return;
        }
        var result = _hub.Auth.Pair(m.Pin, m.Name, m.Role ?? LinkRole.Control, _peer);
        if (result.Client is { } client) OpenSession(client.Cid, client.Role, m.Id, result.Token);
        else AuthFailed(result.Failure, m.Id);
    }

    /// <summary>Answers the failure; once the address has reached the failure
    /// limit the connection closes with 4001 (spec 5).</summary>
    private void AuthFailed(LinkAuthFailure? failure, long id)
    {
        Send(new LinkErr((failure ?? LinkAuthFailure.UnknownToken).ToErrorCode(), id));
        if (!IsAuthenticated && _hub.Auth.IsBlocked(_peer)) _emit(new LinkOutbound.Close(LinkCloseCode.AuthFailed));
    }

    /// <summary>Opens the session this connection drives. A second
    /// authentication on one connection closes the first session, so its
    /// polls, lock and callbacks don't outlive it. <paramref name="reply"/>
    /// is the request to answer ok, or null when the session opens silently
    /// (open access, on hello).</summary>
    private void OpenSession(int? clientId, LinkRole role, long? reply, string? token)
    {
        LinkHubSession? old;
        lock (_lock) old = _session;
        if (old != null) _hub.CloseSession(old.Id);

        var session = _hub.OpenSession(role, clientId: clientId);
        Wire(session);
        lock (_lock) _session = session;
        // A revoke or role change can land between checking the token and
        // registering the session, and would miss it: look again now that the
        // hub can reach it.
        if (clientId is { } cid)
        {
            if (_hub.Auth.Client(cid) is not { } current)
            {
                lock (_lock) if (ReferenceEquals(_session, session)) _session = null;
                _hub.CloseSession(session.Id);
                if (reply is { } rid) Send(new LinkErr(LinkErrorCode.Unauthenticated, rid));
                return;
            }
            // Under open access every session is an admin (spec 6.1), even
            // one that presented its token.
            session.Role = role = _hub.Auth.AuthMode == LinkAuthMode.None ? LinkRole.Admin : current.Role;
        }
        LinkLog.Write($"hub: session {session.Id} ({role}) for {_peer}");
        if (reply is { } id)
        {
            var policy = LinkPolicyDescriptor.From(_hub.Policy.Denied(role));
            Send(LinkOk.WithBody(id, new LinkAuthOkBody(session.Id, role, token, policy)));
        }
    }

    private void Wire(LinkHubSession session)
    {
        // Revoked while connected: the hub has closed the session; drop it so
        // nothing routes, and close with 4002.
        session.OnClosedByHub = () =>
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_session, session)) return;
                _session = null;
            }
            _emit(new LinkOutbound.Close(LinkCloseCode.TokenRevoked));
        };
        session.OnNotify = frame => EmitFrame(frame);
        session.OnResync = frame => EmitFrame(frame);
        session.OnPoll = frame => EmitFrame(frame);
        session.OnDeviceEvent = HandleDeviceEvent;
        session.OnPollError = error => Send(error);
    }

    // ── Commands (spec 8.2) ──

    private void HandleCommand(LinkCmdRequest request, LinkHubSession session)
    {
        if (request.Payload.Length > MaxPayload || request.WLength > MaxPayload)
        {
            NoteFailure(request, LinkStatus.TooLarge);
            EmitFrame(new LinkCmdResponse(request.Tag, LinkStatus.TooLarge));
            return;
        }
        _hub.Submit(request, session.Id, response =>
        {
            if (response.Status != LinkStatus.Ok) NoteFailure(request, response.Status);
            EmitFrame(response);
        });
    }

    private int _failuresLogged;

    /// <summary>A refused or failed command, logged (the first few per
    /// connection), so a client that gives up says why in link.log.</summary>
    private void NoteFailure(LinkCmdRequest request, LinkStatus status)
    {
        if (Interlocked.Increment(ref _failuresLogged) > 20) return;
        LinkLog.Write($"hub: session {Current?.Id} {(request.Direction == LinkDirection.Get ? "GET" : "SET")} 0x{request.BRequest:X2} " +
                      $"wValue {request.WValue} wLength {request.WLength} answered {status}");
    }

    // ── Devices (spec 7.4, 7.5) ──

    private void HandleDeviceList(LinkDeviceListRequest m) =>
        Send(LinkOk.WithBody(m.Id, new LinkDeviceListBody(_hub.Registry.Devices.Select(d => d.ToLinkDeviceInfo()).ToList())));

    private void HandleSnapshot(LinkDeviceSnapshotRequest m)
    {
        if (m.Handle != _hub.CurrentHandle || !_hub.IsDeviceAttached || _hub.CurrentSnapshot() is not { } snapshot)
        {
            LinkLog.Write($"hub: no snapshot for handle {m.Handle} (attached {_hub.IsDeviceAttached}, handle {_hub.CurrentHandle})");
            Send(new LinkErr(LinkErrorCode.NoDevice, m.Id));
            return;
        }
        int age = (int)Math.Max(0, snapshot.Age.TotalMilliseconds);
        Send(LinkOk.WithBody(m.Id, new LinkSnapshotBody(m.Handle, snapshot.WireVersion, age,
            Convert.ToBase64String(snapshot.Bulk),
            snapshot.Status is { } status ? Convert.ToBase64String(status) : null)));
    }

    private void HandleRename(LinkDeviceRename m)
    {
        if (Role == LinkRole.Viewer || Current is not { } session)
        {
            Send(new LinkErr(LinkErrorCode.Denied, m.Id));
            return;
        }
        if (m.Handle is < 0 or > 255)
        {
            Send(new LinkErr(LinkErrorCode.NoDevice, m.Id));
            return;
        }
        _hub.RenameDevice((byte)m.Handle, m.Name, session.Id, status => Send(status switch
        {
            LinkStatus.Ok => new LinkOk(m.Id),
            LinkStatus.Locked => new LinkErr(LinkErrorCode.Locked, m.Id),
            LinkStatus.NoDevice => new LinkErr(LinkErrorCode.NoDevice, m.Id),
            LinkStatus.Denied => new LinkErr(LinkErrorCode.Denied, m.Id),
            LinkStatus.Busy or LinkStatus.RateLimited => new LinkErr(LinkErrorCode.Busy, m.Id),
            _ => new LinkErr(LinkErrorCode.Internal, m.Id),
        }));
    }

    private void HandleDeviceEvent(DeviceRegistryEvent e)
    {
        switch (e)
        {
            case DeviceRegistryEvent.Added a:
                Send(new LinkDeviceAdded(a.Device.ToLinkDeviceInfo()));
                break;
            case DeviceRegistryEvent.Changed c:
                Send(new LinkDeviceChanged(c.Device.ToLinkDeviceInfo()));
                break;
            case DeviceRegistryEvent.Removed r:
                Send(new LinkDeviceRemoved(r.Handle, r.Serial));
                break;
        }
    }

    // ── Polls (spec 7.6) ──

    private void HandlePollSubscribe(LinkPollSubscribe m)
    {
        if (Current is not { } session) return;
        if (m.Handle is < 0 or > 255)
        {
            Send(new LinkErr(LinkErrorCode.NoDevice, m.Id));
            return;
        }
        if (m.Polls.Any(p => p.Slot is < 0 or > PollScheduler.MaxSlot))
        {
            Send(new LinkErr(LinkErrorCode.BadRequest, m.Id, "slot must be 0..15"));
            return;
        }
        if (m.Polls.Any(p => p.Len is < 0 or > MaxPayload || p.Req is < 0 or > 255 || p.Val is < 0 or > 0xFFFF || p.Idx is < 0 or > 0xFFFF))
        {
            Send(new LinkErr(LinkErrorCode.BadRequest, m.Id, "poll out of range"));
            return;
        }
        var granted = _hub.SubscribePolls(session.Id, (byte)m.Handle, m.Polls);
        Send(LinkOk.WithBody(m.Id, new LinkPollSubscribeBody(granted)));
    }

    private void HandlePollUnsubscribe(LinkPollUnsubscribe m)
    {
        if (Current is not { } session) return;
        _hub.UnsubscribePolls(session.Id, m.Slots);
        Send(new LinkOk(m.Id));
    }

    // ── Locks (spec 7.7) ──

    private void HandleLockAcquire(LinkLockAcquire m)
    {
        if (Current is not { } session) return;
        // A viewer can't write, so its lock would only block everyone else.
        if (Role == LinkRole.Viewer)
        {
            Send(new LinkErr(LinkErrorCode.Denied, m.Id));
            return;
        }
        int requested = m.TimeoutMs ?? 10000;
        if (requested < 0)
        {
            Send(new LinkErr(LinkErrorCode.BadRequest, m.Id, "timeout_ms must not be negative", "timeout_ms"));
            return;
        }
        if (m.Handle != _hub.CurrentHandle || !_hub.IsDeviceAttached)
        {
            Send(new LinkErr(LinkErrorCode.NoDevice, m.Id));
            return;
        }
        int granted = Math.Min(requested, (int)LinkHub.MaxLockTimeout.TotalMilliseconds);
        if (_hub.AcquireLock(session.Id, TimeSpan.FromMilliseconds(granted)))
            Send(LinkOk.WithBody(m.Id, new LinkLockOkBody(granted)));
        else
            Send(new LinkErr(LinkErrorCode.Locked, m.Id));
    }

    private void HandleLockRelease(LinkLockRelease m)
    {
        if (Current is not { } session) return;
        _hub.ReleaseLock(session.Id);
        Send(new LinkOk(m.Id));
    }

    // ── Admin (spec 7.3, 7.9) ──

    private bool RequireAdmin(long id)
    {
        if (Role == LinkRole.Admin) return true;
        Send(new LinkErr(LinkErrorCode.Denied, id, "admin role required"));
        return false;
    }

    private void HandleAuthList(LinkAuthList m)
    {
        if (!RequireAdmin(m.Id)) return;
        var online = _hub.OnlineClientIds();
        var clients = _hub.Auth.Clients.Select(c => c.ToWire(online.Contains(c.Cid))).ToList();
        Send(LinkOk.WithBody(m.Id, new LinkAuthListBody(clients)));
    }

    private void HandleAuthRevoke(LinkAuthRevoke m)
    {
        if (!RequireAdmin(m.Id)) return;
        // Answer first: revoking our own client closes this connection.
        Send(new LinkOk(m.Id));
        _hub.Auth.Revoke(m.Cid);
    }

    private void HandleAuthSetRole(LinkAuthSetRole m)
    {
        if (!RequireAdmin(m.Id)) return;
        _hub.Auth.SetRole(m.Cid, m.Role);
        Send(new LinkOk(m.Id));
    }

    private void HandleHubStats(LinkHubStatsRequest m)
    {
        if (!RequireAdmin(m.Id)) return;
        Send(LinkOk.WithBody(m.Id, new LinkHubStatsBody(_hub.SessionCount, (int)_hub.Uptime.TotalSeconds)));
    }

    private void HandleHubRename(LinkHubRename m)
    {
        if (!RequireAdmin(m.Id)) return;
        var name = m.Name.Trim();
        if (name.Length == 0)
        {
            Send(new LinkErr(LinkErrorCode.BadRequest, m.Id, "name must not be empty", "name"));
            return;
        }
        _hub.Auth.HubName = name;
        Send(new LinkOk(m.Id));
    }

    // ── Out ──

    private void Send(LinkMessage message)
    {
        string json;
        try { json = message.Encode(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            LinkLog.Write($"hub: couldn't encode {message.TypeName}: {ex.GetType().Name}");
            if (message.RequestId is { } id) _emit(new LinkOutbound.Text(new LinkErr(LinkErrorCode.Internal, id).Encode()));
            return;
        }
        _emit(new LinkOutbound.Text(json));
    }

    private void EmitFrame(LinkFrame frame) => _emit(new LinkOutbound.Binary(frame.Encode()));
}
