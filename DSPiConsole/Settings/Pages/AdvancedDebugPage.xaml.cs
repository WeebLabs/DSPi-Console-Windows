using DSPiConsole.Models;
using DSPiConsole.Services;
using DSPiConsole.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DSPiConsole.Settings.Pages;

/// <summary>
/// Advanced › Debug — single toggle for the in-app debug overlay.
/// Persists to <see cref="AppSettings.ShowDebugInfo"/>.
///
/// <para>
/// Apply cadence: <b>Live</b>. Each toggle change writes the JSON file
/// and fires <see cref="AppSettings.NotifyChanged"/> so the rest of the
/// app picks up the new value without needing a Save click.
/// </para>
///
/// <para>
/// One class, two roles: this type is both the UserControl that renders
/// the page <i>and</i> the <see cref="ISettingsPage"/> descriptor the
/// shell queries for metadata. <c>BuildContent</c> creates a fresh
/// instance so the registry's template is never displayed — that keeps
/// the descriptor stateless while letting page state live on the
/// instance that's actually attached to the visual tree.
/// </para>
/// </summary>
public sealed partial class AdvancedDebugPage : SettingsModule, ISettingsPage
{
    private bool _suppress;

    private readonly TextBlock _onboardingStatus = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    /// <summary>Shown only with the debug overlay on, as on the macOS Console:
    /// a reset takes a new user's wizard away on the spot.</summary>
    private readonly StackPanel _onboardingSection = new() { Spacing = 4 };

    public AdvancedDebugPage()
    {
        InitializeComponent();
        AddOnboardingSection();
    }

    /// <summary>
    /// Puts onboarding into any state on demand, after the macOS Console's
    /// developer section: everything it does depends on state that takes weeks
    /// of real use to reach. Each one rewrites the stored state and applies on
    /// the next launch, the only honest way to test something that happens at
    /// launch.
    /// </summary>
    private void AddOnboardingSection()
    {
        Cards.Children.Add(_onboardingSection);
        _onboardingSection.Visibility = AppSettings.Instance.ShowDebugInfo ? Visibility.Visible : Visibility.Collapsed;
        _onboardingSection.Children.Add(new TextBlock
        {
            Text = "Onboarding (Developer)", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(1, 24, 0, 6),
        });
        _onboardingSection.Children.Add(Row("Start as a new user", "Forgets everything. The next launch runs setup from the top.",
            "Reset Onboarding", () =>
            {
                Onboarding.Coordinator.ResetAll();
                AppSettings.Instance.WhatsNewLastShownVersion = null;
                AppSettings.Instance.Save();
                return "Reset. Relaunch to see a first run.";
            }));
        _onboardingSection.Children.Add(Row("Simulate an existing user",
            "Prior use with no onboarding state: the upgrade-day case, where nothing runs and the setup steps count as seen.",
            "As Existing", () =>
            {
                AppSettings.Instance.OnboardingCohortOverride = "existing";
                AppSettings.Instance.Save();
                return "Relaunch to arrive as an existing user.";
            }));
        _onboardingSection.Children.Add(Row("Simulate an upgrade",
            "Marks everything up to 1.1.6 beta 3 as seen, so only later steps are offered.",
            "As Updater", () =>
            {
                AppSettings.Instance.OnboardingCohortOverride = "updater:1.1.6-beta3";
                AppSettings.Instance.Save();
                return "Relaunch to arrive as an updater from 1.1.6 beta 3.";
            }));
        _onboardingSection.Children.Add(Row("Show the release notes again",
            "Treats this version's notes as unread, so they open on the next launch.",
            "Unread Notes", () =>
            {
                AppSettings.Instance.WhatsNewLastShownVersion = "0.0.1";
                AppSettings.Instance.Save();
                return "Relaunch to see What's New.";
            }));
        _onboardingStatus.Margin = new Thickness(1, 6, 0, 0);
        _onboardingStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _onboardingSection.Children.Add(_onboardingStatus);
        ShowOnboardingStatus(null);
    }

    private FrameworkElement Row(string title, string detail, string button, Func<string> act)
    {
        var action = new Button { Content = button, Width = 150 };
        action.Click += (_, _) => ShowOnboardingStatus(act());
        return new CommunityToolkit.WinUI.Controls.SettingsCard { Header = title, Description = detail, Content = action };
    }

    private void ShowOnboardingStatus(string? note)
    {
        var c = Onboarding.Coordinator;
        int pending = c.Pending.Count;
        _onboardingStatus.Text = $"Current cohort: {c.Cohort}. {pending} step{(pending == 1 ? "" : "s")} pending."
            + (note != null ? Environment.NewLine + note : "");
    }

    private void OnResetChannelNamesClick(object sender, RoutedEventArgs e) => Vm?.ResetChannelNames();

    protected override void Refresh()
    {
        ResetNamesButton.IsEnabled = Vm?.IsDeviceConnected == true;
        _suppress = true;
        try { DebugToggle.IsOn = AppSettings.Instance.ShowDebugInfo; }
        finally { _suppress = false; }
        _onboardingSection.Visibility = AppSettings.Instance.ShowDebugInfo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDebugToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var s = AppSettings.Instance;
        s.ShowDebugInfo = DebugToggle.IsOn;
        _onboardingSection.Visibility = s.ShowDebugInfo ? Visibility.Visible : Visibility.Collapsed;
        s.Save();
        s.NotifyChanged();
    }

    // ── ISettingsPage ──────────────────────────────────────────────────
    public string Id => "advanced.debug";
    public string Title => "Debug";
    public SettingsCategory Category => SettingsCategory.Advanced;
    public string IconGlyph => ""; // Bug
    public int Order => 10;
    public bool IsAvailable(MainViewModel vm) => true;
    public UIElement BuildContent(MainViewModel vm, IPendingChangeTracker tracker)
    {
        var instance = new AdvancedDebugPage();
        instance.Attach(vm, tracker);
        return instance;
    }
}
