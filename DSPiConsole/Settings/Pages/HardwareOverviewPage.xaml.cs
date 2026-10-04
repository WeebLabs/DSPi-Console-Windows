using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// System › Overview — a read-only map of every GPIO the device is holding.
///
/// <para>
/// Only <b>held</b> pins are listed. A feature that is configured but switched
/// off reserves nothing on the device and so appears nowhere here: a disabled
/// optional S/PDIF input, ADAT while stopped, a control interface that is down,
/// a control-surface binding the device refused at boot. That is not a
/// simplification for the sake of the page — it is the firmware's own rule, and
/// the pin pickers on the other pages follow it too.
/// </para>
///
/// <para>
/// Nothing here is editable, so the page stages nothing with the tracker. It
/// rebuilds on the same <see cref="HardwarePins.PinAssignmentsChanged"/>
/// broadcast the pickers use, and on the handful of view-model properties that
/// change which claims are live.
/// </para>
/// </summary>
public sealed partial class HardwareOverviewPage : SettingsModule, ISettingsPage
{
    /// <summary>Columns in the map. Fixed rather than adaptive: the settings
    /// content pane is a known width, and a count that re-decides itself from
    /// the width offered settles a frame late.</summary>
    private const int MapColumns = 9;

    /// <summary>Cell width in the map at full size. The columns are starred and
    /// the grid capped at <see cref="MapWidth"/>, so this is what a cell settles
    /// at whenever the pane has room and what it shrinks from when it has not —
    /// rather than a hard width that keeps its size by spilling over the card.
    /// Bounded because a one- or two-digit number stretched across the pane sits
    /// in a chip four times wider than it needs, reading as a table of something
    /// rather than a pin header.</summary>
    private const double MapCellWidth = 64;

    /// <summary>Gap between map cells, set from here rather than in the XAML so
    /// that <see cref="MapWidth"/> cannot drift from what is actually drawn.</summary>
    private const double MapCellSpacing = 6;

    /// <summary>How wide the map comes out where there is room for it. Both this
    /// and the breakdown below are capped at it rather than set to it, and both
    /// stretch, so they stay exactly this wide and exactly aligned while the pane
    /// allows and shrink together when it does not.
    ///
    /// <para>Set widths clipped instead: the page cannot scroll sideways and the
    /// settings window has no minimum size, so a block wider than the pane was
    /// cut off at both edges with no way to reach it.</para></summary>
    private static double MapWidth => MapColumns * MapCellWidth + (MapColumns - 1) * MapCellSpacing;

    /// <summary>Columns in the breakdown.</summary>
    private const int BreakdownColumns = 4;

    /// <summary>How wide a feature's name may get before it ellipsizes. The
    /// columns divide <see cref="MapWidth"/> between them, so this is sized to
    /// what is left of one after the chip and its gap — a name that ran past it
    /// would push the block wider than the map and out over its card's edge.</summary>
    private const double BreakdownLabelMaxWidth = 90;

    /// <summary>One hue per role, from the macOS build so a chip means the same
    /// thing on both, except Other. Chosen by construction rather than by eye:
    /// hues spread around the wheel, each held inside the narrow lightness band
    /// where the fill carries white text at 4.5:1 — the invariant every one of
    /// these holds, running 4.6 to 5.8.
    ///
    /// <para>Other is amber here rather than the olive macOS uses, which reads as
    /// green next to the teal. The cost is separation: it sits 28 degrees from
    /// the clock brick where the olive sat 47. That only matters on the map,
    /// which mixes all five — each role section shows one colour alone. On the
    /// map the colour is the secondary cue anyway: the reliable read is claimed
    /// (saturated) against free (grey).</para></summary>
    private static Color RoleTint(PinRole role) => role switch
    {
        PinRole.Output => Color.FromArgb(255, 0x02, 0x78, 0xC7),   // blue
        PinRole.Clock => Color.FromArgb(255, 0xBA, 0x38, 0x22),    // brick
        PinRole.Input => Color.FromArgb(255, 0x04, 0x85, 0x6F),    // teal
        PinRole.Control => Color.FromArgb(255, 0x95, 0x43, 0xA7),  // orchid
        _ => Color.FromArgb(255, 0x9E, 0x61, 0x04),                // amber
    };

    private static string RoleTitle(PinRole role) => role switch
    {
        PinRole.Output => "Outputs",
        PinRole.Clock => "Clocks",
        PinRole.Input => "Inputs",
        PinRole.Control => "Control",
        _ => "Other",
    };

    public HardwareOverviewPage()
    {
        InitializeComponent();
        // Subscriptions on Loaded/Unloaded rather than in the constructor: the
        // shell caches page instances and detaches them when you navigate away,
        // so a constructor-only subscription would outlive the page's presence.
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnPinsChanged;
        HardwarePins.PinAssignmentsChanged += OnPinsChanged;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.PropertyChanged += OnVmPropertyChanged;
            // A preset load re-reads the device, and output pins and types are
            // per preset in with-preset mode; the bulk read raises no pin event.
            Vm.BulkRefreshed -= OnBulkRefreshed;
            Vm.BulkRefreshed += OnBulkRefreshed;
        }
        // Another page may have moved a pin while this one was detached.
        Refresh();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        HardwarePins.PinAssignmentsChanged -= OnPinsChanged;
        if (Vm != null)
        {
            Vm.PropertyChanged -= OnVmPropertyChanged;
            Vm.BulkRefreshed -= OnBulkRefreshed;
        }
    }

    private void OnPinsChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void OnBulkRefreshed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    /// <summary>The properties that change which pins are claimed rather than
    /// what they are called. Everything else a pin edit touches arrives through
    /// <see cref="HardwarePins.PinAssignmentsChanged"/>.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsDeviceConnected)
            or nameof(MainViewModel.CsStatus)
            or nameof(MainViewModel.CtrlIfaceStatus)
            or nameof(MainViewModel.Platform))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    protected override void Refresh()
    {
        if (Vm == null) return;

        bool connected = Vm.IsDeviceConnected;
        DisconnectedHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        BodyPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (!connected) return;

        var rows = HardwarePins.ActiveAssignments(Vm);
        var claims = rows.ToDictionary(r => r.Pin);
        int total = HardwarePins.ValidPins.Length;
        int free = total - rows.Count;

        UsageText.Text = $"{rows.Count} of {total} GPIOs in use";
        FreeText.Text = free == 0 ? "none free" : $"{free} free";

        BuildMap(claims);
        BuildLegend(rows);
        BuildGroups(rows);
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── The map ──────────────────────────────────────────────────────────────

    /// <summary>Every valid GPIO in pin order, tinted by what holds it and muted
    /// where free, so occupancy reads as colour rather than by parsing numbers.</summary>
    private void BuildMap(IReadOnlyDictionary<byte, PinAssignment> claims)
    {
        MapGrid.Children.Clear();
        MapGrid.ColumnDefinitions.Clear();
        MapGrid.RowDefinitions.Clear();
        MapGrid.ColumnSpacing = MapCellSpacing;
        MapGrid.RowSpacing = MapCellSpacing;
        MapGrid.MaxWidth = MapWidth;

        var pins = HardwarePins.ValidPins;
        int rowCount = (pins.Length + MapColumns - 1) / MapColumns;
        for (int c = 0; c < MapColumns; c++)
            MapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rowCount; r++)
            MapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < pins.Length; i++)
        {
            byte pin = pins[i];
            claims.TryGetValue(pin, out var claim);
            bool held = claims.ContainsKey(pin);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0, 8, 0, 9),
                Background = new SolidColorBrush(held
                    ? RoleTint(claim.Role)
                    : Color.FromArgb(0x20, 0x88, 0x88, 0x88)),
                Child = new TextBlock
                {
                    Text = pin.ToString(),
                    FontSize = 13,
                    FontFamily = MonoFont,
                    FontWeight = held ? FontWeights.SemiBold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = held
                        ? new SolidColorBrush(Colors.White)
                        : SecondaryBrush,
                },
            };
            ToolTipService.SetToolTip(chip, held ? $"GPIO {pin} — {claim.Label}" : $"GPIO {pin} — available");
            if (held) MakeClickable(chip, claim);
            Grid.SetColumn(chip, i % MapColumns);
            Grid.SetRow(chip, i / MapColumns);
            MapGrid.Children.Add(chip);
        }
    }

    /// <summary>A dot and a count per role in use — the same key as the sections
    /// below, beside the colours it explains. Reads as a tally ("5 Outputs")
    /// rather than a label with a figure after it.</summary>
    private void BuildLegend(IReadOnlyList<PinAssignment> rows)
    {
        LegendPanel.Children.Clear();
        foreach (var group in GroupByRole(rows))
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            entry.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(RoleTint(group.Role)),
            });
            entry.Children.Add(new TextBlock
            {
                Text = $"{group.Rows.Count} {RoleTitle(group.Role)}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = SecondaryBrush,
            });
            LegendPanel.Children.Add(entry);
        }
    }

    // ── The breakdown ────────────────────────────────────────────────────────

    /// <summary>One card per role in use, each listing its pins and the features
    /// holding them.</summary>
    private void BuildGroups(IReadOnlyList<PinAssignment> rows)
    {
        GroupsPanel.Children.Clear();
        foreach (var group in GroupByRole(rows))
        {
            // The role's name goes above its card, not inside it. Inside, the
            // label row and its gap sit on top of the chips while only the
            // card's padding sits under them, and the chips read as hanging
            // low in the card. Outside - the way Settings pages caption their
            // cards - the card holds nothing but chips, so they are centred
            // by construction.
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = RoleTitle(group.Role).ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = SecondaryBrush,
                Margin = new Thickness(2, 0, 0, 0),
            });

            // Always the full column count, even for a role holding one pin: the
            // block's width is the map's, so a fixed count puts every column on
            // the same edge down the whole page rather than re-centring per card.
            int columns = BreakdownColumns;

            // A fixed column count, like the map's: the pane is a known width,
            // and a count that re-decides itself settles a frame late.
            var grid = new Grid
            {
                ColumnSpacing = 16,
                RowSpacing = 12,
                MaxWidth = MapWidth,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            for (int c = 0; c < columns; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rowCount = (group.Rows.Count + columns - 1) / columns;
            for (int r = 0; r < rowCount; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < group.Rows.Count; i++)
            {
                var cell = BuildAssignmentCell(group.Rows[i]);
                Grid.SetColumn(cell, i % columns);
                Grid.SetRow(cell, i / columns);
                grid.Children.Add(cell);
            }
            // Its own card, matching the map's above it.
            section.Children.Add(new Border
            {
                Background = CardBackground,
                BorderBrush = CardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 16, 16, 16),
                Child = grid,
            });
            GroupsPanel.Children.Add(section);
        }
    }

    /// <summary>One pin: a role-tinted GPIO chip and the feature holding it. The
    /// chip is a fixed width so the names line up down every column, and the
    /// column itself sizes to the widest name in it. No role glyph — the card
    /// names the role and the chip is already coloured by it, so a third
    /// indicator would only cost width.</summary>
    private static FrameworkElement BuildAssignmentCell(PinAssignment row)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            // Hit-testable across the whole cell, not just where a glyph lands.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        panel.Children.Add(new Border
        {
            Width = 43,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(0, 3, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(RoleTint(row.Role)),
            Child = new TextBlock
            {
                Text = $"GP{row.Pin}",
                FontSize = 13,
                FontFamily = MonoFont,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White),
            },
        });
        var label = new TextBlock
        {
            Text = row.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = BreakdownLabelMaxWidth,
        };
        ToolTipService.SetToolTip(label, row.Label);
        panel.Children.Add(label);
        MakeClickable(panel, row);
        return panel;
    }

    /// <summary>Make a claimed pin's chip take you to the page that sets it. The
    /// map is a read-only summary, so a click has nowhere else useful to go, and
    /// landing on the page without saying which of its dozen cards you came for
    /// would only move the search — the shell flashes the control on arrival.
    ///
    /// <para>Leaves the tooltip to the caller: the hover state already says the
    /// chip is a target, so saying so again in the tooltip only buries what the
    /// tooltip is there for — what holds the pin.</para>
    ///
    /// <para>A free pin is left inert: there is no page that sets nothing.</para></summary>
    private static void MakeClickable(FrameworkElement element, PinAssignment claim)
    {
        element.Tapped += (_, e) =>
        {
            e.Handled = true;
            SettingsShell.RequestPin(claim.PageId, claim.Pin);
        };
        // Enough of a hover to say it is a target, without a button's chrome
        // covering the colour that makes the map readable.
        element.PointerEntered += (_, _) => element.Opacity = 0.75;
        element.PointerExited += (_, _) => element.Opacity = 1;
    }

    /// <summary>Claimed pins bucketed by role, empty roles dropped, each list in
    /// pin order and the roles in the order the enum declares them.</summary>
    private static IEnumerable<(PinRole Role, IReadOnlyList<PinAssignment> Rows)> GroupByRole(
        IReadOnlyList<PinAssignment> rows)
    {
        foreach (PinRole role in Enum.GetValues<PinRole>())
        {
            var members = rows.Where(r => r.Role == role).OrderBy(r => r.Pin).ToList();
            if (members.Count > 0) yield return (role, members);
        }
    }

    // ── Shared brushes ───────────────────────────────────────────────────────

    private static FontFamily MonoFont => new("Consolas");

    private static Brush CardBackground =>
        Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    private static Brush CardStroke =>
        Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

    private static Brush SecondaryBrush =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var b) && b is Brush br
            ? br : new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    // ── ISettingsPage ────────────────────────────────────────────────────────

    public string Id => "hardware.overview";
    public string Title => "Overview";
    public SettingsCategory Category => SettingsCategory.System;
    public string IconGlyph => ""; // AllApps (a grid)
    public int Order => 5;   // leads the group: it is the summary of the rest
    public bool IsAvailable(MainViewModel vm) => true;

    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var page = new HardwareOverviewPage();
        page.Attach(vm, tracker);
        return page;
    }
}
