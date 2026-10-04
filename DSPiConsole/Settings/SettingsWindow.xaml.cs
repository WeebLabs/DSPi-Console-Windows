using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DSPiConsole.Settings;

/// <summary>
/// The modeless Settings window. Owns one <see cref="SettingsShell"/>
/// instance bound to the application's <see cref="MainViewModel"/>.
///
/// <para>
/// Title-bar styling matches the main window verbatim:
/// <c>ExtendsContentIntoTitleBar=true</c> + a transparent drag-region
/// Grid + a <see cref="DesktopAcrylicController"/> backdrop with the
/// same dark tint. This is the proven pattern in this codebase —
/// alternatives (Mica via <c>SystemBackdrop</c>, manual
/// <c>AppWindowTitleBar</c> color overrides) were tried and produced
/// inconsistent results across Win10/Win11.
/// </para>
///
/// <para>
/// Single-instance is enforced by <c>MainWindow</c> — it tracks an
/// optional reference and activates the existing window instead of
/// creating a duplicate.
/// </para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 680;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>The window's pending-change tracker. Single instance
    /// per window — closed-and-reopened settings windows get fresh
    /// trackers, so abandoned edits don't leak across sessions.</summary>
    internal IPendingChangeTracker Tracker { get; }

    // Acrylic backdrop — mirror MainWindow. The controller must live
    // as long as the window does; disposing it on Closed releases the
    // composition resources.
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public SettingsWindow(MainViewModel vm)
    {
        try
        {
            Tracker = new PendingChangeTracker();

            InitializeComponent();
            Title = "DSPi Console — Settings";

            // Title bar wiring — matches MainWindow.cs lines 103/105/106:
            //   ExtendsContentIntoTitleBar=true makes our content draw to
            //   the top of the window. The acrylic backdrop tints the
            //   whole window (including the title-bar strip) dark.
            //   SetTitleBar(AppTitleBar) tells the OS which area of our
            //   content is the drag region; caption buttons are drawn
            //   by Windows on the right edge of that area, with glyph
            //   colors picked from the App's RequestedTheme (Dark per
            //   App.xaml).
            this.ExtendsContentIntoTitleBar = true;
            SetupAcrylicBackdrop();
            this.SetTitleBar(AppTitleBar);

            // Build the shell first; any XAML-load or VM-binding failure
            // surfaces here rather than mid-Activate. The shell receives
            // both the VM and the tracker; pages get the tracker via
            // their BuildContent(vm, tracker) call.
            var shell = new SettingsShell(vm, Tracker);
            ContentHost.Children.Add(shell);

            // Size the window to a comfortable default. Anything that
            // depends on the HWND (size / icon) happens AFTER the
            // visual tree is set up.
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            appWindow.Resize(new SizeInt32(
                (int)(DefaultWidth * scale),
                (int)(DefaultHeight * scale)));

            try { appWindow.SetIcon("Assets/Icon.ico"); }
            catch { /* asset missing in some configs — not fatal */ }

            // Intercept close to prompt-on-pending-changes. AppWindow.Closing
            // fires before the window is destroyed and provides a cancel
            // hook via args.Cancel = true. The ContentDialog hangs off the
            // shell's XamlRoot so it renders within this window.
            appWindow.Closing += OnAppWindowClosing;
        }
        catch (Exception ex)
        {
            WriteCrashLog("SettingsWindow ctor", ex);
            throw;
        }
    }

    /// <summary>
    /// Stand up the desktop-acrylic backdrop. Copy of
    /// <c>MainWindow.SetupAcrylicBackdrop</c> — same tint, same opacity,
    /// same lifecycle pattern. Keeps the two windows' chrome visually
    /// indistinguishable.
    /// </summary>
    private void SetupAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
            return;

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true  // Keep translucency visible even when unfocused
        };

        this.Closed += (_, _) =>
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _backdropConfig = null;
        };

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 32),
            TintOpacity = 0.5f,
            LuminosityOpacity = 0.8f
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    // Re-entry guard: when we call appWindow.Close() programmatically
    // after the user picks Apply / Discard, we don't want the Closing
    // handler to re-prompt. Set _allowClose=true just before the call.
    private bool _allowClose;
    /// <summary>A pending-changes prompt is on screen.</summary>
    private bool _confirming;

    /// <summary>
    /// Close from code (the main window's Settings gear), asking about pending
    /// changes exactly as the close button does: <see cref="Window.Close"/>
    /// does not raise AppWindow.Closing, so calling it directly would drop
    /// staged changes without a word. A second request while the prompt is
    /// showing does nothing.
    /// </summary>
    internal async void RequestClose()
    {
        if (_confirming) return;
        if (Tracker.Count == 0)
        {
            _allowClose = true;
            Close();
            return;
        }
        Activate();
        try { await ConfirmCloseAsync(); }
        catch (Exception ex) { WriteCrashLog("SettingsWindow.RequestClose", ex); }
    }

    /// <summary>
    /// As <see cref="RequestClose"/>, for a caller that goes on only once
    /// Settings has closed (the main window quitting): true when it closed
    /// (nothing pending, saved, or discarded), false when the user cancelled
    /// or a save failed and the window stays open.
    /// </summary>
    internal async Task<bool> TryCloseAsync()
    {
        if (_confirming) return false;
        if (Tracker.Count == 0)
        {
            _allowClose = true;
            Close();
            return true;
        }
        Activate();
        try { await ConfirmCloseAsync(); }
        catch (Exception ex) { WriteCrashLog("SettingsWindow.TryCloseAsync", ex); }
        return _allowClose;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || Tracker.Count == 0) return;
        if (_confirming) { args.Cancel = true; return; }

        // We need a synchronous decision here, but ContentDialog is
        // async. The pattern: cancel the close, await the user's
        // choice, then either commit or discard and re-close.
        args.Cancel = true;
        try
        {
            await ConfirmCloseAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog("SettingsWindow.OnAppWindowClosing", ex);
        }
    }

    private async Task ConfirmCloseAsync()
    {
        _confirming = true;
        try { await ConfirmCloseCoreAsync(); }
        finally { _confirming = false; }
    }

    private async Task ConfirmCloseCoreAsync()
    {
        // Same flash/global clarifier as the InfoBar in SettingsShell.xaml.
        // Settings tracked here are all device-flash writes (master volume
        // mode, DAC mute config, output assignments, startup preset, …),
        // distinct from per-preset DSP state — spell that out so the user
        // knows what "Save to flash" actually does at close time.
        var countLine = Tracker.Count == 1
            ? "You have 1 unsaved device setting."
            : $"You have {Tracker.Count} unsaved device settings.";
        var dialog = new ContentDialog
        {
            Title = "Pending device changes",
            Content = countLine
                + " Saving writes them directly to device flash and applies them globally — they are independent of the currently loaded preset."
                + "\n\nSave to flash before closing?",
            PrimaryButtonText = "Save to flash",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ContentHost.XamlRoot,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            // Apply, then close. If Apply fails partially the
            // window stays open so the user can retry.
            var report = await Tracker.ApplyAllAsync();
            if (report.Failed == 0)
            {
                _allowClose = true;
                this.Close();
            }
            // If failures: leave window open; InfoBar already shows
            // failure state via the shell's tracker.Changed handler.
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // Discard and close.
            Tracker.DiscardAll();
            _allowClose = true;
            this.Close();
        }
        // Cancel: do nothing; window stays open.
    }

    /// <summary>Append a crash entry to <c>%LOCALAPPDATA%\DSPiConsole\settings-crash.log</c>.
    /// Best-effort — IO errors here are swallowed (we're already in a
    /// failing path; we just want a breadcrumb when we can leave one).</summary>
    internal static void WriteCrashLog(string context, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DSPiConsole");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings-crash.log");
            File.AppendAllText(path,
                $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {context} ----\n{ex}\n\n");
        }
        catch { /* nothing to do */ }
    }
}
