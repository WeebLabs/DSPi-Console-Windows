using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole.Controls;

/// <summary>The size, title and dark title bar every tool window opens with,
/// as the Loudness and Psychoacoustic Bass windows set them.</summary>
public static class ToolWindowChrome
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>The fill of a graph or showcase panel: a little darker than the
    /// window, as the macOS Console's control background at 60 % reads in dark
    /// mode.</summary>
    public static readonly Color PanelColor = Color.FromArgb(153, 22, 22, 24);

    public static void Apply(Window window, string title, int width, int height)
    {
        var hWnd = WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hWnd));
        if (appWindow == null) return;
        double scale = GetDpiForWindow(hWnd) / 96.0;
        appWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
        appWindow.Title = title;
        var bar = appWindow.TitleBar;
        bar.ForegroundColor = Color.FromArgb(255, 220, 220, 220);
        bar.BackgroundColor = Color.FromArgb(255, 32, 32, 32);
        bar.InactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
        bar.InactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
        bar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
        bar.ButtonBackgroundColor = Color.FromArgb(255, 32, 32, 32);
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
        bar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
        bar.ButtonHoverForegroundColor = Colors.White;
        bar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
    }
}
