using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
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

    /// <summary>Sizes and dresses the window. With <paramref name="fitContent"/>,
    /// the window also grows on opening, once its content is laid out, until
    /// all of it shows: the sizes given are outer sizes, and the title bar and
    /// text wrapping leave a body slightly taller than they allow. It never
    /// shrinks, and stays within the screen's work area.</summary>
    public static void Apply(Window window, string title, int width, int height, bool fitContent = false)
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

        if (fitContent)
        {
            // The content is set after this call, so wait for the first
            // activation to find it, then for its first layout.
            void OnActivated(object sender, WindowActivatedEventArgs e)
            {
                window.Activated -= OnActivated;
                if (window.Content is not FrameworkElement root) return;
                if (root.IsLoaded) FitHeight(appWindow, root);
                else
                {
                    void OnLoaded(object s, RoutedEventArgs a)
                    {
                        root.Loaded -= OnLoaded;
                        FitHeight(appWindow, root);
                    }
                    root.Loaded += OnLoaded;
                }
            }
            window.Activated += OnActivated;
        }
    }

    /// <summary>Grows the client area by however much taller the content would
    /// be with unlimited height (a scrolling body then reports its whole
    /// extent), keeping the window on screen.</summary>
    private static void FitHeight(AppWindow appWindow, FrameworkElement root)
    {
        if (root.ActualWidth <= 0 || root.XamlRoot is not { } xamlRoot) return;
        root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
        double overflow = root.DesiredSize.Height - root.ActualHeight;
        root.InvalidateMeasure();
        if (overflow < 1) return;

        var client = appWindow.ClientSize;
        int frame = appWindow.Size.Height - client.Height;
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int height = Math.Min(client.Height + (int)Math.Ceiling(overflow * xamlRoot.RasterizationScale), work.Height - frame);
        if (height <= client.Height) return;
        appWindow.ResizeClient(new SizeInt32(client.Width, height));

        var pos = appWindow.Position;
        int overBottom = pos.Y + appWindow.Size.Height - (work.Y + work.Height);
        if (overBottom > 0)
            appWindow.Move(new PointInt32(pos.X, Math.Max(work.Y, pos.Y - overBottom)));
    }
}
