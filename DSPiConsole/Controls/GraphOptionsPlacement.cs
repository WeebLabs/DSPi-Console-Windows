using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace DSPiConsole.Controls;

/// <summary>
/// Where the graph options open: centred below the gear. Near a screen edge,
/// where half the panel would not fit on that side, it aligns with the gear's
/// edge instead and extends the other way, so it always stays on screen.
/// </summary>
public static class GraphOptionsPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    /// <summary>The panel's width plus its presenter border and a small margin.</summary>
    private const double NeededWidth = 280 + 2 + 6;

    public static void ShowAt(Flyout flyout, FrameworkElement anchor, Window window)
    {
        // Lets the popup leave the window's bounds, which centring under a
        // gear near the window's edge needs.
        flyout.ShouldConstrainToRootBounds = false;
        // Translucent, as the macOS Console's popover is: desktop acrylic
        // behind a presenter with no fill of its own (GraphOptionsPresenterStyle).
        flyout.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        flyout.Placement = Placement(anchor, window);
        flyout.ShowAt(anchor);
    }

    private static FlyoutPlacementMode Placement(FrameworkElement anchor, Window window)
    {
        try
        {
            if (anchor.XamlRoot is not { } root) return FlyoutPlacementMode.Bottom;
            double scale = root.RasterizationScale;
            var hwnd = WindowNative.GetWindowHandle(window);
            var origin = new POINT();
            if (!ClientToScreen(hwnd, ref origin)) return FlyoutPlacementMode.Bottom;
            var topLeft = anchor.TransformToVisual(null).TransformPoint(new Point(0, 0));
            double left = origin.X + topLeft.X * scale, right = left + anchor.ActualWidth * scale;
            double centre = (left + right) / 2, half = NeededWidth * scale / 2;
            int top = origin.Y + (int)Math.Round(topLeft.Y * scale);
            var area = DisplayArea.GetFromPoint(new PointInt32((int)centre, top), DisplayAreaFallback.Nearest).WorkArea;
            if (centre + half > area.X + area.Width) return FlyoutPlacementMode.BottomEdgeAlignedRight;
            if (centre - half < area.X) return FlyoutPlacementMode.BottomEdgeAlignedLeft;
            return FlyoutPlacementMode.Bottom;
        }
        catch
        {
            return FlyoutPlacementMode.Bottom;
        }
    }
}
