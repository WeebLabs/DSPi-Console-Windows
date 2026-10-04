using System.ComponentModel;
using DSPiConsole.Controls;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DSPiConsole;

/// <summary>
/// The tube modeller (firmware 1.1.6 beta 4, wire V31): valve-style harmonic
/// colour, supply sag and a tube amplifier's output stage. Basic shows the tube
/// on a shelf with drive and mix; Advanced shows every parameter with the
/// stage's transfer curve. The choice is remembered. After the macOS Console's
/// TubeModellerView.
/// </summary>
public sealed class TubeModellerWindow : Window
{
    private sealed record StartingPoint(string Name, string Detail, int Type, float DriveDb, int Rectifier, bool Xfmr,
                                        float Damping = TubeLimits.DefaultXfmrDamping, float ResHz = TubeLimits.DefaultXfmrResHz);

    /// <summary>The spec's suggestions (§6). Each sets every non-character
    /// control, mix and trim included, so applying one lands on the same sound
    /// whatever came before; the type sets the character controls.</summary>
    private static readonly StartingPoint[] StartingPoints =
    {
        new("Clean default", "12AX7, level-neutral, output stage on", 1, TubeLimits.DefaultDriveDb, 1, TubeLimits.DefaultXfmrEnabled),
        new("Warm hi-fi", "12AU7 line stage, tightly damped", 5, -3, 1, true, Damping: 10),
        new("Single-ended sweetness", "300B, loose damping", 16, 3, 1, true, Damping: 2),
        new("Guitar-amp style", "12AX7 pushed, 5U4, loose damping", 1, 15, 2, true, Damping: 2, ResHz: 100),
        new("Push-pull power", "EL34 with the output stage on", 12, 0, 1, true, Damping: 6),
    };

    private readonly MainViewModel _vm;
    private readonly Brush _secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static readonly Color Accent = (Color)Application.Current.Resources["SystemAccentColor"];
    private readonly ContentControl _bodyHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TubeIcon _icon;
    private readonly SegmentedPicker _mode = new(new[] { "Basic", "Advanced" }, height: 24, fontSize: 12) { Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ToggleSwitch _enable = new() { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private bool _advanced = AppSettings.Instance.TubeModellerAdvanced;
    private bool _updating;

    // Body controls, rebuilt with the body.
    private TubeTransferGraph? _graph;
    private TextBlock? _second, _third;
    private ParameterRow? _drive, _mix, _trim, _bias, _asym, _hardness, _sag, _damping, _res;
    private DropDownButton? _typeButton, _presets, _maskPresets;
    private TextBlock? _typeCaption, _characterSource, _rectifierSummary, _lift, _showcaseName, _showcaseCaption;
    private SegmentedPicker? _rectifier;
    private ToggleSwitch? _xfmr;
    private StackPanel? _xfmrRows;
    private TubeIllustration? _illustration;
    private Border? _warmth;
    private bool _warmthLit;
    private readonly List<(int Type, Button Chip)> _shelf = new();
    private readonly List<ToggleButton> _outputChips = new();

    public TubeModellerWindow(MainViewModel vm)
    {
        _vm = vm;
        ToolWindowChrome.Apply(this, "Tube Modeller", 900, 700, fitContent: true);
        _icon = new TubeIcon(27, new SolidColorBrush(Accent)) { VerticalAlignment = VerticalAlignment.Center };

        var root = new Grid { Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildHeader());
        var rule = Rule();
        Grid.SetRow(rule, 1);
        root.Children.Add(rule);
        Grid.SetRow(_bodyHost, 2);
        root.Children.Add(_bodyHost);
        Content = root;

        _enable.Toggled += (_, _) => { if (!_updating) _vm.TubeEnabled = _enable.IsOn; };
        _mode.Picked += i =>
        {
            _advanced = i == 1;
            AppSettings.Instance.TubeModellerAdvanced = _advanced;
            AppSettings.Instance.Save();
            BuildBody();
        };

        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnVmPropertyChanged;
        BuildBody();
    }

    // ── Layout helpers ──

    private static Border Rule() => new() { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Caption(string text = "") => new() { Text = text, FontSize = 9, Foreground = _secondary, TextWrapping = TextWrapping.Wrap };

    private static Grid Row(FrameworkElement left, FrameworkElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private static DropDownButton MenuButton(string text, MenuFlyout menu) => new()
    {
        Content = text, FontSize = 11, Padding = new Thickness(8, 2, 8, 3), Flyout = menu,
        Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
    };

    private static MenuFlyoutItem MenuItem(string text, Action act)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => act();
        return item;
    }

    private static Grid Columns(FrameworkElement left, FrameworkElement right)
    {
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(left);
        var divider = new Border { Width = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] };
        Grid.SetColumn(divider, 1);
        columns.Children.Add(divider);
        Grid.SetColumn(right, 2);
        columns.Children.Add(right);
        return columns;
    }

    private TubeShaper Shaper(float? drive = null, float? bias = null, float? asym = null, float? hardness = null, float? mix = null, float? trim = null) =>
        new(drive ?? _vm.TubeDriveDb, bias ?? _vm.TubeBiasPct, asym ?? _vm.TubeAsymDb,
            hardness ?? _vm.TubeHardnessPct, mix ?? _vm.TubeMixPct, trim ?? _vm.TubeTrimDb);

    /// <summary>A row whose drag goes to the device and, when it shapes the
    /// curve, to the graph.</summary>
    private ParameterRow Param(string title, string unit, float min, float max, ushort index, Action<float> set,
                               Func<float, TubeShaper>? shaper = null) =>
        new(title, unit, min, max, set: set, live: v =>
        {
            _vm.SendTubeParamLive(index, v, min, max);
            if (shaper != null) { _graph?.SetLive(shaper(Math.Clamp(v, min, max))); UpdateHarmonics(); }
        });

    // ── Header ──

    private FrameworkElement BuildHeader()
    {
        var header = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Tube Modeller", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titles.Children.Add(new TextBlock { Text = "Valve-style harmonic colour, supply sag and a tube amplifier's output stage", FontSize = 10, Foreground = _secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        header.Children.Add(_icon);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_mode, 2);
        header.Children.Add(_mode);
        Grid.SetColumn(_enable, 3);
        header.Children.Add(_enable);
        return header;
    }

    // ── Body ──

    private void BuildBody()
    {
        _shelf.Clear();
        _outputChips.Clear();
        _graph = null;
        _illustration = null;
        _warmth = null;
        _drive = _mix = _trim = _bias = _asym = _hardness = _sag = _damping = _res = null;
        _typeButton = _presets = _maskPresets = null;
        _typeCaption = _characterSource = _rectifierSummary = _lift = _showcaseName = _showcaseCaption = _second = _third = null;
        _rectifier = null;
        _xfmr = null;
        _xfmrRows = null;

        if (!_vm.TubeSupported)
        {
            var note = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24) };
            note.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = _secondary });
            note.Children.Add(new TextBlock { Text = "Requires DSPi firmware 1.1.6 beta 4 or later.", FontSize = 12, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
            note.Children.Add(new TextBlock { Text = "Update the DSPi firmware to use the Tube Modeller.", FontSize = 10, Foreground = _secondary, HorizontalAlignment = HorizontalAlignment.Center });
            _bodyHost.Content = note;
            Refresh();
            return;
        }

        var body = _advanced ? BuildAdvanced() : BuildBasic();
        _bodyHost.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    private FrameworkElement BuildAdvanced()
    {
        var left = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };

        // Transfer curve and its harmonics.
        var startMenu = new MenuFlyout();
        foreach (var p in StartingPoints)
            startMenu.Items.Add(MenuItem($"{p.Name} - {p.Detail}", () => ApplyStartingPoint(p)));
        _presets = MenuButton("Apply preset", startMenu);
        left.Children.Add(Row(SectionLabel("TRANSFER CURVE"), _presets));
        _graph = new TubeTransferGraph();
        left.Children.Add(new Border
        {
            Height = 188, CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)), BorderThickness = new Thickness(1),
            Child = _graph,
        });
        _second = new TextBlock { FontSize = 11, Width = 46, FontFamily = new FontFamily("Cascadia Code, Consolas") };
        _third = new TextBlock { FontSize = 11, Width = 46, FontFamily = new FontFamily("Cascadia Code, Consolas") };
        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        values.Children.Add(new TextBlock { Text = "2nd", FontSize = 10, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center });
        values.Children.Add(_second);
        values.Children.Add(new TextBlock { Text = "3rd", FontSize = 10, Foreground = _secondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        values.Children.Add(_third);
        var harmonics = Row(SectionLabel("AT FULL SCALE"), values);
        harmonics.Margin = new Thickness(0, -6, 0, 0);
        ToolTipService.SetToolTip(harmonics, "Level of the second and third harmonic relative to the fundamental, for a full-scale sine through the static curve after mix and trim. Sag lowers the drive on sustained loud passages and the output stage adds its own low-frequency lift, so the running figures sit somewhat lower.");
        left.Children.Add(harmonics);
        left.Children.Add(Rule());

        // Stage.
        left.Children.Add(SectionLabel("STAGE"));
        left.Children.Add(BuildTypeRow());
        _drive = Param("Drive", "dB", TubeLimits.DriveMinDb, TubeLimits.DriveMaxDb, TubeParam.DriveDb, v => _vm.TubeDriveDb = v, v => Shaper(drive: v));
        _drive.ScrollStep = 0.5f; _drive.MaxDecimals = 1;
        _drive.Help = "Gain ahead of the shaper. At 0 dB a full-scale signal just reaches the knee, so this alone sets how hard the stage is driven. The shaper carries matching makeup gain, so clean material keeps its level at every drive; harmonics and sag rise with it.";
        _mix = Param("Mix", "%", TubeLimits.MixMinPct, TubeLimits.MixMaxPct, TubeParam.MixPct, v => _vm.TubeMixPct = v, v => Shaper(mix: v));
        _mix.ScrollStep = 1;
        _mix.Help = "Blend of the processed signal with the untouched input. The dry path is sample-aligned with the wet one, so blending never combs.";
        _trim = Param("Output Trim", "dB", TubeLimits.TrimMinDb, TubeLimits.TrimMaxDb, TubeParam.TrimDb, v => _vm.TubeTrimDb = v, v => Shaper(trim: v));
        _trim.ScrollStep = 0.5f; _trim.MaxDecimals = 1;
        _trim.Help = "Level of the processed signal only. A hard-driven, strongly asymmetric setting can push the wet path above full scale; watch the output clip indicators and bring it back here.";
        left.Children.Add(_drive);
        left.Children.Add(_mix);
        left.Children.Add(_trim);
        left.Children.Add(Rule());
        left.Children.Add(BuildOutputs());

        // Character.
        _characterSource = Caption();
        right.Children.Add(Row(SectionLabel("CHARACTER"), _characterSource));
        _bias = Param("Bias", "%", TubeLimits.BiasMinPct, TubeLimits.BiasMaxPct, TubeParam.BiasPct, v => _vm.TubeBiasPct = v, v => Shaper(bias: v));
        _bias.ScrollStep = 1;
        _bias.Help = "Shifts the operating point along the curve. Positive values give the classic warm second harmonic that grows with level; negative values give the same amount with the even products inverted, which only matters when mixed with the dry signal.";
        _asym = Param("Asymmetry", "dB", TubeLimits.AsymMinDb, TubeLimits.AsymMaxDb, TubeParam.AsymDb, v => _vm.TubeAsymDb = v, v => Shaper(asym: v));
        _asym.ScrollStep = 0.5f; _asym.MaxDecimals = 1;
        _asym.Help = "How much later the negative half reaches its knee than the positive half. Adds even-order content at heavy drive. Zero is symmetric, as in a push-pull stage.";
        _hardness = Param("Knee Hardness", "%", TubeLimits.HardnessMinPct, TubeLimits.HardnessMaxPct, TubeParam.HardnessPct, v => _vm.TubeHardnessPct = v, v => Shaper(hardness: v));
        _hardness.ScrollStep = 1;
        _hardness.Help = "Blends from a soft cubic knee (0%) to a harder quintic one (100%). Clean material stays at the same level at every setting; only how abruptly the stage runs out changes.";
        _sag = Param("Sag", "%", TubeLimits.SagMinPct, TubeLimits.SagMaxPct, TubeParam.SagPct, v => _vm.TubeSagPct = v);
        _sag.ScrollStep = 1;
        _sag.Help = "Supply-sag compression: sustained heavy drive pulls the gain down slowly, then recovers. The rectifier below scales the depth and sets the timing.";
        right.Children.Add(_bias);
        right.Children.Add(_asym);
        right.Children.Add(_hardness);
        right.Children.Add(_sag);

        var rect = new StackPanel { Spacing = 6 };
        rect.Children.Add(new TextBlock { Text = "Rectifier", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        _rectifier = new SegmentedPicker(new[] { "Solid state", "GZ34", "5U4", "5Y3" }, height: 24, fontSize: 11);
        _rectifier.Picked += r => _vm.TubeRectifier = r;
        rect.Children.Add(_rectifier);
        _rectifierSummary = Caption();
        rect.Children.Add(_rectifierSummary);
        ToolTipService.SetToolTip(rect, "The power-supply rectifier sets how deep and how slow the sag is. Solid state switches sag off entirely; the valve rectifiers get progressively softer and slower from GZ34 to 5Y3.");
        right.Children.Add(rect);
        right.Children.Add(Rule());

        // Output stage.
        var stageTitles = new StackPanel { Spacing = 2 };
        stageTitles.Children.Add(SectionLabel("OUTPUT STAGE"));
        stageTitles.Children.Add(Caption("A valve amplifier's loose grip on the speaker."));
        _xfmr = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        _xfmr.Toggled += (_, _) => { if (!_updating) _vm.TubeXfmrEnabled = _xfmr.IsOn; };
        var stageHeader = Row(stageTitles, _xfmr);
        ToolTipService.SetToolTip(stageHeader, "A tube amplifier's high source impedance lets the speaker's own impedance curve shape the response: a broad bump at the woofer resonance and a small lift at the top. Nothing here is nonlinear, and the firmware skips the whole stage while it is off.");
        right.Children.Add(stageHeader);
        _xfmrRows = new StackPanel { Spacing = 14 };
        _damping = Param("Damping Factor", "", TubeLimits.XfmrDampingMin, TubeLimits.XfmrDampingMax, TubeParam.XfmrDamping, v => _vm.TubeXfmrDamping = v);
        _damping.ScrollStep = 0.5f; _damping.MaxDecimals = 1; _damping.Ends = ("1 (loose)", "20 (tight)");
        _damping.Help = "The speaker's nominal impedance divided by the amplifier's source impedance. A single-ended triode amplifier without feedback sits around 2 to 3; a push-pull pentode amplifier with feedback around 8 to 15. It sets the size of both the bell and the top lift.";
        _lift = Caption();
        _res = Param("Speaker Resonance", "Hz", TubeLimits.XfmrResMinHz, TubeLimits.XfmrResMaxHz, TubeParam.XfmrResHz, v => _vm.TubeXfmrResHz = v);
        _res.ScrollStep = 1; _res.Ends = ("30 Hz", "150 Hz");
        _res.Help = "Where the loudspeaker resonates in its enclosure, which is where the bell sits. Q is fixed at 0.707, so the bump is broad. 85 Hz suits a typical small to medium woofer; larger drivers sit lower.";
        _xfmrRows.Children.Add(_damping);
        _xfmrRows.Children.Add(_lift);
        _xfmrRows.Children.Add(_res);
        right.Children.Add(_xfmrRows);

        return Columns(left, right);
    }

    /// <summary>The tube picker, grouped by the three kinds of stage the rows
    /// model. Custom keeps the current character and applies no row.</summary>
    private FrameworkElement BuildTypeRow()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Custom", () => _vm.TubeType = TubeLimits.TypeCustom));
        void Group(string title, int from, int to)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            for (int t = from; t <= to; t++)
            {
                int type = t;
                menu.Items.Add(MenuItem(TubeTables.TypeName(t), () => _vm.TubeType = type));
            }
        }
        Group("Preamp triodes", 1, 8);
        Group("Preamp pentodes", 9, 10);
        Group("Power stages", 11, 16);
        _typeButton = new DropDownButton { FontSize = 12, Flyout = menu, Padding = new Thickness(10, 3, 8, 4) };
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(Row(new TextBlock { Text = "Tube", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }, _typeButton));
        _typeCaption = Caption();
        row.Children.Add(_typeCaption);
        ToolTipService.SetToolTip(row, "Loads the bias, asymmetry, knee hardness and sag of a real tube. Drive, mix, the rectifier and the output stage are left alone. Editing any of the four character controls switches this to Custom.");
        return row;
    }

    private FrameworkElement BuildBasic()
    {
        // The tube on show.
        _illustration = new TubeIllustration { Width = 168, Height = 280, HorizontalAlignment = HorizontalAlignment.Center };
        _showcaseName = new TextBlock { FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _showcaseCaption = new TextBlock { FontSize = 10, Foreground = _secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 16, 12, 16) };
        stack.Children.Add(_illustration);
        stack.Children.Add(_showcaseName);
        stack.Children.Add(_showcaseCaption);
        // A warm pool of light behind the glass while the stage is on.
        _warmth = new Border
        {
            CornerRadius = new CornerRadius(10),
            Opacity = (_warmthLit = _vm.TubeEnabled) ? 1 : 0,
            Background = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.7, RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(26, 255, 159, 10), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0, 255, 159, 10), Offset = 1 },
                },
            },
        };
        var showcase = new Grid { Margin = new Thickness(16) };
        showcase.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(ToolWindowChrome.PanelColor),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)), BorderThickness = new Thickness(1),
        });
        showcase.Children.Add(_warmth);
        showcase.Children.Add(stack);

        // The shelf, drive and mix, and where it applies.
        var right = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
        var shelf = new StackPanel { Spacing = 10 };
        shelf.Children.Add(SectionLabel("TUBE"));
        shelf.Children.Add(ShelfGroup("Preamp triodes", 1, 8));
        shelf.Children.Add(ShelfGroup("Preamp pentodes", 9, 10));
        shelf.Children.Add(ShelfGroup("Power stages", 11, 16));
        ToolTipService.SetToolTip(shelf, "Loads the character of a real tube: its bias, asymmetry, knee hardness and sag. Drive, mix and everything in Advanced keep their values.");
        right.Children.Add(shelf);
        right.Children.Add(Rule());
        _drive = Param("Drive", "dB", TubeLimits.DriveMinDb, TubeLimits.DriveMaxDb, TubeParam.DriveDb, v => _vm.TubeDriveDb = v);
        _drive.ScrollStep = 0.5f; _drive.MaxDecimals = 1; _drive.Ends = ("Clean", "Overdrive");
        _drive.Help = "How hard the tube is driven. Drive moves the knee, not the level: at the -12 dB default the knee sits 12 dB above full scale and the colour is subtle, the -30 dB floor is close to transparent, and the top of the range is overdrive.";
        _mix = Param("Mix", "%", TubeLimits.MixMinPct, TubeLimits.MixMaxPct, TubeParam.MixPct, v => _vm.TubeMixPct = v);
        _mix.ScrollStep = 1; _mix.Ends = ("Dry", "All tube");
        _mix.Help = "Blends the tube with the untouched signal. Below 100% the original transients stay intact under the colour, which is the easiest way to use heavy drive subtly.";
        right.Children.Add(_drive);
        right.Children.Add(_mix);
        right.Children.Add(Rule());
        right.Children.Add(BuildOutputs());

        return Columns(showcase, right);
    }

    private FrameworkElement ShelfGroup(string title, int from, int to)
    {
        var group = new StackPanel { Spacing = 5 };
        group.Children.Add(Caption(title));
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int count = to - from + 1;
        for (int r = 0; r < (count + 3) / 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int t = from; t <= to; t++)
        {
            int type = t, i = t - from;
            var row = TubeTables.Types[t];
            var chip = new Button
            {
                Content = row?.ShortName ?? TubeTables.TypeName(t),
                FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(2, 0, 2, 0),
            };
            ToolTipService.SetToolTip(chip, row != null ? $"{row.Name}: {row.Style}" : TubeTables.TypeName(t));
            chip.Click += (_, _) => _vm.TubeType = type;
            Grid.SetColumn(chip, i % 4);
            Grid.SetRow(chip, i / 4);
            grid.Children.Add(chip);
            _shelf.Add((type, chip));
        }
        group.Children.Add(grid);
        return group;
    }

    private FrameworkElement BuildOutputs()
    {
        var section = new StackPanel { Spacing = 10 };
        int all = _vm.NumOutputChannels >= 16 ? 0xFFFF : (1 << _vm.NumOutputChannels) - 1;
        var maskMenu = new MenuFlyout();
        maskMenu.Items.Add(MenuItem("All outputs", () => _vm.TubeOutputMask = all));
        // Colour on a sub feed is mostly wasted: the crossover removes it again.
        maskMenu.Items.Add(MenuItem("Exclude sub", () => _vm.TubeOutputMask = all & ~(1 << _vm.PdmOutputIndex)));
        maskMenu.Items.Add(MenuItem("None", () => _vm.TubeOutputMask = 0));
        _maskPresets = MenuButton("Presets", maskMenu);
        section.Children.Add(Row(SectionLabel("OUTPUTS"), _maskPresets));
        var chips = new Grid { ColumnSpacing = 6 };
        ToolTipService.SetToolTip(chips, "Tube runs before the crossover and the per-output EQ, where a real preamp sits: a sub output saturates the full-band program and then low-passes the result.");
        var outputs = _vm.ActiveOutputs;
        for (int o = 0; o < _vm.NumOutputChannels; o++)
        {
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int output = o;
            var chip = new ToggleButton
            {
                Content = (o + 1).ToString(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 26, Padding = new Thickness(0),
            };
            ToolTipService.SetToolTip(chip, o < outputs.Count ? _vm.GetChannelName(outputs[o]) : $"Out {o + 1}");
            chip.Click += (_, _) => _vm.SetTubeOutputChannel(output, chip.IsChecked == true);
            Grid.SetColumn(chip, o);
            chips.Children.Add(chip);
            _outputChips.Add(chip);
        }
        section.Children.Add(chips);
        return section;
    }

    private void ApplyStartingPoint(StartingPoint p)
    {
        _vm.TubeType = p.Type;
        _vm.TubeDriveDb = p.DriveDb;
        _vm.TubeRectifier = p.Rectifier;
        _vm.TubeXfmrDamping = p.Damping;
        _vm.TubeXfmrResHz = p.ResHz;
        _vm.TubeXfmrEnabled = p.Xfmr;
        _vm.TubeMixPct = TubeLimits.MixMaxPct;
        _vm.TubeTrimDb = 0;
    }

    // ── Sync with the view model ──

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? name = e.PropertyName;
        if (name == nameof(MainViewModel.Status))
        {
            DispatcherQueue.TryEnqueue(UpdateBloom);
            return;
        }
        if (name == null || !(name.StartsWith("Tube") || name == nameof(MainViewModel.IsDeviceConnected))) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (name == nameof(MainViewModel.TubeSupported)) BuildBody();
            else Refresh();
        });
    }

    private void UpdateHarmonics()
    {
        if (_graph == null || _second == null || _third == null) return;
        var (second, third) = _graph.Shown.Harmonics();
        static string Db(double v) => v <= -100 ? "none" : $"{v:0} dB";
        _second.Text = Db(second);
        _third.Text = Db(third);
        _second.Foreground = second > -100 ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : _secondary;
        _third.Foreground = third > -100 ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : _secondary;
    }

    private void Refresh()
    {
        bool connected = _vm.IsDeviceConnected, supported = _vm.TubeSupported, enabled = _vm.TubeEnabled;
        _updating = true;
        _enable.IsOn = enabled;
        _enable.IsEnabled = connected && supported;
        _updating = false;
        _icon.Lit = enabled;
        _mode.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        _mode.Selected = _advanced ? 1 : 0;
        if (!supported) return;

        int type = _vm.TubeType;
        var row = type > 0 && type < TubeTables.Types.Count ? TubeTables.Types[type] : null;

        foreach (var r in new[] { _drive, _mix, _trim, _bias, _asym, _hardness, _sag, _damping, _res })
            if (r != null) r.IsEnabled = connected;
        if (_drive != null) _drive.Value = _vm.TubeDriveDb;
        if (_mix != null) _mix.Value = _vm.TubeMixPct;
        if (_trim != null) _trim.Value = _vm.TubeTrimDb;
        if (_bias != null) _bias.Value = _vm.TubeBiasPct;
        if (_asym != null) _asym.Value = _vm.TubeAsymDb;
        if (_hardness != null) _hardness.Value = _vm.TubeHardnessPct;
        if (_sag != null)
        {
            _sag.Value = _vm.TubeSagPct;
            _sag.Opacity = _vm.TubeRectifier == TubeLimits.RectifierSolidState ? 0.5 : 1;
        }
        if (_damping != null) _damping.Value = _vm.TubeXfmrDamping;
        if (_res != null) _res.Value = _vm.TubeXfmrResHz;
        foreach (var b in new[] { _typeButton, _presets, _maskPresets }) if (b != null) b.IsEnabled = connected;

        if (_graph != null)
        {
            _graph.SetShaper(Shaper(), enabled);
            UpdateHarmonics();
        }
        if (_typeButton != null) _typeButton.Content = TubeTables.TypeName(type);
        if (_typeCaption != null)
            _typeCaption.Text = row == null ? "Character controls as set, no tube row applied."
                : row.PushPull && !_vm.TubeXfmrEnabled ? $"{row.Style}. Meant for use with the output stage on." : $"{row.Style}.";
        if (_characterSource != null) _characterSource.Text = row != null ? $"from {row.Name}" : "custom";
        if (_rectifier != null)
        {
            int r = _vm.TubeRectifier;
            _rectifier.Selected = r;
            _rectifier.IsEnabled = connected;
            _rectifierSummary!.Text = r > TubeLimits.RectifierSolidState && r < TubeTables.Rectifiers.Count
                ? $"Sag depth x{TubeTables.Rectifiers[r].DepthScale:0.0}, {TubeTables.Rectifiers[r].AttackMs:0} ms attack, {TubeTables.Rectifiers[r].ReleaseMs:0} ms release."
                : "No sag: the supply holds up however hard the stage is driven.";
        }
        if (_xfmr != null)
        {
            _updating = true;
            _xfmr.IsOn = _vm.TubeXfmrEnabled;
            _xfmr.IsEnabled = connected;
            _updating = false;
            // The firmware skips these stages while the output stage is off.
            _xfmrRows!.Visibility = _vm.TubeXfmrEnabled ? Visibility.Visible : Visibility.Collapsed;
            // A source impedance of Zn/df against a speaker rising to 4x nominal
            // at resonance and 2x at the top lifts the response by these.
            float df = Math.Max(_vm.TubeXfmrDamping, TubeLimits.XfmrDampingMin);
            double bell = 20 * Math.Log10(4 * (df + 1) / (4 * df + 1)), top = 20 * Math.Log10(2 * (df + 1) / (2 * df + 1));
            _lift!.Text = $"+{bell:0.0} dB at resonance, +{top:0.0} dB at the top.";
        }

        foreach (var (t, chip) in _shelf)
        {
            bool on = t == type;
            chip.Background = on ? new SolidColorBrush(Accent) : (Brush)Application.Current.Resources["ButtonBackground"];
            chip.Foreground = on ? new SolidColorBrush(Colors.White) : (Brush)Application.Current.Resources["ButtonForeground"];
            chip.IsEnabled = connected;
        }
        if (_illustration != null)
        {
            _illustration.Family = TubeFamilies.Of(type);
            _illustration.Lit = enabled;
            _showcaseName!.Text = row?.Name ?? "Custom";
            _showcaseCaption!.Text = row == null ? "Character set by hand. Pick a tube to load one, or fine-tune it in Advanced."
                : row.PushPull && !_vm.TubeXfmrEnabled ? $"{row.Style}. Meant for use with the output stage, in Advanced." : $"{row.Style}.";
            // Only on a change: every tube property refreshes, and a restarted
            // fade would stall the one in progress.
            if (_warmth != null && enabled != _warmthLit)
            {
                _warmthLit = enabled;
                TubeIcon.Fade(_warmth, enabled ? 1 : 0, TimeSpan.FromSeconds(enabled ? 0.9 : 0.6));
            }
        }

        int mask = _vm.TubeOutputMask;
        for (int o = 0; o < _outputChips.Count; o++)
        {
            _outputChips[o].IsChecked = (mask & (1 << o)) != 0;
            _outputChips[o].IsEnabled = connected;
        }
    }

    /// <summary>The bloom follows the loudest output the stage processes, from
    /// the status stream the main window already polls: no extra requests.</summary>
    private void UpdateBloom()
    {
        if (_illustration == null) return;
        int mask = _vm.TubeOutputMask;
        if (!_vm.TubeEnabled || !_vm.IsDeviceConnected || mask == 0 || _vm.Status is not { } status)
        {
            _illustration.Bloom = 0;
            return;
        }
        var outputs = _vm.ActiveOutputs;
        float peak = 0;
        for (int o = 0; o < Math.Min(outputs.Count, 16); o++)
        {
            if ((mask & (1 << o)) == 0) continue;
            int id = (int)outputs[o].Id;
            if (id < status.Peaks.Length && float.IsFinite(status.Peaks[id])) peak = Math.Max(peak, status.Peaks[id]);
        }
        _illustration.Bloom = Math.Sqrt(Math.Min(peak, 1));
    }
}
