using DSPiConsole.Core.Onboarding;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;

namespace DSPiConsole.Services;

/// <summary>
/// The one onboarding coordinator the app runs on, its persistence in
/// <see cref="AppSettings"/>, and the release notes. Shared because tool windows
/// read and write the same completed set as the main window, as on the macOS
/// Console (OnboardingCoordinator.shared).
/// </summary>
public static class Onboarding
{
    private static OnboardingCoordinator? _coordinator;

    public static OnboardingCoordinator Coordinator =>
        _coordinator ??= new OnboardingCoordinator(new Store(AppSettings.Instance), AppInfo.ExpectedFirmware);

    public static IOnboardingDevice Device(MainViewModel vm) => new VmDevice(vm);

    // ── What's New ──

    private static IReadOnlyList<ReleaseNotes>? _notes;

    /// <summary>Every release in WhatsNew.json, newest first.</summary>
    public static IReadOnlyList<ReleaseNotes> ReleaseNotes
    {
        get
        {
            if (_notes != null) return _notes;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "WhatsNew.json");
                _notes = File.Exists(path) ? WhatsNew.Parse(File.ReadAllText(path)) : Array.Empty<ReleaseNotes>();
            }
            catch { _notes = Array.Empty<ReleaseNotes>(); }
            return _notes;
        }
    }

    /// <summary>Notes to show at launch. A new install is marked read instead:
    /// someone seeing the app for the first time has nothing to catch up on.</summary>
    public static IReadOnlyList<ReleaseNotes> TakeUnreadReleaseNotes()
    {
        var s = AppSettings.Instance;
        string? shown = s.WhatsNewLastShownVersion;
        if (shown == null)
        {
            if (!s.LoadedFromFile)
            {
                MarkReleaseNotesRead();
                return Array.Empty<ReleaseNotes>();
            }
            // Settings from a build before release notes existed: an upgrade
            // from 1.1.6 beta 2 or earlier, the last release without them.
            shown = PreReleaseNotesVersion;
        }
        return WhatsNew.Unread(ReleaseNotes, shown, AppInfo.ExpectedFirmware);
    }

    /// <summary>The last Windows release that had no release notes window.</summary>
    private const string PreReleaseNotesVersion = "1.1.6-beta2";

    public static void MarkReleaseNotesRead()
    {
        if (AppInfo.ExpectedFirmware is not { } current) return;
        AppSettings.Instance.WhatsNewLastShownVersion = current.TagSuffix ?? current.ToString();
        AppSettings.Instance.Save();
    }

    private sealed class Store(AppSettings settings) : IOnboardingStore
    {
        private bool _simulatedPriorUse;

        public IReadOnlyCollection<string>? CompletedStepIds
        {
            get => settings.OnboardingCompletedStepIds;
            set => settings.OnboardingCompletedStepIds = value?.ToList();
        }
        public string? LastSeenVersion { get => settings.OnboardingLastSeenVersion; set => settings.OnboardingLastSeenVersion = value; }
        public bool? TourDeclined { get => settings.OnboardingTourDeclined; set => settings.OnboardingTourDeclined = value; }
        public DateTime? FirstLaunch { get => settings.OnboardingFirstLaunch; set => settings.OnboardingFirstLaunch = value; }
        public bool SimulateFreshInstall { get => settings.OnboardingSimulateFreshInstall; set => settings.OnboardingSimulateFreshInstall = value; }
        public bool HasPriorAppUse => settings.LoadedFromFile || _simulatedPriorUse;
        public string? CohortOverride { get => settings.OnboardingCohortOverride; set => settings.OnboardingCohortOverride = value; }
        public bool ForceWizard { get => settings.OnboardingForceWizard; set => settings.OnboardingForceWizard = value; }
        public void SimulatePriorUse() => _simulatedPriorUse = true;
        public void Save() => settings.Save();
    }

    private sealed class VmDevice(MainViewModel vm) : IOnboardingDevice
    {
        public bool ControlSurfacesSupported => vm.ControlSurfacesSupported;
        public bool CsAuxSupported => vm.CsAuxSupported;
        public bool AdatSupported => vm.AdatSupported;
        public bool I2sInputSupported => vm.InputI2sSupported;
        public string Platform => vm.Platform;
        public bool SubharmSupported => vm.SubharmSupported;
        public bool TubeSupported => vm.TubeSupported;
        public bool LimiterSupported => vm.LimiterSupported;
    }
}
