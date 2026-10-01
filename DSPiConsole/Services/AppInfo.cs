using System.Reflection;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Services;

/// <summary>
/// The app's own version, and the firmware version it therefore expects.
/// Console and firmware ship as a matched pair carrying the same version
/// (as the macOS Console does), so the expected firmware is derived from the
/// app version rather than kept by hand: bumping the app version is the one
/// release step nobody forgets.
/// </summary>
public static class AppInfo
{
    /// <summary>The informational version ("1.1.6-beta4", from the project's
    /// Version or the publish command's -p:Version), without any "+commit"
    /// build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>The firmware this build expects a device to run. Null when the
    /// app version cannot be read, in which case nothing may claim a mismatch.</summary>
    public static FirmwareVersion? ExpectedFirmware { get; } = FirmwareVersion.Parse(Version);

    public const string FirmwareReleasesUrl = "https://github.com/WeebLabs/DSPi/releases";
    public const string ConsoleReleasesUrl = "https://github.com/WeebLabs/DSPi-Console-Windows/releases";

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "";
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
