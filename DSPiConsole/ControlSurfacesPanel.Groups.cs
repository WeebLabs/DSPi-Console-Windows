using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Settings;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// Target groups and macros (firmware caps v9, commands 0x20–0x26). A group is a
/// named set of channels one binding drives as a unit; a macro is a sequence of up
/// to eight delayed steps a button, remote key or this page can fire.
///
/// <para>Both follow the same three-tier persistence as bindings: an Apply is a
/// live-only preview on the device, and the settings window's pending-changes
/// prompt writes it to flash or reverts it — the same prompt the System pages use.
/// Steps are written before the header carrying the final step count, so a macro
/// fired mid-edit never sees a count past its written steps (groups/macros spec,
/// s3).</para>
///
/// <para>The sections are hidden outright on a pre-v9 firmware, which advertises
/// no group or macro slots and STALLs the commands.</para>
/// </summary>
public sealed partial class ControlSurfacesPanel
{
    private readonly CsGroup[] _groupDrafts = new CsGroup[CsLimits.MaxGroups];
    private readonly CsMacro[] _macroDrafts = new CsMacro[CsLimits.MaxMacros];

    private readonly HashSet<int> _groupExpanded = new();
    private readonly HashSet<int> _macroExpanded = new();
    // A slot is "shown" once it holds something or the user just added it, so a
    // freshly added (still empty) card doesn't vanish before its first Apply.
    private readonly HashSet<int> _groupAdded = new();
    private readonly HashSet<int> _macroAdded = new();

    private int? _applyingGroup;
    private int? _applyingMacro;

    // Live handles refreshed without a section rebuild: the pieces that change on
    // every edit (pill, title, summary, Apply) and the macro Run button.
    private readonly Dictionary<int, (Border Pill, Ellipse Dot, TextBlock Label)> _groupPills = new();
    private readonly Dictionary<int, (Border Pill, Ellipse Dot, TextBlock Label)> _macroPills = new();
    private readonly Dictionary<int, (TextBlock Title, TextBlock Summary)> _groupLabels = new();
    private readonly Dictionary<int, (TextBlock Title, TextBlock Summary)> _macroLabels = new();
    private readonly Dictionary<int, Button> _groupApply = new();
    private readonly Dictionary<int, Button> _macroApply = new();
    private readonly Dictionary<int, Button> _macroFireButtons = new();
    // Per-group member-checkbox relabellers, re-run when a channel is renamed in
    // the sidebar (same contract as the binding cards' channel pickers).
    private readonly Dictionary<int, Action> _groupChannelRelabel = new();
    // Host for each group card's member list, refilled in place on a kind change.
    private readonly Dictionary<int, ContentControl> _groupMemberHosts = new();
    private readonly Dictionary<int, TextBlock> _groupUsedBy = new();
    private readonly Dictionary<int, TextBox> _groupNameBoxes = new();
    private readonly Dictionary<int, ComboBox> _groupKindCombos = new();

    // Macro card internals. Every edit inside a card updates these in place: only
    // adding or removing a whole group/macro touches the section panels.
    private readonly Dictionary<int, FrameworkElement> _groupCards = new();
    private readonly Dictionary<int, FrameworkElement> _macroCards = new();
    private readonly Dictionary<int, TextBox> _macroNameBoxes = new();
    private readonly Dictionary<int, StackPanel> _macroStepHosts = new();
    private readonly Dictionary<int, TextBlock> _macroEmptyHints = new();
    private readonly Dictionary<int, Button> _macroAddStep = new();
    private readonly Dictionary<(int Macro, int Step), MacroStepCard> _macroStepCards = new();

    /// <summary>The mutable parts of one macro-step card: its editor rows (refilled
    /// on a noun/action change) and the header chrome whose enabled state depends on
    /// the step's position in the sequence.</summary>
    private sealed class MacroStepCard
    {
        public FrameworkElement Card = null!;
        public StackPanel Rows = null!;
        public Button Up = null!;
        public Button Down = null!;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _macroPoll;
    private int _macroPollTicks;
    private const int MacroPollFastMs = 400;
    private const int MacroPollSlowMs = 2000;
    private const int MacroPollFastTicks = 25;   // ~10 s of close watching

    // Segoe Fluent Icons: reorder chevrons on a macro step.
    private const string GlyphChevronUp = "";
    private const string GlyphChevronDown = "";

    // ── Seeding and top-level build ──────────────────────────────────────────

    private void SeedGroupMacroDrafts()
    {
        for (int i = 0; i < CsLimits.MaxGroups; i++)
            _groupDrafts[i] = i < _vm.CsGroups.Count ? _vm.CsGroups[i].Clone() : CsGroup.Cleared();
        for (int i = 0; i < CsLimits.MaxMacros; i++)
            _macroDrafts[i] = i < _vm.CsMacros.Count ? _vm.CsMacros[i].Clone() : new CsMacro();
        // A reload is authoritative: anything the device doesn't hold was either
        // applied (and now seeds from the device) or discarded by a revert.
        _groupAdded.RemoveWhere(i => !_groupDrafts[i].IsConfigured);
        _macroAdded.RemoveWhere(i => !_macroDrafts[i].IsConfigured);
    }

    private void BuildGroupsAndMacros()
    {
        bool supported = _vm.CsGroupsSupported;
        GroupsSection.Visibility = Vis(supported && _section == CsSection.Groups);
        MacrosSection.Visibility = Vis(supported && _section == CsSection.Macros);
        if (!supported) return;

        // The bindings panel builds neither, but still needs the drafts seeded —
        // its group and macro pickers read them, and a group edit elsewhere has to
        // be able to report which controls it stranded.
        if (_section == CsSection.Groups) RebuildGroupCards();
        if (_section == CsSection.Macros) RebuildMacroCards();
    }

    private bool GroupShown(int idx) => _groupDrafts[idx].IsConfigured || _groupAdded.Contains(idx);
    private bool MacroShown(int idx) => _macroDrafts[idx].IsConfigured || _macroAdded.Contains(idx);

    /// <summary>
    /// A free group slot, preferring one nothing on the device still names.
    /// Deleting a group only switches off the controls that used it; they keep
    /// its slot as their target, and the firmware brings them back up when a
    /// group is next applied to that slot, so a new group reusing it would
    /// silently take them over. Falls back to a referenced slot only when no
    /// other is free (<paramref name="referenced"/> then says so).
    /// </summary>
    private int FirstFreeGroup(out bool referenced)
    {
        int fallback = -1;
        for (int i = 0; i < _vm.CsGroupMax; i++)
        {
            if (GroupShown(i)) continue;
            if (!GroupReferenced(i)) { referenced = false; return i; }
            if (fallback < 0) fallback = i;
        }
        referenced = fallback >= 0;
        return fallback;
    }

    private int FirstFreeGroup() => FirstFreeGroup(out _);

    /// <summary>True when a stored binding, remote button, macro step or
    /// display page still targets group slot <paramref name="idx"/>.</summary>
    private bool GroupReferenced(int idx)
    {
        foreach (var b in _vm.CsBindings)
            if (b.IsConfigured && b.IsGrouped && b.Target == idx) return true;
        foreach (var c in _vm.CsIrCommands)
            if (c.IsConfigured && c.IsGrouped && c.Target == idx) return true;
        foreach (var m in _vm.CsMacros)
            for (int st = 0; st < Math.Min(m.StepCount, m.Steps.Length); st++)
                if (m.Steps[st].IsGrouped && m.Steps[st].Target == idx) return true;
        foreach (var page in _vm.CsDisplayPages)
            if (page.IsActive && page.IsGrouped && page.Target == idx) return true;
        return false;
    }

    private int FirstFreeMacro()
    {
        for (int i = 0; i < _vm.CsMacroMax; i++)
            if (!MacroShown(i)) return i;
        return -1;
    }

    // ── Groups ───────────────────────────────────────────────────────────────

    private void RebuildGroupCards()
    {
        _building = true;
        try
        {
            GroupsPanel.Children.Clear();
            _groupCards.Clear();
            _groupPills.Clear();
            _groupLabels.Clear();
            _groupApply.Clear();
            _groupChannelRelabel.Clear();
            _groupMemberHosts.Clear();
            _groupUsedBy.Clear();
            _groupNameBoxes.Clear();
            _groupKindCombos.Clear();
            for (int i = 0; i < _vm.CsGroupMax; i++)
                if (GroupShown(i)) GroupsPanel.Children.Add(BuildGroupCard(i));
        }
        finally { _building = false; }
        UpdateGroupsChrome();
    }

    /// <summary>Section heading count and Add button state — everything outside the
    /// cards themselves.</summary>
    private void UpdateGroupsChrome()
    {
        int shown = _groupCards.Count;
        // The page title already names the section; this line only carries the count.
        GroupsHeading.Text = shown > 0 ? $"{shown} of {_vm.CsGroupMax} groups in use" : "";
        GroupsEmptyHint.Visibility = Vis(shown == 0);
        AddGroupButton.IsEnabled = FirstFreeGroup() >= 0;
        // A card built here starts with a default-enabled Apply; the refresh is
        // what puts it (and its tooltip) into the right state.
        RefreshGroupMacroIndicators();
    }

    /// <summary>Build one group's card and insert it at its slot-ordered position,
    /// leaving the other cards untouched.</summary>
    private void InsertGroupCard(int idx)
    {
        int pos = 0;
        for (int i = 0; i < idx; i++) if (_groupCards.ContainsKey(i)) pos++;
        _building = true;
        try { GroupsPanel.Children.Insert(pos, BuildGroupCard(idx)); }
        finally { _building = false; }
        UpdateGroupsChrome();
    }

    /// <summary>Remove one group's card element and drop its handles.</summary>
    private void RemoveGroupCard(int idx)
    {
        if (_groupCards.TryGetValue(idx, out var card)) GroupsPanel.Children.Remove(card);
        _groupCards.Remove(idx);
        _groupPills.Remove(idx);
        _groupLabels.Remove(idx);
        _groupApply.Remove(idx);
        _groupChannelRelabel.Remove(idx);
        _groupMemberHosts.Remove(idx);
        _groupUsedBy.Remove(idx);
        _groupNameBoxes.Remove(idx);
        _groupKindCombos.Remove(idx);
        UpdateGroupsChrome();
    }

    private FrameworkElement BuildGroupCard(int idx)
    {
        var draft = _groupDrafts[idx];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _groupExpanded.Contains(idx),
        };
        expander.Expanding += (_, _) => { _groupExpanded.Add(idx); Reveal.Bring(expander); };
        expander.Collapsed += (_, _) => _groupExpanded.Remove(idx);

        expander.Header = BuildRecordHeader(
            glyph: "",                                  // linked / chain
            accent: Color.FromArgb(255, 0x7F, 0xB2, 0xE8),    // soft blue
            title: GroupTitle(idx),
            summary: GroupSummary(idx),
            statusText: GroupStatusText(idx),
            statusColor: GroupStatusColor(idx),
            onDelete: () => RemoveGroup(idx),
            extra: null,
            pill: out var pill,
            labels: out var labels);
        _groupPills[idx] = pill;
        _groupLabels[idx] = labels;

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var nameBox = new TextBox { PlaceholderText = "Name", Text = draft.Name, MinWidth = 220 };
        nameBox.LostFocus += (_, _) => CommitGroupName(idx, nameBox.Text);
        nameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitGroupName(idx, nameBox.Text); };
        _groupNameBoxes[idx] = nameBox;
        body.Children.Add(Row("Name", nameBox));

        // Channel space. Changing it re-bases the member mask, so the chips are
        // rebuilt and the old selection dropped (bit N means a different channel).
        var kinds = GroupKindOptions().ToList();
        var kindCombo = new ComboBox { MinWidth = 180 };
        int sel = 0;
        for (int i = 0; i < kinds.Count; i++)
        {
            kindCombo.Items.Add(new ComboBoxItem { Content = kinds[i].label, Tag = kinds[i].kind });
            if (kinds[i].kind == draft.Kind) sel = i;
        }
        kindCombo.SelectedIndex = sel;
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (kindCombo.SelectedItem is ComboBoxItem it && it.Tag is CsTarget kind && kind != _groupDrafts[idx].Kind)
            {
                // Bit N means a different channel in the new space, so the old
                // selection can't carry over; only the member list is replaced.
                _groupDrafts[idx].Kind = kind;
                _groupDrafts[idx].MemberMask = 0;
                PopulateGroupMembers(idx);
                RefreshGroupMacroIndicators();
            }
        };
        _groupKindCombos[idx] = kindCombo;
        body.Children.Add(Row("Channels", kindCombo));

        var memberHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _groupMemberHosts[idx] = memberHost;
        body.Children.Add(memberHost);
        PopulateGroupMembers(idx);

        var usedBy = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush };
        _groupUsedBy[idx] = usedBy;
        body.Children.Add(usedBy);

        body.Children.Add(BuildGroupApplyRow(idx));
        expander.Content = body;
        _groupCards[idx] = expander;
        return expander;
    }

    /// <summary>Reflect the draft back into an existing group card's controls —
    /// after an Apply or a Revert — without tearing the card down.</summary>
    private void SyncGroupCard(int idx)
    {
        if (!_groupCards.ContainsKey(idx)) { RebuildGroupCards(); return; }
        var draft = _groupDrafts[idx];
        bool wasBuilding = _building;
        _building = true;
        try
        {
            if (_groupNameBoxes.TryGetValue(idx, out var nameBox) && nameBox.Text != draft.Name)
                nameBox.Text = draft.Name;
            if (_groupKindCombos.TryGetValue(idx, out var kindCombo))
            {
                var kinds = GroupKindOptions().Select(k => k.kind).ToList();
                int sel = kinds.IndexOf(draft.Kind);
                if (sel >= 0) kindCombo.SelectedIndex = sel;
            }
        }
        finally { _building = wasBuilding; }
        PopulateGroupMembers(idx);
        RefreshGroupMacroIndicators();
    }

    /// <summary>Fill (or refill) a group card's member list in place, e.g. after the
    /// channel space changed. The rest of the card is left alone.</summary>
    private void PopulateGroupMembers(int idx)
    {
        if (!_groupMemberHosts.TryGetValue(idx, out var host)) return;
        var kind = _groupDrafts[idx].Kind;
        int count = _vm.CsChannelCount(kind);
        _groupChannelRelabel.Remove(idx);
        host.Content = count > 0 ? BuildGroupMemberPicker(idx, kind, count) : null;
    }

    /// <summary>Member picker: one named checkbox per channel of the group's
    /// space, in two columns. Names are the same user-editable channel names the
    /// sidebar shows, and are relabelled in place on a rename.</summary>
    private FrameworkElement BuildGroupMemberPicker(int idx, CsTarget kind, int count)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (count + 1) / 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var boxes = new List<(CheckBox Box, int Bit)>();
        for (int ch = 0; ch < count; ch++)
        {
            int bit = ch;
            var box = new CheckBox
            {
                Content = ChannelLabel(kind, bit),
                IsChecked = (_groupDrafts[idx].MemberMask & (1u << bit)) != 0,
                MinWidth = 0,
                FontSize = 12,
            };
            box.Checked += (_, _) =>
            {
                if (_building) return;
                _groupDrafts[idx].MemberMask |= 1u << bit;
                RefreshGroupMacroIndicators();
            };
            box.Unchecked += (_, _) =>
            {
                if (_building) return;
                _groupDrafts[idx].MemberMask &= ~(1u << bit);
                RefreshGroupMacroIndicators();
            };
            Grid.SetColumn(box, bit % 2);
            Grid.SetRow(box, bit / 2);
            grid.Children.Add(box);
            boxes.Add((box, bit));
        }

        _groupChannelRelabel[idx] = () =>
        {
            foreach (var (box, bit) in boxes) box.Content = ChannelLabel(kind, bit);
        };
        return grid;
    }

    private FrameworkElement BuildGroupApplyRow(int idx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) =>
        {
            _groupDrafts[idx] = _vm.CsGroups[idx].Clone();
            SyncGroupCard(idx);
        };
        // The firmware rejects an empty group outright, so an unpopulated draft
        // can't be applied; delete the card to clear the slot instead. Enabled
        // state and tooltip both come from RefreshGroupMacroIndicators.
        var apply = new Button { Content = "Apply", Style = AccentStyle };
        apply.Click += (_, _) => _ = ApplyGroupAsync(idx);
        _groupApply[idx] = apply;
        panel.Children.Add(revert);
        panel.Children.Add(apply);
        return panel;
    }

    private void CommitGroupName(int idx, string text)
    {
        _groupDrafts[idx].Name = (text ?? "").Trim();
        RefreshGroupMacroIndicators();
    }

    private IEnumerable<(CsTarget kind, string label)> GroupKindOptions()
    {
        if (_vm.CsChannelCount(CsTarget.InputCh) > 0) yield return (CsTarget.InputCh, "Inputs");
        if (_vm.CsChannelCount(CsTarget.OutputCh) > 0) yield return (CsTarget.OutputCh, "Outputs");
        if (_vm.CsChannelCount(CsTarget.DspCh) > 0) yield return (CsTarget.DspCh, "All channels");
    }

    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        int idx = FirstFreeGroup(out bool referenced);
        if (idx < 0) return;
        if (referenced)
            ShowToast("Every free group slot is still used by a control whose group was deleted. " +
                      "Applying this group turns that control back on for its channels.");
        // Outputs are what a group is usually for (speaker sets), so start there
        // when the platform offers them.
        var kinds = GroupKindOptions().Select(k => k.kind).ToList();
        _groupDrafts[idx] = new CsGroup
        {
            Kind = kinds.Contains(CsTarget.OutputCh) ? CsTarget.OutputCh
                 : kinds.Count > 0 ? kinds[0] : CsTarget.DspCh,
            MemberMask = 0,
            Name = "",
        };
        _groupAdded.Add(idx);
        _groupExpanded.Add(idx);
        // Insert just the new card; the existing ones stay as they are.
        InsertGroupCard(idx);
        if (_groupCards.TryGetValue(idx, out var card)) Reveal.Bring(card);
    }

    private async void RemoveGroup(int idx)
    {
        bool wasLive = _vm.CsGroups[idx].IsConfigured;
        _groupDrafts[idx] = CsGroup.Cleared();
        _groupAdded.Remove(idx);
        _groupExpanded.Remove(idx);
        RemoveGroupCard(idx);
        if (!wasLive) return;

        // Clearing a group deactivates the bindings that referenced it (the
        // firmware reports the failure per slot rather than refusing the edit),
        // so the binding cards need their status and target pickers refreshed.
        string name = GroupTitle(idx);
        byte status = await Task.Run(() => _vm.SetCsGroup(idx, CsGroup.Cleared()));
        _groupDrafts[idx] = _vm.CsGroups[idx].Clone();
        var orphaned = RefreshGroupedBindingCards();
        RefreshStatusIndicators();
        RaiseStateChanged();

        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
        else ReportOrphanedControls(name, orphaned);
    }

    private async Task ApplyGroupAsync(int idx)
    {
        if (_applyingGroup != null) return;
        _applyingGroup = idx;
        RefreshGroupMacroIndicators();

        var group = _groupDrafts[idx].Clone();
        byte status = await Task.Run(() => _vm.SetCsGroup(idx, group));

        _applyingGroup = null;
        // Only a success re-seeds from the device (which may have truncated a long
        // name). A rejection keeps the user's edit on screen so they can fix what
        // the firmware objected to instead of losing it.
        if (status == CsStatus.Success) _groupDrafts[idx] = _vm.CsGroups[idx].Clone();
        // The card stays put; only its controls restate the device's answer.
        SyncGroupCard(idx);
        // A rename or a member change shows up in every binding that targets it —
        // and re-scoping to another channel space orphans them, exactly as a
        // delete does.
        var orphaned = RefreshGroupedBindingCards();
        RefreshStatusIndicators();
        RaiseStateChanged();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
        else ReportOrphanedControls(GroupTitle(idx), orphaned);
    }

    /// <summary>Tell the user when a group edit left controls stranded. The
    /// firmware deactivates a binding whose group no longer resolves, and the card
    /// falls back to a plain channel — a different binding than the one they had,
    /// which a casual Apply would then commit.</summary>
    private void ReportOrphanedControls(string groupName, List<string> orphaned)
    {
        if (orphaned.Count == 0) return;
        ShowToast($"\"{groupName}\" was used by {string.Join(", ", orphaned)} — " +
                  $"{(orphaned.Count == 1 ? "that control is" : "those controls are")} " +
                  "now off and fell back to a single channel. Re-target and apply.");
    }

    private bool GroupDirty(int idx) => !_groupDrafts[idx].WireEquals(_vm.CsGroups[idx]);

    private string GroupSummary(int idx)
    {
        var g = _groupDrafts[idx];
        if (!g.IsConfigured) return "No channels selected";
        int n = g.MemberCount;
        return $"{n} channel{(n == 1 ? "" : "s")}  ·  {_vm.CsGroupMembersLabel(g)}";
    }

    private string GroupStatusText(int idx)
    {
        if (GroupDirty(idx)) return "Pending";
        byte health = _vm.CsExtStatus?.GroupHealth(idx) ?? CsStatus.Success;
        return health == CsStatus.Success ? "Active" : "Invalid";
    }

    private Color GroupStatusColor(int idx) =>
        GroupStatusText(idx) == "Active"
            ? Color.FromArgb(255, 100, 200, 140)
            : Color.FromArgb(255, 240, 180, 90);

    /// <summary>Re-populate everything on the bindings page that names a group: a
    /// binding card whose parameter takes a channel, a remote button's target
    /// picker, and the display's own pages. A group added, renamed or re-scoped
    /// has to appear in — or leave — all three, not only the cards already
    /// pointing at one, which is what made a new group unselectable until the
    /// settings window was reopened. Returns the names of the controls whose group
    /// reference no longer resolves and was therefore dropped back to a plain
    /// channel.</summary>
    private List<string> RefreshGroupedBindingCards()
    {
        var orphaned = new List<string>();
        // Scan every draft rather than only the slots this panel drew a card for:
        // the Channel Groups page holds no binding cards at all, and it's the page
        // most likely to strand one. PopulateSlotBody no-ops without a card.
        int slots = Math.Min(_vm.CsSlotCount, CsLimits.MaxBindings);
        for (int slot = 0; slot < slots; slot++)
        {
            var draft = _drafts[slot];
            if (!draft.IsConfigured) continue;
            if (draft.IsGrouped)
            {
                SanitizeDraft(slot);
                if (!_drafts[slot].IsGrouped) orphaned.Add(SlotTitle(slot));
            }
            // The two containers have no target picker of their own: a receiver's
            // remote buttons and a panel's pages are refreshed on their own below,
            // and rebuilding either card body would tear that section down.
            else if (draft.Type is CsType.Ir or CsType.Display) continue;
            else if (_vm.CsNounDescFor(draft.Noun) is not { IsTargeted: true }) continue;
            PopulateSlotBody(slot);
        }
        // A remote key can carry a group too since caps v10, and a group edit
        // strands one exactly as it strands a control.
        for (int sub = 0; sub < _vm.CsIrMax; sub++)
        {
            var cmd = _irDrafts[sub];
            if (cmd.IsGrouped)
            {
                string title = IrCommandTitle(sub);
                SanitizeIrDraft(sub);
                if (!_irDrafts[sub].IsGrouped) orphaned.Add(title);
            }
            else if (_vm.CsNounDescFor(cmd.Noun) is not { IsTargeted: true }) continue;
            if (_irBodies.ContainsKey(sub)) RefreshIrCommandCard(sub);
        }
        // A display page picks a channel or a group from the same list.
        PopulateDisplayHost();
        return orphaned;
    }

    /// <summary>Rebuild the step rows of every macro card on screen. A step's
    /// target picker lists groups, so a group edit has to reach it the same way it
    /// reaches a binding's picker.</summary>
    private void RefreshMacroStepCards()
    {
        foreach (int idx in _macroCards.Keys.ToList())
            for (int step = 0; step < _macroDrafts[idx].StepCount; step++)
                PopulateMacroStepRows(idx, step);
        RefreshGroupMacroIndicators();
    }

    // ── Macros ───────────────────────────────────────────────────────────────

    private void RebuildMacroCards()
    {
        _building = true;
        try
        {
            MacrosPanel.Children.Clear();
            _macroCards.Clear();
            _macroPills.Clear();
            _macroLabels.Clear();
            _macroApply.Clear();
            _macroFireButtons.Clear();
            _macroNameBoxes.Clear();
            _macroStepHosts.Clear();
            _macroEmptyHints.Clear();
            _macroAddStep.Clear();
            _macroStepCards.Clear();
            for (int i = 0; i < _vm.CsMacroMax; i++)
                if (MacroShown(i)) MacrosPanel.Children.Add(BuildMacroCard(i));
        }
        finally { _building = false; }
        UpdateMacrosChrome();
    }

    private void UpdateMacrosChrome()
    {
        int shown = _macroCards.Count;
        MacrosHeading.Text = shown > 0 ? $"{shown} of {_vm.CsMacroMax} macros in use" : "";
        MacrosEmptyHint.Visibility = Vis(shown == 0);
        AddMacroButton.IsEnabled = FirstFreeMacro() >= 0;
    }

    /// <summary>Build one macro's card and insert it at its slot-ordered position,
    /// leaving the other cards untouched.</summary>
    private void InsertMacroCard(int idx)
    {
        int pos = 0;
        for (int i = 0; i < idx; i++) if (_macroCards.ContainsKey(i)) pos++;
        _building = true;
        try { MacrosPanel.Children.Insert(pos, BuildMacroCard(idx)); }
        finally { _building = false; }
        UpdateMacrosChrome();
    }

    /// <summary>Remove one macro's card element and drop its handles.</summary>
    private void RemoveMacroCard(int idx)
    {
        if (_macroCards.TryGetValue(idx, out var card)) MacrosPanel.Children.Remove(card);
        _macroCards.Remove(idx);
        _macroPills.Remove(idx);
        _macroLabels.Remove(idx);
        _macroApply.Remove(idx);
        _macroFireButtons.Remove(idx);
        _macroNameBoxes.Remove(idx);
        _macroStepHosts.Remove(idx);
        _macroEmptyHints.Remove(idx);
        _macroAddStep.Remove(idx);
        foreach (var key in _macroStepCards.Keys.Where(k => k.Macro == idx).ToList())
            _macroStepCards.Remove(key);
        UpdateMacrosChrome();
    }

    private FrameworkElement BuildMacroCard(int idx)
    {
        var draft = _macroDrafts[idx];
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _macroExpanded.Contains(idx),
        };
        expander.Expanding += (_, _) => { _macroExpanded.Add(idx); Reveal.Bring(expander); };
        expander.Collapsed += (_, _) => _macroExpanded.Remove(idx);

        expander.Header = BuildMacroHeader(idx);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var nameBox = new TextBox { PlaceholderText = "Name", Text = draft.Name, MinWidth = 220 };
        nameBox.LostFocus += (_, _) => CommitMacroName(idx, nameBox.Text);
        nameBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) CommitMacroName(idx, nameBox.Text); };
        _macroNameBoxes[idx] = nameBox;
        body.Children.Add(Row("Name", nameBox));

        // Steps live in their own host panel so one can be appended or dropped
        // without disturbing the rest of the card.
        var stepHost = new StackPanel { Spacing = 8 };
        _macroStepHosts[idx] = stepHost;
        for (int s = 0; s < draft.StepCount; s++)
            stepHost.Children.Add(BuildMacroStepCard(idx, s));
        body.Children.Add(stepHost);

        var hint = new TextBlock
        {
            Text = "No steps yet. Each step fires one parameter change after its own delay.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush,
        };
        _macroEmptyHints[idx] = hint;
        body.Children.Add(hint);

        var addStep = new Button { Content = "Add Step", HorizontalAlignment = HorizontalAlignment.Left };
        addStep.Click += (_, _) => AddMacroStep(idx);
        _macroAddStep[idx] = addStep;
        body.Children.Add(addStep);

        body.Children.Add(BuildMacroApplyRow(idx));
        expander.Content = body;
        _macroCards[idx] = expander;
        UpdateMacroStepChrome(idx);
        return expander;
    }

    /// <summary>Refresh what depends on a macro's step count and step positions:
    /// the reorder buttons, the empty hint, the Add Step button, and the header.
    /// Nothing is rebuilt.</summary>
    private void UpdateMacroStepChrome(int idx)
    {
        int count = _macroDrafts[idx].StepCount;
        for (int s = 0; s < count; s++)
        {
            if (!_macroStepCards.TryGetValue((idx, s), out var step)) continue;
            step.Up.IsEnabled = s > 0;
            step.Down.IsEnabled = s < count - 1;
        }
        if (_macroEmptyHints.TryGetValue(idx, out var hint))
            hint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_macroAddStep.TryGetValue(idx, out var add))
            add.IsEnabled = count < _vm.CsMacroStepMax;
        RefreshGroupMacroIndicators();
    }

    /// <summary>Reflect the draft back into an existing macro card — after an Apply
    /// or a Revert — by adding or dropping step cards at the end and refilling the
    /// rest in place, instead of rebuilding the card.</summary>
    private void SyncMacroCard(int idx)
    {
        if (!_macroCards.ContainsKey(idx) || !_macroStepHosts.TryGetValue(idx, out var host))
        {
            RebuildMacroCards();
            return;
        }
        var draft = _macroDrafts[idx];
        bool wasBuilding = _building;
        _building = true;
        try
        {
            if (_macroNameBoxes.TryGetValue(idx, out var nameBox) && nameBox.Text != draft.Name)
                nameBox.Text = draft.Name;

            while (host.Children.Count > draft.StepCount)
            {
                int last = host.Children.Count - 1;
                host.Children.RemoveAt(last);
                _macroStepCards.Remove((idx, last));
            }
            while (host.Children.Count < draft.StepCount)
                host.Children.Add(BuildMacroStepCard(idx, host.Children.Count));
        }
        finally { _building = wasBuilding; }

        for (int s = 0; s < draft.StepCount; s++) PopulateMacroStepRows(idx, s);
        UpdateMacroStepChrome(idx);
    }

    private FrameworkElement BuildMacroHeader(int idx)
    {
        // Only what the device already holds can be fired: an unapplied draft isn't
        // there yet. While this macro is the running one the button stops it.
        var fire = new Button { Content = _vm.CsRunningMacro == idx ? "Stop" : "Run", Padding = new Thickness(12, 3, 12, 4) };
        fire.IsEnabled = (_vm.CsMacros[idx].StepCount > 0 || _vm.CsRunningMacro == idx);
        fire.Click += (_, _) => FireMacro(idx);
        _macroFireButtons[idx] = fire;

        var header = BuildRecordHeader(
            glyph: "",                                  // play
            accent: Color.FromArgb(255, 0xC9, 0x9A, 0xF0),    // soft purple
            title: MacroTitle(idx),
            summary: MacroSummary(idx),
            statusText: MacroStatusText(idx),
            statusColor: MacroStatusColor(idx),
            onDelete: () => RemoveMacro(idx),
            extra: fire,
            pill: out var pill,
            labels: out var labels);
        _macroPills[idx] = pill;
        _macroLabels[idx] = labels;
        return header;
    }

    private string GroupTitle(int idx) =>
        _groupDrafts[idx].Name.Length > 0 ? _groupDrafts[idx].Name : $"Group {idx + 1}";

    private string MacroTitle(int idx) =>
        _macroDrafts[idx].Name.Length > 0 ? _macroDrafts[idx].Name : $"Macro {idx + 1}";

    private FrameworkElement BuildMacroStepCard(int macro, int step)
    {
        var draft = _macroDrafts[macro].Steps[step];
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var sb) && sb is Brush sbr
                ? sbr : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Padding = new Thickness(10, 8, 10, 10),
        };
        var panel = new StackPanel { Spacing = 8 };

        // Step header: ordinal, reorder, remove.
        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock
        {
            Text = $"Step {step + 1}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        head.Children.Add(label);
        var up = StepIconButton(GlyphChevronUp, "Move up", step > 0, () => MoveMacroStep(macro, step, -1), 1);
        var down = StepIconButton(GlyphChevronDown, "Move down", step < _macroDrafts[macro].StepCount - 1,
            () => MoveMacroStep(macro, step, +1), 2);
        head.Children.Add(up);
        head.Children.Add(down);
        head.Children.Add(StepIconButton("", "Move up", step > 0, () => MoveMacroStep(macro, step, -1), 1));
        head.Children.Add(StepIconButton("", "Move down", step < _macroDrafts[macro].StepCount - 1,
            () => MoveMacroStep(macro, step, +1), 2));
        head.Children.Add(StepIconButton("", "Remove this step", true, () => RemoveMacroStep(macro, step), 3));
        panel.Children.Add(head);

        // The editable rows live in their own panel so a noun or action change can
        // refill just this step in place, the way a binding card refills its body.
        var rows = new StackPanel { Spacing = 8 };
        panel.Children.Add(rows);
        _macroStepCards[(macro, step)] = new MacroStepCard
        {
            Card = card, Rows = rows, Up = up, Down = down,
        };
        PopulateMacroStepRows(macro, step);

        card.Child = panel;
        return card;
    }

    /// <summary>Fill (or refill) one macro step's editor rows in place. Used instead
    /// of rebuilding the macro card when only this step's own options change, so the
    /// card keeps its scroll position and the other steps are left alone.</summary>
    private void PopulateMacroStepRows(int macro, int step)
    {
        if (!_macroStepCards.TryGetValue((macro, step), out var card)) return;
        var panel = card.Rows;
        bool wasBuilding = _building;
        _building = true;
        try
        {
            panel.Children.Clear();
            var draft = _macroDrafts[macro].Steps[step];

            TextBox delayBox = null!;
            delayBox = NumberField(draft.PreDelaySeconds, CsUnit.None, v =>
            {
                var s = _macroDrafts[macro].Steps[step];
                s.PreDelaySeconds = v;
                // The wire field is 10 ms units capped at ~10.9 minutes, so echo
                // what was actually stored instead of leaving a typed -5 or 9999
                // on screen that the step doesn't hold.
                if (Math.Abs(s.PreDelaySeconds - v) > 0.0005)
                    delayBox.Text = FormatNumber(s.PreDelaySeconds);
                RefreshGroupMacroIndicators();
            });
            ToolTipService.SetToolTip(delayBox,
                "Wait this long before running the step (10 ms resolution, up to 655 s)");
            panel.Children.Add(Row("Delay (s)", delayBox));

            panel.Children.Add(BuildMacroStepNounRow(macro, step, draft));

            var nd = _vm.CsNounDescFor(draft.Noun);
            if (nd != null)
            {
                var actions = MacroStepActions(nd);
                if (actions.Count > 1)
                    panel.Children.Add(BuildMacroStepActionRow(macro, step, draft, actions));

                if (nd.IsTargeted) panel.Children.Add(BuildMacroStepTargetRows(macro, step, nd));
                var operand = BuildMacroStepOperand(macro, step, nd);
                if (operand != null) panel.Children.Add(operand);
                if (nd.Kind == CsKind.Enum && (CsAction)draft.Action is CsAction.Inc or CsAction.Dec)
                {
                    var wrap = new CheckBox
                    {
                        Content = "Wrap around at the ends",
                        IsChecked = (draft.Flags & CsFlags.Wrap) != 0,
                    };
                    wrap.Checked += (_, _) =>
                    { _macroDrafts[macro].Steps[step].Flags |= CsFlags.Wrap; RefreshGroupMacroIndicators(); };
                    wrap.Unchecked += (_, _) =>
                    { _macroDrafts[macro].Steps[step].Flags &= ~CsFlags.Wrap; RefreshGroupMacroIndicators(); };
                    panel.Children.Add(wrap);
                }
            }
        }
        finally { _building = wasBuilding; }
        RefreshGroupMacroIndicators();
    }

    private FrameworkElement BuildMacroStepNounRow(int macro, int step, CsMacroStep draft)
    {
        var nounCombo = new ComboBox { MinWidth = 200 };
        int selected = -1, i2 = 0;
        foreach (var (noun, _) in MacroStepNouns())
        {
            nounCombo.Items.Add(new ComboBoxItem { Content = CsNounInfo.Name(noun), Tag = noun });
            if (noun == draft.Noun) selected = i2;
            i2++;
        }
        nounCombo.SelectedIndex = selected;
        nounCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (nounCombo.SelectedItem is ComboBoxItem it && it.Tag is int noun)
            {
                var s = _macroDrafts[macro].Steps[step];
                s.Noun = (byte)noun;
                var nd2 = _vm.CsNounDescFor(noun);
                var acts = nd2 != null ? MacroStepActions(nd2) : new List<CsAction>();
                s.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
                s.Value = 0; s.Step = 0; s.Target = 0; s.Index = 0;
                if (nd2?.TargetKind == CsTarget.Aux && TargetChoices(noun, nd2) is { Count: > 0 } aux)
                    s.Target = (byte)aux[0];
                // The group reference and the wrap bit both belong to the old
                // noun's channel space and value kind.
                s.Flags &= ~(CsFlags.Group | CsFlags.Wrap);
                PopulateMacroStepRows(macro, step);
            }
        };
        return Row("Controls", nounCombo);
    }

    private FrameworkElement BuildMacroStepActionRow(int macro, int step, CsMacroStep draft,
                                                     List<CsAction> actions)
    {
        var actionCombo = new ComboBox { MinWidth = 160 };
        int sel = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            actionCombo.Items.Add(new ComboBoxItem { Content = ActionName(actions[i]), Tag = actions[i] });
            if ((byte)actions[i] == draft.Action) sel = i;
        }
        actionCombo.SelectedIndex = sel;
        actionCombo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (actionCombo.SelectedItem is ComboBoxItem it && it.Tag is CsAction a)
            {
                var s = _macroDrafts[macro].Steps[step];
                s.Action = (byte)a;
                s.Value = 0; s.Step = 0;
                if (a is not (CsAction.Inc or CsAction.Dec)) s.Flags &= ~CsFlags.Wrap;
                PopulateMacroStepRows(macro, step);
            }
        };
        return Row("Action", actionCombo);
    }

    private static Button StepIconButton(string glyph, string tip, bool enabled, Action onClick, int column)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            IsEnabled = enabled,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => onClick();
        Grid.SetColumn(b, column);
        return b;
    }

    /// <summary>Channel or group picker for a macro step. Steps carry the same
    /// GROUP flag as bindings; only LINK_ABS and GROUP_ALL are out of scope here
    /// (a step has no gesture and no indicator).</summary>
    private FrameworkElement BuildMacroStepTargetRows(int macro, int step, CsNounDesc nd)
    {
        var draft = _macroDrafts[macro].Steps[step];
        var panel = new StackPanel { Spacing = 8 };

        var groups = CompatibleGroups(nd).ToList();
        var combo = new ComboBox { MinWidth = 180 };
        FillTargetCombo(combo, draft.Noun, nd, groups, draft.IsGrouped, draft.Target);
        combo.SelectionChanged += (_, _) =>
        {
            if (_building) return;
            if (combo.SelectedItem is not ComboBoxItem it) return;
            var s = _macroDrafts[macro].Steps[step];
            if (it.Tag is int ch) { s.Flags &= ~CsFlags.Group; s.Target = (byte)ch; }
            else if (it.Tag is GroupTag g) { s.Flags |= CsFlags.Group; s.Target = (byte)g.Index; }
            RefreshGroupMacroIndicators();
        };
        panel.Children.Add(Row(TargetRowLabel(nd, groups.Count > 0), combo));
        if (nd.TargetKind == CsTarget.Aux && combo.Items.Count == 0)
            panel.Children.Add(AuxTargetHint(draft.Noun));

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
                {
                    _macroDrafts[macro].Steps[step].Index = (byte)band;
                    RefreshGroupMacroIndicators();
                }
            };
            panel.Children.Add(Row("Band", bandCombo));
        }
        return panel;
    }

    private FrameworkElement? BuildMacroStepOperand(int macro, int step, CsNounDesc nd)
    {
        var draft = _macroDrafts[macro].Steps[step];
        var action = (CsAction)draft.Action;

        // Every one of these writes the draft, so each has to re-run the dirty
        // check: the card's Apply button is enabled from it.
        if (action is CsAction.Inc or CsAction.Dec)
        {
            string label = CsWire.UnitStepsInOctaves(nd.Unit) ? "Step (octaves)"
                : nd.Unit == CsUnit.None ? "Step (positions)" : StepLabel(nd.Unit);
            var box = NumberField(CsWire.DecodeStep(draft.Step, nd.Unit), CsUnit.None, v =>
            {
                _macroDrafts[macro].Steps[step].Step = CsWire.EncodeStep(v, nd.Unit);
                RefreshGroupMacroIndicators();
            });
            return Row(label, box);
        }
        if (action != CsAction.Set) return null;   // Toggle / Trigger take no operand

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
                {
                    _macroDrafts[macro].Steps[step].Value = v;
                    RefreshGroupMacroIndicators();
                }
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
                {
                    _macroDrafts[macro].Steps[step].Value = v;
                    RefreshGroupMacroIndicators();
                }
            };
            return Row("Value", combo);   // a step's noun is never the macro noun
        }
        var num = NumberField(CsWire.DecodeValue(draft.Value, nd.Unit), nd.Unit, v =>
        {
            _macroDrafts[macro].Steps[step].Value = CsWire.EncodeValue(v, nd.Unit);
            RefreshGroupMacroIndicators();
        });
        return Row(ValueLabel(nd.Unit), num);
    }

    private FrameworkElement BuildMacroApplyRow(int idx)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var revert = new Button { Content = "Revert" };
        revert.Click += (_, _) =>
        {
            _macroDrafts[idx] = _vm.CsMacros[idx].Clone();
            SyncMacroCard(idx);
        };
        var apply = new Button { Content = "Apply", Style = AccentStyle };
        apply.IsEnabled = MacroDirty(idx) && _applyingMacro == null;
        apply.Click += (_, _) => _ = ApplyMacroAsync(idx);
        _macroApply[idx] = apply;
        panel.Children.Add(revert);
        panel.Children.Add(apply);
        return panel;
    }

    private void CommitMacroName(int idx, string text)
    {
        _macroDrafts[idx].Name = (text ?? "").Trim();
        RefreshGroupMacroIndicators();
    }

    private void OnAddMacroClick(object sender, RoutedEventArgs e)
    {
        int idx = FirstFreeMacro();
        if (idx < 0) return;
        _macroDrafts[idx] = new CsMacro();
        _macroAdded.Add(idx);
        _macroExpanded.Add(idx);
        InsertMacroCard(idx);
        if (_macroCards.TryGetValue(idx, out var card)) Reveal.Bring(card);
    }

    private async void RemoveMacro(int idx)
    {
        bool wasLive = _vm.CsMacros[idx].IsConfigured;
        _macroDrafts[idx] = new CsMacro();
        _macroAdded.Remove(idx);
        _macroExpanded.Remove(idx);
        RemoveMacroCard(idx);
        if (!wasLive) return;

        byte status = await Task.Run(() => _vm.SetCsMacro(idx, new CsMacro()));
        _macroDrafts[idx] = _vm.CsMacros[idx].Clone();
        RefreshStatusIndicators();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
    }

    /// <summary>Append a step: one new card joins the step host, nothing else in the
    /// macro card is touched.</summary>
    private void AddMacroStep(int idx)
    {
        var macro = _macroDrafts[idx];
        if (macro.StepCount >= _vm.CsMacroStepMax) return;
        if (!_macroStepHosts.TryGetValue(idx, out var host)) { SyncMacroCard(idx); return; }

        var step = new CsMacroStep();
        var noun = MacroStepNouns().FirstOrDefault();
        if (noun.nd != null)
        {
            step.Noun = (byte)noun.noun;
            var acts = MacroStepActions(noun.nd);
            step.Action = acts.Count > 0 ? (byte)acts[0] : (byte)0;
        }
        int at = macro.StepCount;
        macro.Steps[at] = step;
        macro.StepCount++;

        _building = true;
        try { host.Children.Add(BuildMacroStepCard(idx, at)); }
        finally { _building = false; }
        UpdateMacroStepChrome(idx);
    }

    /// <summary>Remove a step. The steps after it shift up one, so their cards are
    /// refilled with the shifted data and the now-surplus last card is dropped —
    /// the cards above the removal never move.</summary>
    private void RemoveMacroStep(int idx, int step)
    {
        var macro = _macroDrafts[idx];
        if (step < 0 || step >= macro.StepCount) return;
        if (!_macroStepHosts.TryGetValue(idx, out var host)) { SyncMacroCard(idx); return; }

        for (int s = step; s < macro.StepCount - 1; s++)
            macro.Steps[s] = macro.Steps[s + 1];
        macro.Steps[macro.StepCount - 1] = new CsMacroStep();
        macro.StepCount--;

        int last = host.Children.Count - 1;
        if (last >= 0)
        {
            host.Children.RemoveAt(last);
            _macroStepCards.Remove((idx, last));
        }
        for (int s = step; s < macro.StepCount; s++) PopulateMacroStepRows(idx, s);
        UpdateMacroStepChrome(idx);
    }

    /// <summary>Swap a step with its neighbour: both cards are refilled with the
    /// swapped data in place, so neither card is recreated.</summary>
    private void MoveMacroStep(int idx, int step, int delta)
    {
        var macro = _macroDrafts[idx];
        int dest = step + delta;
        if (step < 0 || step >= macro.StepCount || dest < 0 || dest >= macro.StepCount) return;
        (macro.Steps[step], macro.Steps[dest]) = (macro.Steps[dest], macro.Steps[step]);
        PopulateMacroStepRows(idx, step);
        PopulateMacroStepRows(idx, dest);
        UpdateMacroStepChrome(idx);
    }

    private async Task ApplyMacroAsync(int idx)
    {
        if (_applyingMacro != null) return;
        _applyingMacro = idx;
        RefreshGroupMacroIndicators();

        var macro = _macroDrafts[idx].Clone();
        byte status = await Task.Run(() => _vm.SetCsMacro(idx, macro));

        _applyingMacro = null;
        // A partial write (one step rejected, the rest already staged) leaves the
        // device half-updated; keeping the draft means the user still has their
        // whole sequence to correct and re-apply, rather than the device's
        // fragment. Only a clean apply re-seeds.
        if (status == CsStatus.Success) _macroDrafts[idx] = _vm.CsMacros[idx].Clone();
        SyncMacroCard(idx);
        // Bindings and remote buttons that fire a macro show its name.
        RefreshStatusIndicators();
        RaiseStateChanged();
        if (status != CsStatus.Success) ShowToast(CsStatus.Message(status));
    }

    private async void FireMacro(int idx)
    {
        // Firing the running macro cancels it at its current step boundary; the
        // Run button doubles as Stop while it is the one running.
        bool running = _vm.CsRunningMacro == idx;
        bool ok = await Task.Run(() => running ? Cancel() : _vm.CsMacroFire(idx));
        RefreshGroupMacroIndicators();
        if (!running)
        {
            if (!ok) ShowToast("The device would not run that macro.");
            else StartMacroPoll();
        }

        bool Cancel() { _vm.CsMacroCancel(); return true; }
    }

    /// <summary>Poll the extended status while a macro runs. Macro progress is the
    /// one piece of CS state the firmware doesn't push a notification for, and a
    /// sequence with long pre-delays can run for minutes.</summary>
    private void StartMacroPoll()
    {
        _macroPoll ??= DispatcherQueue.CreateTimer();
        if (_macroPoll.IsRunning) return;
        _macroPollTicks = 0;
        _macroPoll.Interval = TimeSpan.FromMilliseconds(MacroPollFastMs);
        _macroPoll.Tick += OnMacroPollTick;
        _macroPoll.Start();
    }

    private async void OnMacroPollTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // A read that fails (unplugged mid-run, device wedged) would otherwise
        // leave the last "running" reading in place and poll forever.
        bool alive = await Task.Run(() => _vm.RefreshCsExtStatus());
        RefreshGroupMacroIndicators();
        if (!alive || _vm.CsRunningMacro == null) { StopMacroPoll(); return; }

        // A step's pre-delay reaches ~10.9 minutes, so a macro can legitimately
        // run for a long time. Watch it closely at first, then ease off rather
        // than keep a twice-a-second transfer going for minutes.
        if (++_macroPollTicks == MacroPollFastTicks)
            sender.Interval = TimeSpan.FromMilliseconds(MacroPollSlowMs);
    }

    private bool MacroDirty(int idx) => !_macroDrafts[idx].WireEquals(_vm.CsMacros[idx]);

    private string MacroSummary(int idx)
    {
        var m = _macroDrafts[idx];
        if (m.StepCount == 0) return "No steps";
        double total = 0;
        for (int s = 0; s < m.StepCount; s++) total += m.Steps[s].PreDelaySeconds;
        string steps = $"{m.StepCount} step{(m.StepCount == 1 ? "" : "s")}";
        return total > 0 ? $"{steps}  ·  {FormatNumber(total)} s total delay" : steps;
    }

    private string MacroStatusText(int idx)
    {
        if (_vm.CsRunningMacro == idx) return "Running";
        if (MacroDirty(idx)) return "Pending";
        byte health = _vm.CsExtStatus?.MacroHealth(idx) ?? CsStatus.Success;
        return health == CsStatus.Success ? "Ready" : "Invalid";
    }

    private Color MacroStatusColor(int idx) => MacroStatusText(idx) switch
    {
        "Running" => Color.FromArgb(255, 100, 200, 140),
        "Ready" => Color.FromArgb(255, 140, 160, 180),
        _ => Color.FromArgb(255, 240, 180, 90),
    };

    /// <summary>Nouns a macro step may drive: everything accepting one of the step
    /// actions, minus the macro noun itself (steps may not fire macros) and
    /// Browse/Adjust (a stored sequence that edits whatever happens to be on the
    /// display's screen is non-deterministic, and the firmware rejects it at step
    /// validation for exactly that reason).</summary>
    private IEnumerable<(int noun, CsNounDesc nd)> MacroStepNouns()
    {
        for (int n = 0; n < _vm.CsNounDescs.Count; n++)
        {
            if (n is (int)CsNoun.Macro or (int)CsNoun.PageValue) continue;
            var nd = _vm.CsNounDescs[n];
            if (nd == null || !nd.IsAvailable) continue;
            if (MacroStepActions(nd).Count == 0) continue;
            yield return (n, nd);
        }
    }

    /// <summary>The macro-step action subset a noun accepts: SET, TOGGLE, INC, DEC
    /// or TRIGGER (groups/macros spec, s1.4).</summary>
    private static List<CsAction> MacroStepActions(CsNounDesc nd)
    {
        var subset = new[] { CsAction.Set, CsAction.Toggle, CsAction.Inc, CsAction.Dec, CsAction.Trigger };
        return subset.Where(nd.SupportsAction).ToList();
    }

    // ── Shared card chrome ───────────────────────────────────────────────────

    /// <summary>The group/macro card header: tinted glyph badge, title + summary,
    /// an optional inline action button, a status pill and a delete button. Same
    /// shape as a binding card's header so the three sections read as one list.
    /// The pill and the two text blocks come back out so an edit can refresh them
    /// in place instead of rebuilding the card (which would drop typing focus).</summary>
    private FrameworkElement BuildRecordHeader(string glyph, Color accent, string title, string summary,
                                               string statusText, Color statusColor, Action onDelete,
                                               FrameworkElement? extra,
                                               out (Border Pill, Ellipse Dot, TextBlock Label) pill,
                                               out (TextBlock Title, TextBlock Summary) labels)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x2A, accent.R, accent.G, accent.B)),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 15,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var summaryText = new TextBlock
        {
            Text = summary,
            FontSize = 11,
            Foreground = SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        stack.Children.Add(titleText);
        stack.Children.Add(summaryText);
        labels = (titleText, summaryText);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);

        if (extra != null)
        {
            extra.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(extra, 2);
            grid.Children.Add(extra);
        }

        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(dot);
        content.Children.Add(label);
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        pill = (capsule, dot, label);
        SetPill(pill, statusText, statusColor);
        Grid.SetColumn(capsule, 3);
        grid.Children.Add(capsule);

        var del = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
        };
        ToolTipService.SetToolTip(del, "Remove");
        del.Click += (_, _) => onDelete();
        Grid.SetColumn(del, 4);
        grid.Children.Add(del);

        return grid;
    }

    // ── Status refresh ───────────────────────────────────────────────────────

    /// <summary>Refresh the group and macro cards' status pills and buttons. Card
    /// bodies are left alone, so an open editor keeps its focus and scroll.</summary>
    private void RefreshGroupMacroIndicators()
    {
        if (!_vm.CsGroupsSupported) return;

        foreach (var (idx, pill) in _groupPills)
            SetPill(pill, GroupStatusText(idx), GroupStatusColor(idx));
        foreach (var (idx, labels) in _groupLabels)
        {
            labels.Title.Text = GroupTitle(idx);
            labels.Summary.Text = GroupSummary(idx);
        }
        foreach (var (idx, usedBy) in _groupUsedBy)
        {
            // Live controls only: what an edit to this group would deactivate.
            int n = 0;
            for (int s = 0; s < Math.Min(_vm.CsSlotCount, _vm.CsBindings.Count); s++)
                if (_vm.CsBindings[s] is { IsConfigured: true, IsGrouped: true } b && b.Target == idx) n++;
            usedBy.Text = $"Used by {n} control{(n == 1 ? "" : "s")}. Emptying this group or changing its channel "
                        + "type deactivates them until it fits again.";
            usedBy.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (idx, apply) in _groupApply)
        {
            bool populated = _groupDrafts[idx].IsConfigured;
            apply.Content = _applyingGroup == idx ? "Applying…" : "Apply";
            apply.IsEnabled = GroupDirty(idx) && populated && _applyingGroup == null;
            // The reason it's disabled changes as members are ticked, so the
            // tooltip has to follow rather than stay at its build-time text.
            ToolTipService.SetToolTip(apply, populated
                ? "Preview this group on the device"
                : "Select at least one channel");
        }

        foreach (var (idx, pill) in _macroPills)
            SetPill(pill, MacroStatusText(idx), MacroStatusColor(idx));
        foreach (var (idx, labels) in _macroLabels)
        {
            labels.Title.Text = MacroTitle(idx);
            labels.Summary.Text = MacroSummary(idx);
        }
        foreach (var (idx, apply) in _macroApply)
        {
            // Writing a macro is up to eight deferred SETs, which can take a
            // noticeable moment; say so rather than just going grey.
            bool inFlight = _applyingMacro == idx;
            apply.Content = inFlight ? "Applying…" : "Apply";
            apply.IsEnabled = MacroDirty(idx) && _applyingMacro == null;
        }
        foreach (var (idx, fire) in _macroFireButtons)
        {
            // Running a macro mid-write would fire a half-written sequence.
            bool running = _vm.CsRunningMacro == idx;
            fire.IsEnabled = (_vm.CsMacros[idx].StepCount > 0 || running)
                             && _applyingMacro == null;
            fire.Content = running ? "Stop" : "Run";
            // Run fires what the device holds, not the draft on screen — worth
            // saying while the card has edits that haven't been applied.
            ToolTipService.SetToolTip(fire, running ? "Stop the running macro"
                : MacroDirty(idx) ? "Runs the version on the device — apply your changes first"
                : "Run this macro on the device");
        }
    }

    /// <summary>Stop the macro-progress poll when the window closes.</summary>
    private void StopMacroPoll()
    {
        if (_macroPoll == null) return;
        _macroPoll.Stop();
        _macroPoll.Tick -= OnMacroPollTick;
    }
}
