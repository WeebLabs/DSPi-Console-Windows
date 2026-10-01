using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Firmware;

/// <summary>The two chips a DSPi runs on, as their ROM bootloaders present them.</summary>
public enum BootloaderChip { RP2040, RP2350 }

public static class BootloaderChips
{
    /// <summary>Vendor ID of a board in BOOTSEL. Not the DSPi's own 0x2E8B: in
    /// bootloader mode the board runs Raspberry Pi's ROM loader, which
    /// enumerates under Raspberry Pi's vendor ID.</summary>
    public const ushort VendorId = 0x2E8A;

    public static readonly BootloaderChip[] All = { BootloaderChip.RP2040, BootloaderChip.RP2350 };

    public static ushort ProductId(this BootloaderChip chip) => chip == BootloaderChip.RP2040 ? (ushort)0x0003 : (ushort)0x000F;

    /// <summary>Label of the drive the ROM loader mounts to receive a UF2.</summary>
    public static string VolumeName(this BootloaderChip chip) => chip == BootloaderChip.RP2040 ? "RPI-RP2" : "RP2350";

    /// <summary>Token in a release asset's file name, e.g. DSPi-RP2350-v1.1.7.uf2.</summary>
    public static string AssetToken(this BootloaderChip chip) => chip == BootloaderChip.RP2040 ? "RP2040" : "RP2350";

    public static string DisplayName(this BootloaderChip chip) => chip == BootloaderChip.RP2040 ? "RP2040 (Pico)" : "RP2350 (Pico 2)";
}

/// <summary>One board in BOOTSEL. <see cref="DrivePath"/> is null while the
/// board is on the USB bus but its drive has not appeared, which is ordinary
/// for a second or two after it enumerates; callers must not read that as
/// "no board".</summary>
public sealed record BootloaderBoard(BootloaderChip Chip, string? DrivePath);

/// <summary>Finds boards in BOOTSEL. An interface so tests can drive the
/// installer with no hardware attached.</summary>
public interface IBootloaderLocator
{
    /// <summary>The most recent scan; never scans inline.</summary>
    IReadOnlyList<BootloaderBoard> CurrentBoards { get; }

    /// <summary>Raised on the UI thread after every scan, changed or not: a
    /// board waiting for its drive looks the same scan to scan, and the
    /// installer needs those ticks to decide the drive is not coming.</summary>
    event Action<IReadOnlyList<BootloaderBoard>>? Changed;

    void Start();
    void Stop();
}

/// <summary>Waits for a DSPi to come back after a flash and reports the
/// version it claims. Called off the UI thread.</summary>
public interface IFirmwareVerifier
{
    Task<FirmwareVersion?> AwaitDeviceVersionAsync(TimeSpan timeout, CancellationToken cancel);
}
