using DSPiConsole.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DSPiConsole;

/// <summary>
/// Onboarding in the main window: the Getting Started wizard takes the
/// console's place for a new user (or when asked for from Help), crossfading
/// with it, and release notes appear once after an update. The console itself
/// knows nothing of either. After the macOS Console's MainWindowRoot.
/// </summary>
public sealed partial class MainWindow
{
    private GettingStartedView? _gettingStarted;
    private WhatsNewWindow? _whatsNewWindow;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(350);

    private void InitializeOnboarding()
    {
        var coordinator = Onboarding.Coordinator;
        coordinator.Evaluate(Onboarding.Device(ViewModel));
        coordinator.Changed += () => DispatcherQueue.TryEnqueue(() => UpdateOnboardingHost(animate: true));
        UpdateOnboardingHost(animate: false);

        // Release notes are not onboarding: shown after an update, never on a
        // first run. After the window is up, so they open in front of it.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var unread = Onboarding.TakeUnreadReleaseNotes();
            if (unread.Count > 0) ShowWhatsNew();
        });
    }

    private bool WizardShown => _gettingStarted != null;

    private void UpdateOnboardingHost(bool animate)
    {
        bool show = Onboarding.Coordinator.ShouldTakeOverMainWindow();
        if (show == WizardShown) return;
        if (show)
        {
            _gettingStarted = new GettingStartedView(ViewModel, Onboarding.Coordinator.FinishSetup);
            OnboardingHost.Content = _gettingStarted;
            OnboardingHost.Visibility = Visibility.Visible;
            Fade(OnboardingHost, 0, 1, animate, null);
            Fade(RootGrid, 1, 0, animate, () => { if (WizardShown) RootGrid.Visibility = Visibility.Collapsed; });
        }
        else
        {
            var leaving = _gettingStarted!;
            _gettingStarted = null;
            leaving.Shutdown();
            RootGrid.Visibility = Visibility.Visible;
            Fade(RootGrid, 0, 1, animate, null);
            Fade(OnboardingHost, 1, 0, animate, () =>
            {
                if (WizardShown) return;
                OnboardingHost.Visibility = Visibility.Collapsed;
                OnboardingHost.Content = null;
            });
        }
        ApplyWizardMenuState();
    }

    /// <summary>Both are on screen for the length of the fade, so one never
    /// vanishes before the other arrives.</summary>
    private static void Fade(UIElement element, double from, double to, bool animate, Action? done)
    {
        if (!animate)
        {
            element.Opacity = to;
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation { From = from, To = to, Duration = Crossfade, EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var board = new Storyboard();
        board.Children.Add(animation);
        board.Completed += (_, _) => { element.Opacity = to; done?.Invoke(); };
        board.Begin();
    }

    /// <summary>While the wizard has the window, every menu item but Help,
    /// Settings and Exit is disabled, accelerators included: the console they
    /// act on is not on screen.</summary>
    private void ApplyWizardMenuState()
    {
        if (!WizardShown && !_wizardLockedMenu) return;
        // Submenus too: a disabled submenu does not disable its items'
        // shortcuts (Ctrl+I imports filters from anywhere). Items the menu
        // gates by device state are set again each time it opens.
        void Apply(IList<MenuFlyoutItemBase> items)
        {
            foreach (var item in items)
            {
                if (item == HelpMenu || item == SettingsMenuItem || item == ExitMenuItem || item is MenuFlyoutSeparator) continue;
                item.IsEnabled = !WizardShown;
                if (item is MenuFlyoutSubItem sub) Apply(sub.Items);
            }
        }
        Apply(MainMenu.Items);
        _wizardLockedMenu = WizardShown;
    }

    private bool _wizardLockedMenu;

    // ── Help ──

    private void OnGettingStartedClick(object sender, RoutedEventArgs e) => Onboarding.Coordinator.RequestSetup();

    private void OnWhatsNewClick(object sender, RoutedEventArgs e) => ShowWhatsNew();

    private void ShowWhatsNew()
    {
        if (_whatsNewWindow != null)
        {
            _whatsNewWindow.Activate();
            return;
        }
        _whatsNewWindow = new WhatsNewWindow();
        _whatsNewWindow.Closed += (_, _) => _whatsNewWindow = null;
        _whatsNewWindow.Activate();
    }

    private async void OnConsoleGitHubClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl(AppInfo.ConsoleReleasesUrl)));

    private async void OnFirmwareGitHubClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(RepoUrl(AppInfo.FirmwareReleasesUrl)));

    private static string RepoUrl(string releases) =>
        releases.EndsWith("/releases", StringComparison.Ordinal) ? releases[..^"/releases".Length] : releases;
}
