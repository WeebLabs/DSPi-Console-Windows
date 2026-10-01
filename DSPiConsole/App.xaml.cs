using Microsoft.UI.Xaml;
using Microsoft.UI.Composition.SystemBackdrops;
using WinRT;

namespace DSPiConsole;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSPI_SUBHARM_HARNESS") != null)
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dspi-harness-exceptions.log");
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try { System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {e.Exception}\n{Environment.StackTrace}\n\n"); } catch { }
            };
            _window = new Debugging.SubharmHarness();
            _window.Activate();
            return;
        }
#endif
        _window = new MainWindow();
        _window.Activate();
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSPI_EDITOR_HARNESS") != null)
            new Debugging.GraphEditorHarness().Activate();
#endif
    }
}
