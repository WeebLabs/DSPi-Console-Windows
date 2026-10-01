using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Services;

namespace DSPiConsole.ViewModels;

/// <summary>
/// The connected device's firmware version and how it compares with what this
/// Console expects (<see cref="AppInfo.ExpectedFirmware"/>). A mismatch is a
/// broken state rather than a nag: features are gated on the device's own
/// version, so the wrong firmware means controls that do nothing or are
/// missing. Wrong in either direction is reported, never guessed at.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Decoded from REQ_GET_PLATFORM on connect; null while
    /// disconnected or before the reply lands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirmwareMatch))]
    private FirmwareVersion? _deviceFirmwareVersion;

    /// <summary>Null whenever too little is known to tell the user anything:
    /// disconnected, no reply yet, or the app's own version unreadable.</summary>
    public FirmwareMatch? FirmwareMatch
    {
        get
        {
            if (!IsDeviceConnected || DeviceFirmwareVersion is not { } device || AppInfo.ExpectedFirmware is not { } expected)
                return null;
            int order = device.CompareTo(expected);
            return order == 0 ? Core.Models.FirmwareMatch.Match
                 : order < 0 ? Core.Models.FirmwareMatch.DeviceOlder
                 : Core.Models.FirmwareMatch.DeviceNewer;
        }
    }

    partial void OnIsDeviceConnectedChanged(bool value)
    {
        if (!value) DeviceFirmwareVersion = null;
        OnPropertyChanged(nameof(FirmwareMatch));
    }
}
