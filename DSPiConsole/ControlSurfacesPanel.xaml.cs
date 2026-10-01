using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Settings;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Which of the editor's three sections a <see cref="ControlSurfacesPanel"/>
/// instance shows. The three Settings pages under Control each build one panel
/// with a different value; everything else about them is identical.
/// </summary>
public enum CsSection { Bindings, Groups, Macros }

/// <summary>
/// Control Surfaces + IR remote editor. A caps-driven editor that binds physical
/// GPIO controls (buttons, switches, pots, encoders, LEDs, PWM LEDs), an IR
/// receiver and an I2C display to DSP parameters. Every edit previews live on the
/// device; Save persists to flash, Revert discards. Mirrors the macOS reference
/// app. The display component's own card lives in
/// <c>ControlSurfacesPanel.Display.cs</c>.
///
/// <para>
/// Hosted by three Settings pages rather than its own window. All three sections
/// stay in this one class because they share the card builders, the caps gate and
/// the device-wide dirty state — a page just picks a <see cref="CsSection"/>. The
/// three live instances keep each other current through
/// <see cref="StateChanged"/>, which only ever triggers in-place refreshes: a
/// sibling must never reseed its drafts, or it would throw away edits the user
/// hasn't applied yet.
/// </para>
/// </summary>
public sealed partial class ControlSurfacesPanel : UserControl, IPinHighlightPage
{
    /// <summary>Where a GPIO the Overview linked here is set: the card for the
    /// control holding it, expanded so its pin picker is actually on screen. Only
    /// live bindings hold a pin, which is the same rule that put it on the map.
    ///
    /// <para>False while the panel has yet to draw that card — the settings shell
    /// retries, because this page fills itself from a device read and is usually
    /// still waiting on one when a click first lands here.</para></summary>
    public bool HighlightPin(byte pin)
    {
        if (_section != CsSection.Bindings) return false;
        for (int slot = 0; slot < _vm.CsSlotCount && slot < _drafts.Length; slot++)
        {
            var binding = _vm.CsBindings[slot];
            if (!binding.IsConfigured) continue;
            bool holds = binding.Gpio0 == pin
                         || (binding.Gpio1 != CsLimits.GpioUnused && binding.Gpio1 == pin);
            if (!holds) continue;
            if (!_slotCards.TryGetValue(slot, out var card)) return false;

            // Everything else closes. This list runs to sixteen cards and the
            // one being asked for is usually not the first, so leaving the rest
            // open means arriving on a page of near-identical cards with a ring
            // somewhere below the fold - which is the hunt the click was meant
            // to save. Closed, the whole list fits and the answer is the only
            // thing open on it. Each card's Collapsed handler updates
            // _expanded, so the state this leaves behind is the real one.
            foreach (var other in _slotCards.ToList())
                if (other.Key != slot && other.Value is Expander shut)
                    shut.IsExpanded = false;

            // Then open the card that has the answer: a picker inside a closed
            // one has nothing to show for being rung.
            _expanded.Add(slot);
            if (card is Expander expander) expander.IsExpanded = true;
            bool second = binding.Gpio0 != pin;
            // The ring goes on the picker itself where there is one, and it is
            // raised after the expander, so its scroll supersedes the card's.
            PinFlash.Play(_slotPinCombos.TryGetValue((slot, second), out var combo) ? combo : card);
            return true;
        }
        return false;
    }

    private readonly MainViewModel _vm;
    private readonly CsSection _section;

    /// <summary>Raised after an edit that another section's cards render — a group
    /// added, renamed or cleared, a macro renamed, a binding's slot health changed.
    /// The panel that made the change passes itself so it doesn't handle its own
    /// event; the others refresh in place.</summary>
    internal static event Action<ControlSurfacesPanel>? StateChanged;

    // The event only reaches the panels that are mounted to hear it, and the
    // settings shell shows one page at a time — so the page most likely to care
    // is usually the detached one. (Add a group on Channel Groups and the target
    // pickers on Control Surfaces are exactly that.) A generation counter travels
    // with the event for them: each panel records the count it last refreshed at
    // and catches up on the way back in.
    private static int s_stateGeneration;
    private int _seenStateGeneration;

    private void RaiseStateChanged()
    {
        // The panel that made the change is current by definition.
        _seenStateGeneration = ++s_stateGeneration;
        StateChanged?.Invoke(this);
    }

    // A panel only listens to ControlSurfacesReloaded while it's mounted, so a
    // revert (or a device push) that lands while you're on another page would
    // otherwise leave that page's cached instance showing pre-revert drafts. One
    // process-wide subscription counts the reloads; each panel records the count
    // it last seeded at and re-seeds on the way back in if it fell behind.
    private static int s_reloadGeneration;
    private static MainViewModel? s_generationVm;
    private int _seenGeneration;

    private static void HookReloadCounter(MainViewModel vm)
    {
        if (ReferenceEquals(s_generationVm, vm)) return;
        if (s_generationVm != null) s_generationVm.ControlSurfacesReloaded -= BumpGeneration;
        s_generationVm = vm;
        vm.ControlSurfacesReloaded += BumpGeneration;
    }

    // Subscribed before any panel's own handler, so a panel that IS mounted sees
    // the bumped count when it records _seenGeneration.
    private static void BumpGeneration() => s_reloadGeneration++;

    // Editable drafts (seeded from the VM's live values). A slot is "dirty" when
    // its draft differs from the applied device state.
    private readonly CsBinding[] _drafts = new CsBinding[CsLimits.MaxBindings];
    private readonly IrCommand[] _irDrafts = new IrCommand[CsLimits.MaxIrCommands];
    private readonly string[] _nameEdits = new string[CsLimits.MaxBindings];

    private readonly HashSet<int> _expanded = new();
    private readonly HashSet<int> _irExpanded = new();

    private bool _building;
    private int? _applyingSlot;
    private int? _learningSub;

    // Per-slot / per-sub UI handles refreshed without a full rebuild.
    private readonly Dictionary<int, (Border Pill, Ellipse Dot, TextBlock Label)> _slotPills = new();
    private readonly Dictionary<int, TextBlock> _slotTitles = new();
    private readonly Dictionary<int, TextBlock> _slotSummaries = new();
    private readonly Dictionary<int, Button> _slotApply = new();
    private readonly Dictionary<int, StackPanel> _slotBodies = new();
    private readonly Dictionary<int, FrameworkElement> _slotCards = new();
    // Per-slot pin-combo refreshers: re-run a slot's GPIO pickers in place (e.g.
    // after another slot claims a pin) without recreating the card body.
    private readonly Dictionary<int, List<Action>> _pinRefreshers = new();
    // The picker that sets each of a slot's GPIOs, so a pin arriving from the
    // Overview or a conflict rings the control that sets it, as on every other
    // page. Keyed by whether it is the slot's second GPIO: an encoder has one
    // picker per pin, while a display has a single pair picker that both of its
    // pins resolve to.
    private readonly Dictionary<(int Slot, bool Second), ComboBox> _slotPinCombos = new();
    // Per-slot / per-sub channel-picker relabellers, re-run when the user renames a
    // channel in the sidebar (the combo keeps its items and selection; only the
    // displayed names change).
    private readonly Dictionary<int, Action> _channelRelabel = new();
    private readonly Dictionary<int, Action> _irChannelRelabel = new();

    // Live handles to the IR receiver's command section, so IR edits can rebuild
    // just that section in place instead of tearing down the whole cards panel
    // (which resets scroll / makes the section visibly jump).
    private StackPanel? _irSectionPanel;
    private int _irSectionSlot = -1;
    private Button? _addRemoteButton;
    private TextBlock? _irCountLabel;
    private int _irLeadingCount; // non-card children (count label + optional hint)
    private readonly Dictionary<int, FrameworkElement> _irCommandCards = new();
    private readonly Dictionary<int, StackPanel> _irBodies = new();
    private readonly Dictionary<int, (Border Chip, TextBlock Label)> _irChips = new();
    private readonly Dictionary<int, Button> _irLearnButtons = new();
    private readonly Dictionary<int, TextBlock> _irTitles = new();

    public ControlSurfacesPanel(MainViewModel vm, CsSection section)
    {
        _vm = vm;
        _section = section;
        InitializeComponent();
        HookReloadCounter(vm);
        _seenGeneration = s_reloadGeneration;
        _seenStateGeneration = s_stateGeneration;

        // Subscriptions live on Loaded/Unloaded, not the constructor: the settings
        // shell caches page instances and detaches them from the visual tree when
        // you navigate away, so a constructor-only subscription would survive but
        // a Loaded-only rebuild wouldn't. Same convention as every SettingsModule.
        Loaded += OnPanelLoaded;
        Unloaded += OnPanelUnloaded;

        SeedDrafts();
        BuildAll();
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.ControlSurfacesReloaded -= OnReloaded;
        _vm.ControlSurfacesReloaded += OnReloaded;
        _vm.ChannelNameChanged -= OnChannelNameChanged;
        _vm.ChannelNameChanged += OnChannelNameChanged;
        StateChanged -= OnSiblingStateChanged;
        StateChanged += OnSiblingStateChanged;

        // Catch up on anything that happened while we were detached.
        if (_seenGeneration != s_reloadGeneration)
        {
            _seenGeneration = s_reloadGeneration;
            _seenStateGeneration = s_stateGeneration;
            SeedDrafts();
            BuildAll();
        }
        else
        {
            // A whole reload reseeds everything; short of one, a group or macro
            // edit made elsewhere still has to reach this page's pickers before
            // it is shown again.
            if (_seenStateGeneration != s_stateGeneration) RefreshForSiblingState();
            RefreshStatusIndicators();
        }
    }

    private void OnPanelUnloaded(object sender, RoutedEventArgs e)
    {
        StopMacroPoll();
        StopDisplayPoll();
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.ControlSurfacesReloaded -= OnReloaded;
        _vm.ChannelNameChanged -= OnChannelNameChanged;
        StateChanged -= OnSiblingStateChanged;
    }

    /// <summary>Another section changed something this one displays. Refresh in
    /// place only — reseeding would discard drafts the user hasn't applied.</summary>
    private void OnSiblingStateChanged(ControlSurfacesPanel source)
    {
        if (ReferenceEquals(source, this)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshForSiblingState();
            RefreshStatusIndicators();
        });
    }

    /// <summary>Rebuild what this section renders from another one's state: every
    /// picker that lists groups. Which pickers those are depends on the section —
    /// binding cards, remote buttons and display pages here, macro steps there.</summary>
    private void RefreshForSiblingState()
    {
        _seenStateGeneration = s_stateGeneration;
        if (_section == CsSection.Bindings) RefreshGroupedBindingCards();
        else if (_section == CsSection.Macros) RefreshMacroStepCards();
        else RefreshGroupMacroIndicators();
    }

    /// <summary>A channel was renamed (sidebar edit, preset load, or a device
    /// push) — relabel everything here that shows a channel name.</summary>
    private void OnChannelNameChanged(int channelId) =>
        DispatcherQueue.TryEnqueue(RefreshChannelLabels);

    private void OnReloaded() => DispatcherQueue.TryEnqueue(() =>
    {
        _seenGeneration = s_reloadGeneration;
        SeedDrafts();
        BuildAll();
    });

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CsStatus)
            || e.PropertyName == nameof(MainViewModel.CsExtStatus)
            || e.PropertyName == nameof(MainViewModel.CsDirty))
        {
            DispatcherQueue.TryEnqueue(RefreshStatusIndicators);
        }
        else if (e.PropertyName == nameof(MainViewModel.CsDisplayStatus))
        {
            DispatcherQueue.TryEnqueue(RefreshDisplayStateRow);
        }
    }

    private void SeedDrafts()
    {
        for (int i = 0; i < CsLimits.MaxBindings; i++)
        {
            _drafts[i] = i < _vm.CsBindings.Count ? _vm.CsBindings[i].Clone() : CsBinding.Cleared();
            _nameEdits[i] = i < _vm.CsNames.Count ? _vm.CsNames[i] : "";
        }
        for (int i = 0; i < CsLimits.MaxIrCommands; i++)
            _irDrafts[i] = i < _vm.CsIrCommands.Count ? _vm.CsIrCommands[i].Clone() : new IrCommand();
        SeedGroupMacroDrafts();
    }

    // ── Top-level build ──────────────────────────────────────────────────────

    private void BuildAll()
    {
        if (!_vm.ControlSurfacesSupported || _vm.CsCaps is not { IsValid: true })
        {
            SetBar(UnsupportedBar, true);
            BodyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SetBar(UnsupportedBar, false);
        BodyPanel.Visibility = Visibility.Visible;

        // Only this panel's section is built. The others stay collapsed and their
        // card dictionaries stay empty — every refresh path already skips slots it
        // has no card for, so they no-op rather than misbehave.
        BindingsSection.Visibility = Vis(_section == CsSection.Bindings);
        if (_section == CsSection.Bindings)
        {
            BuildAddMenu();
            RebuildCards();
        }
        BuildGroupsAndMacros();
        RefreshStatusIndicators();
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Open or close an InfoBar and take it out of the layout when closed.
    /// A closed InfoBar draws nothing but still counts as a child, so the enclosing
    /// StackPanel's Spacing reserves a gap for it — which is what pushed the
    /// section text down from the page title.</summary>
    private static void SetBar(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = Vis(open);
    }

    /// <summary>The message bar is user-dismissable, and closing it that way sets
    /// IsOpen without going through <see cref="SetBar"/>.</summary>
    private void OnMessageBarClosed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

    private void BuildAddMenu()
    {
        AddFlyout.Items.Clear();
        var caps = _vm.CsCaps!;
        foreach (CsType t in Enum.GetValues<CsType>())
        {
            if (t == CsType.None) continue;
            if ((int)t >= caps.TypeCount) continue;
            // One IR receiver max — hide once configured.
            if (t == CsType.Ir && (!_vm.CsIrSupported || AnyIrReceiver())) continue;
            // One display per device, likewise (CS_STATUS_DISPLAY_IN_USE).
            if (t == CsType.Display && (!_vm.CsDisplaySupported || AnyDisplay())) continue;
            var item = new MenuFlyoutItem
            {
                Text = TypeName(t),
                Tag = t,
                Icon = new FontIcon { Glyph = TypeGlyph(t) },
            };
            item.Click += (_, _) => AddControl(t);
            AddFlyout.Items.Add(item);
        }
        AddButton.IsEnabled = FirstFreeSlot() >= 0 && AddFlyout.Items.Count > 0;
    }

    private void RebuildCards()
    {
        _building = true;
        try
        {
            CardsPanel.Children.Clear();
            _slotPills.Clear();
            _slotTitles.Clear();
            _slotSummaries.Clear();
            _slotApply.Clear();
            _slotBodies.Clear();
            _slotCards.Clear();
            _pinRefreshers.Clear();
            _slotPinCombos.Clear();
            _channelRelabel.Clear();
            // The display card's own handles go with the cards: a rebuild that no
            // longer draws one would otherwise leave the config sections pointing
            // at a panel that is no longer in the tree.
            ClearDisplayHandles();

            int shown = 0;
            for (int slot = 0; slot < _vm.CsSlotCount; slot++)
            {
                if (!_drafts[slot].IsConfigured) continue;
                CardsPanel.Children.Add(BuildSlotCard(slot));
                shown++;
            }
            EmptyHint.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _building = false; }
        RefreshStatusIndicators();
    }

    // ── One slot card ────────────────────────────────────────────────────────

    private FrameworkElement BuildSlotCard(int slot)
    {
        var draft = _drafts[slot];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expanded.Contains(slot),
        };
        expander.Expanding += (_, _) =>
        {
            _expanded.Add(slot);
            Reveal.Bring(expander);
            UpdateDisplayPoll();
        };
        expander.Collapsed += (_, _) => { _expanded.Remove(slot); UpdateDisplayPoll(); };

        expander.Header = BuildCardHeader(slot);
        expander.Content = BuildCardBody(slot);
        _slotCards[slot] = expander;
        return expander;
    }

    private FrameworkElement BuildCardHeader(int slot)
    {
        var draft = _drafts[slot];
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Tinted type-icon badge.
        var accent = TypeColor(draft.Type);
        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B)),
            Child = new FontIcon
            {
                Glyph = TypeGlyph(draft.Type),
                FontSize = 16,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        // Title (custom name or type) + live binding summary.
        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var title = new TextBlock
        {
            Text = SlotTitle(slot),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var summary = new TextBlock
        {
            Text = SlotSummary(slot),
            FontSize = 11,
            Foreground = SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleStack.Children.Add(title);
        titleStack.Children.Add(summary);
        _slotTitles[slot] = title;
        _slotSummaries[slot] = summary;
        Grid.SetColumn(titleStack, 1);
        grid.Children.Add(titleStack);

        // Status pill (dot + label in a tinted capsule).
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pillContent.Children.Add(dot);
        pillContent.Children.Add(label);
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = pillContent,
        };
        _slotPills[slot] = (pill, dot, label);
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        // Delete.
        var del = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
        };
        ToolTipService.SetToolTip(del, "Remove this control");
        del.Click += (_, _) => RemoveControl(slot);
        Grid.SetColumn(del, 3);
        grid.Children.Add(del);

        return grid;
    }

    private FrameworkElement BuildCardBody(int slot)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _slotBodies[slot] = panel;
        PopulateSlotBody(slot);
        return panel;
    }

    /// <summary>Fill (or refill) a slot card's body in place. Used instead of a full
    /// <see cref="RebuildCards"/> when only this card's options change (noun / action /
    /// operand range) so the card and scroll position don't jump.</summary>
    private void PopulateSlotBody(int slot)
    {
        if (!_slotBodies.TryGetValue(slot, out var panel)) return;
        _building = true;
        try
        {
            panel.Children.Clear();
            // Pickers below re-register fresh closures; a noun without a channel
            // target simply won't put one back.
            _pinRefreshers.Remove(slot);
            _slotPinCombos.Remove((slot, false));
            _slotPinCombos.Remove((slot, true));
            _channelRelabel.Remove(slot);
            var draft = _drafts[slot];

            panel.Children.Add(BuildIdentityRows(slot));
            // The display card opens with a ruled section heading of its own, so
            // the divider here would sit right on top of it as a double line.
            if (draft.Type != CsType.Display) panel.Children.Add(Divider());

            if (draft.Type == CsType.Ir)
            {
                // IR receiver: pin + invert, then the remote-button table.
                panel.Children.Add(BuildPinRows(slot));
                panel.Children.Add(FlagToggle(slot, CsFlags.Invert, "Active-low input (pull-up)"));
                panel.Children.Add(BuildApplyRow(slot));
                panel.Children.Add(BuildIrCommandsSection(slot));
            }
            else if (draft.Type == CsType.Display)
            {
                // Also a container: the panel's wiring and identity, then what it
                // shows (config + pages), which apply as they are edited.
                PopulateDisplayBody(slot, panel);
            }
            else
            {
                var nd = _vm.CsNounDescFor(draft.Noun);

                panel.Children.Add(BuildNounRow(slot));
                if (nd != null)
                {
                    var actions = ValidActions(draft.Type, nd);
                    if (actions.Count > 1) panel.Children.Add(BuildActionRow(slot, actions));
                    if (nd.IsTargeted) panel.Children.Add(BuildTargetRows(slot, nd));
                    if (draft.Type == CsType.Button) panel.Children.Add(BuildEventRow(slot));
                    panel.Children.Add(BuildPinRows(slot));
                    var operand = BuildOperandRows(slot, nd);
                    if (operand != null) panel.Children.Add(operand);
                    if (SupportsIndicatorDelay(draft))
                        panel.Children.Add(BuildIndicatorDelayRows(slot));
                    // The ceiling scales whatever duty the action worked out, so
                    // it belongs to the LED rather than to any one action.
                    if (_vm.CsLedBrightnessSupported && draft.Type == CsType.LedPwm)
                        panel.Children.Add(BuildBrightnessCeilingRow(slot));
                    panel.Children.Add(BuildFlagRows(slot, nd));
                    var note = DisplayNounNote(draft);
                    if (note != null) panel.Children.Add(note);
                }
                panel.Children.Add(BuildApplyRow(slot));
            }
        }
        finally { _building = false; }
        RefreshStatusIndicators();
    }

    // ── Editor rows ──────────────────────────────────────────────────────────

    /// <summary>Name row shown at the top of every slot card's body. The component
    /// type is fixed when the control is added.</summary>
    private FrameworkElement BuildIdentityRows(int slot)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "Name (optional)",
            Text = _nameEdits[slot],
            MinWidth = 220,
        };
        nameBox.LostFocus += (_, _) => CommitName(slot, nameBox.Text);
        nameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitName(slot, nameBox.Text); };
        return Row("Name", nameBox);
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 2, 0, 2),
        Background = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
    };

    private FrameworkElement BuildNounRow(int slot)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 220 };
        var caps = _vm.CsCaps!;
        int selectedIndex = -1, idx = 0;
        foreach (var (noun, nd) in AvailableNouns(draft.Type))
        {
            var item = new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun };
            combo.Items.Add(item);
            if (noun == draft.Noun) selectedIndex = idx;
            idx++;
        }
        combo.SelectedIndex = selectedIndex;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is int noun)
                ChangeNoun(slot, noun);
        };
        return Row("Controls", combo);
    }

    private FrameworkElement BuildActionRow(int slot, List<CsAction> actions)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 180 };
        int sel = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = ActionName(actions[i], draft.Noun), Tag = actions[i] });
            if ((byte)actions[i] == draft.Action) sel = i;
        }
        if (sel < 0) sel = 0;
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsAction a)
                ChangeAction(slot, a);
        };
        return Row("Action", combo);
    }

    private FrameworkElement BuildTargetRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var panel = new StackPanel { Spacing = 8 };

        // Channels first, then any compatible group. Picking a group flips the
        // GROUP flag and re-reads `target` as a group index (caps v9); picking a
        // channel clears it along with the two group-only modifiers.
        var groups = CompatibleGroups(nd).ToList();
        var chCombo = new ComboBox { MinWidth = 180 };
        for (int i = 0; i < nd.TargetCount; i++)
            chCombo.Items.Add(new ComboBoxItem { Content = ChannelLabel(nd.TargetKind, i), Tag = i });
        foreach (int g in groups)
            chCombo.Items.Add(new ComboBoxItem { Content = $"Group: {_vm.CsGroupLabel(g)}", Tag = new GroupTag(g) });
        _channelRelabel[slot] = () => Relabel(chCombo, nd.TargetKind);
        chCombo.SelectedIndex = draft.IsGrouped
            ? (groups.IndexOf(draft.Target) is var gi && gi >= 0 ? nd.TargetCount + gi : -1)
            : (draft.Target < nd.TargetCount ? draft.Target : 0);
        chCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (chCombo.SelectedItem is not ComboBoxItem it) return;
            bool wasGrouped = _drafts[slot].IsGrouped;
            if (it.Tag is int ch)
            {
                _drafts[slot].Flags &= ~(CsFlags.Group | CsFlags.LinkAbs | CsFlags.GroupAll);
                _drafts[slot].Target = (byte)ch;
            }
            else if (it.Tag is GroupTag g)
            {
                _drafts[slot].Flags |= CsFlags.Group;
                _drafts[slot].Target = (byte)g.Index;
            }
            SanitizeDraft(slot);
            // Only crossing between a channel and a group changes which rows the
            // body needs (the group-only modifier toggles); a plain channel switch
            // just restates the summary.
            if (wasGrouped != _drafts[slot].IsGrouped) PopulateSlotBody(slot);
            else RefreshStatusIndicators();
        };
        panel.Children.Add(Row(groups.Count > 0 ? "Target" : "Channel", chCombo));

        if (nd.HasBand)
        {
            var bandCombo = new ComboBox { MinWidth = 180 };
            var opts = BandOptions(draft.Noun).ToList();
            int sel = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                bandCombo.Items.Add(new ComboBoxItem { Content = opts[i].label, Tag = opts[i].band });
                if (opts[i].band == draft.Index) sel = i;
            }
            bandCombo.SelectedIndex = sel;
            bandCombo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (bandCombo.SelectedItem is ComboBoxItem it && it.Tag is int band)
                { _drafts[slot].Index = (byte)band; RefreshStatusIndicators(); }
            };
            panel.Children.Add(Row("Band", bandCombo));
        }
        return panel;
    }

    private FrameworkElement BuildEventRow(int slot)
    {
        var draft = _drafts[slot];
        var combo = new ComboBox { MinWidth = 180 };
        combo.Items.Add(new ComboBoxItem { Content = "Press", Tag = CsEvent.Press });
        combo.Items.Add(new ComboBoxItem { Content = "Long press", Tag = CsEvent.Long });
        combo.Items.Add(new ComboBoxItem { Content = "Double press", Tag = CsEvent.Double });
        combo.SelectedIndex = Math.Clamp((int)draft.Event, 0, 2);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsEvent ev)
            { _drafts[slot].Event = (byte)ev; RefreshStatusIndicators(); }
        };
        return Row("Button event", combo);
    }

    private FrameworkElement BuildPinRows(int slot)
    {
        var draft = _drafts[slot];
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(draft.Type);
        bool twoPins = typeDesc?.PinCount == 2;
        bool adcOnly = typeDesc?.PinClass == CsPinClass.Adc;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Row(twoPins ? "GPIO A" : "GPIO",
            PinCombo(slot, adcOnly, isSecond: false)));
        if (twoPins)
            panel.Children.Add(Row("GPIO B", PinCombo(slot, adcOnly, isSecond: true)));
        return panel;
    }

    private ComboBox PinCombo(int slot, bool adcOnly, bool isSecond)
    {
        var combo = new ComboBox { MinWidth = 160 };

        // Fill (or refill) the candidate list. Called on build and again in place
        // whenever another slot claims/frees a pin, so the options stay current
        // without recreating the card body (which would flash its Apply buttons).
        void Populate()
        {
            var draft = _drafts[slot];
            byte current = isSecond ? draft.Gpio1 : draft.Gpio0;
            var candidates = FreePins(slot, adcOnly).ToList();
            if (current != CsLimits.GpioUnused && !candidates.Contains(current)) candidates.Add(current);
            candidates.Sort();

            combo.Items.Clear();
            int sel = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = $"GPIO {candidates[i]}", Tag = candidates[i] });
                if (candidates[i] == current) sel = i;
            }
            combo.SelectedIndex = sel;
        }
        Populate();

        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is byte pin)
            {
                if (isSecond) _drafts[slot].Gpio1 = pin; else _drafts[slot].Gpio0 = pin;
                RefreshStatusIndicators();
            }
        };

        if (!_pinRefreshers.TryGetValue(slot, out var list))
            _pinRefreshers[slot] = list = new List<Action>();
        list.Add(Populate);
        _slotPinCombos[(slot, isSecond)] = combo;
        return combo;
    }

    private FrameworkElement? BuildOperandRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var action = (CsAction)draft.Action;

        // Browse/Adjust takes its unit, step law and range from whatever page is
        // on screen when the event fires, so there is nothing to set here — and
        // the firmware rejects the binding if anything is.
        if (draft.Noun == (byte)CsNoun.PageValue) return null;

        // Span (pot ADJUST / PWM IND_LEVEL): rangeMin/rangeMax over the noun range.
        if (action is CsAction.Adjust or CsAction.IndLevel && nd.Kind == CsKind.Continuous)
            return BuildSpanRows(slot, nd);

        // Step (encoder STEP, button INC/DEC).
        if (action is CsAction.Step or CsAction.Inc or CsAction.Dec)
            return BuildStepRow(slot, nd);

        // Value (SET / MOMENTARY / IND_EQUALS / IND_ABOVE).
        if (action is CsAction.Set or CsAction.Momentary or CsAction.IndEquals or CsAction.IndAbove)
            return BuildValueRow(slot, nd);

        // Toggle / Follow / Trigger have no operand.
        return null;
    }

    private FrameworkElement BuildSpanRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var panel = new StackPanel { Spacing = 8 };
        bool custom = !(draft.RangeMin == 0 && draft.RangeMax == 0);

        var toggle = new CheckBox { Content = "Limit to a custom range", IsChecked = custom };
        panel.Children.Add(toggle);

        var minBox = NumberField(CsWire.DecodeValue(draft.RangeMin, nd.Unit), nd.Unit, v =>
        { _drafts[slot].RangeMin = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });
        var maxBox = NumberField(CsWire.DecodeValue(draft.RangeMax, nd.Unit), nd.Unit, v =>
        { _drafts[slot].RangeMax = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });

        var minRow = Row("Minimum", minBox);
        var maxRow = Row("Maximum", maxBox);
        minRow.Visibility = maxRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(minRow);
        panel.Children.Add(maxRow);

        toggle.Checked += (_, _) =>
        {
            if (_building) return;
            // Seed a sensible span from the noun's full range.
            _drafts[slot].RangeMin = nd.MinQ; _drafts[slot].RangeMax = nd.MaxQ;
            PopulateSlotBody(slot);
        };
        toggle.Unchecked += (_, _) =>
        {
            if (_building) return;
            _drafts[slot].RangeMin = 0; _drafts[slot].RangeMax = 0;
            PopulateSlotBody(slot);
        };
        return panel;
    }

    private FrameworkElement BuildStepRow(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        if (nd.Kind == CsKind.Enum || nd.Unit == CsUnit.None)
        {
            var box = NumberField(draft.Step == 0 ? CsWire.DefaultStep(nd.Unit) : CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
            { _drafts[slot].Step = CsWire.EncodeStep(v, nd.Unit); RefreshStatusIndicators(); });
            return Row("Step (positions)", box);
        }
        // Hz/Q step is in octaves; dB/%/ms linear.
        string label = CsWire.UnitStepsInOctaves(nd.Unit) ? "Step (octaves)" : StepLabel(nd.Unit);
        var stepBox = NumberField(CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
        { _drafts[slot].Step = CsWire.EncodeStep(v, nd.Unit); RefreshStatusIndicators(); });
        return Row(label, stepBox);
    }

    private FrameworkElement BuildValueRow(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        if (nd.Kind == CsKind.Bool)
        {
            var combo = new ComboBox { MinWidth = 120 };
            combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = (short)0 });
            combo.Items.Add(new ComboBoxItem { Content = "On", Tag = (short)1 });
            combo.SelectedIndex = draft.Value != 0 ? 1 : 0;
            combo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v)
                { _drafts[slot].Value = v; RefreshStatusIndicators(); }
            };
            return Row("Value", combo);
        }
        if (nd.Kind == CsKind.Enum)
        {
            var combo = new ComboBox { MinWidth = 120 };
            for (int i = 0; i < Math.Max(1, (int)nd.EnumCount); i++)
                combo.Items.Add(new ComboBoxItem { Content = EnumLabel(draft.Noun, i), Tag = (short)i });
            combo.SelectedIndex = Math.Clamp((int)draft.Value, 0, Math.Max(0, nd.EnumCount - 1));
            combo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v)
                { _drafts[slot].Value = v; RefreshStatusIndicators(); }
            };
            return Row(draft.Noun == (byte)CsNoun.Macro ? "Macro" : "Value", combo);
        }
        var box = NumberField(CsWire.DecodeValue(draft.Value, nd.Unit), nd.Unit, v =>
        { _drafts[slot].Value = CsWire.EncodeValue(v, nd.Unit); RefreshStatusIndicators(); });
        return Row(ValueLabel(nd.Unit), box);
    }

    private FrameworkElement BuildFlagRows(int slot, CsNounDesc nd)
    {
        var draft = _drafts[slot];
        var action = (CsAction)draft.Action;
        var panel = new StackPanel { Spacing = 2 };

        panel.Children.Add(FlagToggle(slot, CsFlags.Invert,
            draft.Type is CsType.Led or CsType.LedPwm ? "Active-low output" : "Active-low input (pull-up)"));

        if (draft.Type is CsType.Pot or CsType.Encoder)
            panel.Children.Add(FlagToggle(slot, CsFlags.Reverse, "Reverse direction"));
        if (draft.Type == CsType.Encoder)
            panel.Children.Add(FlagToggle(slot, CsFlags.Accel, "Acceleration (fast rotation = bigger steps)"));
        if (nd.Kind == CsKind.Enum && action is CsAction.Step or CsAction.Inc or CsAction.Dec)
            panel.Children.Add(FlagToggle(slot, CsFlags.Wrap, "Wrap around at the ends"));
        if (draft.Type == CsType.Button && action is CsAction.Inc or CsAction.Dec && draft.Event == (byte)CsEvent.Press)
            panel.Children.Add(FlagToggle(slot, CsFlags.Repeat, "Auto-repeat while held"));

        // Group-only modifiers (caps v9). Without a group the firmware rejects
        // either bit, so they are only offered on a grouped binding.
        if (draft.IsGrouped)
        {
            if (action == CsAction.Adjust && nd.Kind == CsKind.Continuous)
                panel.Children.Add(FlagToggle(slot, CsFlags.LinkAbs,
                    "Move every channel to the same value (instead of keeping their offsets)"));
            if (action is CsAction.IndEquals or CsAction.IndAbove)
                panel.Children.Add(FlagToggle(slot, CsFlags.GroupAll,
                    "Light only when every channel matches (instead of any)"));
        }

        return panel;
    }

    /// <summary>Indicator condition timing (caps v8): the condition must hold for
    /// the on-delay before the LED lights and for the off-delay before it goes out.
    /// LED types with IND_EQUALS / IND_ABOVE only — the firmware rejects a non-zero
    /// delay anywhere else. Wire units are 0.1 s; the fields are in seconds.</summary>
    private FrameworkElement BuildIndicatorDelayRows(int slot)
    {
        var draft = _drafts[slot];
        var panel = new StackPanel { Spacing = 8 };

        // 0.1 s units in a uint16, so the field clamps at 0..6553.5 s; echo the
        // stored value back rather than leave an out-of-range number on screen.
        TextBox onBox = null!, offBox = null!;
        onBox = NumberField(draft.OnDelay * CsLimits.IndicatorDelayUnitSeconds, CsUnit.None, v =>
        {
            _drafts[slot].OnDelay = EncodeIndicatorDelay(v);
            double stored = _drafts[slot].OnDelay * CsLimits.IndicatorDelayUnitSeconds;
            if (Math.Abs(stored - v) > 0.005) onBox.Text = FormatNumber(stored);
            RefreshStatusIndicators();
        });
        offBox = NumberField(draft.OffDelay * CsLimits.IndicatorDelayUnitSeconds, CsUnit.None, v =>
        {
            _drafts[slot].OffDelay = EncodeIndicatorDelay(v);
            double stored = _drafts[slot].OffDelay * CsLimits.IndicatorDelayUnitSeconds;
            if (Math.Abs(stored - v) > 0.005) offBox.Text = FormatNumber(stored);
            RefreshStatusIndicators();
        });
        ToolTipService.SetToolTip(onBox, "Hold the condition this long before lighting (0 = immediate)");
        ToolTipService.SetToolTip(offBox, "Hold the condition false this long before going out (0 = immediate)");

        panel.Children.Add(Row("On delay (s)", onBox));
        panel.Children.Add(Row("Off delay (s)", offBox));
        return panel;
    }

    private static ushort EncodeIndicatorDelay(double seconds) =>
        (ushort)Math.Clamp(Math.Round(seconds / CsLimits.IndicatorDelayUnitSeconds), 0, ushort.MaxValue);

    /// <summary>Per-LED brightness ceiling (caps v12). The duty is scaled, not
    /// clipped, so a meter keeps its whole sweep and only its top end moves —
    /// which is how a panel of mismatched LEDs is evened out, and how the lot is
    /// dimmed for a dark room. The wire field's 0 means "unset", identical in
    /// effect to 100, so the box shows 100 for it and only ever writes a real
    /// percentage back.</summary>
    private FrameworkElement BuildBrightnessCeilingRow(int slot)
    {
        var draft = _drafts[slot];
        TextBox box = null!;
        box = NumberField(draft.BaseBright == 0 ? CsLimits.LedBrightMax : draft.BaseBright, CsUnit.None, v =>
        {
            byte pct = (byte)Math.Clamp(Math.Round(v), 1, CsLimits.LedBrightMax);
            _drafts[slot].BaseBright = pct;
            if (Math.Abs(pct - v) > 0.005) box.Text = FormatNumber(pct);
            RefreshStatusIndicators();
        });
        ToolTipService.SetToolTip(box,
            "Cap on how bright this LED gets, as a share of full. Everything below the cap scales with it.");
        return Row("Brightness limit (%)", box);
    }

    /// <summary>A line under the editor for the two nouns whose behaviour depends
    /// on the panel rather than on the binding: what the control will actually do
    /// is a function of the display's own arming gate, which is edited on another
    /// card entirely.</summary>
    private FrameworkElement? DisplayNounNote(CsBinding b)
    {
        string? text = (CsNoun)b.Noun switch
        {
            CsNoun.PageValue => PageValueNote(b),
            CsNoun.DisplayPage => DisplayPageNote(b),
            CsNoun.DisplayEdit => (CsAction)b.Action is CsAction.IndEquals or CsAction.IndAbove
                ? "Lights while editing is armed."
                : "Arms editing of the page on screen. The panel disarms itself again after the "
                  + "editing timeout.",
            _ => null,
        };
        if (text == null) return null;
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = SecondaryBrush,
        };
    }

    /// <summary>Browse/Adjust does two jobs, and which one a control gets depends
    /// on the panel's own gate: with "Arm before editing" off it only ever
    /// adjusts, so a note promising page browsing would describe another config.</summary>
    private string? PageValueNote(CsBinding b)
    {
        bool gated = _vm.CsDisplayCfg.HasFlag(CsDisplayCfgFlags.EditGated);
        string press = PressWord(b);
        return (CsAction)b.Action switch
        {
            CsAction.Step => gated
                ? "Turn to move through pages, or to adjust the shown value once editing is armed."
                : "Turn to adjust the shown value.",
            CsAction.Inc => gated
                ? $"{press} for the next page, or to raise the shown value once editing is armed."
                : $"{press} to raise the shown value.",
            CsAction.Dec => gated
                ? $"{press} for the previous page, or to lower the shown value once editing is armed."
                : $"{press} to lower the shown value.",
            // Toggle is the one action here the device cannot validate up front:
            // the page it lands on isn't known until the press, and the firmware
            // silently no-ops it on anything but an on/off item.
            CsAction.Toggle => gated
                ? $"{press} to toggle the shown value, once editing is armed. Only acts on a page "
                  + "showing an on/off setting."
                : $"{press} to toggle the shown value. Only acts on a page showing an on/off setting.",
            _ => null,
        };
    }

    /// <summary>The page noun names the panel, not a position in a list: the
    /// generic enum wording ("select the next Show Page") reads as nonsense once
    /// the noun itself says "show".</summary>
    private string? DisplayPageNote(CsBinding b)
    {
        string press = PressWord(b);
        return (CsAction)b.Action switch
        {
            CsAction.Step => "Turn to move through the pages on screen.",
            CsAction.Inc => $"{press} to show the next page.",
            CsAction.Dec => $"{press} to show the previous page.",
            CsAction.Set => $"{press} to show a set page.",
            CsAction.IndEquals => "Lights while a set page is on screen.",
            _ => null,
        };
    }

    /// <summary>"Press" / "Long-press" / "Double-press" for a button binding's
    /// gesture; anything else is simply pressed.</summary>
    private static string PressWord(CsBinding b) =>
        b.Type != CsType.Button ? "Press"
        : (CsEvent)b.Event switch
        {
            CsEvent.Long => "Long-press",
            CsEvent.Double => "Double-press",
            _ => "Press",
        };

    private CheckBox FlagToggle(int slot, CsFlags flag, string label)
    {
        var cb = new CheckBox { Content = label, IsChecked = _drafts[slot].Flags.HasFlag(flag) };
        cb.Checked += (_, _) => { _drafts[slot].Flags |= flag; RefreshStatusIndicators(); };
        cb.Unchecked += (_, _) => { _drafts[slot].Flags &= ~flag; RefreshStatusIndicators(); };
        return cb;
    }

    private FrameworkElement BuildApplyRow(int slot)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (_drafts[slot].Type == CsType.Ir)
        {
            var addBtn = new Button { Content = "Add Remote Button" };
            addBtn.Click += (_, _) => AddIrCommand(slot);
            addBtn.IsEnabled = _vm.CsStatus?.IsSlotActive(slot) == true && FirstFreeIrSub() >= 0;
            _addRemoteButton = addBtn;
            panel.Children.Add(addBtn);
        }
        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) => RevertSlot(slot);
        var apply = new Button { Content = "Apply", Style = AccentStyle };
        apply.Click += (_, _) => _ = ApplySlotAsync(slot);
        _slotApply[slot] = apply;
        panel.Children.Add(revert);
        panel.Children.Add(apply);
        return panel;
    }

    // ── IR command table ─────────────────────────────────────────────────────

    private FrameworkElement BuildIrCommandsSection(int slot)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _irSectionPanel = panel;
        _irSectionSlot = slot;
        PopulateIrCommandsSection();
        return panel;
    }

    /// <summary>Fill (or refill) the IR receiver's remote-button list in place.
    /// Used instead of a full <see cref="RebuildCards"/> for IR-only edits so the
    /// receiver card and scroll position don't jump.</summary>
    private void PopulateIrCommandsSection()
    {
        if (_irSectionPanel is not { } panel) return;
        int slot = _irSectionSlot;
        _building = true;
        try
        {
            panel.Children.Clear();
            _irCommandCards.Clear();
            _irBodies.Clear();
            _irChips.Clear();
            _irLearnButtons.Clear();
            _irTitles.Clear();
            _irChannelRelabel.Clear();
            bool receiverLive = _vm.CsStatus?.IsSlotActive(slot) == true;

            _irCountLabel = new TextBlock { Text = IrCountText(), FontWeight = FontWeights.SemiBold };
            panel.Children.Add(_irCountLabel);
            _irLeadingCount = 1;
            if (!receiverLive)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Apply the receiver first, then learn remote buttons.",
                    FontSize = 11, TextWrapping = TextWrapping.Wrap,
                    Foreground = SecondaryBrush,
                });
                _irLeadingCount = 2;
            }

            for (int sub = 0; sub < _vm.CsIrMax; sub++)
                if (IrSubShown(sub))
                    panel.Children.Add(BuildIrCommandCard(sub, receiverLive));

            UpdateAddRemoteButtonState();
        }
        finally { _building = false; }
    }

    private bool IrSubShown(int sub) => _irDrafts[sub].IsConfigured || _irExpanded.Contains(sub);

    private string IrCountText() => $"Remote Buttons ({ConfiguredIrCount()}/{_vm.CsIrMax})";

    private void UpdateIrCount()
    {
        if (_irCountLabel != null) _irCountLabel.Text = IrCountText();
    }

    private void UpdateAddRemoteButtonState()
    {
        if (_addRemoteButton != null)
            _addRemoteButton.IsEnabled =
                _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true && FirstFreeIrSub() >= 0;
    }

    /// <summary>While a learn is in progress every other card's Learn button is
    /// disabled; refresh their enabled state in place without rebuilding them.</summary>
    private void RefreshIrLearnButtons()
    {
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        foreach (var (_, btn) in _irLearnButtons)
            btn.IsEnabled = receiverLive && _learningSub == null;
    }

    /// <summary>Build and insert one remote-button card at its sub-ordered position,
    /// leaving the sibling cards untouched.</summary>
    private void InsertIrCommandCard(int sub)
    {
        if (_irSectionPanel is not { } panel) return;
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        int index = _irLeadingCount;
        for (int s = 0; s < sub; s++)
            if (_irCommandCards.ContainsKey(s)) index++;
        _building = true;
        try { panel.Children.Insert(index, BuildIrCommandCard(sub, receiverLive)); }
        finally { _building = false; }
    }

    /// <summary>Remove one remote-button card and drop its handles.</summary>
    private void RemoveIrCommandCard(int sub)
    {
        if (_irCommandCards.TryGetValue(sub, out var card))
            _irSectionPanel?.Children.Remove(card);
        _irCommandCards.Remove(sub);
        _irBodies.Remove(sub);
        _irChips.Remove(sub);
        _irLearnButtons.Remove(sub);
        _irTitles.Remove(sub);
        _irChannelRelabel.Remove(sub);
    }

    /// <summary>Refresh one remote-button card in place: its body (learn state,
    /// noun / action / operand rows) plus the header chip and title. The card
    /// element itself is never recreated, so the expander doesn't re-animate and
    /// the list doesn't shift under the pointer.</summary>
    private void RefreshIrCommandCard(int sub)
    {
        PopulateIrCommandBody(sub);
        RefreshIrCommandChip(sub);
        UpdateIrTitle(sub);
    }

    /// <summary>Restate the learned-code chip in the card header.</summary>
    private void RefreshIrCommandChip(int sub)
    {
        if (!_irChips.TryGetValue(sub, out var chip)) return;
        var draft = _irDrafts[sub];
        var color = draft.IsConfigured
            ? Color.FromArgb(255, 100, 200, 140)
            : Color.FromArgb(255, 0x90, 0x90, 0x90);
        chip.Label.Text = draft.CodeLabel;
        chip.Label.Foreground = new SolidColorBrush(color);
        chip.Chip.Background = new SolidColorBrush(Color.FromArgb(0x20, color.R, color.G, color.B));
    }

    private FrameworkElement BuildIrCommandCard(int sub, bool receiverLive)
    {
        var draft = _irDrafts[sub];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _irExpanded.Contains(sub),
        };
        expander.Expanding += (_, _) => { _irExpanded.Add(sub); Reveal.Bring(expander); };
        expander.Collapsed += (_, _) => _irExpanded.Remove(sub);

        // Header: binding summary + learned-code chip + delete.
        var hgrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = IrCommandTitle(sub),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _irTitles[sub] = title;
        Grid.SetColumn(title, 0);
        hgrid.Children.Add(title);

        var chipLabel = new TextBlock
        {
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
        };
        var chip = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chipLabel,
        };
        _irChips[sub] = (chip, chipLabel);
        RefreshIrCommandChip(sub);
        Grid.SetColumn(chip, 1);
        hgrid.Children.Add(chip);
        var delc = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 13 },
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
        };
        delc.Click += (_, _) => RemoveIrCommand(sub);
        Grid.SetColumn(delc, 2);
        hgrid.Children.Add(delc);
        expander.Header = hgrid;

        // Body: learn + operand editor, filled by PopulateIrCommandBody so a later
        // noun / action / learn change refills it without recreating the card.
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _irBodies[sub] = body;
        PopulateIrCommandBody(sub);

        expander.Content = body;
        _irCommandCards[sub] = expander;
        return expander;
    }

    /// <summary>Fill (or refill) one remote-button card's body in place.</summary>
    private void PopulateIrCommandBody(int sub)
    {
        if (!_irBodies.TryGetValue(sub, out var body)) return;
        bool receiverLive = _vm.CsStatus?.IsSlotActive(_irSectionSlot) == true;
        var draft = _irDrafts[sub];
        bool wasBuilding = _building;
        _building = true;
        try
        {
            body.Children.Clear();
            _irLearnButtons.Remove(sub);
            _irChannelRelabel.Remove(sub);

            var learnPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (_learningSub == sub)
            {
                // This card shows a Cancel, not a Learn button.
                learnPanel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
                learnPanel.Children.Add(new TextBlock
                {
                    Text = "Point the remote at the receiver…", VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                });
                var cancel = new Button { Content = "Cancel" };
                cancel.Click += (_, _) => CancelLearn();
                learnPanel.Children.Add(cancel);
            }
            else
            {
                var learn = new Button { Content = draft.IsConfigured ? "Re-learn" : "Learn Button" };
                learn.IsEnabled = receiverLive && _learningSub == null;
                learn.Click += (_, _) => StartLearn(sub);
                learnPanel.Children.Add(learn);
                _irLearnButtons[sub] = learn;
            }
            body.Children.Add(learnPanel);

            // Noun / action / target / operand for the IR command (button-shaped).
            body.Children.Add(BuildIrNounRow(sub));
            var nd = _vm.CsNounDescFor(draft.Noun);
            if (nd != null)
            {
                var actions = ValidIrActions(nd);
                if (actions.Count > 1) body.Children.Add(BuildIrActionRow(sub, actions));
                if (nd.IsTargeted) body.Children.Add(BuildIrTargetRows(sub, nd));
                var op = BuildIrOperand(sub, nd);
                if (op != null) body.Children.Add(op);
            }

            var applyRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0),
            };
            var apply = new Button { Content = "Apply", Style = AccentStyle };
            apply.IsEnabled = draft.IsConfigured && receiverLive;
            apply.Click += (_, _) => _ = ApplyIrCommandAsync(sub);
            applyRow.Children.Add(apply);
            body.Children.Add(applyRow);
        }
        finally { _building = wasBuilding; }
    }

    private FrameworkElement BuildIrNounRow(int sub)
    {
        var draft = _irDrafts[sub];
        var combo = new ComboBox { MinWidth = 200 };
        int sel = -1, idx = 0;
        foreach (var (noun, nd) in AvailableIrNouns())
        {
            combo.Items.Add(new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun });
            if (noun == draft.Noun) sel = idx;
            idx++;
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is int noun)
            {
                _irDrafts[sub].Noun = (byte)noun;
                var nd = _vm.CsNounDescFor(noun);
                var acts = nd != null ? ValidIrActions(nd) : new List<CsAction>();
                _irDrafts[sub].Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
                // Operands and the target belong to the noun that had them.
                _irDrafts[sub].Value = 0; _irDrafts[sub].Step = 0;
                _irDrafts[sub].Target = 0; _irDrafts[sub].Index = 0;
                _irDrafts[sub].Flags &= ~(CsFlags.Group | CsFlags.Wrap);
                SanitizeIrDraft(sub);
                RefreshIrCommandCard(sub);
            }
        };
        return Row("Controls", combo);
    }

    private FrameworkElement BuildIrActionRow(int sub, List<CsAction> actions)
    {
        var draft = _irDrafts[sub];
        var combo = new ComboBox { MinWidth = 160 };
        int sel = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = ActionName(actions[i], draft.Noun), Tag = actions[i] });
            if ((byte)actions[i] == draft.Action) sel = i;
        }
        combo.SelectedIndex = sel;
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is ComboBoxItem it && it.Tag is CsAction a)
            {
                _irDrafts[sub].Action = (byte)a;
                SanitizeIrDraft(sub);
                RefreshIrCommandCard(sub);
            }
        };
        return Row("Action", combo);
    }

    private FrameworkElement BuildIrTargetRows(int sub, CsNounDesc nd)
    {
        var draft = _irDrafts[sub];
        var panel = new StackPanel { Spacing = 8 };
        // A remote key may address a group since caps v10, which closed the gap
        // where grouped volume from a remote needed a macro fired per press.
        var groups = _vm.CsIrGroupsSupported ? CompatibleGroups(nd).ToList() : new List<int>();
        var chCombo = new ComboBox { MinWidth = 160 };
        for (int i = 0; i < nd.TargetCount; i++)
            chCombo.Items.Add(new ComboBoxItem { Content = ChannelLabel(nd.TargetKind, i), Tag = i });
        foreach (int g in groups)
            chCombo.Items.Add(new ComboBoxItem { Content = $"Group: {_vm.CsGroupLabel(g)}", Tag = new GroupTag(g) });
        _irChannelRelabel[sub] = () => Relabel(chCombo, nd.TargetKind);
        chCombo.SelectedIndex = draft.IsGrouped
            ? (groups.IndexOf(draft.Target) is var gi && gi >= 0 ? nd.TargetCount + gi : -1)
            : (draft.Target < nd.TargetCount ? draft.Target : 0);
        chCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (chCombo.SelectedItem is not ComboBoxItem it) return;
            if (it.Tag is int ch)
            {
                _irDrafts[sub].Flags &= ~CsFlags.Group;
                _irDrafts[sub].Target = (byte)ch;
            }
            else if (it.Tag is GroupTag g)
            {
                _irDrafts[sub].Flags |= CsFlags.Group;
                _irDrafts[sub].Target = (byte)g.Index;
            }
            UpdateIrTitle(sub);
        };
        panel.Children.Add(Row(groups.Count > 0 ? "Target" : "Channel", chCombo));

        if (nd.HasBand)
        {
            var bandCombo = new ComboBox { MinWidth = 160 };
            var opts = BandOptions(draft.Noun).ToList();
            int sel = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                bandCombo.Items.Add(new ComboBoxItem { Content = opts[i].label, Tag = opts[i].band });
                if (opts[i].band == draft.Index) sel = i;
            }
            bandCombo.SelectedIndex = sel;
            bandCombo.SelectionChanged += (_, _) =>
            {
                if (_building) return;
                if (bandCombo.SelectedItem is ComboBoxItem it && it.Tag is int band)
                    _irDrafts[sub].Index = (byte)band;
            };
            panel.Children.Add(Row("Band", bandCombo));
        }
        return panel;
    }

    private FrameworkElement? BuildIrOperand(int sub, CsNounDesc nd)
    {
        var draft = _irDrafts[sub];
        var action = (CsAction)draft.Action;

        // Browse/Adjust resolves its item at event time (see BuildOperandRows);
        // the firmware requires its value and step to stay zero.
        if (draft.Noun == (byte)CsNoun.PageValue) return null;
        if (action is CsAction.Inc or CsAction.Dec)
        {
            string label = CsWire.UnitStepsInOctaves(nd.Unit) ? "Step (octaves)"
                : nd.Unit == CsUnit.None ? "Step (positions)" : StepLabel(nd.Unit);
            var box = NumberField(CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
                _irDrafts[sub].Step = CsWire.EncodeStep(v, nd.Unit));
            return Row(label, box);
        }
        if (action is CsAction.Set or CsAction.Momentary)
        {
            if (nd.Kind == CsKind.Bool)
            {
                var combo = new ComboBox { MinWidth = 120 };
                combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = (short)0 });
                combo.Items.Add(new ComboBoxItem { Content = "On", Tag = (short)1 });
                combo.SelectedIndex = draft.Value != 0 ? 1 : 0;
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building) return;
                    if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v) _irDrafts[sub].Value = v;
                };
                return Row("Value", combo);
            }
            if (nd.Kind == CsKind.Enum)
            {
                var combo = new ComboBox { MinWidth = 120 };
                for (int i = 0; i < Math.Max(1, (int)nd.EnumCount); i++)
                    combo.Items.Add(new ComboBoxItem { Content = EnumLabel(draft.Noun, i), Tag = (short)i });
                combo.SelectedIndex = Math.Clamp((int)draft.Value, 0, Math.Max(0, nd.EnumCount - 1));
                combo.SelectionChanged += (_, _) =>
                {
                    if (_building) return;
                    if (combo.SelectedItem is ComboBoxItem it && it.Tag is short v)
                    { _irDrafts[sub].Value = v; UpdateIrTitle(sub); }
                };
                return Row(draft.Noun == (byte)CsNoun.Macro ? "Macro" : "Value", combo);
            }
            var box = NumberField(CsWire.DecodeValue(draft.Value, nd.Unit), nd.Unit, v =>
                _irDrafts[sub].Value = CsWire.EncodeValue(v, nd.Unit));
            return Row(ValueLabel(nd.Unit), box);
        }
        return null; // Toggle / Trigger
    }

    // ── Actions: add / remove / change ───────────────────────────────────────

    private async void AddControl(CsType type)
    {
        int slot = FirstFreeSlot();
        if (slot < 0) return;
        _drafts[slot] = MakeDefaultBinding(type, slot);
        _nameEdits[slot] = "";
        _expanded.Add(slot);
        // Insert just the new card instead of rebuilding the panel, so existing
        // cards don't reload.
        InsertSlotCard(slot);
        if (_slotCards.TryGetValue(slot, out var card)) Reveal.Bring(card);
        BuildAddMenu();
        // A freshly-added control is seeded valid — apply immediately.
        await ApplySlotAsync(slot);
    }

    /// <summary>Build one slot's card and insert it into <c>CardsPanel</c> at the
    /// position matching slot order, leaving the other cards untouched.</summary>
    private void InsertSlotCard(int slot)
    {
        int index = 0;
        for (int s = 0; s < slot; s++)
            if (_drafts[s].IsConfigured) index++;
        CardsPanel.Children.Insert(index, BuildSlotCard(slot));
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void ChangeNoun(int slot, int noun)
    {
        var b = _drafts[slot];
        b.Noun = (byte)noun;
        var nd = _vm.CsNounDescFor(noun);
        if (nd != null)
        {
            var acts = ValidActions(b.Type, nd);
            if (!acts.Contains((CsAction)b.Action))
                b.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
            if (!b.IsGrouped && b.Target >= nd.TargetCount) b.Target = 0;
        }
        // Reset operands to defaults for the new noun.
        b.Value = 0; b.Step = 0; b.RangeMin = 0; b.RangeMax = 0;
        SanitizeDraft(slot);
        PopulateSlotBody(slot);
    }

    private void ChangeAction(int slot, CsAction action)
    {
        _drafts[slot].Action = (byte)action;
        _drafts[slot].Value = 0; _drafts[slot].Step = 0;
        _drafts[slot].RangeMin = 0; _drafts[slot].RangeMax = 0;
        SanitizeDraft(slot);
        PopulateSlotBody(slot);
    }

    /// <summary>Drop draft fields the firmware would now reject: a group reference
    /// the new noun can't address, the two group-only flag modifiers outside the
    /// action they belong to, indicator delays on anything but an LED condition,
    /// a brightness ceiling on anything but a PWM LED, and any operand at all on
    /// Browse/Adjust. All of them are strict, all-or-nothing checks in
    /// <c>cs_validate</c>, so an edit that changes the noun or action has to clear
    /// them.</summary>
    private void SanitizeDraft(int slot)
    {
        var b = _drafts[slot];
        var nd = _vm.CsNounDescFor(b.Noun);
        var action = (CsAction)b.Action;

        // Browse/Adjust resolves its item at event time, so it has no static
        // operands to carry and takes no target. It reads as a one-value enum,
        // which would otherwise pick up a step of 1.
        if (b.Noun == (byte)CsNoun.PageValue)
        {
            b.Value = 0; b.Step = 0; b.RangeMin = 0; b.RangeMax = 0;
            b.Target = 0; b.Index = 0;
            b.Flags &= ~(CsFlags.Group | CsFlags.LinkAbs | CsFlags.GroupAll);
        }

        if (b.IsGrouped && (nd == null || GroupKindFor(nd) == null || !CompatibleGroups(nd).Contains(b.Target)))
        {
            b.Flags &= ~CsFlags.Group;
            b.Target = 0;
        }
        if (!b.IsGrouped)
        {
            b.Flags &= ~(CsFlags.LinkAbs | CsFlags.GroupAll);
        }
        else
        {
            if (action != CsAction.Adjust || nd?.Kind != CsKind.Continuous) b.Flags &= ~CsFlags.LinkAbs;
            if (action is not (CsAction.IndEquals or CsAction.IndAbove)) b.Flags &= ~CsFlags.GroupAll;
        }
        if (!SupportsIndicatorDelay(b)) { b.OnDelay = 0; b.OffDelay = 0; }
        // The ceiling byte was reserved before caps v12 and is rejected on every
        // component but a PWM LED, so it has to go with the type it belonged to.
        if (b.Type != CsType.LedPwm || !_vm.CsLedBrightnessSupported) b.BaseBright = 0;
    }

    /// <summary>The IR-command counterpart of <see cref="SanitizeDraft"/>: a
    /// remote key carries a smaller record, but Browse/Adjust is just as strict
    /// about it, and a group reference has to survive the same checks.</summary>
    private void SanitizeIrDraft(int sub)
    {
        var c = _irDrafts[sub];
        var nd = _vm.CsNounDescFor(c.Noun);

        if (c.Noun == (byte)CsNoun.PageValue)
        {
            c.Value = 0; c.Step = 0; c.Target = 0; c.Index = 0;
            c.Flags &= ~(CsFlags.Group | CsFlags.Wrap);
            return;
        }
        if (c.IsGrouped
            && (nd == null || !_vm.CsIrGroupsSupported || GroupKindFor(nd) == null
                || !CompatibleGroups(nd).Contains(c.Target)))
        {
            c.Flags &= ~CsFlags.Group;
            c.Target = 0;
        }
    }

    /// <summary>Whether this binding may carry <c>on_delay</c>/<c>off_delay</c>:
    /// an LED (plain or PWM) driving a boolean indicator condition.</summary>
    private static bool SupportsIndicatorDelay(CsBinding b) =>
        b.Type is CsType.Led or CsType.LedPwm
        && (CsAction)b.Action is CsAction.IndEquals or CsAction.IndAbove;

    private async void RemoveControl(int slot)
    {
        // Unapplied slot → just drop the local draft and its card. It never claimed a
        // pin (only live slots do), so no other card needs refreshing.
        if (!_vm.CsBindings[slot].IsConfigured)
        {
            _drafts[slot] = CsBinding.Cleared();
            _nameEdits[slot] = "";
            RemoveSlotCard(slot);
            BuildAddMenu();
            return;
        }
        bool wasDisplay = _vm.CsBindings[slot].Type == CsType.Display;
        _drafts[slot] = CsBinding.Cleared();
        _nameEdits[slot] = "";
        RemoveSlotCard(slot);
        await Task.Run(() =>
        {
            _vm.SetCsBinding(slot, CsBinding.Cleared());
            if (!string.IsNullOrEmpty(_vm.CsNames[slot])) _vm.SetCsName(slot, "");
            // The panel is detached now; its config and pages stay, but its live
            // state has moved.
            if (wasDisplay) _vm.RefreshCsDisplay();
        });
        HardwarePins.RaisePinAssignmentsChanged();
        SeedDraftFrom(slot);
        // The removed slot freed its GPIO(s) — refresh the other cards' pin lists in
        // place so those pins reappear, without reloading their bodies.
        RefreshOtherSlotPins(slot);
        RefreshStatusIndicators();
        BuildAddMenu();
    }

    /// <summary>Remove one slot's card element and drop its per-slot UI handles,
    /// leaving the other cards untouched.</summary>
    private void RemoveSlotCard(int slot)
    {
        if (_slotCards.TryGetValue(slot, out var card))
            CardsPanel.Children.Remove(card);
        _slotCards.Remove(slot);
        _slotBodies.Remove(slot);
        _slotPills.Remove(slot);
        _slotTitles.Remove(slot);
        _slotSummaries.Remove(slot);
        _slotApply.Remove(slot);
        _pinRefreshers.Remove(slot);
        _slotPinCombos.Remove((slot, false));
        _slotPinCombos.Remove((slot, true));
        _channelRelabel.Remove(slot);
        _expanded.Remove(slot);
        if (slot == _displaySlot)
        {
            // The display card (which hosts the config and page sections) is gone.
            ClearDisplayHandles();
            StopDisplayPoll();
        }
        if (slot == _irSectionSlot)
        {
            // The IR receiver card (which hosts the remote-button section) is gone.
            _irSectionPanel = null;
            _irSectionSlot = -1;
            _addRemoteButton = null;
            _irCountLabel = null;
            _irCommandCards.Clear();
            _irBodies.Clear();
            _irChips.Clear();
            _irLearnButtons.Clear();
            _irTitles.Clear();
            _irChannelRelabel.Clear();
        }
        if (CardsPanel.Children.Count == 0) EmptyHint.Visibility = Visibility.Visible;
    }

    private void CommitName(int slot, string text)
    {
        _nameEdits[slot] = text ?? "";
        // The display card has no Apply button - the whole card applies as it
        // is edited - so a committed name goes to the device from here.
        if (_drafts[slot].Type == CsType.Display && SlotDirty(slot)) ApplyDisplayWiring(slot);
        else RefreshStatusIndicators();
    }

    private void RevertSlot(int slot)
    {
        SeedDraftFrom(slot);
        // Refill this card's body (the name box and every row come back from the
        // restored draft); its header and the other cards are untouched.
        PopulateSlotBody(slot);
    }

    private async Task ApplySlotAsync(int slot)
    {
        if (_applyingSlot != null) return;
        _applyingSlot = slot;
        RefreshStatusIndicators();

        var binding = _drafts[slot].Clone();
        bool bindingChanged = !binding.WireEquals(_vm.CsBindings[slot]);
        bool nameChanged = !string.Equals(_nameEdits[slot], _vm.CsNames[slot], StringComparison.Ordinal);
        string name = _nameEdits[slot];

        byte status = CsStatus.Success;
        await Task.Run(() =>
        {
            if (bindingChanged) status = _vm.SetCsBinding(slot, binding);
            if (status == CsStatus.Success && nameChanged) status = _vm.SetCsName(slot, name);
            // The firmware seeds a display's page table the first time one
            // attaches, so a panel that just came up already has pages the app
            // has never read.
            if (binding.Type == CsType.Display && status == CsStatus.Success) _vm.RefreshCsDisplay();
        });

        _applyingSlot = null;
        HardwarePins.RaisePinAssignmentsChanged();
        // Only a success re-seeds from the device. A binding the firmware refused
        // reads back as an empty slot, which would blank the card and take the
        // message explaining the refusal with it — the display is the first
        // component strict enough to hit that often. A rejected draft stays put.
        if (status == CsStatus.Success) SeedDraftFrom(slot);
        // Fully refresh only the applied card. Applying makes this slot live, so it now
        // claims its GPIO(s) — the other cards just need their pin lists refreshed in
        // place (not their whole bodies, which would flash their Apply buttons).
        PopulateSlotBody(slot);
        RefreshOtherSlotPins(slot);
        BuildAddMenu();
        // Slot health moved, which the group and macro pages show in their pills.
        RaiseStateChanged();

        if (status != CsStatus.Success)
            ShowToast(CsStatus.Message(status));
    }

    /// <summary>Re-run every other slot's GPIO pickers in place so a pin this slot just
    /// claimed (or freed) drops out of (or back into) their option lists, without
    /// recreating their card bodies.</summary>
    private void RefreshOtherSlotPins(int exceptSlot)
    {
        _building = true;
        try
        {
            foreach (var (slot, refreshers) in _pinRefreshers)
            {
                if (slot == exceptSlot) continue;
                foreach (var refresh in refreshers) refresh();
            }
        }
        finally { _building = false; }
    }

    // ── IR command actions ───────────────────────────────────────────────────

    private void AddIrCommand(int slot)
    {
        int sub = FirstFreeIrSub();
        if (sub < 0) return;
        var draft = new IrCommand();
        var noun = AvailableIrNouns().FirstOrDefault();
        if (noun.nd != null)
        {
            draft.Noun = (byte)noun.noun;
            var acts = ValidIrActions(noun.nd);
            draft.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
        }
        _irDrafts[sub] = draft;
        _irExpanded.Add(sub);
        // Insert just the new remote-button card; siblings stay put.
        InsertIrCommandCard(sub);
        if (_irCommandCards.TryGetValue(sub, out var card)) Reveal.Bring(card);
        UpdateIrCount();
        UpdateAddRemoteButtonState();
    }

    private async void RemoveIrCommand(int sub)
    {
        bool wasLive = _vm.CsIrCommands[sub].IsConfigured;
        _irDrafts[sub] = new IrCommand();
        _irExpanded.Remove(sub);
        // Remove just this card; siblings stay put.
        RemoveIrCommandCard(sub);
        UpdateIrCount();
        UpdateAddRemoteButtonState();
        if (wasLive)
        {
            await Task.Run(() => _vm.SetCsIrCommand(sub, new IrCommand()));
            _irDrafts[sub] = _vm.CsIrCommands[sub].Clone();
            UpdateIrCount();
            UpdateAddRemoteButtonState();
        }
    }

    private async Task ApplyIrCommandAsync(int sub)
    {
        var cmd = _irDrafts[sub].Clone();
        if (!cmd.IsConfigured) { ShowToast("Learn a code before applying this button."); return; }
        byte status = await Task.Run(() => _vm.SetCsIrCommand(sub, cmd));
        _irDrafts[sub] = _vm.CsIrCommands[sub].Clone();
        RefreshIrCommandCard(sub);
        UpdateIrCount();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
    }

    private void StartLearn(int sub)
    {
        if (_learningSub != null) return;
        _learningSub = sub;
        PopulateIrCommandBody(sub);  // show the learning spinner on this card
        RefreshIrLearnButtons();     // disable the other cards' Learn buttons

        var proto = _irDrafts[sub];
        _ = Task.Run(async () =>
        {
            bool armed = _vm.CsIrLearnArm();
            if (!armed)
            {
                DispatcherQueue.TryEnqueue(() => FinishLearn(sub, null, "No IR receiver is active."));
                return;
            }
            CsIrLearnResult? result = null;
            for (int i = 0; i < 115 && _learningSub == sub; i++)
            {
                await Task.Delay(100);
                var r = _vm.CsIrLearnRead();
                if (r != null && r.State != CsIrLearnState.Armed) { result = r; break; }
            }
            DispatcherQueue.TryEnqueue(() => FinishLearn(sub, result, null));
        });
    }

    private void FinishLearn(int sub, CsIrLearnResult? result, string? error)
    {
        if (_learningSub != sub) return;
        _learningSub = null;

        if (error == null)
        {
            if (result != null && result.IsDone && result.Code != 0)
            {
                _irDrafts[sub].Proto = result.Proto;
                _irDrafts[sub].Code = result.Code;
                ShowToast($"Learned a {CsWire.IrProtocolName(result.Proto)} code. Apply to keep it.");
            }
            else if (result != null && result.IsTimeout)
            {
                ShowToast("No remote button was detected.");
            }
            else
            {
                ShowToast("Learn stopped.");
            }
        }
        else ShowToast(error);

        // Restore this card (learned chip / Learn button) and re-enable the others.
        RefreshIrCommandCard(sub);
        UpdateIrCount();
        RefreshIrLearnButtons();
    }

    private void CancelLearn()
    {
        int? sub = _learningSub;
        _learningSub = null;
        _ = Task.Run(() => _vm.CsIrLearnCancel());
        if (sub is int s) PopulateIrCommandBody(s);
        RefreshIrLearnButtons();
    }

    // ── Status refresh ───────────────────────────────────────────────────────

    private void RefreshStatusIndicators()
    {
        var status = _vm.CsStatus;
        foreach (var (slot, pill) in _slotPills)
        {
            if (SlotDirty(slot))
                SetPill(pill, "Pending", Color.FromArgb(255, 240, 180, 90));
            else if (status?.IsSlotActive(slot) == true)
                SetPill(pill, "Active", Color.FromArgb(255, 100, 200, 140));
            else
                SetPill(pill, "Inactive", Color.FromArgb(255, 240, 180, 90));
        }
        foreach (var (slot, title) in _slotTitles) title.Text = SlotTitle(slot);
        foreach (var (slot, summary) in _slotSummaries) summary.Text = SlotSummary(slot);
        foreach (var (slot, apply) in _slotApply)
            apply.IsEnabled = SlotDirty(slot) && _applyingSlot == null;

        RefreshGroupMacroIndicators();
        // Nothing here reflects the flash-dirty state any more: the settings
        // window's pending-changes prompt owns that, fed by CsDirty.
    }

    private bool SlotDirty(int slot)
    {
        if (slot >= _vm.CsBindings.Count) return false;
        if (!_drafts[slot].WireEquals(_vm.CsBindings[slot])) return true;
        if (!string.Equals(_nameEdits[slot], _vm.CsNames[slot], StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Per-edit feedback (a rejected Apply, a stranded control). Distinct
    /// from the pending-changes prompt, which reports on persisting to flash.</summary>
    private void ShowToast(string msg)
    {
        MessageBar.Title = "";
        MessageBar.Message = msg;
        MessageBar.Severity = InfoBarSeverity.Informational;
        SetBar(MessageBar, !string.IsNullOrEmpty(msg));
    }

    // ── Helpers: seeding, pins, caps queries ─────────────────────────────────

    private void SeedDraftFrom(int slot)
    {
        _drafts[slot] = _vm.CsBindings[slot].Clone();
        _nameEdits[slot] = _vm.CsNames[slot];
    }

    private int FirstFreeSlot()
    {
        for (int i = 0; i < _vm.CsSlotCount; i++)
            if (!_drafts[i].IsConfigured) return i;
        return -1;
    }

    private int FirstFreeIrSub()
    {
        // A sub is taken once it's learned (IsConfigured) OR while an unlearned draft
        // is still being edited (expanded) — otherwise a second Add would reuse and
        // overwrite the first not-yet-learned button's sub.
        for (int i = 0; i < _vm.CsIrMax; i++)
            if (!_irDrafts[i].IsConfigured && !_irExpanded.Contains(i)) return i;
        return -1;
    }

    private int ConfiguredIrCount() => _irDrafts.Take(_vm.CsIrMax).Count(c => c.IsConfigured);

    private bool AnyIrReceiver()
    {
        for (int i = 0; i < _vm.CsSlotCount; i++)
            if (_drafts[i].Type == CsType.Ir) return true;
        return false;
    }

    /// <summary>Whether a display is already staged or live. Counting drafts keeps
    /// a second one from being added before the first is applied — the device
    /// would refuse it with CS_STATUS_DISPLAY_IN_USE.</summary>
    private bool AnyDisplay()
    {
        for (int i = 0; i < _vm.CsSlotCount; i++)
            if (_drafts[i].Type == CsType.Display) return true;
        return false;
    }

    /// <summary>GPIOs not claimed by any other feature (or another CS slot).</summary>
    private IEnumerable<byte> FreePins(int slot, bool adcOnly)
    {
        var owners = HardwarePins.BuildOwnerMap(_vm, excludeCsSlot: slot);
        var pool = adcOnly ? CsLimits.AdcPins : HardwarePins.ValidPins;
        foreach (var pin in pool)
            if (!owners.ContainsKey(pin)) yield return pin;
    }

    private IEnumerable<(int noun, CsNounDesc nd)> AvailableNouns(CsType type)
    {
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable) continue;
            // Aux-output nouns (caps v17) target a binding slot holding an aux
            // output, which this window cannot configure yet.
            if (nd.TargetKind == CsTarget.Aux) continue;
            // Only nouns whose action set intersects this component's actions.
            if (typeDesc != null && (typeDesc.Value.Actions & nd.Actions) == 0) continue;
            yield return (n, nd);
        }
    }

    private IEnumerable<(int noun, CsNounDesc nd)> AvailableIrNouns()
    {
        // IR commands are button-shaped; use the button type's action mask.
        var caps = _vm.CsCaps!;
        var btn = caps.DescFor(CsType.Button);
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable || nd.TargetKind == CsTarget.Aux) continue;
            if (btn != null && (btn.Value.Actions & nd.Actions) == 0) continue;
            yield return (n, nd);
        }
    }

    private List<CsAction> ValidActions(CsType type, CsNounDesc nd)
    {
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        var list = new List<CsAction>();
        foreach (CsAction a in Enum.GetValues<CsAction>())
            if ((typeDesc?.SupportsAction(a) ?? false) && nd.SupportsAction(a))
                list.Add(a);
        return list;
    }

    private List<CsAction> ValidIrActions(CsNounDesc nd)
    {
        // Button subset: INC, DEC, TOGGLE, SET, TRIGGER, MOMENTARY.
        var subset = new[] { CsAction.Inc, CsAction.Dec, CsAction.Toggle, CsAction.Set, CsAction.Trigger, CsAction.Momentary };
        return subset.Where(nd.SupportsAction).ToList();
    }

    /// <summary>Tag marking a target picker's group entries apart from its plain
    /// channel entries (whose tag is the channel index).</summary>
    private sealed record GroupTag(int Index);

    /// <summary>The group kind a noun can address, or null if it can't be grouped.
    /// Band nouns index the DSP channel space, and each member is validated
    /// against the band separately.</summary>
    private static CsTarget? GroupKindFor(CsNounDesc nd) => nd.TargetKind switch
    {
        CsTarget.InputCh => CsTarget.InputCh,
        CsTarget.OutputCh => CsTarget.OutputCh,
        CsTarget.DspCh or CsTarget.DspBand => CsTarget.DspCh,
        _ => null,
    };

    /// <summary>Configured groups a binding on this noun may target: same channel
    /// space, and at least one member inside the noun's range.</summary>
    private IEnumerable<int> CompatibleGroups(CsNounDesc nd)
    {
        var want = GroupKindFor(nd);
        if (want == null) yield break;
        uint limit = nd.TargetCount >= 32 ? uint.MaxValue : (1u << nd.TargetCount) - 1u;
        for (int g = 0; g < _vm.CsGroupMax; g++)
        {
            var grp = _vm.CsGroups[g];
            if (!grp.IsConfigured || grp.Kind != want.Value) continue;
            if ((grp.MemberMask & limit) == 0) continue;
            yield return g;
        }
    }

    /// <summary>Enum value label. The macro noun's positions are the user's macro
    /// names, which live in the VM rather than the static noun table.</summary>
    private string EnumLabel(int noun, int value) =>
        noun == (int)CsNoun.Macro ? _vm.CsMacroLabel(value) : CsNounInfo.EnumLabel(noun, value);

    private IEnumerable<(int band, string label)> BandOptions(int noun)
    {
        for (int b = 0; b < 10; b++) yield return (b, $"PEQ {b + 1}");
        if (noun == (int)CsNoun.FilterFreq || noun == (int)CsNoun.FilterBypass)
            for (int b = 20; b < 24; b++) yield return (b, $"Crossover {b - 19}");
    }

    private CsBinding MakeDefaultBinding(CsType type, int slot)
    {
        var b = new CsBinding { Type = type, Gpio1 = CsLimits.GpioUnused };
        var caps = _vm.CsCaps!;
        var typeDesc = caps.DescFor(type);
        bool adcOnly = typeDesc?.PinClass == CsPinClass.Adc;
        bool twoPins = typeDesc?.PinCount == 2;

        // The display is a container: SDA, SCL, a model in `index` and an address
        // in `value`, every other field 0. SDA must be an even GPIO and SCL the
        // odd one above it (the pin mux pairs them), so the pins are seeded as a
        // legal pair rather than the next two free numbers.
        if (type == CsType.Display)
        {
            var (sda, scl) = FreeI2cPair(slot);
            b.Gpio0 = sda;
            b.Gpio1 = scl;
            b.Index = (byte)CsDisplayModel.Ssd1306_128x64;
            b.Value = 0;   // 0 means the model's conventional address
            return b;
        }

        // Pins.
        var free = FreePins(slot, adcOnly).ToList();
        if (free.Count > 0) b.Gpio0 = free[0];
        if (twoPins) b.Gpio1 = free.Count > 1 ? free[1] : CsLimits.GpioUnused;

        if (type == CsType.Button) b.Event = (byte)CsEvent.Press;

        if (type == CsType.Ir)
        {
            // Container binding: noun/action must be 0.
            b.Noun = 0; b.Action = 0;
            return b;
        }

        // Default noun/action = first available intersecting the type.
        var noun = AvailableNouns(type).FirstOrDefault();
        if (noun.nd != null)
        {
            b.Noun = (byte)noun.noun;
            var acts = ValidActions(type, noun.nd);
            if (acts.Count > 0) b.Action = (byte)acts[0];
        }
        return b;
    }

    // ── Small view helpers ───────────────────────────────────────────────────

    private static Style? AccentStyle =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;

    private static Brush SecondaryBrush =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    private static Grid Row(string label, FrameworkElement control)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        Grid.SetColumn(lbl, 0);
        g.Children.Add(lbl);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>"Value (dB)" / "Value (ms)" …, or a bare "Value" for a unitless
    /// noun (or one whose unit this build doesn't know).</summary>
    private static string ValueLabel(CsUnit unit)
    {
        string sym = CsWire.UnitSymbol(unit);
        return sym.Length > 0 ? $"Value ({sym})" : "Value";
    }

    /// <summary>"Step (dB)" / "Step (ms)" …, bare "Step" for an unknown unit.</summary>
    private static string StepLabel(CsUnit unit)
    {
        string sym = CsWire.UnitSymbol(unit);
        return sym.Length > 0 ? $"Step ({sym})" : "Step";
    }

    /// <summary>A numeric text field that parses on Enter / focus loss and calls
    /// back with the parsed value. Unit is used only for display formatting.</summary>
    private static TextBox NumberField(double value, CsUnit unit, Action<double> onChanged)
    {
        var box = new TextBox { MinWidth = 100, Text = FormatNumber(value) };
        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
            {
                onChanged(v);
                box.Text = FormatNumber(v);
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) Commit(); };
        return box;
    }

    private static string FormatNumber(double v) =>
        v == Math.Truncate(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Update a status pill's dot, label and tinted capsule in one go.</summary>
    private static void SetPill((Border Pill, Ellipse Dot, TextBlock Label) pill, string text, Color c)
    {
        pill.Label.Text = text;
        pill.Label.Foreground = new SolidColorBrush(c);
        pill.Dot.Fill = new SolidColorBrush(c);
        pill.Pill.Background = new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
    }

    /// <summary>Header line for a remote-button card, e.g. "Volume (Output 1) · Increase".</summary>
    private string IrCommandTitle(int sub)
    {
        var d = _irDrafts[sub];
        var nd = _vm.CsNounDescFor(d.Noun);
        if (nd == null) return "Remote button";
        string s = CsNounInfo.Name(d.Noun);
        if (nd.IsTargeted)
            s += d.IsGrouped
                ? $" ({_vm.CsGroupLabel(d.Target)})"
                : $" ({ChannelLabel(nd.TargetKind, d.Target)})";
        else if (d.Noun == (byte)CsNoun.Macro) s += $" ({_vm.CsMacroLabel(d.Value)})";
        return $"{s} · {ActionName((CsAction)d.Action, d.Noun)}";
    }

    private void UpdateIrTitle(int sub)
    {
        if (_irTitles.TryGetValue(sub, out var t)) t.Text = IrCommandTitle(sub);
    }

    /// <summary>Card title: the user's custom name, falling back to the type name.</summary>
    private string SlotTitle(int slot) =>
        string.IsNullOrWhiteSpace(_nameEdits[slot]) ? TypeName(_drafts[slot].Type) : _nameEdits[slot].Trim();

    /// <summary>One-line binding summary shown under the card title, e.g.
    /// "Button · Volume · Output 1 · GPIO 5".</summary>
    private string SlotSummary(int slot)
    {
        var d = _drafts[slot];
        if (!d.IsConfigured) return "";
        var parts = new List<string>();
        // Lead with the type when a custom name has displaced it from the title.
        if (!string.IsNullOrWhiteSpace(_nameEdits[slot])) parts.Add(TypeName(d.Type));

        if (d.Type == CsType.Ir)
        {
            int n = ConfiguredIrCount();
            parts.Add(n == 1 ? "1 remote button" : $"{n} remote buttons");
        }
        else if (d.Type == CsType.Display)
        {
            parts.Add(CsDisplayModels.Name(d.Index));
            parts.Add($"0x{DisplayAddress(d):X2}");
        }
        else
        {
            var nd = _vm.CsNounDescFor(d.Noun);
            if (nd != null)
            {
                string noun = CsNounInfo.Name(d.Noun);
                if (nd.IsTargeted) noun += $" ({BindingTargetLabel(d, nd)})";
                else if (d.Noun == (byte)CsNoun.Macro) noun += $" ({_vm.CsMacroLabel(d.Value)})";
                parts.Add(noun);
            }
        }
        if (d.Gpio0 != CsLimits.GpioUnused)
            parts.Add(d.Gpio1 != CsLimits.GpioUnused ? $"GPIO {d.Gpio0} + {d.Gpio1}" : $"GPIO {d.Gpio0}");
        return string.Join("  ·  ", parts);
    }

    private static string TypeGlyph(CsType t) => t switch
    {
        CsType.Button => "",   // power-button circle
        CsType.Switch => "",   // switch arrows
        CsType.Pot => "",      // dial / meter
        CsType.Encoder => "",  // rotate
        CsType.Led => "",      // lightbulb
        CsType.LedPwm => "",   // brightness
        CsType.Ir => "",       // remote
        CsType.Display => "",  // monitor
        _ => "",
    };

    /// <summary>Per-type badge tint, drawn from the app's channel-colour palette.</summary>
    private static Color TypeColor(CsType t) => t switch
    {
        CsType.Button => Color.FromArgb(255, 0x4A, 0x8F, 0xE3),  // blue
        CsType.Switch => Color.FromArgb(255, 0x45, 0xC2, 0xA3),  // teal
        CsType.Pot => Color.FromArgb(255, 0xF0, 0xC4, 0x59),     // amber
        CsType.Encoder => Color.FromArgb(255, 0xBA, 0x87, 0xF3), // purple
        CsType.Led => Color.FromArgb(255, 0x85, 0xC6, 0x62),     // green
        CsType.LedPwm => Color.FromArgb(255, 0x52, 0xB9, 0xD8),  // cyan
        CsType.Ir => Color.FromArgb(255, 0xF5, 0x73, 0x73),      // coral
        CsType.Display => Color.FromArgb(255, 0x04, 0x85, 0x6F),  // teal
        _ => Color.FromArgb(255, 0x90, 0x90, 0x90),
    };

    private static string TypeName(CsType t) => t switch
    {
        CsType.Button => "Button",
        CsType.Switch => "Switch",
        CsType.Pot => "Potentiometer",
        CsType.Encoder => "Rotary Encoder",
        CsType.Led => "LED",
        CsType.LedPwm => "LED (dimmable)",
        CsType.Ir => "IR Remote",
        CsType.Display => "Display",
        _ => "None",
    };

    /// <summary>Browse/Adjust is not stepping a list: unarmed it moves a page,
    /// armed it moves the shown value (and on an on/off page the firmware reads up
    /// as on, down as off). A direction is the only honest label for that.</summary>
    private static string ActionName(CsAction a, byte noun) =>
        noun == (byte)CsNoun.PageValue && a is CsAction.Inc or CsAction.Dec
            ? (a == CsAction.Inc ? "Up" : "Down")
            : ActionName(a);

    private static string ActionName(CsAction a) => a switch
    {
        CsAction.Adjust => "Adjust (absolute)",
        CsAction.Step => "Step per detent",
        CsAction.Inc => "Increase",
        CsAction.Dec => "Decrease",
        CsAction.Toggle => "Toggle",
        CsAction.Set => "Set value",
        CsAction.Follow => "Follow switch",
        CsAction.Trigger => "Trigger",
        CsAction.IndEquals => "Indicate (equals)",
        CsAction.Momentary => "Momentary (while held)",
        CsAction.IndAbove => "Indicate (above)",
        CsAction.IndLevel => "Indicate level",
        _ => a.ToString(),
    };

    /// <summary>Channel target label: the user-editable sidebar name, kept in sync
    /// by <see cref="RefreshChannelLabels"/> while this window is open.</summary>
    private string ChannelLabel(CsTarget kind, int i) => _vm.CsTargetLabel(kind, i);

    /// <summary>What a binding's <c>target</c> names — a channel, or the group it
    /// stands for when the GROUP flag is set.</summary>
    private string BindingTargetLabel(CsBinding b, CsNounDesc nd) =>
        b.IsGrouped ? _vm.CsGroupLabel(b.Target) : ChannelLabel(nd.TargetKind, b.Target);

    /// <summary>Rewrite a channel picker's item captions in place. Items and the
    /// current selection are untouched, so no SelectionChanged handler fires.</summary>
    private void Relabel(ComboBox combo, CsTarget kind)
    {
        foreach (var o in combo.Items)
            if (o is ComboBoxItem item && item.Tag is int i)
                item.Content = ChannelLabel(kind, i);
    }

    /// <summary>Re-read every channel name shown in this window after a sidebar
    /// rename: the target pickers, the IR command titles, and the card summaries
    /// (refreshed by <see cref="RefreshStatusIndicators"/>).</summary>
    private void RefreshChannelLabels()
    {
        foreach (var relabel in _channelRelabel.Values) relabel();
        foreach (var relabel in _irChannelRelabel.Values) relabel();
        foreach (var relabel in _groupChannelRelabel.Values) relabel();
        foreach (int sub in _irTitles.Keys) UpdateIrTitle(sub);
        RefreshStatusIndicators();
    }
}
