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
        _window = new MainWindow();
        _window.Activate();
#if DEBUG
        if (Environment.GetEnvironmentVariable("DSPI_EDITOR_HARNESS") != null)
            new Debugging.GraphEditorHarness().Activate();
#endif
    }
}
