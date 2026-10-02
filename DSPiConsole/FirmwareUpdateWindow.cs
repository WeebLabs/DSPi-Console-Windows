using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core.Firmware;
using DSPiConsole.Core.Models;
using DSPiConsole.Services;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Installs the bundled firmware onto the connected device, or onto a board
/// already in BOOTSEL. The user confirms once, here; everything after that is
/// <see cref="FirmwareInstaller"/>'s state machine, including the restart into
/// the bootloader that used to be the whole feature. A fresh window, and a fresh
/// installer, on every open. After the macOS Console's FirmwareUpdateView.
/// </summary>
public sealed class FirmwareUpdateWindow : Window
{
    private static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    private static readonly Color Orange = Color.FromArgb(255, 255, 159, 10);

    private readonly MainViewModel _vm;
    private readonly Action _exportConfiguration;
    private readonly FirmwareInstaller _installer;
    private readonly ViewModelFirmwareVerifier _verifier;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private readonly Color _accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private readonly StackPanel _summary = new() { Spacing = 6 };
    private readonly Grid _steps = new();
    private readonly ContentControl _card = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ContentControl _hint = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _buttons = new() { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 8 };

    /// <summary>The user has committed to the update. The installer never
    /// flashes on its own, so a board reaching Ready only starts a write once
    /// this is set.</summary>
    private bool _confirmed;
    private bool _rebootRequested;
    /// <summary>The chip last seen by detection, so the writing card can still
    /// name it: Writing carries only a fraction.</summary>
    private BootloaderChip? _lastSeenChip;
    private ProgressBar? _writeBar;
    private TextBlock? _writePercent;

    /// <summary>Bytes are going to the board: closing now would cut the write short.</summary>
    public bool IsWriting => _installer.State is FirmwareInstallState.Writing;

    private static string Bundled => AppInfo.ExpectedFirmware?.ToString() ?? "unknown";
    private string? DeviceVersion => _vm.IsDeviceConnected ? _vm.DeviceFirmwareVersion?.ToString() : null;
    private bool Downgrade => _vm.FirmwareMatch == FirmwareMatch.DeviceNewer;
    private string PrimaryTitle => Downgrade ? "Downgrade" : "Update Firmware";

    public FirmwareUpdateWindow(MainViewModel vm, Action exportConfiguration)
    {
        _vm = vm;
        _exportConfiguration = exportConfiguration;
        ToolWindowChrome.Apply(this, "Firmware Update", 480, 540);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Post(Action a) => dispatcher.TryEnqueue(() => a());
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Firmware");
        _verifier = new ViewModelFirmwareVerifier(vm, dispatcher);
        _installer = new FirmwareInstaller(
            new SystemBootloaderLocator(Post),
            _verifier,
            chip => FirmwareImage.Bundled(chip, folder, AppInfo.ExpectedFirmware),
            Post);

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(BuildHeader());
        Place(root, Rule(), 1);
        var body = new Grid { Padding = new Thickness(16), RowSpacing = 14 };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = h });
        body.Children.Add(Card(_summary, new Thickness(12, 10, 12, 10)));
        Place(body, _steps, 1);
        Place(body, _card, 2);
        Place(body, _hint, 3);
        Place(root, body, 2);
        Place(root, Rule(), 3);
        Place(root, _buttons, 4);
        Content = root;

        _installer.StateChanged += OnStateChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) =>
        {
            // A write in progress carries on; only its reports stop coming here.
            _installer.StateChanged -= OnStateChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _installer.StopWatching();
            _verifier.Dispose();
        };
        // The write carries on without the window, but a window reopened
        // mid-write would offer the same board again; it stays until the
        // bytes are out.
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        if (appWindow != null) appWindow.Closing += (_, args) => { if (IsWriting) args.Cancel = true; };
        _installer.BeginWatching();
        Refresh();
    }

    private static void Place(Grid g, FrameworkElement e, int row) { Grid.SetRow(e, row); g.Children.Add(e); }
    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private static Border Card(UIElement child, Thickness padding) => FirmwareInstallUi.Card(child, padding);

    private FrameworkElement BuildHeader()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(16, 12, 16, 12) };
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 22, Foreground = new SolidColorBrush(_accent), VerticalAlignment = VerticalAlignment.Center });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Firmware Update", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = $"Install firmware {Bundled} onto a DSPi board", FontSize = 10, Foreground = _secondary });
        header.Children.Add(titles);
        return header;
    }

    private void OnStateChanged()
    {
        switch (_installer.State)
        {
            case FirmwareInstallState.Ready r: _lastSeenChip = r.Board.Chip; break;
            case FirmwareInstallState.WaitingForVolume w: _lastSeenChip = w.Chip; break;
        }
        // Progress moves the bar it already has rather than rebuilding the card.
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
        RefreshSummary();
        RefreshSteps();
        RefreshCard();
        RefreshHint();
        RefreshButtons();
    }

    // ── Version summary ──

    private void RefreshSummary()
    {
        _summary.Children.Clear();
        _summary.Children.Add(ValueRow("This Console", Bundled, false));
        _summary.Children.Add(ValueRow("Connected device", DeviceVersion ?? "None", DeviceVersion == null));
        if (Downgrade)
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            var orange = new SolidColorBrush(Orange);
            warning.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = orange, VerticalAlignment = VerticalAlignment.Center });
            warning.Children.Add(new TextBlock
            {
                Text = "The device is newer than this Console, so this would be a downgrade.",
                FontSize = 10, Foreground = orange, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            });
            _summary.Children.Add(warning);
            // The other way out of a mismatch: a Console that matches the device.
            var link = new HyperlinkButton { Content = "Get the DSPi Console that matches the device", FontSize = 10, Padding = new Thickness(0) };
            link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(AppInfo.ConsoleReleasesUrl));
            _summary.Children.Add(link);
        }
    }

    private static FrameworkElement ValueRow(string label, string value, bool secondary) =>
        FirmwareInstallUi.ValueRow(label, value, secondary);

    // ── Step strip: Prepare, Write, Verify, Done ──

    private int CurrentStep => _installer.State switch
    {
        FirmwareInstallState.Writing => 1,
        FirmwareInstallState.WaitingForDevice => 2,
        FirmwareInstallState.Verified => 3,
        _ => 0,
    };

    private void RefreshSteps() => FirmwareInstallUi.FillStepStrip(_steps,
        new[] { "Prepare", "Write", "Verify", "Done" }, CurrentStep,
        // A failure keeps the strip but takes the emphasis off it; the card tells the story.
        dimmed: _installer.State is FirmwareInstallState.Failed);

    // ── Status card ──

    private void RefreshCard()
    {
        _writeBar = null;
        _writePercent = null;
        var accent = new SolidColorBrush(_accent);
        _card.Content = _installer.State switch
        {
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard when _confirmed => StateCard(null, accent, true,
                "Looking for the board",
                "Waiting for it to appear in bootloader mode. If nothing happens after a few seconds, unplug the board, hold BOOTSEL, and plug it back in."),
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard when _vm.IsDeviceConnected => StateCard("", accent, false,
                "Ready when you are",
                $"Click {PrimaryTitle} to begin. The device will restart into bootloader mode, and audio will stop until the update finishes. Nothing is written without this click."),
            FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard => StateCard("", _secondary, false,
                "Connect a board",
                "No device is connected. Hold the BOOTSEL button while plugging a board in, and it will appear here."),
            FirmwareInstallState.WaitingForVolume w => StateCard(null, accent, true,
                $"{w.Chip.DisplayName()} found",
                $"The board is in bootloader mode. Waiting for its {w.Chip.VolumeName()} drive to appear; this usually takes a second or two."),
            FirmwareInstallState.Ready r when _confirmed => StateCard(null, accent, true,
                "Preparing to write",
                $"Opening the {r.Board.Chip.DisplayName()}'s {r.Board.Chip.VolumeName()} drive."),
            FirmwareInstallState.Ready r => StateCard("", new SolidColorBrush(Green), false,
                $"{r.Board.Chip.DisplayName()} ready",
                $"The board is in bootloader mode and ready to receive firmware {Bundled}. Click {PrimaryTitle} to begin."),
            FirmwareInstallState.Writing w => WritingCard(w.Fraction),
            FirmwareInstallState.WaitingForDevice => StateCard(null, accent, true,
                "Firmware written",
                "The board is restarting with its new firmware. This can take up to half a minute; leave it plugged in."),
            FirmwareInstallState.Verified v => StateCard("", new SolidColorBrush(Green), false,
                "Update complete",
                $"The device is back and confirmed running firmware {v.Version}.", 36),
            FirmwareInstallState.Failed f => FirmwareInstallUi.FailureCard(f.Error),
            _ => null,
        };
    }

    private static Border StateCard(string? glyph, Brush tint, bool spinning, string title, string message, double iconSize = 28) =>
        FirmwareInstallUi.StateCard(glyph, tint, spinning, title, message, iconSize);

    private Border WritingCard(double fraction)
    {
        var (card, bar, percent) = FirmwareInstallUi.WritingCard(fraction, _lastSeenChip?.DisplayName(), Bundled);
        _writeBar = bar;
        _writePercent = percent;
        return card;
    }

    // ── Hints ──

    private void RefreshHint()
    {
        bool waiting = _installer.State is FirmwareInstallState.Idle or FirmwareInstallState.WaitingForBoard;
        bool noBoard = _installer.State is FirmwareInstallState.Failed { Error.Kind: FirmwareInstallErrorKind.NoBoardFound };
        if ((waiting && (_confirmed || !_vm.IsDeviceConnected)) || noBoard)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            hint.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Top });
            hint.Children.Add(new TextBlock
            {
                Text = "Hold the BOOTSEL button on your Pico-compatible device while plugging it into your computer.",
                FontSize = 10, Foreground = _secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
            });
            _hint.Content = hint;
        }
        else if (waiting && _vm.IsDeviceConnected && !_confirmed)
        {
            // What Firmware Update used to be, kept for anyone flashing a build
            // of their own: restart into the bootloader and write nothing.
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            hint.Children.Add(new TextBlock { Text = "Flashing a UF2 of your own?", FontSize = 10, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
            var link = new HyperlinkButton { Content = "Enter bootloader mode without installing", FontSize = 10, Padding = new Thickness(0) };
            link.Click += (_, _) => EnterBootloader();
            hint.Children.Add(link);
            _hint.Content = hint;
        }
        else _hint.Content = null;
    }

    // ── Buttons ──

    private void RefreshButtons()
    {
        _buttons.Children.Clear();
        _buttons.ColumnDefinitions.Clear();
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button? primary = null;
        if (_installer.State is FirmwareInstallState.Verified)
        {
            // A second board is a second decision: back to detection, nothing armed.
            left.Children.Add(Button("Update Another Board", ResetRun));
            primary = Button("Done", Close);
        }
        else
        {
            if (!IsWriting) left.Children.Add(Button("Cancel", Close));
            // A UF2 does not touch the preset sectors, but a wire-format change
            // between versions can leave them unreadable. Offered, not forced.
            if (_vm.IsDeviceConnected && !_confirmed)
                left.Children.Add(Button("Export Configuration...", _exportConfiguration));
            switch (_installer.State)
            {
                case FirmwareInstallState.Failed:
                    primary = Button("Try Again", ResetRun);
                    break;
                case FirmwareInstallState.Writing or FirmwareInstallState.WaitingForDevice:
                    break;
                default:
                    primary = Button(PrimaryTitle, Start);
                    primary.IsEnabled = !_confirmed;
                    break;
            }
        }
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _buttons.Children.Add(left);
        if (primary != null)
        {
            primary.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Grid.SetColumn(primary, 1);
            _buttons.Children.Add(primary);
        }
    }

    private static Button Button(string text, Action act)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => act();
        return b;
    }

    /// <summary>Commits to the update. The installer writes as soon as a board
    /// is ready, now or after the restart below, so the order the user and the
    /// hardware arrive in stops mattering.</summary>
    private void Start()
    {
        _confirmed = true;
        // From now: a device connected before this has not been rewritten.
        _verifier.Reset();
        // A board already in bootloader mode is the one to write; restarting
        // the connected device as well would strand it there.
        bool boardWaiting = _installer.State is FirmwareInstallState.Ready or FirmwareInstallState.WaitingForVolume;
        _installer.InstallWhenReady();
        if (_vm.IsDeviceConnected && !_rebootRequested && !boardWaiting)
        {
            _rebootRequested = true;
            EnterBootloader();
        }
        Refresh();
    }

    /// <summary>The device drops off the bus answering, so there is no reply to wait for.</summary>
    private void EnterBootloader()
    {
        if (!_vm.IsDeviceConnected) return;
        var device = _vm.Device;
        _ = Task.Run(() => { try { device.EnterBootloaderMode(); } catch { } });
    }

    /// <summary>Starts over: the window's flags and the installer's freeze and
    /// commitment together. Backs Try Again and Update Another Board.</summary>
    private void ResetRun()
    {
        _confirmed = false;
        _rebootRequested = false;
        _installer.Reset();
        _verifier.Reset();
        Refresh();
    }
}

/// <summary>
/// Waits for the device to come back after a flash by watching the view model,
/// read on the UI thread. A version counts only after the device has been seen
/// disconnected (or the window opened with none connected), so a reconnect the
/// app has not yet noticed is never taken for the board that was just written.
/// </summary>
internal sealed class ViewModelFirmwareVerifier : IFirmwareVerifier, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private volatile bool _sawDisconnected;

    public ViewModelFirmwareVerifier(MainViewModel vm, DispatcherQueue dispatcher)
    {
        _vm = vm;
        _dispatcher = dispatcher;
        Reset();
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>For a new run: a board left connected from the last one has
    /// not been rewritten, so it must drop off before it can count.</summary>
    public void Reset() => _sawDisconnected = !_vm.IsDeviceConnected;

    public void Dispose() => _vm.PropertyChanged -= OnVmPropertyChanged;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDeviceConnected) && !_vm.IsDeviceConnected) _sawDisconnected = true;
    }

    public async Task<FirmwareVersion?> AwaitDeviceVersionAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ReadAsync() is { } version) return version;
            await Task.Delay(250, cancel);
        }
        return null;
    }

    private Task<FirmwareVersion?> ReadAsync()
    {
        var done = new TaskCompletionSource<FirmwareVersion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
                done.TrySetResult(_sawDisconnected && _vm.IsDeviceConnected ? _vm.DeviceFirmwareVersion : null)))
            done.TrySetResult(null);
        return done.Task;
    }
}
