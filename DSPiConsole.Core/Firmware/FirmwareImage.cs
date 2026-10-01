using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Firmware;

/// <summary>
/// A UF2 image shipped with the app, in its Firmware folder. The Console ships
/// the firmware of its own version so the pair can never be mismatched by
/// updating one and not the other. Port of the macOS Console's FirmwareImage.
/// </summary>
public sealed record FirmwareImage(BootloaderChip Chip, string Path, FirmwareVersion Version)
{
    /// <summary>
    /// The bundled image for a chip. Throws <see cref="FirmwareInstallException"/>
    /// with <see cref="FirmwareInstallErrorKind.ImageStale"/> when the image's
    /// version is not the app's, which catches a release that bumped the app
    /// version and forgot to refresh the .uf2 files: a stale image would
    /// otherwise ship and quietly downgrade every device it touched.
    /// </summary>
    public static FirmwareImage Bundled(BootloaderChip chip, string directory, FirmwareVersion? expected)
    {
        // An older image can sit beside the current one (an install unzipped
        // over the last, a publish folder that was not cleaned), so the one
        // that matches is chosen, and stale only when none does.
        var candidates = (Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.uf2") : Enumerable.Empty<string>())
            .Where(f => System.IO.Path.GetFileName(f).Contains($"-{chip.AssetToken()}-", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path: f, Version: VersionFromAssetName(System.IO.Path.GetFileName(f))))
            .Where(c => c.Version != null)
            .OrderByDescending(c => c.Version!.Value)
            .ToList();
        if (candidates.Count == 0)
            throw new FirmwareInstallException(FirmwareInstallError.ImageMissing(chip.DisplayName()));
        if (expected is { } e)
        {
            if (candidates.FirstOrDefault(c => c.Version == e) is { Path: not null } match)
                return new FirmwareImage(chip, match.Path, e);
            throw new FirmwareInstallException(FirmwareInstallError.ImageStale(candidates[0].Version!.Value.ToString(), e.ToString()));
        }
        return new FirmwareImage(chip, candidates[0].Path, candidates[0].Version!.Value);
    }

    /// <summary>"1.1.7" from "DSPi-RP2350-v1.1.7.uf2", "1.1.6 beta 4" from
    /// "DSPi-RP2350-v1.1.6-beta4.uf2".</summary>
    public static FirmwareVersion? VersionFromAssetName(string name)
    {
        int marker = name.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        string tail = name[(marker + 2)..];
        if (tail.EndsWith(".uf2", StringComparison.OrdinalIgnoreCase)) tail = tail[..^4];
        return FirmwareVersion.Parse(tail);
    }
}

public enum FirmwareInstallErrorKind
{
    NoBoardFound, MultipleBoards, VolumeNotMounted, ImageMissing, ImageStale,
    WriteFailed, DeviceDidNotReturn, VersionMismatch,
}

/// <summary>Why an install stopped, with one sentence fit to show a user.</summary>
public sealed record FirmwareInstallError(FirmwareInstallErrorKind Kind, string Message)
{
    public static FirmwareInstallError NoBoardFound() => new(FirmwareInstallErrorKind.NoBoardFound,
        "No board in bootloader mode was found. Hold the BOOTSEL button while plugging the board in.");

    public static FirmwareInstallError MultipleBoards(int count) => new(FirmwareInstallErrorKind.MultipleBoards,
        $"{count} boards are in bootloader mode. Disconnect all but the one you want to update.");

    public static FirmwareInstallError VolumeNotMounted(string name) => new(FirmwareInstallErrorKind.VolumeNotMounted,
        $"The board is connected but its {name} drive has not appeared. If Windows is still setting the drive up, wait a moment; otherwise unplug the board and plug it back in while holding BOOTSEL.");

    public static FirmwareInstallError ImageMissing(string chip) => new(FirmwareInstallErrorKind.ImageMissing,
        $"This build of DSPi Console does not include firmware for the {chip}.");

    public static FirmwareInstallError ImageStale(string bundled, string expected) => new(FirmwareInstallErrorKind.ImageStale,
        $"The bundled firmware is version {bundled} but this Console expects {expected}. The app was built incorrectly; do not install it.");

    public static FirmwareInstallError WriteFailed(string detail) => new(FirmwareInstallErrorKind.WriteFailed,
        $"Writing the firmware failed: {detail}");

    public static FirmwareInstallError DeviceDidNotReturn() => new(FirmwareInstallErrorKind.DeviceDidNotReturn,
        "The firmware was written but the device did not reappear. Unplug it, plug it back in, and check whether it works.");

    public static FirmwareInstallError VersionMismatch(string expected, string got) => new(FirmwareInstallErrorKind.VersionMismatch,
        $"The device came back running firmware {got} instead of {expected}.");

    /// <summary>Detection-side stumbles (no board yet, a drive that has not
    /// mounted) are ordinary and should read that way; a failure after bytes
    /// have moved deserves the warning.</summary>
    public bool IsMundane => Kind is FirmwareInstallErrorKind.NoBoardFound
        or FirmwareInstallErrorKind.VolumeNotMounted or FirmwareInstallErrorKind.MultipleBoards;
}

public sealed class FirmwareInstallException(FirmwareInstallError error) : Exception(error.Message)
{
    public FirmwareInstallError Error { get; } = error;
}
