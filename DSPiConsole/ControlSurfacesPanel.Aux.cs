using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Auxiliary outputs (caps v18) and the pieces the binding pages share with
/// them: which section shows which slot, the type menu on a card's badge, and
/// the target pickers that can address an aux output. An aux output is a
/// container in a binding slot that owns one GPIO, switched (and, dimmable,
/// levelled) by controls pointed at it through the Aux Switch / Aux Level
/// nouns. Its live state is runtime only; the card's other rows are the slot's
/// stored config and share the binding's Apply. After the macOS Console.
/// </summary>
public sealed partial class ControlSurfacesPanel
{
    private sealed record AuxLiveRow(ToggleSwitch Toggle, Slider? Level, TextBlock? LevelText, TextBlock Detail);

    private readonly Dictionary<int, AuxLiveRow> _auxLive = new();
    private readonly Dictionary<int, (StackPanel Host, string Shown)> _auxDrivenBy = new();
    /// <summary>Set while live values are written into the controls, so their
    /// change handlers do not send them back.</summary>
    private bool _auxSync;
    /// <summary>When each slot's level was last sent from here. The device
    /// echoes every change back, and an echo of an earlier value arriving mid
    /// drag would pull the thumb back under the pointer.</summary>
    private readonly Dictionary<int, DateTime> _auxLevelSent = new();

    private static bool IsAuxType(CsType t) => t is CsType.AuxOut or CsType.AuxPwm;

    /// <summary>A slot holding an aux output, staged or live; it is shown on
    /// the Auxiliary Outputs page and nowhere else.</summary>
    private bool IsAuxSlot(int slot) =>
        _drafts[slot].IsAux || (slot < _vm.CsBindings.Count && _vm.CsBindings[slot].IsAux);

    private bool ShowsSlot(int slot) =>
        _drafts[slot].IsConfigured && IsAuxSlot(slot) == (_section == CsSection.Aux);

    /// <summary>Follow the device for every slot this page does not show: the
    /// other binding page owns those, and its edits land here only this way.</summary>
    private void ReseedForeignSlots()
    {
        for (int slot = 0; slot < _vm.CsSlotCount; slot++)
        {
            if (!_slotCards.ContainsKey(slot))
            {
                if (!ShowsSlot(slot)) SeedDraftFrom(slot);
            }
            else if (_vm.CsBindings[slot] is { IsConfigured: true } live && live.IsAux != (_section == CsSection.Aux))
            {
                // The other page's control now holds this slot; an unapplied
                // card here would overwrite it.
                RemoveSlotCard(slot);
                SeedDraftFrom(slot);
            }
        }
        BuildAddMenu();
    }

    private void ApplySectionText()
    {
        if (_section != CsSection.Aux) return;
        EmptyGlyph.Glyph = "";
        EmptyTitle.Text = "No auxiliary outputs set up";
        EmptyText.Text = "Put a relay, lamp or fan on a spare GPIO, then point a button, knob or remote key at it "
                       + "from the Control Surfaces page.";
        AddButtonText.Text = "Add Output";
        SectionFooter.Text =
            "An auxiliary output is a GPIO the DSPi switches or dims for you and never reads itself: it changes "
            + "nothing about the sound. It exists so a button, knob, remote key or macro can drive something the "
            + "device knows nothing about: an amplifier trigger, a speaker relay, a panel lamp, a fan.\n\n"
            + "An on/off output follows its switch. A dimmable output follows its switch and its level, so one "
            + "button and one knob can share a lamp. Turn on \"Active-low output\" for the relay and opto-isolator "
            + "boards that switch when the pin goes low.\n\n"
            + "A GPIO is a 3.3 V pin good for a few milliamps. Anything real needs a MOSFET, a transistor with a "
            + "flyback diode, or an opto-isolated relay module in between, and a dimmed load should have its own "
            + "supply so its switching noise stays out of the DAC.\n\n"
            + "Switching an output is instant and never writes to flash. The pin, name and power-on behaviour are "
            + "stored on the device alongside the controls and share their Save and Revert.";
        SectionFooter.Visibility = Visibility.Visible;
    }

    // ── Type menu on the badge ──

    private void PaintBadge(int slot)
    {
        if (!_slotBadges.TryGetValue(slot, out var badge)) return;
        var type = _drafts[slot].Type;
        var accent = TypeColor(type);
        badge.Icon.Glyph = TypeGlyph(type);
        badge.Icon.Foreground = new SolidColorBrush(accent);
        badge.Badge.Background = new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B));
    }

    /// <summary>The types this slot may become: an aux slot changes between the
    /// two aux kinds, any other slot between every other kind.</summary>
    private void FillTypeMenu(MenuFlyout menu, int slot)
    {
        menu.Items.Clear();
        var caps = _vm.CsCaps;
        if (caps == null) return;
        bool aux = _section == CsSection.Aux;
        var current = _drafts[slot].Type;
        bool OtherHolds(CsType t)
        {
            for (int s = 0; s < _vm.CsSlotCount; s++)
                if (s != slot && (_drafts[s].Type == t || _vm.CsBindings[s].Type == t)) return true;
            return false;
        }
        foreach (CsType t in Enum.GetValues<CsType>())
        {
            if (t == CsType.None || (int)t >= caps.TypeCount || IsAuxType(t) != aux) continue;
            var item = new ToggleMenuFlyoutItem
            {
                Text = TypeName(t),
                Icon = new FontIcon { Glyph = TypeGlyph(t) },
                IsChecked = t == current,
                // One receiver and one panel per device.
                IsEnabled = t switch
                {
                    CsType.Ir => _vm.CsIrSupported && !OtherHolds(CsType.Ir),
                    CsType.Display => _vm.CsDisplaySupported && !OtherHolds(CsType.Display),
                    _ => true,
                },
            };
            var type = t;
            item.Click += (_, _) => ChangeType(slot, type);
            menu.Items.Add(item);
        }
    }

    /// <summary>Start the slot over as another type: a full reset to that
    /// type's defaults (the name stays). Staged, like any other edit.</summary>
    private void ChangeType(int slot, CsType type)
    {
        var old = _drafts[slot].Type;
        if (type == old) return;
        // The receiver's and the panel's own sections go with the body.
        if (old == CsType.Display) { ClearDisplayHandles(); StopDisplayPoll(); }
        if (old == CsType.Ir && slot == _irSectionSlot)
        {
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
        RemoveAuxHandles(slot);
        _drafts[slot] = MakeDefaultBinding(type, slot);
        PaintBadge(slot);
        PopulateSlotBody(slot);
        BuildAddMenu();
        // The display card has no Apply: its wiring applies as it is edited.
        if (type == CsType.Display) ApplyDisplayWiring(slot);
    }

    /// <summary>A save rewrote these slots' stored boot fields (an "as last
    /// saved" aux output). Take the new boot state into the drafts, keeping any
    /// other edit still pending, and refill the cards that show them.</summary>
    private void OnBindingsReread(IReadOnlyList<int> slots)
    {
        foreach (int slot in slots)
        {
            var live = _vm.CsBindings[slot];
            var d = _drafts[slot];
            if (!live.IsAux || !d.IsAux) { SeedDraftFrom(slot); }
            else
            {
                d.Extras = (byte)((d.Extras & ~(byte)CsAuxExtras.BootOn) | (live.Extras & (byte)CsAuxExtras.BootOn));
                d.Value = live.Value;
            }
            if (_slotCards.ContainsKey(slot)) PopulateSlotBody(slot);
        }
        RefreshStatusIndicators();
    }

    // ── Targets ──

    /// <summary>What a noun's target picker offers: every channel, or for an
    /// aux noun the slots holding a live aux output it can drive (a level
    /// needs a dimmable one).</summary>
    private List<int> TargetChoices(int noun, CsNounDesc nd)
    {
        if (nd.TargetKind != CsTarget.Aux) return Enumerable.Range(0, nd.TargetCount).ToList();
        var choices = new List<int>();
        for (int s = 0; s < Math.Min((int)nd.TargetCount, _vm.CsSlotCount); s++)
        {
            var t = _vm.CsBindings[s].Type;
            if (t == CsType.AuxPwm || (t == CsType.AuxOut && noun != (int)CsNoun.AuxLevel)) choices.Add(s);
        }
        return choices;
    }

    /// <summary>Fill a target picker: the noun's channels (or aux outputs), then
    /// any compatible group, selecting what the record addresses.</summary>
    private void FillTargetCombo(ComboBox combo, int noun, CsNounDesc nd, IList<int> groups, bool grouped, int target)
    {
        var choices = TargetChoices(noun, nd);
        foreach (int i in choices)
            combo.Items.Add(new ComboBoxItem { Content = ChannelLabel(nd.TargetKind, i), Tag = i });
        foreach (int g in groups)
            combo.Items.Add(new ComboBoxItem { Content = $"Group: {_vm.CsGroupLabel(g)}", Tag = new GroupTag(g) });
        int sel = grouped
            ? (groups.IndexOf(target) is var gi and >= 0 ? choices.Count + gi : -1)
            : choices.IndexOf(target);
        // A channel out of range falls back to the first, as before; an aux
        // target that no longer holds an output shows as unset instead.
        if (sel < 0 && !grouped && nd.TargetKind != CsTarget.Aux && choices.Count > 0) sel = 0;
        combo.SelectedIndex = sel;
    }

    private static string TargetRowLabel(CsNounDesc nd, bool groups) =>
        nd.TargetKind == CsTarget.Aux ? "Aux output" : groups ? "Target" : "Channel";

    private TextBlock AuxTargetHint(int noun) => new()
    {
        Text = noun == (int)CsNoun.AuxLevel
            ? "No dimmable output to point at yet. Add one on the Auxiliary Outputs page, then choose it here."
            : "No auxiliary output to point at yet. Add one on the Auxiliary Outputs page, then choose it here.",
        FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush,
    };

    // ── The aux card ──

    private void PopulateAuxBody(int slot, StackPanel panel)
    {
        var d = _drafts[slot];
        bool pwm = d.Type == CsType.AuxPwm;
        RemoveAuxHandles(slot);

        // Live: the switch and level act now, whatever is staged below.
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        toggle.Toggled += (_, _) => { if (!_auxSync && !_building) _vm.SetCsAuxOn(slot, toggle.IsOn); };
        var detail = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush };
        panel.Children.Add(Row("Output", toggle));
        Slider? level = null;
        TextBlock? levelText = null;
        if (pwm)
        {
            level = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            levelText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
            var text = levelText;
            level.ValueChanged += (_, e) =>
            {
                text.Text = $"{Math.Round(e.NewValue)}%";
                if (_auxSync || _building) return;
                _auxLevelSent[slot] = DateTime.UtcNow;
                _vm.SetCsAuxLevel(slot, (ushort)Math.Round(e.NewValue * 256));
            };
            ToolTipService.SetToolTip(level, "How bright or fast the load runs while the output is on.");
            panel.Children.Add(Row("Level", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { level, levelText } }));
        }
        panel.Children.Add(detail);
        _auxLive[slot] = new AuxLiveRow(toggle, level, levelText, detail);
        RefreshAuxLive(slot);
        panel.Children.Add(Divider());

        // Wiring.
        panel.Children.Add(BuildPinRows(slot));
        var invert = FlagToggle(slot, CsFlags.Invert, "Active-low output");
        ToolTipService.SetToolTip(invert, pwm
            ? "Invert the PWM duty for a driver that switches on when the pin goes low."
            : "Drive the pin low to switch the load on, which is what most relay and opto-isolator boards expect.");
        panel.Children.Add(invert);
        if (pwm)
        {
            TextBox box = null!;
            box = NumberField(d.BaseBright == 0 ? 100 : d.BaseBright, CsUnit.None, v =>
            {
                byte pct = (byte)Math.Clamp(Math.Round(v), 1, 100);
                _drafts[slot].BaseBright = pct;
                if (Math.Abs(pct - v) > 0.005) box.Text = FormatNumber(pct);
                RefreshStatusIndicators();
            });
            ToolTipService.SetToolTip(box, "Cap on the output's duty as a share of full. Everything below the cap scales with it.");
            panel.Children.Add(Row("Level limit (%)", box));
            var linear = ExtrasToggle(slot, CsAuxExtras.Linear, "Linear response");
            ToolTipService.SetToolTip(linear, "Off: the level follows the eye's curve, right for a lamp. "
                + "On: duty is proportional to the level, right for a fan or heater.");
            panel.Children.Add(linear);
        }
        panel.Children.Add(BuildDelayRows(slot));
        panel.Children.Add(Divider());

        // Power-on: a fixed state, or whatever was live at the last save.
        var fixedRows = new StackPanel { Spacing = 8 };
        var startsOn = ExtrasToggle(slot, CsAuxExtras.BootOn, "Starts on");
        ToolTipService.SetToolTip(startsOn, "Leave this off for anything that should never wake with the device, such as an amplifier trigger.");
        fixedRows.Children.Add(startsOn);
        if (pwm)
        {
            var bootLevel = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            var bootText = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
            bootLevel.Value = Math.Round(d.Value / 256.0);
            bootText.Text = $"{bootLevel.Value}%";
            bootLevel.ValueChanged += (_, e) =>
            {
                bootText.Text = $"{Math.Round(e.NewValue)}%";
                if (_building) return;
                _drafts[slot].Value = (short)Math.Clamp(Math.Round(e.NewValue * 256), 0, MainViewModel.CsAuxLevelMaxQ8);
                RefreshStatusIndicators();
            };
            ToolTipService.SetToolTip(bootLevel, "The level this output comes up at.");
            fixedRows.Children.Add(Row("Starting level", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { bootLevel, bootText } }));
        }
        var savedNote = new TextBlock
        {
            Text = "Saving takes a copy of the switch and level as they are at that moment, and the output comes back "
                 + "that way after a restart. Changing them afterwards does not move the stored values until the next save.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush,
        };
        bool saved = ((CsAuxExtras)d.Extras).HasFlag(CsAuxExtras.BootSaved);
        fixedRows.Visibility = Vis(!saved);
        savedNote.Visibility = Vis(saved);
        var bootMode = new ComboBox { MinWidth = 160 };
        bootMode.Items.Add("Fixed");
        bootMode.Items.Add("As last saved");
        bootMode.SelectedIndex = saved ? 1 : 0;
        bootMode.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            bool last = bootMode.SelectedIndex == 1;
            // Only the mode bit moves: the fixed state underneath is kept for
            // when it is chosen again.
            if (last) _drafts[slot].Extras |= (byte)CsAuxExtras.BootSaved;
            else _drafts[slot].Extras &= unchecked((byte)~CsAuxExtras.BootSaved);
            fixedRows.Visibility = Vis(!last);
            savedNote.Visibility = Vis(last);
            RefreshStatusIndicators();
        };
        ToolTipService.SetToolTip(bootMode, "What this output does when the device starts up.");
        panel.Children.Add(Row("At power-on", bootMode));
        panel.Children.Add(fixedRows);
        panel.Children.Add(savedNote);
        panel.Children.Add(Divider());

        // What drives it, from the other pages.
        var drivenHost = new StackPanel { Spacing = 2 };
        var drivenRow = Row("Driven by", drivenHost);
        ToolTipService.SetToolTip(drivenRow, "Controls on the Control Surfaces page, remote keys and macros pointed at this output.");
        panel.Children.Add(drivenRow);
        _auxDrivenBy[slot] = (drivenHost, "");
        RefreshAuxDrivenBy(slot);

        panel.Children.Add(BuildApplyRow(slot));
    }

    private CheckBox ExtrasToggle(int slot, CsAuxExtras flag, string label)
    {
        var cb = new CheckBox { Content = label, IsChecked = ((CsAuxExtras)_drafts[slot].Extras).HasFlag(flag) };
        cb.Checked += (_, _) => { _drafts[slot].Extras |= (byte)flag; RefreshStatusIndicators(); };
        cb.Unchecked += (_, _) => { _drafts[slot].Extras &= unchecked((byte)~flag); RefreshStatusIndicators(); };
        return cb;
    }

    private void RemoveAuxHandles(int slot)
    {
        _auxLive.Remove(slot);
        _auxDrivenBy.Remove(slot);
    }

    // ── Live state ──

    private void OnAuxChanged(int slot) => RefreshAuxLive(slot);

    private void RefreshAuxLive()
    {
        foreach (int slot in _auxLive.Keys) RefreshAuxLive(slot);
    }

    /// <summary>Write the device's live switch and level into a card, and
    /// enable them only while the output is running: the commands act on the
    /// live component, so a staged one has nothing to switch.</summary>
    private void RefreshAuxLive(int slot)
    {
        if (!_auxLive.TryGetValue(slot, out var row)) return;
        bool live = _vm.IsDeviceConnected && _vm.CsBindings[slot].IsAux && _vm.CsStatus?.IsSlotActive(slot) == true;
        // A level needs the device's output to be dimmable, whatever is staged.
        bool liveDimmable = live && _vm.CsBindings[slot].Type == CsType.AuxPwm;
        _auxSync = true;
        try
        {
            row.Toggle.IsOn = _vm.CsAuxOn[slot];
            row.Toggle.IsEnabled = live;
            if (row.Level != null)
            {
                row.Level.IsEnabled = liveDimmable;
                bool echo = _auxLevelSent.TryGetValue(slot, out var sent)
                            && DateTime.UtcNow - sent < TimeSpan.FromMilliseconds(500);
                if (!echo)
                {
                    row.Level.Value = Math.Round(_vm.CsAuxLevelQ8[slot] / 256.0);
                    row.LevelText!.Text = $"{row.Level.Value}%";
                }
            }
        }
        finally { _auxSync = false; }
        row.Detail.Text = live
            ? "Switches the pin now. Instant, and never written to flash."
            : "Apply the output first; the switch works once it is running.";
    }

    // ── Driven by ──

    private void RefreshAuxDrivenBy()
    {
        foreach (int slot in _auxDrivenBy.Keys.ToList()) RefreshAuxDrivenBy(slot);
    }

    /// <summary>The controls, remote keys and macros pointed at this output.
    /// Refilled only when what it says has changed.</summary>
    private void RefreshAuxDrivenBy(int slot)
    {
        if (!_auxDrivenBy.TryGetValue(slot, out var entry)) return;
        static bool AuxNoun(byte noun) => noun is (byte)CsNoun.Aux or (byte)CsNoun.AuxLevel;

        var lines = new List<string>();
        for (int s = 0; s < _vm.CsSlotCount; s++)
        {
            var b = _drafts[s];
            if (s == slot || !b.IsConfigured || b.IsAux || b.IsGrouped || b.Target != slot || !AuxNoun(b.Noun)) continue;
            string name = !string.IsNullOrWhiteSpace(_vm.CsNames[s]) ? _vm.CsNames[s].Trim()
                : TypeName(_vm.CsBindings[s].IsConfigured ? _vm.CsBindings[s].Type : b.Type);
            lines.Add($"{name} - {ActionName((CsAction)b.Action, b.Noun)} on {CsNounInfo.Name(b.Noun, b.Type)}");
        }
        var also = new List<string>();
        if (_vm.CsIrSupported)
        {
            int keys = _vm.CsIrCommands.Take(_vm.CsIrMax)
                .Count(c => c.IsConfigured && !c.IsGrouped && c.Target == slot && AuxNoun(c.Noun));
            if (keys > 0) also.Add(keys == 1 ? "1 remote key" : $"{keys} remote keys");
        }
        var macros = new List<string>();
        for (int m = 0; m < _vm.CsMacroMax; m++)
        {
            var mac = _vm.CsMacros[m];
            if (mac.Steps.Take(mac.StepCount).Any(st => st.IsConfigured && !st.IsGrouped && st.Target == slot && AuxNoun(st.Noun)))
                macros.Add(_vm.CsMacroLabel(m));
        }
        if (macros.Count > 0) also.Add((macros.Count == 1 ? "the macro " : "the macros ") + string.Join(", ", macros));

        if (lines.Count == 0 && also.Count == 0)
            lines.Add(_drafts[slot].Type == CsType.AuxPwm
                ? "Nothing yet. Add a button on \"Aux Switch\" or an encoder or fader on \"Aux Level\" and point it at this output."
                : "Nothing yet. Add a button or switch on \"Aux Switch\" and point it at this output.");
        else if (also.Count > 0)
            lines.Add((lines.Count > 0 ? "Also driven by " : "Driven by ") + string.Join(" and ", also) + ".");

        string shown = string.Join("\n", lines);
        if (shown == entry.Shown) return;
        _auxDrivenBy[slot] = (entry.Host, shown);
        entry.Host.Children.Clear();
        foreach (var line in lines)
            entry.Host.Children.Add(new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
    }
}
