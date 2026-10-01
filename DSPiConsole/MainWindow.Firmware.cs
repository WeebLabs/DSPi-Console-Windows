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

    private async void OnFirmwareMismatchActionClick(object sender, RoutedEventArgs e)
    {
        string device = ViewModel.DeviceFirmwareVersion?.ToString() ?? "unknown";
        var expected = AppInfo.ExpectedFirmware;
        bool newer = ViewModel.FirmwareMatch == FirmwareMatch.DeviceNewer;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        if (newer)
        {
            dialog.Title = "Newer firmware";
            dialog.Content = $"This device runs firmware {device}, but this DSPi Console is {expected}. "
                + "Controls for features added since then are not shown, and settings this version does not "
                + "know about are left alone.\n\nInstall the DSPi Console release that matches the firmware, "
                + "or install firmware " + expected + " on the device.";
            dialog.PrimaryButtonText = "Open Console Releases";
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Windows.System.Launcher.LaunchUriAsync(new Uri(AppInfo.ConsoleReleasesUrl));
            return;
        }

        string? tag = expected?.TagSuffix is { } t ? "v" + t : null;
        dialog.Title = "Update firmware";
        dialog.Content = $"This device runs firmware {device}; DSPi Console expects {expected}.\n\n"
            + $"Download the {expected} firmware for this board from the firmware releases page, then reboot "
            + "the device into its bootloader. It appears as a USB drive: copy the .uf2 file onto it and it "
            + "restarts with the new firmware.\n\nRebooting stops audio output immediately.";
        dialog.PrimaryButtonText = "Open Firmware Releases";
        dialog.SecondaryButtonText = "Reboot into Bootloader";
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            string url = tag != null ? $"{AppInfo.FirmwareReleasesUrl}/tag/{tag}" : AppInfo.FirmwareReleasesUrl;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        else if (result == ContentDialogResult.Secondary && ViewModel.IsDeviceConnected)
        {
            _ = Task.Run(() => ViewModel.Device.EnterBootloaderMode());
        }
    }
}
