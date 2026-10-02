using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// A compact horizontal row of numbered toggle "chips" for editing a bitmask.
/// Each chip maps to a specific bit index (not necessarily contiguous — e.g.
/// only the enabled outputs), shows that channel's 1-based number, and surfaces
/// its real name in the tooltip. A checked chip is filled (the default
/// ToggleButton accent state). Used by the multichannel DSP mask selectors
/// (leveller / loudness / crossfeed).
///
/// The grid never owns the mask — it reports single-bit toggles via the
/// <c>onToggle(bit, isOn)</c> callback and re-renders from an authoritative
/// mask via <see cref="SetMask"/>, so the ViewModel stays the single source of
/// truth.
///
/// A caller that passes <c>onSecondaryToggle</c> also gets a second, independent
/// per-chip state driven by right-click (the siggen polarity-invert mask): a chip
/// in that state fills amber instead of the accent colour. The secondary state is
/// only meaningful on a checked chip, so right-clicking an unchecked one is ignored.
/// </summary>
public sealed class MaskChipGrid
{
    /// <summary>Fill for a chip whose secondary state is set. Matches the amber used
    /// for pot controls in the Control Surfaces window.</summary>
    private static readonly Color Amber = Color.FromArgb(255, 0xF0, 0xC4, 0x59);
    private static readonly Color AmberPointerOver = Color.FromArgb(255, 0xFA, 0xD1, 0x72);
    private static readonly Color AmberPressed = Color.FromArgb(255, 0xD8, 0xAE, 0x45);
    /// <summary>Amber is a light fill, so checked text flips to near-black on it.</summary>
    private static readonly Color AmberText = Color.FromArgb(255, 0x1B, 0x1B, 0x1B);

    /// <summary>A chip plus the brushes its checked visual states resolve to. The
    /// brush instances are installed in the chip's own Resources before the template
    /// expands, so recolouring them in place repaints the chip live.</summary>
    private sealed class Chip
    {
        public ToggleButton Button = null!;
        public int Bit;
        public SolidColorBrush? Fill, FillPointerOver, FillPressed, Text;
    }

    private readonly List<Chip> _chips = new();
    private readonly Action<int, bool> _onToggle;
    private bool _suppress;

    /// <summary>The visual root to place in the window.</summary>
    public Panel Root { get; }

    /// <summary>Contiguous bit indices 0..n-1 — the common "one chip per channel" case.</summary>
    public static IReadOnlyList<int> AllBits(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <param name="bitIndices">The bit each chip controls, in display order. Chip
    /// caption is the 1-based bit number, so a filtered set (e.g. enabled outputs)
    /// still shows real channel numbers.</param>
    /// <param name="tooltipForBit">Tooltip text for a given bit index.</param>
    /// <param name="onToggle">Called with the bit index and its new state on user toggle.</param>
    /// <param name="stretch">When true, chips share the container width equally (each
    /// in a star-sized column) so the row fills the space rather than leaving a gap on
    /// the right. When false, chips are a fixed compact width and left-packed.</param>
    /// <param name="captionForBit">Optional override for a chip's caption; return null
    /// to use the default 1-based number (e.g. return "S" for the sub output).</param>
    /// <param name="onSecondaryToggle">Optional right-click handler, called with the bit
    /// index. Enables the amber secondary state; drive it back via
    /// <see cref="SetSecondaryMask"/>. Right-clicks on unchecked chips are swallowed.</param>
    public MaskChipGrid(IReadOnlyList<int> bitIndices, Func<int, string> tooltipForBit,
                        Action<int, bool> onToggle, bool stretch = false,
                        Func<int, string?>? captionForBit = null,
                        Action<int>? onSecondaryToggle = null)
    {
        _onToggle = onToggle;
        int count = bitIndices.Count;

        Grid? grid = null;
        StackPanel? stack = null;
        if (stretch)
        {
            grid = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        }

        for (int j = 0; j < count; j++)
        {
            int bit = bitIndices[j];
            var chip = new ToggleButton
            {
                Content = captionForBit?.Invoke(bit) ?? (bit + 1).ToString(),
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 12
            };
            if (stretch)
            {
                // Fill the column; MinWidth 0 lets many chips shrink to fit the
                // row instead of overflowing (and clipping the last chip).
                chip.HorizontalAlignment = HorizontalAlignment.Stretch;
                chip.MinWidth = 0;
                Grid.SetColumn(chip, j);
                grid!.Children.Add(chip);
            }
            else
            {
                chip.MinWidth = 34;
                chip.Width = 34;
                stack!.Children.Add(chip);
            }
            ToolTipService.SetToolTip(chip, tooltipForBit(bit));
            chip.Checked += (_, _) => { if (!_suppress) _onToggle(bit, true); };
            chip.Unchecked += (_, _) => { if (!_suppress) _onToggle(bit, false); };

            var entry = new Chip { Button = chip, Bit = bit };

            // The darker accent with white text, as the console's other toggles.
            AccentToggle.Apply(chip);
            if (onSecondaryToggle != null)
            {
                // Own the brushes the checked states resolve to, so the fill can be
                // switched between accent and amber later by recolouring in place.
                entry.Fill = new SolidColorBrush(AccentToggle.Fill);
                entry.FillPointerOver = new SolidColorBrush(AccentToggle.FillPointerOver);
                entry.FillPressed = new SolidColorBrush(AccentToggle.FillPressed);
                entry.Text = new SolidColorBrush(AccentToggle.Text);

                chip.Resources["ToggleButtonBackgroundChecked"] = entry.Fill;
                chip.Resources["ToggleButtonBackgroundCheckedPointerOver"] = entry.FillPointerOver;
                chip.Resources["ToggleButtonBackgroundCheckedPressed"] = entry.FillPressed;
                chip.Resources["ToggleButtonForegroundChecked"] = entry.Text;
                chip.Resources["ToggleButtonForegroundCheckedPointerOver"] = entry.Text;
                chip.Resources["ToggleButtonForegroundCheckedPressed"] = entry.Text;

                chip.RightTapped += (_, e) =>
                {
                    e.Handled = true;
                    if (chip.IsChecked == true) onSecondaryToggle(bit);
                };
            }

            _chips.Add(entry);
        }

        Root = (Panel?)grid ?? stack!;
    }

    /// <summary>Reflect an authoritative mask into the chip states without firing callbacks.</summary>
    public void SetMask(uint mask)
    {
        _suppress = true;
        foreach (var c in _chips)
            c.Button.IsChecked = (mask & (1u << c.Bit)) != 0;
        _suppress = false;
    }

    /// <summary>Reflect the secondary (amber) mask. No-op for grids built without an
    /// <c>onSecondaryToggle</c> handler.</summary>
    public void SetSecondaryMask(uint mask)
    {
        foreach (var c in _chips)
        {
            if (c.Fill == null) continue;
            bool on = (mask & (1u << c.Bit)) != 0;
            c.Fill.Color = on ? Amber : AccentToggle.Fill;
            c.FillPointerOver!.Color = on ? AmberPointerOver : AccentToggle.FillPointerOver;
            c.FillPressed!.Color = on ? AmberPressed : AccentToggle.FillPressed;
            c.Text!.Color = on ? AmberText : AccentToggle.Text;
        }
    }
}
