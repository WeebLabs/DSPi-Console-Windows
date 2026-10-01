using System;
using System.Collections.Generic;
using System.Linq;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DSPiConsole.Settings;

/// <summary>
/// The Settings window's main layout host. Reads pages from
/// <see cref="SettingsRegistry"/>, builds a NavigationView with one
/// collapsible group per <see cref="SettingsCategory"/>, and swaps the
/// content area when the user picks a page.
///
/// <para>
/// Owns the window's <see cref="IPendingChangeTracker"/> and passes it
/// to each page's <c>BuildContent</c>. Subscribes to the tracker's
/// <see cref="IPendingChangeTracker.Changed"/> event to repaint the
/// sidebar pending-dots (per category + per page) and the top
/// pending-changes InfoBar.
/// </para>
///
/// <para>
/// Page content is built lazily on first navigation and cached for the
/// lifetime of the window. Initial page selection is deferred to the
/// Loaded event — selecting items during construction can race the
/// NavigationView's template application and trigger XAML failures.
/// </para>
/// </summary>
public sealed partial class SettingsShell : UserControl
{
    private readonly MainViewModel _vm;
    private readonly IPendingChangeTracker _tracker;
    private readonly Dictionary<string, UIElement> _pageCache = new();
    private readonly Dictionary<string, ISettingsPage> _pageById;

    // Per-nav-item handles for the pending-dot. Keyed by page id and by
    // category enum. The dot itself is a small Ellipse we hang off each
    // NavigationViewItem.Content via composition (Grid with text + dot).
    private readonly Dictionary<string, Microsoft.UI.Xaml.Shapes.Ellipse> _pageDots = new();
    private readonly Dictionary<SettingsCategory, Microsoft.UI.Xaml.Shapes.Ellipse> _categoryDots = new();

    private bool _initialSelectionDone;

    /// <summary>The shell's pending-change tracker. Exposed so the
    /// hosting <see cref="SettingsWindow"/> can hook close-confirm and
    /// show the top InfoBar.</summary>
    public IPendingChangeTracker Tracker => _tracker;

    public SettingsShell(MainViewModel vm, IPendingChangeTracker tracker)
    {
        try
        {
            _vm = vm;
            _tracker = tracker;
            InitializeComponent();

            _pageById = SettingsRegistry.Pages.ToDictionary(p => p.Id);

            BuildNavMenu();
            Nav.SelectionChanged += OnNavSelectionChanged;
            _tracker.Changed += OnTrackerChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.OutputConfigStateChanged += OnOutputConfigStateChanged;

            Loaded += OnShellLoaded;
            PinNavigationRequested += OnPinNavigationRequested;
            Unloaded += (_, _) =>
            {
                _tracker.Changed -= OnTrackerChanged;
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.OutputConfigStateChanged -= OnOutputConfigStateChanged;
                PinNavigationRequested -= OnPinNavigationRequested;
            };

            // The Control Interfaces page gates on a device probe (0xF9) that
            // nothing in the connect flow performs — without this kick the flag
            // stays false and the page can never appear in the nav. When the
            // probe confirms support, OnVmPropertyChanged rebuilds the menu.
            ProbeControlInterfaces();
            ProbeControlSurfaces();
            SyncControlSurfacesStaged();
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell ctor", ex);
            throw;
        }
    }

    private void OnShellLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialSelectionDone) return;
        _initialSelectionDone = true;
        SyncOutputConfigStaged();
        try
        {
            // Default landing page is System › Overview — it answers "what is
            // the device doing with its pins" before you have picked a page,
            // and every claim on it is a link to the page that sets it, so it
            // works as a table of contents for the rest of the category.
            // Fall back to the first registry-ordered available page if the
            // overview isn't registered (e.g. its XAML failed to parse).
            const string DefaultPageId = "hardware.overview";
            var landing = SettingsRegistry.Pages.FirstOrDefault(p =>
                              p.Id == DefaultPageId && p.IsAvailable(_vm))
                          ?? SettingsRegistry.Pages.FirstOrDefault(p =>
                              p.Category != SettingsCategory.About && p.IsAvailable(_vm));
            if (landing != null)
            {
                var item = FindMenuItem(landing.Id);
                if (item != null) Nav.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnShellLoaded", ex);
            throw;
        }
    }

    /// <summary>
    /// Populate the NavigationView with one expandable parent per category
    /// (except About, which goes into FooterMenuItems). Each parent's
    /// children are the registry pages in that category, in Order. Pages
    /// whose <see cref="ISettingsPage.IsAvailable"/> returns false are
    /// elided — including the parent if the whole category empties out.
    /// </summary>
    private void BuildNavMenu()
    {
        var byCategory = SettingsRegistry.Pages
            .Where(p => p.IsAvailable(_vm))
            .GroupBy(p => p.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ToList());

        foreach (SettingsCategory cat in Enum.GetValues(typeof(SettingsCategory)))
        {
            if (cat == SettingsCategory.About) continue;
            if (!byCategory.TryGetValue(cat, out var pages) || pages.Count == 0) continue;

            var (title, glyph) = SettingsCategoryInfo.For(cat);

            var parent = new NavigationViewItem
            {
                Content = MakeNavContent(title, isCategory: true, out var catDot),
                Icon = new FontIcon { Glyph = glyph },
                SelectsOnInvoked = false,
                // System starts expanded — it's the category that holds
                // the default landing page (Overview, see OnShellLoaded)
                // and the most-visited group overall, so
                // collapsing it just makes the user click it open on every
                // Settings open. Other categories stay collapsed so the
                // nav tree doesn't fill the sidebar by default.
                IsExpanded = cat == SettingsCategory.System,
            };
            _categoryDots[cat] = catDot;

            foreach (var page in pages)
            {
                parent.MenuItems.Add(new NavigationViewItem
                {
                    Content = MakeNavContent(page.Title, isCategory: false, out var pageDot),
                    Tag = page.Id,
                });
                _pageDots[page.Id] = pageDot;
            }

            Nav.MenuItems.Add(parent);
        }

        if (byCategory.TryGetValue(SettingsCategory.About, out var aboutPages))
        {
            var (aboutTitle, aboutGlyph) = SettingsCategoryInfo.For(SettingsCategory.About);
            foreach (var page in aboutPages.OrderBy(p => p.Order))
            {
                Nav.FooterMenuItems.Add(new NavigationViewItem
                {
                    Content = MakeNavContent(aboutTitle, isCategory: false, out _),
                    Tag = page.Id,
                    Icon = new FontIcon { Glyph = aboutGlyph },
                });
            }
        }
    }

    /// <summary>
    /// Build a Grid that's used as a NavigationViewItem's Content: a
    /// TextBlock with the page/category label, plus a small Ellipse on
    /// the right that we toggle visible when the tracker reports a
    /// pending change in this scope. The Ellipse is returned via the
    /// out parameter so we can repaint it later.
    /// </summary>
    private static Grid MakeNavContent(string label, bool isCategory, out Microsoft.UI.Xaml.Shapes.Ellipse dot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isCategory
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        // Accent-coloured pending dot. SystemAccentColor isn't a named
        // member of Microsoft.UI.Colors; pull the active theme brush
        // from app resources so the dot tracks Light/Dark + user accent
        // changes. Fallback to Microsoft's default Windows blue if the
        // resource isn't present for some reason.
        var fill = Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var brush)
            && brush is Microsoft.UI.Xaml.Media.Brush b
                ? b
                : (Microsoft.UI.Xaml.Media.Brush)new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 0, 120, 212));
        dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8,
            Fill = fill,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(dot, 1);
        grid.Children.Add(dot);

        return grid;
    }

    /// <summary>Refresh the pending-dot visibility on every nav item
    /// and the top InfoBar's open/close state + message. Runs every
    /// time the tracker changes; cheap (10–20 ellipses + one InfoBar).</summary>
    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        // Compute which pages and categories currently have pending edits.
        var pendingPages = new HashSet<string>();
        var pendingCategories = new HashSet<SettingsCategory>();
        foreach (var change in _tracker.Pending)
        {
            pendingPages.Add(change.PageId);
            if (_pageById.TryGetValue(change.PageId, out var page))
                pendingCategories.Add(page.Category);
        }

        foreach (var kvp in _pageDots)
            kvp.Value.Visibility = pendingPages.Contains(kvp.Key)
                ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kvp in _categoryDots)
            kvp.Value.Visibility = pendingCategories.Contains(kvp.Key)
                ? Visibility.Visible : Visibility.Collapsed;

        // InfoBar: open when there are pending changes; message shows
        // count. Singular vs plural for the polished "1 pending change"
        // case so we don't read like a beta tool.
        var n = _tracker.Count;
        PendingTitle.Text = n == 1 ? "1 pending device change" : $"{n} pending device changes";
        AnimatePendingOverlay(n > 0);
    }

    private bool _overlayShown;

    /// <summary>Slide the pending-changes overlay up from (show) / down to (hide)
    /// the bottom of the window.</summary>
    private void AnimatePendingOverlay(bool show)
    {
        _overlayShown = show;
        const double hiddenY = 200; // safely past the card height so it clears the edge
        if (show)
        {
            PendingOverlay.Visibility = Visibility.Visible;
            AnimateOverlayY(0, null);
        }
        else
        {
            if (PendingOverlay.Visibility != Visibility.Visible) return; // already down
            AnimateOverlayY(hiddenY, () =>
            {
                if (!_overlayShown) PendingOverlay.Visibility = Visibility.Collapsed;
            });
        }
    }

    private void AnimateOverlayY(double to, Action? onCompleted)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, PendingTransform);
        Storyboard.SetTargetProperty(anim, "Y");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (onCompleted != null) sb.Completed += (_, _) => onCompleted();
        sb.Begin();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDeviceConnected))
        {
            DispatcherQueue.TryEnqueue(SyncOutputConfigStaged);
            DispatcherQueue.TryEnqueue(ProbeControlInterfaces); // re-probe on (re)connect
            DispatcherQueue.TryEnqueue(ProbeControlSurfaces);
        }
        else if (e.PropertyName == nameof(MainViewModel.ControlInterfacesSupported)
                 || e.PropertyName == nameof(MainViewModel.ControlSurfacesSupported))
        {
            DispatcherQueue.TryEnqueue(RebuildNavMenu);
        }
        else if (e.PropertyName == nameof(MainViewModel.CsDirty))
        {
            DispatcherQueue.TryEnqueue(SyncControlSurfacesStaged);
        }
    }

    /// <summary>Probe support and re-read the UART / I2C config + live status.
    /// Runs on every (re)connect, not just while support is still unknown — the
    /// flag being true says the last device answered 0xF9, not that the cached
    /// configs still describe the device now on the bus.</summary>
    private void ProbeControlInterfaces()
    {
        if (_vm.IsDeviceConnected)
            _ = System.Threading.Tasks.Task.Run(_vm.FetchControlInterfaces);
    }

    /// <summary>Probe caps and read the whole live control-surface config. Unlike
    /// the other capability flags this one is never seeded from the bulk blob, so
    /// without this probe ControlSurfacesSupported stays false, the three Control
    /// pages never appear, and nothing would ever trigger the fetch. Reading the
    /// config here (not just the caps) also means the pages build populated.</summary>
    private void ProbeControlSurfaces()
    {
        if (_vm.IsDeviceConnected)
            _ = System.Threading.Tasks.Task.Run(_vm.FetchControlSurfaces);
    }

    /// <summary>Rebuild the nav after a page's availability changed (e.g. the
    /// control-interfaces probe answered), preserving the current selection and
    /// pending-dots. No-op when the set of shown pages is already correct.</summary>
    private void RebuildNavMenu()
    {
        var available = SettingsRegistry.Pages
            .Where(p => p.Category != SettingsCategory.About && p.IsAvailable(_vm))
            .Select(p => p.Id)
            .ToHashSet();
        if (available.SetEquals(_pageDots.Keys)) return;

        var selectedId = (Nav.SelectedItem as NavigationViewItem)?.Tag as string;
        Nav.MenuItems.Clear();
        Nav.FooterMenuItems.Clear();
        _pageDots.Clear();
        _categoryDots.Clear();
        BuildNavMenu();
        if (selectedId != null && FindMenuItem(selectedId) is { } item)
            Nav.SelectedItem = item;
        OnTrackerChanged(this, EventArgs.Empty); // repaint dots on the fresh items
    }

    private void OnOutputConfigStateChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(SyncOutputConfigStaged);

    /// <summary>
    /// Reflect the VM's unsaved control-surface edits as staged entries, the same
    /// way <see cref="SyncOutputConfigStaged"/> does for the IO block. Both are
    /// live-on-the-device-but-not-in-flash, so they belong in the same prompt
    /// rather than each editor growing its own Save/Revert bar. Each entry's Apply
    /// persists the whole config via CS Save; the call no-ops once clean, so a
    /// batch of entries still writes flash once.
    /// </summary>
    private void SyncControlSurfacesStaged()
    {
        var desired = _vm.IsDeviceConnected
            ? _vm.GetControlSurfaceChanges()
            : System.Array.Empty<PresetDiff.IoChange>();

        var desiredKeys = new HashSet<string>();
        foreach (var c in desired) desiredKeys.Add(c.Key);

        foreach (var pc in _tracker.Pending.Where(p => p.Key.StartsWith("cs.")).ToList())
            if (!desiredKeys.Contains(pc.Key))
                _tracker.Discard(pc.Key);

        var existing = _tracker.Pending.ToDictionary(p => p.Key);
        foreach (var c in desired)
        {
            if (existing.TryGetValue(c.Key, out var ex) && ex.NewDisplay == c.New)
                continue;
            _tracker.Stage(new PendingChange(
                Key: c.Key,
                PageId: PageForCsKey(c.Key),
                FieldLabel: c.Label,
                OldDisplay: c.Old,
                NewDisplay: c.New,
                Apply: () => System.Threading.Tasks.Task.Run(() => _vm.CsSave())));
        }
    }

    /// <summary>Send each control-surface change's pending-dot to the page that
    /// edits it, so a macro edit doesn't dot the Control Surfaces page.</summary>
    private static string PageForCsKey(string key) =>
        key.StartsWith("cs.group") ? "control.groups"
        : key.StartsWith("cs.macro") ? "control.macros"
        : "control.surfaces";   // cs.slot.* / cs.ir.*

    /// <summary>
    /// Reflect the VM's unsaved independent-mode IO-block changes as staged
    /// entries in the shared tracker — one per changed field, so the pending-
    /// changes InfoBar shows an accurate device-level count. The edits are already
    /// live-applied to RAM; each entry's Apply persists the whole block via "Save
    /// Output Config" (0x52), guarded so only one flash write happens per batch.
    /// </summary>
    private void SyncOutputConfigStaged()
    {
        var desired = _vm.IsDeviceConnected
            ? _vm.GetOutputConfigChanges()
            : System.Array.Empty<PresetDiff.IoChange>();

        var desiredKeys = new HashSet<string>();
        foreach (var c in desired) desiredKeys.Add(c.Key);

        // Drop io.* entries no longer changed (e.g. reverted to the saved value).
        foreach (var pc in _tracker.Pending.Where(p => p.Key.StartsWith("io.")).ToList())
            if (!desiredKeys.Contains(pc.Key))
                _tracker.Discard(pc.Key);

        // Stage new / changed entries; skip identical re-stages to avoid churn.
        var existing = _tracker.Pending.ToDictionary(p => p.Key);
        foreach (var c in desired)
        {
            if (existing.TryGetValue(c.Key, out var ex) && ex.NewDisplay == c.New)
                continue;
            _tracker.Stage(new PendingChange(
                Key: c.Key,
                PageId: PageForIoKey(c.Key),
                FieldLabel: c.Label,
                OldDisplay: c.Old,
                NewDisplay: c.New,
                Apply: async () => await _vm.SaveOutputConfig()));
        }
    }

    /// <summary>Map an IO-block change key to the settings page that owns it, so
    /// the sidebar pending-dot lands on the right page instead of always the
    /// Output Assignment page.</summary>
    private static string PageForIoKey(string key) =>
        // Master Clock owns what every interface shares: the generated rate and
        // all of MCK. io.i2s-rate has to be matched exactly, before the io.i2s-
        // prefix below claims it.
        key is "io.i2s-rate" ? "hardware.master-clock"
        : key.StartsWith("io.mck") ? "hardware.master-clock"
        : key.StartsWith("io.spdif") ? "hardware.spdif-input"
        : key.StartsWith("io.adat") ? "hardware.adat"        // both directions share one page
        // Clock mode, clock pins, BCK, slave BCK, RX pins and channel count are
        // all one page now.
        : key.StartsWith("io.i2s-") || key.StartsWith("io.bck") ? "hardware.i2s"
        // io.pin.* / io.slot.*, and io.limiter.*: the limiter has no settings
        // page (it lives on the channel page), so its outputs' page owns it.
        : "hardware.output-assignment";

    private async void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        DiscardButton.IsEnabled = false;
        try
        {
            bool hadCsChanges = _tracker.Pending.Any(p => p.Key.StartsWith("cs."));
            _tracker.DiscardAll();
            // Output-config and control-surface edits are already live-applied to
            // RAM, so dropping the staged entries isn't enough — both need their
            // own revert to the last saved values. (The other staged pages were
            // never applied, so DiscardAll IS their revert.) On completion the
            // dirty flags clear and the entries stay gone via
            // OnOutputConfigStateChanged / the CsDirty property change.
            await _vm.RevertOutputConfig();
            // Reloads the stored config and raises ControlSurfacesReloaded, which
            // is what re-seeds the editor pages' drafts.
            if (hadCsChanges) await System.Threading.Tasks.Task.Run(() => _vm.CsRevert());
            HardwarePins.RaisePinAssignmentsChanged();
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnDiscardClick", ex);
        }
        finally
        {
            DiscardButton.IsEnabled = true;
            // Repaint the visible page so it reflects the reverted values (its
            // combos re-sync from VM state; other pages refresh on next nav).
            RefreshVisiblePage();
        }
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        try
        {
            var report = await _tracker.ApplyAllAsync();
            // On full success the tracker empties → Changed fires → the overlay
            // slides away. On partial failure it stays up with the remaining
            // count; surface the failure in the title line.
            if (report.Failed > 0)
                PendingTitle.Text = report.Applied > 0
                    ? $"{report.Applied} applied, {report.Failed} failed"
                    : $"{report.Failed} failed — retry?";
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnApplyClick", ex);
            PendingTitle.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            DiscardButton.IsEnabled = true;
            RefreshVisiblePage();
        }
    }

    /// <summary>Re-Refresh the currently-displayed page so its UI
    /// reflects the post-tracker-change state (clears inline diff
    /// chips, etc.). No-op if no page is selected.</summary>
    private void RefreshVisiblePage()
    {
        if (PageHost.Content is SettingsModule mod)
            mod.RefreshFromShell();
    }

    /// <summary>Raised when something asks to be shown where a GPIO is set —
    /// the Overview's map, which is read-only and so has nowhere to send a click
    /// except the page that owns the pin. Static because the settings window is
    /// single-instance, the same reason <see cref="HardwarePins"/> broadcasts its
    /// pin changes that way.</summary>
    internal static event Action<string, byte>? PinNavigationRequested;

    /// <summary>Ask the open settings window to show where <paramref name="pin"/>
    /// is set. Does nothing if no shell is listening.</summary>
    internal static void RequestPin(string pageId, byte pin) =>
        PinNavigationRequested?.Invoke(pageId, pin);

    private void OnPinNavigationRequested(string pageId, byte pin) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindMenuItem(pageId) is not { } item) return;
            // Selecting the item builds (or reuses) the page through the same
            // path a click down the sidebar takes.
            Nav.SelectedItem = item;
            HighlightWhenReady(pin, attempts: 8);
        });

    /// <summary>Flash the control that sets the pin, once the page has one to
    /// flash. A page that was just built has not laid out yet, and one that
    /// fills itself from the device — Control Surfaces — is not merely late but
    /// waiting on a read, so this retries briefly rather than asking once and
    /// giving up on the answer it was always going to get.</summary>
    private void HighlightWhenReady(byte pin, int attempts)
    {
        if (PageHost.Content is IPinHighlightPage page && page.HighlightPin(pin)) return;
        if (attempts <= 0) return;
        var retry = DispatcherQueue.CreateTimer();
        retry.Interval = TimeSpan.FromMilliseconds(150);
        retry.IsRepeating = false;
        retry.Tick += (_, _) => HighlightWhenReady(pin, attempts - 1);
        retry.Start();
    }

    private NavigationViewItem? FindMenuItem(string pageId)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if ((item.Tag as string) == pageId) return item;
            foreach (var child in item.MenuItems.OfType<NavigationViewItem>())
                if ((child.Tag as string) == pageId) return child;
        }
        foreach (var item in Nav.FooterMenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == pageId) return item;
        return null;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            if (item.Tag is not string pageId) return;
            if (!_pageById.TryGetValue(pageId, out var page)) return;

            if (!_pageCache.TryGetValue(pageId, out var content))
            {
                content = page.BuildContent(_vm, _tracker);
                _pageCache[pageId] = content;
            }

            HeaderIcon.Glyph = page.IconGlyph;
            HeaderTitle.Text = page.Title;
            // The old page may still have a reveal steering the shared
            // scroller; whatever it was bringing into view is not on the page
            // the user just asked for.
            Reveal.Cancel(PageScroll);
            PageHost.Content = content;
            // Every page shares this one scroller, so a page opens wherever the
            // last one was left unless it is sent back to the top. The layout is
            // flushed first: the swap only invalidates it, and scroll requests
            // are queued for the pass that runs it — resetting before that pass
            // let the old page's last request land after the reset, which is how
            // the Overview kept turning up scrolled. Instant, not animated: an
            // animation here would be the new page sliding up from an offset
            // that was never its own.
            PageScroll.UpdateLayout();
            PageScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        catch (Exception ex)
        {
            SettingsWindow.WriteCrashLog("SettingsShell.OnNavSelectionChanged", ex);
        }
    }
}
