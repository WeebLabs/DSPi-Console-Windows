using DSPiConsole.Core.Models;
using DSPiConsole.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole;

/// <summary>
/// The firmware mismatch banner. Hidden whenever the versions agree, whenever
/// too little is known to be sure, and for the rest of the session once closed:
/// a mismatch is a broken state, so hiding it is a way to get on with the
/// session rather than a preference, and the next launch says so again.
/// </summary>
public sealed partial class MainWindow
{
    private bool _firmwareBarHiddenThisSession;

    private void UpdateFirmwareMismatchBar()
    {
        var match = ViewModel.FirmwareMatch;
        if (match is null or FirmwareMatch.Match || _firmwareBarHiddenThisSession)
        {
            FirmwareMismatchBar.IsOpen = false;
            return;
        }
        string device = ViewModel.DeviceFirmwareVersion?.ToString() ?? "unknown";
        string expected = AppInfo.ExpectedFirmware?.ToString() ?? "unknown";
        bool newer = match == FirmwareMatch.DeviceNewer;
        FirmwareMismatchBar.Title = newer ? "Newer firmware" : "Firmware update needed";
        FirmwareMismatchBar.Message = newer
            ? $"This device runs firmware {device}, which is newer than DSPi Console {expected}. Some of its features may not be shown."
            : $"This device runs firmware {device}; DSPi Console expects {expected}.";
        FirmwareMismatchButton.Content = newer ? "Details..." : "Update...";
        FirmwareMismatchBar.IsOpen = true;
    }

    private void OnFirmwareMismatchBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) _firmwareBarHiddenThisSession = true;
    }

    private void OnFirmwareMismatchActionClick(object sender, RoutedEventArgs e) => ShowFirmwareUpdate();

    private FirmwareUpdateWindow? _firmwareUpdateWindow;

    /// <summary>Tools › Update Firmware and the banner's button. A fresh window
    /// each time it opens, so a finished run never reappears; a second request
    /// while it is open brings it forward.</summary>
    private void ShowFirmwareUpdate()
    {
        if (_firmwareUpdateWindow != null)
        {
            _firmwareUpdateWindow.Activate();
            return;
        }
        _firmwareUpdateWindow = new FirmwareUpdateWindow(ViewModel, () => OnExportPresetClick(this, new RoutedEventArgs()));
        _firmwareUpdateWindow.Closed += (_, _) => _firmwareUpdateWindow = null;
        _firmwareUpdateWindow.Activate();
    }
}
