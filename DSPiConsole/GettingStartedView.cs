using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Firmware;
using DSPiConsole.Core.Models;
using DSPiConsole.Services;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DSPiConsole;

/// <summary>
/// The Getting Started wizard. Replaces the console inside the main window
/// rather than opening over it: a first-time user would otherwise face an
/// interface they cannot read yet. Its one objective is a Pico running
/// verified DSPi firmware; outputs, wiring and routing belong to the app
/// proper. The install happens right here, on the same installer the Firmware
/// Update window uses. After the macOS Console's GettingStartedView.
/// </summary>
public sealed class GettingStartedView : UserControl
{
    private enum Stage { Welcome, Board, Done }
    private static readonly string[] StageLabels = { "Welcome", "Board", "Done" };

    private readonly MainViewModel _vm;
    private readonly Action _finish;
    private readonly FirmwareInstaller _installer;
    private readonly ViewModelFirmwareVerifier _verifier;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly TextBlock _subtitle = new() { FontSize = 11 };
    private readonly Grid _steps = new() { MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _footer = new() { Padding = new Thickness(24, 16, 24, 16), ColumnSpacing = 8 };

    private Stage _stage = Stage.Welcome;
    /// <summary>The user has committed to an install; the installer never flashes on its own.</summary>
    private bool _confirmed;
    /// <summary>The connected device has been told to restart into the bootloader.</summary>
    private bool _rebootRequested;
    private BootloaderChip? _lastSeenChip;
    private ProgressBar? _writeBar;
    private TextBlock? _writePercent;
    private bool _shutDown;

    private static string Bundled => AppInfo.ExpectedFirmware?.ToString() ?? "unknown";

    public GettingStartedView(MainViewModel vm, Action finish)
    {
        _vm = vm;
        _finish = finish;
        _subtitle.Foreground = _secondary;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Post(Action a) => dispatcher.TryEnqueue(() => a());
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Firmware");
        _verifier = new ViewModelFirmwareVerifier(vm, dispatcher);
        _installer = new FirmwareInstaller(new SystemBootloaderLocator(Post), _verifier,
            chip => FirmwareImage.Bundled(chip, folder, AppInfo.ExpectedFirmware), Post);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(BuildHeader());
        Place(root, Rule(), 1);
        Place(root, new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Padding = new Thickness(28), MaxWidth = 616, HorizontalAlignment = HorizontalAlignment.Center, Child = _content },
        }, 2);
        Place(root, Rule(), 3);
        Place(root, _footer, 4);
        Content = root;

        _installer.StateChanged += OnInstallerChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _installer.BeginWatching();
        Refresh();
    }

    /// <summary>Stops detection and lets go of the view model; the host calls
    /// it when the wizard leaves the window. A write in progress carries on.</summary>
    public void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        _installer.StateChanged -= OnInstallerChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _installer.StopWatching();
        _verifier.Dispose();
    }

    /// <summary>Bytes are going to a board: the window must not close now.</summary>
    public bool IsWriting => _installer.State is FirmwareInstallState.Writing;

    private static void Place(Grid g, FrameworkElement e, int row) { Grid.SetRow(e, row); g.Children.Add(e); }
    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private FrameworkElement BuildHeader()
    {
        var header = new StackPanel { Spacing = 14, Padding = new Thickness(24, 14, 24, 14) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(new FontIcon { Glyph = "", FontSize = 20, Foreground = new SolidColorBrush(FirmwareInstallUi.Accent), VerticalAlignment = VerticalAlignment.Center });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Getting Started", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(_subtitle);
        titleRow.Children.Add(titles);
        header.Children.Add(titleRow);
        header.Children.Add(_steps);
        return header;
    }

    private void OnInstallerChanged()
    {
        switch (_installer.State)
        {
            case FirmwareInstallState.Ready r: _lastSeenChip = r.Board.Chip; break;
            case FirmwareInstallState.WaitingForVolume w: _lastSeenChip = w.Chip; break;
        }
        // An install armed before Back was pressed still shows its progress.
        if (_installer.State is FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice && _stage != Stage.Board)
        {
            _stage = Stage.Board;
            Refresh();
            return;
        }
        if (_installer.State is FirmwareInstallState.Writing writing && _writeBar != null && _writePercent != null)
        {
            _writeBar.Value = writing.Fraction * 100;
            _writePercent.Text = $"{Math.Round(writing.Fraction * 100)}%";
            return;
        }
        Refresh();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected) or nameof(MainViewModel.DeviceFirmwareVersion) or nameof(MainViewModel.FirmwareMatch))
            Refresh();
    }

    private void Refresh()
    {
        if (_shutDown) return;
        _subtitle.Text = $"Step {(int)_stage + 1} of {StageLabels.Length}";
        FirmwareInstallUi.FillStepStrip(_steps, StageLabels, (int)_stage, dimmed: false, dotWidth: 60);
        _writeBar = null;
        _writePercent = null;
        _content.Content = _stage switch
        {
            Stage.Welcome => StepBody("Welcome to DSPi Console",
                "DSPi turns a Raspberry Pi Pico into a remarkably capable audio processor: equalisation, crossovers, upmixing, loudness compensation and more, applied live to whatever you play.\n\nSetup is short and has one job: getting the DSPi firmware onto your Pico. Once it is running, everything else is set up in the app as you need it.",
                InfoRow("", "Connect your Pico and install the DSPi firmware, right here."),
                InfoRow("", "The board restarts and the app confirms the install worked."),
                InfoRow("", "Outputs, wiring and audio are then yours to shape in the console.")),
            Stage.Board => StepBody(BoardTitle, BoardBlurb, BoardCard()),
            _ => StepBody("You are set up",
                "Your Pico is running the DSPi firmware, and the console is ready whenever it is plugged in. A few places worth knowing about:",
                InfoRow("", "Choose which outputs your build uses, and the pins that carry them, in Settings under Hardware."),
                InfoRow("", "Pick the DSPi as the output device in Windows Sound settings to hear your computer through it."),
                InfoRow("", "Click an input or output in the sidebar to edit its filters."),
                InfoRow("", "Help in the menu holds release notes and links, and this wizard can be run again.")),
        };
        RefreshFooter();
    }

    // ── Board ──

    /// <summary>The connected, running DSPi to talk about instead of hunting
    /// for a bootloader, while detection is still looking and nothing has been
    /// committed.</summary>
    private FirmwareMatch? ConnectedDeviceMatch =>
        !_confirmed && _vm.IsDeviceConnected
        && _installer.State is FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard
            ? _vm.FirmwareMatch : null;

    private string DeviceVersion => _vm.DeviceFirmwareVersion?.ToString() ?? "unknown";

    private string BoardTitle => _installer.State switch
    {
        FirmwareInstallState.Verified => "Your device has been prepared",
        FirmwareInstallState.Failed => "Something needs attention",
        FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice => "Installing firmware",
        _ => ConnectedDeviceMatch switch
        {
            FirmwareMatch.Match => "Your device is ready",
            FirmwareMatch.DeviceOlder => "Update your firmware",
            FirmwareMatch.DeviceNewer => "Your firmware is newer",
            _ => "Prepare your Pico",
        },
    };

    private string BoardBlurb => _installer.State switch
    {
        FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice =>
            "Console ships the firmware it expects, so nothing needs a download. Keep the Pico plugged in until it checks back in.",
        FirmwareInstallState.Verified => "The device has successfully restarted and DSPi Firmware is correctly installed.",
        FirmwareInstallState.Failed => "This is almost always fixable. Follow the card below, then try again; nothing has been lost.",
        _ => ConnectedDeviceMatch switch
        {
            FirmwareMatch.Match => "This step installs the DSPi firmware, and your connected device is already running it. There is nothing to do here.",
            FirmwareMatch.DeviceOlder => "Your DSPi is already connected, so no buttons need holding: the app can restart it and install the matching firmware in one step.",
            FirmwareMatch.DeviceNewer => "This Console ships an older firmware than your device is running. Updating the app is usually the better fix, but you can also downgrade the device to match.",
            _ => "In this step, we are going to install the DSPi firmware on your Pico-compatible device. Follow the directions below.",
        },
    };

    private FrameworkElement BoardCard()
    {
        var accent = new SolidColorBrush(FirmwareInstallUi.Accent);
        var green = new SolidColorBrush(FirmwareInstallUi.Green);
        Border card = _installer.State switch
        {
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard when _confirmed => FirmwareInstallUi.StateCard(null, accent, true,
                "Looking for your Pico",
                "Waiting for it to appear in bootloader mode. If nothing happens after a few seconds, unplug it, hold BOOTSEL, and plug it back in."),
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard when ConnectedDeviceMatch is { } match => ConnectedDeviceCard(match),
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard => FirmwareInstallUi.StateCard(null, accent, true,
                "Waiting for your Pico",
                "Hold the BOOTSEL button while connecting your Pico-compatible device to your computer. Once detected, it will appear here."),
            FirmwareInstallState.WaitingForVolume w => FirmwareInstallUi.StateCard(null, accent, true,
                $"{w.Chip.DisplayName()} found",
                $"The board is in bootloader mode. Waiting for its {w.Chip.VolumeName()} drive to appear; this usually takes a second or two."),
            FirmwareInstallState.Ready r when _confirmed => FirmwareInstallUi.StateCard(null, accent, true,
                "Preparing to write", $"Opening the {r.Board.Chip.DisplayName()}'s {r.Board.Chip.VolumeName()} drive."),
            FirmwareInstallState.Ready r => FirmwareInstallUi.StateCard("", green, false,
                $"{r.Board.Chip.DisplayName()} ready", "The device is now in bootloader mode and ready to receive firmware.",
                accessory: ActionButton("Install DSPi Firmware", BeginInstall, accentStyle: true)),
            FirmwareInstallState.Writing w => Writing(w.Fraction),
            FirmwareInstallState.WaitingForDevice => FirmwareInstallUi.StateCard(null, accent, true,
                "Firmware written", "The board is restarting with its new firmware. This can take up to half a minute; leave it plugged in."),
            FirmwareInstallState.Verified v => FirmwareInstallUi.StateCard("", green, false,
                $"Firmware {v.Version} installed", "That was the whole job. Continue to finish setup.", 36),
            FirmwareInstallState.Failed f => FailureWithRetry(f.Error),
            _ => FirmwareInstallUi.StateCard(null, accent, true, "", ""),
        };
        card.MinHeight = 190;
        return card;
    }

    private Border Writing(double fraction)
    {
        var (card, bar, percent) = FirmwareInstallUi.WritingCard(fraction, _lastSeenChip?.DisplayName(), Bundled);
        _writeBar = bar;
        _writePercent = percent;
        return card;
    }

    private Border FailureWithRetry(FirmwareInstallError error)
    {
        var card = FirmwareInstallUi.FailureCard(error);
        if (card.Child is StackPanel stack)
        {
            var retry = ActionButton("Try Again", ResetInstallRun, accentStyle: false);
            retry.HorizontalAlignment = HorizontalAlignment.Center;
            retry.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(retry);
        }
        return card;
    }

    /// <summary>A DSPi already connected and running: a current one sails
    /// through, a mismatched one is updated in place, no buttons held.</summary>
    private Border ConnectedDeviceCard(FirmwareMatch match) => match switch
    {
        FirmwareMatch.Match => FirmwareInstallUi.StateCard("", new SolidColorBrush(FirmwareInstallUi.Green), false,
            $"Firmware {Bundled} already installed",
            "Your DSPi is running the firmware this Console ships, so there is nothing to install. Continue to finish setup.", 36),
        FirmwareMatch.DeviceOlder => FirmwareInstallUi.StateCard("", new SolidColorBrush(FirmwareInstallUi.Accent), false,
            "Firmware update available",
            $"Your DSPi is running firmware {DeviceVersion}; this Console pairs with {Bundled}. The device will restart into bootloader mode and come back updated. Audio stops until it finishes.",
            accessory: ActionButton("Update DSPi Firmware", BeginConnectedDeviceInstall, accentStyle: true)),
        _ => FirmwareInstallUi.StateCard("", new SolidColorBrush(FirmwareInstallUi.Orange), false,
            "This would be a downgrade",
            $"Your DSPi is running firmware {DeviceVersion}, which is newer than this Console expects ({Bundled}). A newer Console is the better fix, but you can downgrade the device to match this one.",
            accessory: ActionButton("Downgrade Firmware", BeginConnectedDeviceInstall, accentStyle: false)),
    };

    private static Button ActionButton(string text, Action act, bool accentStyle)
    {
        var b = new Button { Content = text, Padding = new Thickness(16, 7, 16, 8) };
        if (accentStyle) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>Install onto a board already in BOOTSEL.</summary>
    private void BeginInstall()
    {
        _confirmed = true;
        // From now: a device connected before this has not been rewritten.
        _verifier.Reset();
        _installer.InstallWhenReady();
        Refresh();
    }

    /// <summary>Update the device that is connected and running: arm the
    /// installer, then ask the device to restart into bootloader mode.</summary>
    private void BeginConnectedDeviceInstall()
    {
        _confirmed = true;
        _verifier.Reset();
        _installer.InstallWhenReady();
        if (!_rebootRequested && _vm.IsDeviceConnected)
        {
            _rebootRequested = true;
            var device = _vm.Device;
            _ = Task.Run(() => { try { device.EnterBootloaderMode(); } catch { } });
        }
        Refresh();
    }

    private void ResetInstallRun()
    {
        _confirmed = false;
        _rebootRequested = false;
        _installer.Reset();
        _verifier.Reset();
        Refresh();
    }

    // ── Footer ──

    private bool InstallInFlight => _installer.State is FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice;

    /// <summary>The board stage has no Continue while it still has work to do:
    /// either the firmware went on and was confirmed, or the device already
    /// runs exactly what this Console ships.</summary>
    private bool ShowsContinue => _stage != Stage.Board
        || _installer.State is FirmwareInstallState.Verified
        || ConnectedDeviceMatch == FirmwareMatch.Match;

    private void RefreshFooter()
    {
        _footer.Children.Clear();
        _footer.ColumnDefinitions.Clear();
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Always there, one click, and it never comes back.
        var skip = new Button { Content = "Skip Setup", IsEnabled = !InstallInFlight };
        skip.Click += (_, _) => _finish();
        _footer.Children.Add(skip);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_stage != Stage.Welcome && !InstallInFlight)
        {
            var back = new Button { Content = "Back" };
            back.Click += (_, _) => { _stage--; Refresh(); };
            right.Children.Add(back);
        }
        if (ShowsContinue)
        {
            var next = new Button { Content = _stage == Stage.Done ? "Start Using DSPi Console" : "Continue" };
            // The install button owns the accent while it is on screen.
            if (!(_stage == Stage.Board && !_confirmed && _installer.State is FirmwareInstallState.Ready))
                next.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            next.Click += (_, _) =>
            {
                if (_stage == Stage.Done) { _finish(); return; }
                _stage++;
                Refresh();
            };
            right.Children.Add(next);
        }
        Grid.SetColumn(right, 1);
        _footer.Children.Add(right);
    }

    // ── Pieces ──

    private FrameworkElement StepBody(string title, string blurb, params FrameworkElement[] extra)
    {
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = blurb, FontSize = 13, Foreground = _secondary, TextWrapping = TextWrapping.Wrap });
        var rows = new StackPanel { Spacing = 12 };
        foreach (var e in extra) rows.Children.Add(e);
        body.Children.Add(rows);
        return body;
    }

    private static FrameworkElement InfoRow(string glyph, string text)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = new SolidColorBrush(FirmwareInstallUi.Accent), VerticalAlignment = VerticalAlignment.Top });
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(t, 1);
        row.Children.Add(t);
        return row;
    }
}
