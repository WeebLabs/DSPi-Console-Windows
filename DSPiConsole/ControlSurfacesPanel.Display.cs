using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Settings;
using Microsoft.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The I2C display component (firmware caps v10; control_surfaces_display_spec).
/// A panel is a container binding like the IR receiver — SDA, SCL, a model and an
/// address — and everything it shows lives apart from it: one device-global
/// config record and a table of pages, each page a {noun, target} drawn from the
/// same noun table the bindings use.
///
/// <para>
/// The whole card applies as it is edited. The wiring fields are each valid
/// alone - any model, pair and address make a lawful binding - so there is no
/// batch for an Apply button to hold together, and the config and page halves
/// were live already. A refused edit stays on the card with the reason stated,
/// and Save and Revert still govern whether any of it sticks, through the
/// settings window's pending-changes prompt.
/// </para>
/// </summary>
public sealed partial class ControlSurfacesPanel
{
    // The slot whose card is currently hosting the display sections, and the
    // panel those sections live in — so a live edit can refill them in place
    // instead of rebuilding the whole card body under the user's cursor.
    private int _displaySlot = -1;
    private StackPanel? _displayHost;
    private TextBlock? _displayStateLabel;
    private TextBlock? _displayStateDetail;
    private Ellipse? _displayStateDot;

    // The live half is split so an edit repaints only what it changed. The config
    // rows change shape with the config (a cycle mode swaps the page picker for a
    // dwell) and no page row depends on any of them; the page rows change only
    // when a page does. Rebuilding the lot on every write flashed every checkbox
    // in the list, and the panel's own cycling did it once a dwell.
    private StackPanel? _displayCfgHost;
    private StackPanel? _displayPagesHost;
    private StackPanel? _displayPagesList;
    private TextBlock? _displayWarning;
    private ComboBox? _displayHomePageCombo;
    private readonly Dictionary<int, FrameworkElement> _displayPageRows = new();
    private readonly Dictionary<int, Ellipse> _displayPageMarkers = new();

    // A display write blocks on the device's deferred-apply poll, and the view
    // model serializes them anyway (the config and page 0 share a poll key). Count
    // them rather than dropping an edit made while one is in flight: the rows are
    // refilled once, when the last write lands, so nothing rebuilds under a picker
    // the user is still using.
    private int _displayWrites;
    private bool DisplayApplying => _displayWrites > 0;

    // The panel's own live state (which page is up, whether editing is armed) is
    // the one piece of display state the firmware doesn't push a notification
    // for. Poll it only while a display card is actually open.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _displayPoll;
    private const int DisplayPollMs = 1000;

    /// <summary>Drop every handle into a display card that is no longer in the
    /// tree, so a later refresh fills nothing rather than a detached panel.</summary>
    private void ClearDisplayHandles()
    {
        _displaySlot = -1;
        _displayHost = null;
        _displayCfgHost = null;
        _displayPagesHost = null;
        _displayPagesList = null;
        _displayWarning = null;
        _displayHomePageCombo = null;
        _displayStateLabel = null;
        _displayStateDetail = null;
        _displayStateDot = null;
        _displayPageRows.Clear();
        _displayPageMarkers.Clear();
    }

    // ── Card body ────────────────────────────────────────────────────────────

    /// <summary>Fill a display card's body: the panel's wiring behind Apply, then
    /// what it shows.</summary>
    private void PopulateDisplayBody(int slot, StackPanel panel)
    {
        _displaySlot = slot;

        // The name row and its divider are already in place: every card body opens
        // with them, whatever the component.
        panel.Children.Add(SectionHeading("Wiring"));
        panel.Children.Add(BuildDisplayModelRow(slot));
        panel.Children.Add(BuildDisplayPinRow(slot));
        panel.Children.Add(BuildDisplayAddressRow(slot));
        panel.Children.Add(BuildDisplayStateRow());

        // Everything below is device-global and applies live, so it is hosted in
        // its own panel that a write can refill on its own.
        var host = new StackPanel { Spacing = 8 };
        _displayHost = host;
        panel.Children.Add(host);
        PopulateDisplayHost();
        UpdateDisplayPoll();
    }

    /// <summary>Build (or rebuild) the whole live-applying half of the display
    /// card. Its three parts refresh independently afterwards — see the field
    /// declarations — so this runs only when the card itself is built, or when
    /// something outside it moves everything at once (a group edit, which every
    /// page's target picker lists).</summary>
    private void PopulateDisplayHost()
    {
        var host = _displayHost;
        if (host == null) return;
        bool wasBuilding = _building;
        _building = true;
        try
        {
            host.Children.Clear();
            // The rows below have no Apply button to carry a refusal, so the last
            // one the device turned down is stated here until the next write. It
            // keeps its place in the tree and hides, so saying it costs no layout.
            _displayWarning = WarningLine("");
            host.Children.Add(_displayWarning);
            _displayCfgHost = new StackPanel { Spacing = 8 };
            host.Children.Add(_displayCfgHost);
            _displayPagesHost = new StackPanel { Spacing = 8 };
            host.Children.Add(_displayPagesHost);
        }
        finally { _building = wasBuilding; }

        RefreshDisplayWarning();
        PopulateDisplayCfgHost();
        PopulateDisplayPagesHost();
    }

    /// <summary>What the panel rests on, how editing is armed, and how it looks.
    /// Refilled on a config write, which is the only thing that changes it.</summary>
    private void PopulateDisplayCfgHost()
    {
        var host = _displayCfgHost;
        if (host == null) return;
        bool wasBuilding = _building;
        _building = true;
        try
        {
            host.Children.Clear();
            _displayHomePageCombo = null;
            var cfg = _vm.CsDisplayCfg;

            var behavior = new StackPanel { Spacing = 8 };
            behavior.Children.Add(SectionHeading("Behavior"));
            behavior.Children.Add(BuildDisplayModeRow(cfg));
            if (cfg.Mode == CsDisplayMode.Fixed) behavior.Children.Add(BuildDisplayHomePageRow(cfg));
            else
                behavior.Children.Add(BuildDisplaySecondsRow(
                    "Cycle Every (s)", "How long each page stays up.",
                    cfg.Dwell, CsLimits.DisplayMinDwell,
                    (c, v) => c.Dwell = v));
            behavior.Children.Add(BuildDisplaySecondsRow(
                "Pop-Up Hold (s)",
                "Duration for which a change remains on-screen. Zero turns pop-ups off.",
                cfg.OverlayHold, 0, (c, v) => c.OverlayHold = v));
            if (cfg.OverlayHold > 0)
                behavior.Children.Add(BuildDisplayFlagToggle(
                    CsDisplayCfgFlags.OverlayAny, "All changes pop up",
                    "Show changes made by a knob, button or remote key even if the parameter "
                    + "doesn't correspond to a dashboard page."));

            behavior.Children.Add(BuildDisplaySecondsRow(
                "Editing Times Out (s)",
                "Disarm editing after this long untouched. Zero leaves it armed until switched off.",
                cfg.EditTimeout, 0, (c, v) => c.EditTimeout = v));
            behavior.Children.Add(BuildDisplayFlagToggle(
                CsDisplayCfgFlags.EditGated, "Arm before editing",
                "When enabled, an encoder or button browses pages unless Allow Editing is "
                + "toggled. When disabled, an encoder or button will always adjust the "
                + "displayed value."));
            // Gated with nothing able to arm it is a dead end the device cannot
            // refuse — both halves are valid on their own, and the control just
            // browses forever. Only said once a control actually depends on it.
            if (DisplayEditingUnreachable())
                behavior.Children.Add(WarningLine(
                    "Nothing can arm editing, so a Browse/Adjust control can only browse pages. "
                    + "Bind a button or remote key to Allow Editing."));

            var appearance = new StackPanel { Spacing = 8 };
            appearance.Children.Add(SectionHeading("Appearance"));
            appearance.Children.Add(BuildDisplayBrightnessRow(cfg));
            if (_vm.CsDisplayAlignSupported)
            {
                appearance.Children.Add(BuildDisplayAlignRow(
                    "Name Alignment", "Horizontal placement of the current page's name.",
                    cfg.LabelAlign, (c, a) => c.LabelAlign = a));
                appearance.Children.Add(BuildDisplayAlignRow(
                    "Value Alignment", "Horizontal placement of the current page's value.",
                    cfg.ValueAlign, (c, a) => c.ValueAlign = a));
            }

            // Side by side rather than stacked: each section is a handful of
            // narrow rows, and one atop the other left the card tall and empty
            // down its right half. Two sections, one column each - the editing
            // rows live under Behavior, which is what they govern.
            var columns = new Grid { ColumnSpacing = 28 };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(behavior, 0);
            columns.Children.Add(behavior);
            Grid.SetColumn(appearance, 1);
            columns.Children.Add(appearance);
            host.Children.Add(columns);
        }
        finally { _building = wasBuilding; }
    }

    /// <summary>The page list. Refilled only when a page appears or vanishes; a
    /// page edited in place replaces its own row, and the on-screen marker moves
    /// without rebuilding anything at all.</summary>
    private void PopulateDisplayPagesHost()
    {
        var host = _displayPagesHost;
        if (host == null) return;
        bool wasBuilding = _building;
        _building = true;
        try
        {
            host.Children.Clear();
            _displayPageRows.Clear();
            _displayPageMarkers.Clear();

            host.Children.Add(SectionHeading("Dashboard Pages",
                $"{ActiveDisplayPages().Count()} of {_vm.CsDisplayPageCount}"));
            // A touch more air than the config rows: each of these packs
            // several compact controls, and at the config rhythm they read as
            // a block rather than a list.
            var pages = new StackPanel { Spacing = 10 };
            _displayPagesList = pages;
            foreach (int i in ActiveDisplayPages())
            {
                var row = BuildDisplayPageRow(i);
                _displayPageRows[i] = row;
                pages.Children.Add(row);
            }
            host.Children.Add(pages);
            host.Children.Add(BuildDisplayAddPageRow());
        }
        finally { _building = wasBuilding; }
    }

    /// <summary>Swap one page's row for a freshly built one, leaving the rest of
    /// the list alone.</summary>
    private void RefreshDisplayPageRow(int index)
    {
        if (_displayPagesList == null || !_displayPageRows.TryGetValue(index, out var old)
            || _displayPagesList.Children.IndexOf(old) is var pos && pos < 0)
        {
            PopulateDisplayPagesHost();
            return;
        }
        bool wasBuilding = _building;
        _building = true;
        try
        {
            var row = BuildDisplayPageRow(index);
            _displayPageRows[index] = row;
            _displayPagesList.Children[pos] = row;
        }
        finally { _building = wasBuilding; }
    }

    /// <summary>Move the on-screen marker. The panel cycles on its own, so this
    /// runs once a second while a display card is open — nothing is rebuilt for
    /// it, only two dots change opacity.</summary>
    private void RefreshDisplayPageMarkers()
    {
        byte current = _vm.CsDisplayStatus.CurrentPage;
        foreach (var (index, marker) in _displayPageMarkers)
        {
            bool onScreen = current == index;
            marker.Opacity = onScreen ? 1 : 0;
            ToolTipService.SetToolTip(marker, onScreen ? "Currently on screen" : null);
        }
    }

    /// <summary>State the last refused live write, or take the line away.</summary>
    private void RefreshDisplayWarning()
    {
        if (_displayWarning == null) return;
        bool refused = _vm.CsDisplayLastStatus != CsStatus.Success;
        _displayWarning.Text = refused ? CsStatus.Message(_vm.CsDisplayLastStatus) : "";
        _displayWarning.Visibility = refused ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Restate the home-page picker's captions after a page edit: it
    /// lists pages by what they show. Items and selection are untouched, so no
    /// SelectionChanged fires.</summary>
    private void RelabelDisplayHomePages()
    {
        if (_displayHomePageCombo == null) return;
        foreach (var o in _displayHomePageCombo.Items)
            if (o is ComboBoxItem item && item.Tag is int i) item.Content = DisplayPageMenuLabel(i);
    }

    // ── Wiring rows ──────────────────────────────────────────────────────────

    private FrameworkElement BuildDisplayModelRow(int slot)
    {
        var combo = new ComboBox { MinWidth = 220 };
        int sel = -1;
        for (int m = (int)CsDisplayModel.Lcd1602; m < _vm.CsDisplayModelCount; m++)
        {
            if (m == _drafts[slot].Index) sel = combo.Items.Count;
            combo.Items.Add(new ComboBoxItem { Content = CsDisplayModels.Name(m), Tag = m });
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not int model) return;
            // The address is stored as 0 = "the model's usual address", so a model
            // change carries that convention with it rather than stranding the
            // previous model's default as a literal.
            if (_drafts[slot].Value != 0
                && (byte)_drafts[slot].Value == CsDisplayModels.DefaultAddress(_drafts[slot].Index))
                _drafts[slot].Value = 0;
            _drafts[slot].Index = (byte)model;
            // Applying rebuilds the body, which renames the address default.
            ApplyDisplayWiring(slot);
        };
        var row = Row("Model", combo);
        ToolTipService.SetToolTip(combo, "The type of display connected.");
        return row;
    }

    /// <summary>SCL is not offered separately: the pin mux fixes it as SDA + 1, so
    /// a second picker could only build pairs the device refuses.</summary>
    private FrameworkElement BuildDisplayPinRow(int slot)
    {
        var combo = new ComboBox { MinWidth = 220 };

        void Populate()
        {
            byte current = _drafts[slot].Gpio0;
            var pairs = I2cSdaCandidates(slot).ToList();
            if (!pairs.Contains(current) && current != CsLimits.GpioUnused) pairs.Add(current);
            pairs.Sort();
            combo.Items.Clear();
            int sel = -1;
            for (int i = 0; i < pairs.Count; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = $"GPIO {pairs[i]} / {pairs[i] + 1}", Tag = pairs[i] });
                if (pairs[i] == current) sel = i;
            }
            combo.SelectedIndex = sel;
        }
        Populate();

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is byte sda)
            {
                _drafts[slot].Gpio0 = sda;
                _drafts[slot].Gpio1 = (byte)(sda + 1);
                ApplyDisplayWiring(slot);
            }
        };
        if (!_pinRefreshers.TryGetValue(slot, out var list))
            _pinRefreshers[slot] = list = new List<Action>();
        list.Add(Populate);
        // One picker for the pair, so SDA and SCL both resolve to it.
        _slotPinCombos[(slot, false)] = combo;
        _slotPinCombos[(slot, true)] = combo;

        ToolTipService.SetToolTip(combo, "Clock and data pins are chosen in fixed pairs.");
        return Row("SDA/SCL Pins", combo);
    }

    private FrameworkElement BuildDisplayAddressRow(int slot)
    {
        byte modelDefault = CsDisplayModels.DefaultAddress(_drafts[slot].Index);
        var combo = new ComboBox { MinWidth = 220 };
        combo.Items.Add(new ComboBoxItem
        {
            Content = $"Default (0x{modelDefault:X2})",
            Tag = (short)0,
        });
        foreach (byte addr in CsDisplayModels.CommonAddresses)
            combo.Items.Add(new ComboBoxItem { Content = $"0x{addr:X2}", Tag = (short)addr });
        int sel = 0;
        for (int i = 0; i < combo.Items.Count; i++)
            if (combo.Items[i] is ComboBoxItem it && it.Tag is short v && v == _drafts[slot].Value) sel = i;
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is short addr)
            { _drafts[slot].Value = addr; ApplyDisplayWiring(slot); }
        };
        ToolTipService.SetToolTip(combo,
            $"7-bit I2C address. Default is the model's usual one (0x{modelDefault:X2}).");
        return Row("Address", combo);
    }

    /// <summary>Live panel state. A miswired or absent module announces itself
    /// through the I2C abort counter: the driver keeps retrying, so the count
    /// climbs rather than the feature failing silently. The number itself tells
    /// nobody anything they can act on, so it only decides whether the wiring
    /// advice shows.</summary>
    private FrameworkElement BuildDisplayStateRow()
    {
        var stack = new StackPanel { Spacing = 1 };
        _displayStateDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _displayStateLabel = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        head.Children.Add(_displayStateDot);
        head.Children.Add(_displayStateLabel);
        _displayStateDetail = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush,
        };
        stack.Children.Add(head);
        stack.Children.Add(_displayStateDetail);
        RefreshDisplayStateRow();
        return Row("Panel State", stack);
    }

    private void RefreshDisplayStateRow()
    {
        if (_displayStateLabel == null || _displayStateDetail == null || _displayStateDot == null) return;
        var st = _vm.CsDisplayStatus;
        // The dot carries the colour - the same language as the on-screen page
        // marker down in the page list - and the words stay in the text colour,
        // which reads as a status rather than a stray line of tinted text.
        var (text, colour) = st.InitState switch
        {
            CsDisplayInitState.Live => ("Running", Color.FromArgb(255, 100, 200, 140)),
            CsDisplayInitState.Starting => ("Starting up", Color.FromArgb(255, 150, 150, 150)),
            CsDisplayInitState.Error => ("Not responding", Color.FromArgb(255, 240, 180, 90)),
            _ => ("Not started", Color.FromArgb(255, 150, 150, 150)),
        };
        _displayStateDot.Fill = new SolidColorBrush(colour);
        _displayStateLabel.Text = text;
        _displayStateDetail.Text = st.NakCount > 0
            ? "Check wiring, pull-up resistors, and the address."
            : "Reported by the device.";
    }

    // ── Config rows ──────────────────────────────────────────────────────────

    private FrameworkElement BuildDisplayModeRow(CsDisplayCfg cfg)
    {
        var combo = new ComboBox { MinWidth = 220 };
        combo.Items.Add(new ComboBoxItem { Content = "One page", Tag = CsDisplayMode.Fixed });
        combo.Items.Add(new ComboBoxItem { Content = "Cycle Dashboard", Tag = CsDisplayMode.CycleSelected });
        combo.Items.Add(new ComboBoxItem { Content = "Cycle All", Tag = CsDisplayMode.CycleAll });
        combo.SelectedIndex = Math.Clamp((int)cfg.Mode, 0, 2);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not CsDisplayMode mode) return;
            var next = _vm.CsDisplayCfg.Clone();
            next.Mode = mode;
            // Either cycle mode has a dwell floor, so entering one with an unset
            // dwell would otherwise be refused on a setting the user never saw.
            if (mode != CsDisplayMode.Fixed)
                next.Dwell = Math.Max(CsLimits.DisplayMinDwell, next.Dwell);
            _ = ApplyDisplayCfgAsync(next);
        };
        ToolTipService.SetToolTip(combo, "What the panel rests on between changes.");
        return DisplayRow("Idle Behavior", combo);
    }

    private FrameworkElement BuildDisplayHomePageRow(CsDisplayCfg cfg)
    {
        var combo = new ComboBox { MinWidth = 220 };
        for (int i = 0; i < _vm.CsDisplayPageCount; i++)
            combo.Items.Add(new ComboBoxItem { Content = DisplayPageMenuLabel(i), Tag = i });
        combo.SelectedIndex = Math.Clamp((int)cfg.HomePage, 0, Math.Max(0, combo.Items.Count - 1));
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not int page) return;
            var next = _vm.CsDisplayCfg.Clone();
            next.HomePage = (byte)page;
            _ = ApplyDisplayCfgAsync(next);
        };
        ToolTipService.SetToolTip(combo, "Which page rests on screen.");
        _displayHomePageCombo = combo;
        return DisplayRow("Page", combo);
    }

    /// <summary>A 0.1 s-unit config field, entered in whole seconds.</summary>
    private FrameworkElement BuildDisplaySecondsRow(string label, string tip, ushort raw,
                                                    ushort minimum, Action<CsDisplayCfg, ushort> set)
    {
        TextBox box = null!;
        box = NumberField(raw * CsLimits.IndicatorDelayUnitSeconds, CsUnit.None, v =>
        {
            ushort tenths = (ushort)Math.Clamp(Math.Round(v / CsLimits.IndicatorDelayUnitSeconds), 0, ushort.MaxValue);
            // The firmware refuses a dwell under its floor, so snap rather than
            // let a value the field allowed fail on apply.
            if (!(tenths == 0 && minimum == 0)) tenths = Math.Max(minimum, tenths);
            double stored = tenths * CsLimits.IndicatorDelayUnitSeconds;
            if (Math.Abs(stored - v) > 0.005) box.Text = FormatNumber(stored);
            var next = _vm.CsDisplayCfg.Clone();
            set(next, tenths);
            _ = ApplyDisplayCfgAsync(next);
        });
        ToolTipService.SetToolTip(box, tip);
        return DisplayRow(label, box);
    }

    private FrameworkElement BuildDisplayFlagToggle(CsDisplayCfgFlags flag, string label, string tip)
    {
        var cb = new CheckBox { Content = label, IsChecked = _vm.CsDisplayCfg.HasFlag(flag) };
        void Toggle(bool on)
        {
            if (_building) return;
            var next = _vm.CsDisplayCfg.Clone();
            next.SetFlag(flag, on);
            _ = ApplyDisplayCfgAsync(next);
        }
        cb.Checked += (_, _) => Toggle(true);
        cb.Unchecked += (_, _) => Toggle(false);
        cb.IsEnabled = !DisplayApplying;
        ToolTipService.SetToolTip(cb, tip);
        return cb;
    }

    private FrameworkElement BuildDisplayBrightnessRow(CsDisplayCfg cfg)
    {
        TextBox box = null!;
        box = NumberField(cfg.Brightness, CsUnit.None, v =>
        {
            byte level = (byte)Math.Clamp(Math.Round(v), 0, 255);
            if (Math.Abs(level - v) > 0.005) box.Text = FormatNumber(level);
            var next = _vm.CsDisplayCfg.Clone();
            next.Brightness = level;
            _ = ApplyDisplayCfgAsync(next);
        });
        ToolTipService.SetToolTip(box,
            "OLED contrast, applied when the panel next starts. Zero is the driver default.");
        return DisplayRow("Brightness", box);
    }

    /// <summary>One line's horizontal alignment (caps v11). The two 2-bit fields
    /// ride in the config's flags byte, so the config record exposes them as
    /// properties and the ordinary config write drives them.</summary>
    private FrameworkElement BuildDisplayAlignRow(string label, string tip, CsDisplayAlign current,
                                                  Action<CsDisplayCfg, CsDisplayAlign> set)
    {
        var combo = new ComboBox { MinWidth = 220 };
        combo.Items.Add(new ComboBoxItem { Content = "Left", Tag = CsDisplayAlign.Left });
        combo.Items.Add(new ComboBoxItem { Content = "Centre", Tag = CsDisplayAlign.Centre });
        combo.Items.Add(new ComboBoxItem { Content = "Right", Tag = CsDisplayAlign.Right });
        combo.SelectedIndex = Math.Clamp((int)current, 0, 2);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not CsDisplayAlign a) return;
            var next = _vm.CsDisplayCfg.Clone();
            set(next, a);
            _ = ApplyDisplayCfgAsync(next);
        };
        ToolTipService.SetToolTip(combo, tip);
        return DisplayRow(label, combo);
    }

    // ── Dashboard pages ──────────────────────────────────────────────────────

    private IEnumerable<int> ActiveDisplayPages()
    {
        for (int i = 0; i < _vm.CsDisplayPageCount; i++)
            if (_vm.CsDisplayPages[i].IsActive) yield return i;
    }

    private int FirstFreeDisplayPage()
    {
        for (int i = 0; i < _vm.CsDisplayPageCount; i++)
            if (!_vm.CsDisplayPages[i].IsActive) return i;
        return -1;
    }

    /// <summary>Nouns a page can show: anything the platform has, minus the three
    /// display nouns themselves, which the firmware rejects as page nouns (a page
    /// showing which page is shown says nothing). The macro noun stays — its live
    /// read is the running macro, worth a page on a panel that fires them.</summary>
    private IEnumerable<(int noun, CsNounDesc nd)> DisplayPageNouns()
    {
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            if (n is (int)CsNoun.DisplayPage or (int)CsNoun.DisplayEdit or (int)CsNoun.PageValue) continue;
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable) continue;
            yield return (n, nd);
        }
    }

    private string DisplayPageMenuLabel(int index)
    {
        if (index < 0 || index >= _vm.CsDisplayPages.Count) return $"Page {index + 1}";
        var page = _vm.CsDisplayPages[index];
        return page.IsActive
            ? $"{index + 1}. {_vm.CsDisplayPageSummary(page)}"
            : $"Page {index + 1} (empty)";
    }

    /// <summary>One page, on one row: its ordinal and a marker for the page the
    /// panel is showing, what it shows, and the options that apply to the value.
    /// A page is a {noun, target} and two flags — a card per page spent six rows
    /// on four fields, and a list of them is easier to read across than down.</summary>
    private FrameworkElement BuildDisplayPageRow(int index)
    {
        var page = _vm.CsDisplayPages[index];
        var nd = _vm.CsNounDescFor(page.Noun);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // ordinal + marker
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // pickers
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // options + remove

        // The ordinal is what the "Page" picker up in Behavior names, and the
        // marker holds its column whether or not this page is the one on screen,
        // so the pickers beside it stay in a line instead of shuffling sideways
        // every time the panel cycles.
        bool onScreen = _vm.CsDisplayStatus.CurrentPage == index;
        var lead = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        lead.Children.Add(new TextBlock
        {
            Text = $"{index + 1}",
            FontSize = 11,
            Width = 14,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SecondaryBrush,
        });
        var marker = new Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromArgb(255, 100, 200, 140)),
            Opacity = onScreen ? 1 : 0,
        };
        if (onScreen) ToolTipService.SetToolTip(marker, "Currently on screen");
        _displayPageMarkers[index] = marker;
        lead.Children.Add(marker);
        Grid.SetColumn(lead, 0);
        grid.Children.Add(lead);

        // Fixed picker widths, so the target column starts at the same place on
        // every row however wide the item beside it reads.
        var pickers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pickers.Children.Add(BuildDisplayPageNounCombo(index, page));
        if (nd != null && nd.IsTargeted)
        {
            pickers.Children.Add(BuildDisplayPageTargetCombo(index, page, nd));
            if (nd.HasBand) pickers.Children.Add(BuildDisplayPageBandCombo(index, page));
        }
        Grid.SetColumn(pickers, 1);
        grid.Children.Add(pickers);

        // The value options ride in this row rather than one of their own: on a
        // page with no target they were a whole row holding a single checkbox.
        // Both labels are the same on every row, so they land in a column down
        // the list without being given a width.
        //
        // The group stands off the pickers by the same margin the remove button
        // stands off the group. A row carrying a band picker fills the width, so
        // without it the first checkbox butts straight up against a picker.
        var options = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Large text is a graphic-OLED feature; character modules ignore the flag.
        // Read the model off this card's own draft, so switching panel type shows
        // the right options before the wiring change is applied.
        int model = _displaySlot >= 0 ? _drafts[_displaySlot].Index : 0;
        if (CsDisplayModels.IsGraphic(model))
            options.Children.Add(DisplayPageFlagToggle(index, CsDisplayPageFlags.Large, "Large value",
                "Renders the value pixel-doubled.", true));
        if (_vm.CsDisplayBarSupported)
        {
            // Greyed rather than hidden on a noun that cannot carry a bar: the box
            // keeps its place, and the tooltip says why it is out instead of
            // leaving a silent gap.
            bool allowed = DisplayPageBarAllowed(page.Noun);
            options.Children.Add(DisplayPageFlagToggle(index, CsDisplayPageFlags.Bar, "Level bar",
                allowed ? DisplayBarTip(CsDisplayModels.IsGraphic(model), model)
                        : "Only a value with a range can be drawn as a bar.",
                allowed));
        }
        var del = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            // Stand clear of the two checkboxes: those are settings on the page,
            // this throws the page away, and at the stack's own spacing the
            // nearer box sits close enough to catch a stray click.
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !DisplayApplying,
        };
        ToolTipService.SetToolTip(del, "Remove this page");
        del.Click += (_, _) => _ = ApplyDisplayPageAsync(index, new CsDisplayPage());
        options.Children.Add(del);
        Grid.SetColumn(options, 2);
        grid.Children.Add(options);

        return grid;
    }

    private ComboBox BuildDisplayPageNounCombo(int index, CsDisplayPage page)
    {
        var combo = DisplayPageCombo(200);
        int sel = -1;
        foreach (var (noun, _) in DisplayPageNouns())
        {
            if (noun == page.Noun) sel = combo.Items.Count;
            combo.Items.Add(new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun });
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not int noun) return;
            var next = _vm.CsDisplayPages[index].Clone();
            next.Noun = (byte)noun;
            next.Target = 0;
            next.Index = 0;
            next.SetFlag(CsDisplayPageFlags.Group, false);
            // An aux noun addresses an output, and the page applies at once:
            // point it at one the device will accept, or leave the page alone.
            if (_vm.CsNounDescFor(noun) is { TargetKind: CsTarget.Aux } nd)
            {
                var aux = TargetChoices(noun, nd);
                if (aux.Count == 0)
                {
                    ShowToast("Add an auxiliary output first; a page on it needs one to show.");
                    DispatcherQueue.TryEnqueue(() => RefreshDisplayPageRow(index));
                    return;
                }
                next.Target = (byte)aux[0];
            }
            // A bar needs a range to plot inside; switching to a switch or a mode
            // leaves it with none, and the device rejects the whole page rather
            // than ignoring the flag.
            if (!DisplayPageBarAllowed((byte)noun)) next.SetFlag(CsDisplayPageFlags.Bar, false);
            _ = ApplyDisplayPageAsync(index, next);
        };
        ToolTipService.SetToolTip(combo, "What this page shows.");
        return combo;
    }

    private ComboBox BuildDisplayPageTargetCombo(int index, CsDisplayPage page, CsNounDesc nd)
    {
        var groups = CompatibleGroups(nd).ToList();
        var combo = DisplayPageCombo(160);
        FillTargetCombo(combo, page.Noun, nd, groups, page.IsGrouped, page.Target);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it) return;
            var next = _vm.CsDisplayPages[index].Clone();
            if (it.Tag is int ch) { next.SetFlag(CsDisplayPageFlags.Group, false); next.Target = (byte)ch; }
            else if (it.Tag is GroupTag g) { next.SetFlag(CsDisplayPageFlags.Group, true); next.Target = (byte)g.Index; }
            _ = ApplyDisplayPageAsync(index, next);
        };
        ToolTipService.SetToolTip(combo,
            groups.Count > 0 ? "Which channel or group this page shows." : "Which channel this page shows.");
        return combo;
    }

    private ComboBox BuildDisplayPageBandCombo(int index, CsDisplayPage page)
    {
        var combo = DisplayPageCombo(120);
        var opts = BandOptions(page.Noun).ToList();
        int sel = 0;
        for (int i = 0; i < opts.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = opts[i].label, Tag = opts[i].band });
            if (opts[i].band == page.Index) sel = i;
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it || it.Tag is not int band) return;
            var next = _vm.CsDisplayPages[index].Clone();
            next.Index = (byte)band;
            _ = ApplyDisplayPageAsync(index, next);
        };
        ToolTipService.SetToolTip(combo, "Which filter band this page shows.");
        return combo;
    }

    /// <summary>A picker sized for the page list: a set width so the columns line
    /// up down the rows, and the compact height the row depends on.</summary>
    private ComboBox DisplayPageCombo(double width) => new()
    {
        Width = width,
        MinWidth = 0,
        FontSize = 12,
        IsEnabled = !DisplayApplying && _vm.IsDeviceConnected,
    };

    private CheckBox DisplayPageFlagToggle(int index, CsDisplayPageFlags flag, string label,
                                           string tip, bool enabled)
    {
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = label, FontSize = 12 },
            MinWidth = 0,
            IsChecked = (_vm.CsDisplayPages[index].Flags & flag) != 0,
            IsEnabled = enabled && !DisplayApplying,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Toggle(bool on)
        {
            if (_building) return;
            var next = _vm.CsDisplayPages[index].Clone();
            next.SetFlag(flag, on);
            _ = ApplyDisplayPageAsync(index, next);
        }
        cb.Checked += (_, _) => Toggle(true);
        cb.Unchecked += (_, _) => Toggle(false);
        ToolTipService.SetToolTip(cb, tip);
        return cb;
    }

    private FrameworkElement BuildDisplayAddPageRow()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            // Indented to the picker column - the ordinal's 26 and the row
            // grid's 12 - so the button sits under the content it adds to,
            // rather than hanging off the left edge where only the section
            // heading lives. The extra top margin sets it off from the last
            // row: it is the list's footer action, not another row of it.
            Margin = new Thickness(38, 6, 0, 0),
        };
        int free = FirstFreeDisplayPage();
        var add = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Add Page", VerticalAlignment = VerticalAlignment.Center },
                },
            },
            IsEnabled = free >= 0 && _vm.IsDeviceConnected && !DisplayApplying,
        };
        add.Click += (_, _) =>
        {
            int slot = FirstFreeDisplayPage();
            if (slot < 0) return;
            var page = new CsDisplayPage
            {
                Noun = (byte)(DisplayPageNouns().Select(p => p.noun).FirstOrDefault()),
                Flags = CsDisplayPageFlags.Active,
            };
            _ = ApplyDisplayPageAsync(slot, page);
        };
        panel.Children.Add(add);
        if (free < 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"All {_vm.CsDisplayPageCount} page slots are in use.",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SecondaryBrush,
            });
        else if (!ActiveDisplayPages().Any())
            panel.Children.Add(new TextBlock
            {
                Text = "No pages yet — the panel rests on its idle line until one is added.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SecondaryBrush,
            });
        return panel;
    }

    /// <summary>Whether a page may carry a level bar (caps v13). The bar plots the
    /// value inside the noun's own range, so it needs one: the firmware refuses the
    /// flag on every switch and mode, and on any continuous noun with no span.</summary>
    private bool DisplayPageBarAllowed(byte noun)
    {
        if (!_vm.CsDisplayBarSupported) return false;
        var nd = _vm.CsNounDescFor(noun);
        return nd != null && nd.Kind == CsKind.Continuous && nd.Max > nd.Min;
    }

    /// <summary>Where the bar ends up, which is worth saying: on a two-row
    /// character panel it costs the value its own row, and the name shortens to
    /// make room.</summary>
    private static string DisplayBarTip(bool graphic, int model) =>
        graphic ? "Fills the value's own row behind the text, with no row given up."
        : CsDisplayModels.HasSpareRow(model) ? "Draws the bar on the bottom row."
        : "Draws the bar on the bottom row, moving the value up beside its name.";

    // ── Applying (live, no Apply button) ─────────────────────────────────────

    /// <summary>Send the wiring draft as it is edited. Writes serialize through
    /// the slot-apply guard, which drops a call made while one is in flight -
    /// fine under an Apply button that disables itself, not for live pickers. An
    /// edit landing mid-write is queued instead, and the re-run sends the draft
    /// as it stands then, accumulated rather than lost.</summary>
    private bool _displayWiringQueued;
    private async void ApplyDisplayWiring(int slot)
    {
        if (_applyingSlot != null)
        {
            _displayWiringQueued = true;
            return;
        }
        do
        {
            _displayWiringQueued = false;
            await ApplySlotAsync(slot);
        } while (_displayWiringQueued);
    }

    private async Task ApplyDisplayCfgAsync(CsDisplayCfg cfg)
    {
        _displayWrites++;
        try { await Task.Run(() => _vm.SetCsDisplayCfg(cfg)); }
        finally { _displayWrites--; }
        if (_displayWrites > 0) return;
        // The config's own rows change shape with it; no page row does.
        PopulateDisplayCfgHost();
        AfterDisplayWrite();
    }

    private async Task ApplyDisplayPageAsync(int index, CsDisplayPage page)
    {
        bool wasActive = _vm.CsDisplayPages[index].IsActive;
        byte wasNoun = _vm.CsDisplayPages[index].Noun;
        _displayWrites++;
        try { await Task.Run(() => _vm.SetCsDisplayPage(index, page)); }
        finally { _displayWrites--; }
        if (_displayWrites > 0) return;

        // Repaint the least that restates the truth. A row that appeared or
        // vanished moves the list; a different item on the same row changes which
        // pickers it needs; a device that kept something other than what was sent
        // has to be shown. A flag the device took as sent is already on screen -
        // rebuilding for it is what made every checkbox in the list flash.
        var live = _vm.CsDisplayPages[index];
        if (live.IsActive != wasActive) PopulateDisplayPagesHost();
        else if (live.Noun != wasNoun || !live.WireEquals(page)) RefreshDisplayPageRow(index);
        // A page is listed by what it shows, so the home-page picker follows it.
        RelabelDisplayHomePages();
        AfterDisplayWrite();
    }

    /// <summary>What every live write restates: a refusal, the panel's own state,
    /// and the card's pending pill.</summary>
    private void AfterDisplayWrite()
    {
        RefreshDisplayWarning();
        RefreshDisplayStateRow();
        RefreshStatusIndicators();
        UpdateDisplayPoll();
    }

    // ── The arming dead end ──────────────────────────────────────────────────

    /// <summary>True when editing is gated, some control depends on that gate, and
    /// nothing on the device can lift it.</summary>
    private bool DisplayEditingUnreachable() =>
        _vm.CsDisplayCfg.HasFlag(CsDisplayCfgFlags.EditGated)
        && UsesNoun((byte)CsNoun.PageValue) && !CanArmDisplayEditing();

    /// <summary>Whether any control or remote key drives <paramref name="noun"/>.
    /// Drafts count alongside the live bindings, so a staged edit isn't nagged
    /// about before it is applied.</summary>
    private bool UsesNoun(byte noun)
    {
        for (int slot = 0; slot < _vm.CsSlotCount; slot++)
        {
            if (_drafts[slot].IsConfigured && _drafts[slot].Noun == noun) return true;
            if (_vm.CsBindings[slot].IsConfigured && _vm.CsBindings[slot].Noun == noun) return true;
        }
        for (int sub = 0; sub < _vm.CsIrMax; sub++)
        {
            if (_irDrafts[sub].IsConfigured && _irDrafts[sub].Noun == noun) return true;
            if (_vm.CsIrCommands[sub].IsConfigured && _vm.CsIrCommands[sub].Noun == noun) return true;
        }
        return false;
    }

    /// <summary>Whether anything can actually arm editing: a control, a remote key,
    /// or a macro step that writes the noun. An LED bound to it only reports the
    /// state, so the indicator actions don't count.</summary>
    private bool CanArmDisplayEditing()
    {
        static bool Writes(byte noun, byte action) =>
            noun == (byte)CsNoun.DisplayEdit
            && (CsAction)action is not (CsAction.IndEquals or CsAction.IndAbove or CsAction.IndLevel);

        for (int slot = 0; slot < _vm.CsSlotCount; slot++)
        {
            if (Writes(_drafts[slot].Noun, _drafts[slot].Action)) return true;
            if (Writes(_vm.CsBindings[slot].Noun, _vm.CsBindings[slot].Action)) return true;
        }
        for (int sub = 0; sub < _vm.CsIrMax; sub++)
        {
            if (Writes(_irDrafts[sub].Noun, _irDrafts[sub].Action)) return true;
            if (Writes(_vm.CsIrCommands[sub].Noun, _vm.CsIrCommands[sub].Action)) return true;
        }
        // A macro step can arm it too, and the button firing that macro is then
        // the arm control.
        for (int m = 0; m < _vm.CsMacroMax; m++)
        {
            var draft = _macroDrafts[m];
            for (int s = 0; s < draft.StepCount; s++)
                if (Writes(draft.Steps[s].Noun, draft.Steps[s].Action)) return true;
            var live = _vm.CsMacros[m];
            for (int s = 0; s < live.StepCount; s++)
                if (Writes(live.Steps[s].Noun, live.Steps[s].Action)) return true;
        }
        return false;
    }

    // ── Live-state poll ──────────────────────────────────────────────────────

    /// <summary>Run the panel-state poll only while an expanded display card is on
    /// screen: it is the one place the on-screen page marker and the abort counter
    /// are shown, and it costs an 8-byte GET a second.</summary>
    private void UpdateDisplayPoll()
    {
        bool want = _section == CsSection.Bindings
                    && _vm.CsDisplaySupported
                    && _vm.IsDeviceConnected
                    && _vm.CsDisplaySlot is int slot
                    && _expanded.Contains(slot);
        if (want) StartDisplayPoll(); else StopDisplayPoll();
    }

    private void StartDisplayPoll()
    {
        _displayPoll ??= DispatcherQueue.CreateTimer();
        if (_displayPoll.IsRunning) return;
        _displayPoll.Interval = TimeSpan.FromMilliseconds(DisplayPollMs);
        _displayPoll.Tick += OnDisplayPollTick;
        _displayPoll.Start();
    }

    private void StopDisplayPoll()
    {
        if (_displayPoll == null) return;
        _displayPoll.Stop();
        _displayPoll.Tick -= OnDisplayPollTick;
    }

    private async void OnDisplayPollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (DisplayApplying) return;
        byte page = _vm.CsDisplayStatus.CurrentPage;
        // A read that fails (unplugged, device wedged) would otherwise leave the
        // last reading in place and keep polling a device that has gone.
        bool alive = await Task.Run(() => _vm.RefreshCsDisplayStatus());
        if (!alive) { StopDisplayPoll(); return; }
        RefreshDisplayStateRow();
        // Only the marker moves with the page. Rebuilding the list for it would
        // close a picker the user had open, and in a cycle mode it would do that
        // every dwell.
        if (_vm.CsDisplayStatus.CurrentPage != page) RefreshDisplayPageMarkers();
    }

    // ── Wiring helpers ───────────────────────────────────────────────────────

    /// <summary>Legal SDA pins. The RP2040/RP2350 mux pairs GPIOs so that bit 0
    /// picks SDA (even) or SCL (odd) and bit 1 picks the bus instance, so SDA is
    /// always an even GPIO whose odd neighbour is its SCL. The instance the
    /// external I2C control interface holds is dropped: the firmware rejects an
    /// overlap outright, so offering it could only build a pair that fails.</summary>
    private IEnumerable<byte> I2cSdaCandidates(int slot)
    {
        var owners = HardwarePins.BuildOwnerMap(_vm, excludeCsSlot: slot);
        var valid = new HashSet<byte>(HardwarePins.ValidPins);
        int? blockedInstance = _vm.ControlInterfacesSupported && _vm.CtrlIfaceStatus?.I2cLive == true
            ? (_vm.I2cCtrlConfig.SdaPin >> 1) & 1
            : null;
        foreach (byte sda in HardwarePins.ValidPins)
        {
            if (sda % 2 != 0 || !valid.Contains((byte)(sda + 1))) continue;
            if (blockedInstance != null && ((sda >> 1) & 1) == blockedInstance) continue;
            // Keep whatever this display already holds selectable.
            if (_drafts[slot].Type == CsType.Display && sda == _drafts[slot].Gpio0) { yield return sda; continue; }
            if (owners.ContainsKey(sda) || owners.ContainsKey((byte)(sda + 1))) continue;
            yield return sda;
        }
    }

    /// <summary>The first free legal SDA/SCL pair, falling back to the lowest legal
    /// pair when every one of them is claimed.</summary>
    private (byte Sda, byte Scl) FreeI2cPair(int slot)
    {
        foreach (byte sda in I2cSdaCandidates(slot)) return (sda, (byte)(sda + 1));
        byte first = HardwarePins.ValidPins.FirstOrDefault(p => p % 2 == 0);
        return (first, (byte)(first + 1));
    }

    /// <summary>The address the panel actually answers on: the model's own when the
    /// binding stores 0.</summary>
    private static byte DisplayAddress(CsBinding b) =>
        b.Value == 0 ? CsDisplayModels.DefaultAddress(b.Index) : (byte)b.Value;

    // ── Small view helpers ───────────────────────────────────────────────────

    /// <summary>A small-caps heading with a hairline rule running out to the
    /// card's edge, so the display card reads as short chapters rather than one
    /// long list - a bare label was too faint to hold a section together.
    /// <paramref name="trailing"/> sits at the rule's far end: the page count,
    /// on the one list with a fixed number of slots.</summary>
    private static FrameworkElement SectionHeading(string title, string? trailing = null)
    {
        var grid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = SecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(text);
        var rule = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var b)
                         && b is Brush brush ? brush : SecondaryBrush,
        };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        if (trailing != null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tail = new TextBlock
            {
                Text = trailing,
                FontSize = 10,
                Foreground = SecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tail, 2);
            grid.Children.Add(tail);
        }
        return grid;
    }

    /// <summary>A label/control row whose control is disabled while a live display
    /// write is in flight.</summary>
    private Grid DisplayRow(string label, FrameworkElement control)
    {
        if (control is Control c) c.IsEnabled = !DisplayApplying && _vm.IsDeviceConnected;
        var row = Row(label, control);
        // These rows live in a half-width column, where a fixed picker width
        // outgrows the column at the default window width and clips. A picker
        // stretches to the column instead: it cannot be wider than its home,
        // and every one shares the column's edge, which is all the fixed width
        // was buying. The number fields stay at their natural width - a short
        // value in a full-width box reads worse, and they cannot clip.
        if (control is ComboBox combo)
        {
            combo.MinWidth = 0;
            combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        // These labels are wordier than the binding editor's, and the label column
        // is a fixed width, so let one wrap rather than run under its control.
        if (row.Children.Count > 0 && row.Children[0] is TextBlock lbl)
            lbl.TextWrapping = TextWrapping.Wrap;
        return row;
    }

    private static TextBlock WarningLine(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 180, 90)),
    };
}
