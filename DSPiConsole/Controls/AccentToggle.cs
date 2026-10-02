using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// The checked look shared by the console's toggles (Link Pair, the PEQ / XO
/// tabs, the mask chips): the system accent's darker shade with white text.
/// The stock checked fill is the accent's pale tint, which in dark mode only
/// reads with near-black text; the darker shade is calmer and keeps white text
/// legible.
/// </summary>
internal static class AccentToggle
{
    public static Color Fill => Accent("SystemAccentColorDark1");
    public static Color FillPointerOver => Accent("SystemAccentColor");
    public static Color FillPressed => Accent("SystemAccentColorDark2");
    public static Color Text => Colors.White;

    public static void Apply(ToggleButton button)
    {
        button.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(Fill);
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(FillPointerOver);
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(FillPressed);
        var text = new SolidColorBrush(Text);
        button.Resources["ToggleButtonForegroundChecked"] = text;
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = text;
        button.Resources["ToggleButtonForegroundCheckedPressed"] = text;
    }

    private static Color Accent(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : Color.FromArgb(255, 0, 90, 158);
}
