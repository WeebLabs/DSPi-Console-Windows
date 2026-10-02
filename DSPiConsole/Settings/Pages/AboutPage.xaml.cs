using System.Reflection;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// About — read-only metadata about the app and the connected device.
/// Lives in the NavigationView's FooterMenuItems (pinned to the bottom
/// of the sidebar) thanks to <c>SettingsCategory.About</c>.
///
/// <para>
/// App version reads from the entry-assembly informational-version
/// attribute (set by the .csproj or Git tag at publish time). Platform
/// and firmware come from <see cref="MainViewModel"/> properties that
/// are populated at connect time.
/// </para>
/// </summary>
public sealed partial class AboutPage : SettingsModule, ISettingsPage
{
    public AboutPage() { InitializeComponent(); }

    private async void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    protected override void Refresh()
    {
        // App version — prefer InformationalVersion (allows "1.2.3+sha"),
        // fall back to FileVersion or "dev" if neither is set.
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                   ?? "dev";
        AppVersionText.Text = version;

        // Platform string + firmware label come from the VM. They may be
        // empty before a device is connected — render a hyphen instead
        // of leaving the row blank.
        PlatformText.Text = string.IsNullOrEmpty(Vm?.Platform) ? "—" : Vm.Platform;
        FirmwareText.Text = BuildFirmwareLabel();
    }

    /// <summary>Compose a short device summary. Currently just the platform
    /// string; future enhancements can include firmware version + serial
    /// when MainViewModel surfaces them.</summary>
    private string BuildFirmwareLabel()
    {
        if (Vm == null) return "—";
        if (string.IsNullOrEmpty(Vm.Platform)) return "Not connected";
        return Vm.Platform;
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "about";
    public string Title => "About";
    public SettingsCategory Category => SettingsCategory.About;
    public string IconGlyph => ""; // Info
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var p = new AboutPage();
        p.Attach(vm, tracker);
        return p;
    }
}
