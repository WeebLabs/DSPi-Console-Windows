using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Firmware;

/// <summary>Where an install is. Records, so an unchanged state is not republished.</summary>
public abstract record FirmwareInstallState
{
    private FirmwareInstallState() { }

    public sealed record Idle : FirmwareInstallState;
    /// <summary>Nothing in BOOTSEL yet.</summary>
    public sealed record WaitingForBoard : FirmwareInstallState;
    /// <summary>A board on the bus whose drive has not appeared. Ordinary for a
    /// second or two after it enumerates, so a wait, not a failure.</summary>
    public sealed record WaitingForVolume(BootloaderChip Chip) : FirmwareInstallState;
    /// <summary>Exactly one board, its drive mounted, ready to write when the user says so.</summary>
    public sealed record Ready(BootloaderBoard Board) : FirmwareInstallState;
    /// <summary>Writing, with the fraction of the image sent so far.</summary>
    public sealed record Writing(double Fraction) : FirmwareInstallState;
    /// <summary>The bytes are out and the board is restarting.</summary>
    public sealed record WaitingForDevice : FirmwareInstallState;
    public sealed record Verified(FirmwareVersion Version) : FirmwareInstallState;
    public sealed record Failed(FirmwareInstallError Error) : FirmwareInstallState;
}

/// <summary>
/// Detects a board in BOOTSEL, writes the bundled UF2 to it, and confirms the
/// device came back running what was written. Deliberately does none of the
/// deciding: it never flashes on its own, never chooses between two boards, and
/// never touches presets; callers own the confirmation. Port of the macOS
/// Console's FirmwareInstaller.
/// <para>Call it on the UI thread. The write and the wait for the device run
/// on the thread pool and hand their states back through <c>post</c>.</para>
/// </summary>
public sealed class FirmwareInstaller
{
    /// <summary>Fraction of the image that must be out before a write error is
    /// read as the board restarting rather than a failure. The ROM loader
    /// restarts as it takes the last blocks, so the drive disappears under the
    /// final write; a flasher that reports that as an error is wrong.</summary>
    public const double RebootThreshold = 0.95;

    /// <summary>How long to wait for the board to come back after a write:
    /// the restart, re-enumeration, and the app's reconnect and version read.
    /// Generous on purpose: timing out after a flash that worked tells the user
    /// their device is broken when it is not.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Write chunk size: a typical image moves the bar a few dozen times.</summary>
    public const int WriteChunkSize = 16 * 1024;

    /// <summary>How long a board may sit on the bus with no drive before that
    /// is a failure. The drive always lags enumeration.</summary>
    public static readonly TimeSpan VolumeWaitTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Whether a write error means the board restarted (the ordinary
    /// ending) rather than that the write failed. Either signal is enough:
    /// nearly all the bytes are out, or the drive has actually gone.</summary>
    public static bool IsRebootSignal(double fractionWritten, bool driveStillPresent) =>
        fractionWritten >= RebootThreshold || !driveStillPresent;

    private readonly IBootloaderLocator _locator;
    private readonly IFirmwareVerifier _verifier;
    private readonly Func<BootloaderChip, FirmwareImage> _imageProvider;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _now;
    private readonly Func<string, bool> _driveExists;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Set once an install begins, cleared only by <see cref="Reset"/>.
    /// While set, detection leaves the state alone: mid-write because the board
    /// vanishing is the expected restart, afterwards because the outcome must
    /// stay on screen rather than be overwritten by the next scan.</summary>
    private bool _installing;
    private DateTime? _volumeWaitStarted;
    /// <summary>The user has committed to an update, so the next ready board is
    /// written without asking again. The board may be ready before the user
    /// commits or after; only one of those orders is a state change.</summary>
    private bool _armed;

    public FirmwareInstallState State { get; private set; } = new FirmwareInstallState.Idle();

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>The write and verification of the current install, for tests.</summary>
    public Task? CurrentRun { get; private set; }

    public bool IsArmed => _armed;

    public FirmwareInstaller(IBootloaderLocator locator, IFirmwareVerifier verifier,
        Func<BootloaderChip, FirmwareImage> imageProvider, Action<Action> post,
        Func<DateTime>? now = null, Func<string, bool>? driveExists = null)
    {
        _locator = locator;
        _verifier = verifier;
        _imageProvider = imageProvider;
        _post = post;
        _now = now ?? (() => DateTime.UtcNow);
        _driveExists = driveExists ?? Directory.Exists;
    }

    // ── Detection ──

    public void BeginWatching()
    {
        _locator.Changed += ApplyBoards;
        _locator.Start();
        ApplyBoards(_locator.CurrentBoards);
    }

    /// <summary>Stops detection, and abandons a wait for the device. A write
    /// in progress is never cut short.</summary>
    public void StopWatching()
    {
        _locator.Changed -= ApplyBoards;
        _locator.Stop();
        _stop.Cancel();
    }

    /// <summary>Returns a finished install to detection so another can run. The
    /// outcome is frozen so the user can read it; this is the one way back, and
    /// it clears the commitment too: a second board is a second decision.
    /// Refused mid-install.</summary>
    public void Reset()
    {
        if (State is FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice) return;
        _installing = false;
        _armed = false;
        _volumeWaitStarted = null;
        ApplyBoards(_locator.CurrentBoards);
    }

    private void ApplyBoards(IReadOnlyList<BootloaderBoard> boards)
    {
        if (_installing) return;
        FirmwareInstallState next;
        switch (boards.Count)
        {
            case 0:
                _volumeWaitStarted = null;
                next = new FirmwareInstallState.WaitingForBoard();
                break;
            case 1 when boards[0].DrivePath != null:
                _volumeWaitStarted = null;
                next = new FirmwareInstallState.Ready(boards[0]);
                break;
            case 1:
                // The drive mounts a moment after the board enumerates: wait,
                // and only call it a failure once it is clearly not coming.
                var started = _volumeWaitStarted ??= _now();
                next = _now() - started >= VolumeWaitTimeout
                    ? new FirmwareInstallState.Failed(FirmwareInstallError.VolumeNotMounted(boards[0].Chip.VolumeName()))
                    : new FirmwareInstallState.WaitingForVolume(boards[0].Chip);
                break;
            default:
                _volumeWaitStarted = null;
                next = new FirmwareInstallState.Failed(FirmwareInstallError.MultipleBoards(boards.Count));
                break;
        }
        SetState(next);
        // Committed before the board arrived: write it now that it is here.
        if (_armed && next is FirmwareInstallState.Ready ready) Install(ready.Board);
    }

    // ── Install ──

    /// <summary>Records the user's decision, and writes as soon as there is a
    /// board, immediately if one is ready. The only way callers should start an
    /// install: waiting for Ready to arrive misses the board plugged in first.</summary>
    public void InstallWhenReady()
    {
        _armed = true;
        if (State is FirmwareInstallState.Ready ready) Install(ready.Board);
    }

    private void Install(BootloaderBoard board)
    {
        if (_installing) return;
        // Consumed here, success or not: left standing, a failure before the
        // write would retrigger on every scan, and a later board would be
        // flashed on a decision made about an earlier one.
        _armed = false;
        // Frozen from the first possible failure, not the first byte, or the
        // next scan would overwrite a missing-image error with Ready.
        _installing = true;

        if (board.DrivePath is not { } drive)
        {
            SetState(new FirmwareInstallState.Failed(FirmwareInstallError.VolumeNotMounted(board.Chip.VolumeName())));
            return;
        }
        FirmwareImage image;
        try { image = _imageProvider(board.Chip); }
        catch (FirmwareInstallException e) { SetState(new FirmwareInstallState.Failed(e.Error)); return; }
        catch (Exception e) { SetState(new FirmwareInstallState.Failed(FirmwareInstallError.WriteFailed(e.Message))); return; }

        SetState(new FirmwareInstallState.Writing(0));
        var cancel = _stop.Token;
        CurrentRun = Task.Run(async () =>
        {
            try
            {
                WriteImage(image, drive, fraction => Post(new FirmwareInstallState.Writing(fraction)));
            }
            catch (FirmwareInstallException e) { Post(new FirmwareInstallState.Failed(e.Error)); return; }
            catch (Exception e) { Post(new FirmwareInstallState.Failed(FirmwareInstallError.WriteFailed(e.Message))); return; }

            Post(new FirmwareInstallState.WaitingForDevice());
            // The copy returning proves nothing. Success is the device coming
            // back and saying it runs what was written.
            FirmwareVersion? reported;
            try { reported = await _verifier.AwaitDeviceVersionAsync(VerifyTimeout, cancel); }
            catch (OperationCanceledException) { return; }
            if (reported is not { } version)
                Post(new FirmwareInstallState.Failed(FirmwareInstallError.DeviceDidNotReturn()));
            else if (version != image.Version)
                Post(new FirmwareInstallState.Failed(FirmwareInstallError.VersionMismatch(image.Version.ToString(), version.ToString())));
            else
                Post(new FirmwareInstallState.Verified(version));
        });
    }

    /// <summary>Streams the image onto the drive in chunks. Returns normally
    /// when the board restarts under the write (past
    /// <see cref="RebootThreshold"/>, or once the drive has gone); before that,
    /// an error is a real failure.</summary>
    private void WriteImage(FirmwareImage image, string drive, Action<double> progress)
    {
        FileStream source;
        try { source = File.OpenRead(image.Path); }
        catch { throw new FirmwareInstallException(FirmwareInstallError.WriteFailed("could not read the bundled image")); }
        using (source)
        {
            long total = source.Length, written = 0;
            FileStream sink;
            try
            {
                // Write-through and unbuffered, so progress follows the bytes
                // reaching the board, and a short last chunk is not held back
                // until the close, whose errors mean nothing.
                sink = new FileStream(Path.Combine(drive, Path.GetFileName(image.Path)),
                    FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (Exception e)
            {
                throw new FirmwareInstallException(FirmwareInstallError.WriteFailed($"could not open {drive} for writing ({e.Message})"));
            }
            var buffer = new byte[WriteChunkSize];
            try
            {
                while (true)
                {
                    int n;
                    // A failed read must not be mistaken for the end of the
                    // file, which would flash a truncated image.
                    try { n = source.Read(buffer, 0, buffer.Length); }
                    catch { throw new FirmwareInstallException(FirmwareInstallError.WriteFailed("could not read the bundled image")); }
                    if (n == 0) break;
                    try { sink.Write(buffer, 0, n); }
                    // Any error: a vanishing drive can surface as access denied
                    // or an aborted operation as well as an I/O error.
                    catch (Exception e)
                    {
                        double fraction = total > 0 ? (double)written / total : 0;
                        if (IsRebootSignal(fraction, _driveExists(drive))) return;
                        throw new FirmwareInstallException(FirmwareInstallError.WriteFailed(e.Message));
                    }
                    written += n;
                    progress(total > 0 ? (double)written / total : 0);
                }
            }
            finally
            {
                // Once every byte is out the board may already be gone, so a
                // failure to close means nothing.
                try { sink.Dispose(); } catch { }
            }
        }
    }

    private void Post(FirmwareInstallState next) => _post(() => SetState(next));

    private void SetState(FirmwareInstallState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke();
    }
}
