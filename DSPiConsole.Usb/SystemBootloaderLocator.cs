using System.Runtime.InteropServices;
using System.Text;
using DSPiConsole.Core.Firmware;

namespace DSPiConsole.Usb;

/// <summary>
/// Finds boards in BOOTSEL by pairing two independent signals: the USB bus says
/// which chip with certainty (the ROM loader's vendor and product IDs, read with
/// SetupAPI), and the drive with the loader's volume label says where to write.
/// Neither alone is enough, and the drive lags enumeration, so a board is
/// reported without a drive until its drive appears rather than hidden. Scans
/// once a second on the thread pool while started; results are handed to the
/// UI thread through <c>post</c>. After the macOS Console's SystemBootloaderLocator.
/// </summary>
public sealed class SystemBootloaderLocator : IBootloaderLocator
{
    private readonly Action<Action> _post;
    private Timer? _timer;
    private int _scanning;
    private volatile bool _started;

    public IReadOnlyList<BootloaderBoard> CurrentBoards { get; private set; } = Array.Empty<BootloaderBoard>();
    public event Action<IReadOnlyList<BootloaderBoard>>? Changed;

    public SystemBootloaderLocator(Action<Action> post) => _post = post;

    public void Start()
    {
        _started = true;
        _timer ??= new Timer(_ => Rescan(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Rescan()
    {
        // A drive going away under a flash can leave a scan slow; never stack them.
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            var boards = Scan();
            _post(() =>
            {
                if (!_started) return;
                CurrentBoards = boards;
                Changed?.Invoke(boards);
            });
        }
        catch { }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    private static List<BootloaderBoard> Scan()
    {
        var boards = new List<BootloaderBoard>();
        var instances = UsbInstanceIds();
        foreach (var chip in BootloaderChips.All)
        {
            string prefix = $@"USB\VID_{BootloaderChips.VendorId:X4}&PID_{chip.ProductId():X4}\";
            int count = instances.Count(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (count == 0) continue;
            var drives = DrivesLabelled(chip.VolumeName());
            // Two identical boards mount two drives with the same label and
            // nothing pairs them to their USB devices; the installer refuses to
            // act on more than one board anyway, and it needs the true count.
            for (int i = 0; i < count; i++)
                boards.Add(new BootloaderBoard(chip, i < drives.Count ? drives[i] : null));
        }
        return boards;
    }

    /// <summary>Root paths of the removable drives with this volume label.</summary>
    private static List<string> DrivesLabelled(string label)
    {
        var found = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady
                    && string.Equals(drive.VolumeLabel, label, StringComparison.OrdinalIgnoreCase))
                    found.Add(drive.RootDirectory.FullName);
            }
            catch { }
        }
        return found;
    }

    // ── SetupAPI: the present USB devices' instance IDs ──
    // A composite device lists itself ("USB\VID_2E8A&PID_000F\<serial>") and
    // each interface ("...&MI_00\..."); only the first form starts with the
    // VID/PID prefix followed directly by a backslash, so each board counts once.

    private const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static List<string> UsbInstanceIds()
    {
        var ids = new List<string>();
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return ids;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var id = new StringBuilder(512);
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                if (SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out _)) ids.Add(id.ToString());
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return ids;
    }
}
