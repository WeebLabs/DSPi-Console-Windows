using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Onboarding;

/// <summary>Which kind of user this is, which decides how onboarding is presented.</summary>
public enum OnboardingCohort
{
    /// <summary>No prior state of any kind: gets the full sequence, framed as setup.</summary>
    NewUser,
    /// <summary>Has seen earlier steps; only what is new since is offered, quietly.</summary>
    Updater,
    /// <summary>Was using the app before onboarding existed: everything shipped
    /// so far is marked seen, and the tour is offered once, opt in.</summary>
    ExistingUser,
    /// <summary>Nothing to show.</summary>
    UpToDate,
    /// <summary>Asked never to be shown this again.</summary>
    Declined,
}

/// <summary>Where onboarding keeps its state. A null means never written,
/// which is how a first onboarding launch is told apart.</summary>
public interface IOnboardingStore
{
    IReadOnlyCollection<string>? CompletedStepIds { get; set; }
    string? LastSeenVersion { get; set; }
    bool? TourDeclined { get; set; }
    DateTime? FirstLaunch { get; set; }
    /// <summary>Written by a developer reset to demand the new-user path on the
    /// next launch; consumed on sight.</summary>
    bool SimulateFreshInstall { get; set; }
    /// <summary>Whether the app had been used before (any settings existed).</summary>
    bool HasPriorAppUse { get; }
    /// <summary>Developer override: "fresh", "existing", "declined" or
    /// "updater:1.1.6". Applied once, then cleared.</summary>
    string? CohortOverride { get; set; }
    /// <summary>Developer override: show the wizard regardless, for one launch.</summary>
    bool ForceWizard { get; set; }
    /// <summary>Marks the app as used before, for the "existing" override.</summary>
    void SimulatePriorUse();
    void Save();
}

/// <summary>
/// Decides what onboarding to show and remembers what has been shown. What a
/// user sees is a function of persisted ids, the catalogue and the connected
/// hardware, so it is testable without a window. Port of the macOS Console's
/// OnboardingCoordinator.
/// </summary>
public sealed class OnboardingCoordinator
{
    /// <summary>
    /// Master switch for everything after first-run setup: the basics tour, its
    /// offer, and the first-open hint cards. Off, as on the macOS Console, which
    /// ships with them switched off; nothing is recorded as seen while it is,
    /// so turning it on later picks up exactly where it left off. The Getting
    /// Started wizard is unaffected.
    /// </summary>
    public const bool PostSetupOnboardingEnabled = false;

    private readonly IOnboardingStore _store;
    private readonly FirmwareVersion? _appVersion;
    private readonly bool _postSetupEnabled;
    /// <summary>The developer's force-wizard request, taken at the first
    /// evaluation and cleared from the store, so it holds for one launch.</summary>
    private bool _forceWizard;

    public OnboardingCohort Cohort { get; private set; } = OnboardingCohort.UpToDate;

    /// <summary>The app version in its tag spelling ("1.1.6-beta4"), which
    /// parses back, unlike the prose form.</summary>
    private string? VersionTag => _appVersion is { } v ? v.TagSuffix ?? v.ToString() : null;
    public IReadOnlyList<OnboardingStep> Pending => _pending;
    private List<OnboardingStep> _pending = new();

    /// <summary>Raised when anything observable changes.</summary>
    public event Action? Changed;

    public OnboardingCoordinator(IOnboardingStore store, FirmwareVersion? appVersion,
        bool postSetupEnabled = PostSetupOnboardingEnabled)
    {
        _store = store;
        _appVersion = appVersion;
        _postSetupEnabled = postSetupEnabled;
    }

    // ── State ──

    public IReadOnlySet<string> CompletedIds => (_store.CompletedStepIds ?? Array.Empty<string>()).ToHashSet();

    private void SetCompleted(IEnumerable<string> ids)
    {
        _store.CompletedStepIds = ids.Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();
        _store.Save();
    }

    private bool IsFirstOnboardingLaunch =>
        _store.CompletedStepIds == null && _store.LastSeenVersion == null
        && _store.TourDeclined == null && _store.FirstLaunch == null;

    // ── Evaluation ──

    /// <summary>Works out what this user should be shown. Call once the device
    /// state is known: applicability depends on the attached hardware.</summary>
    public void Evaluate(IOnboardingDevice device)
    {
        ApplyCohortOverride();
        if (_store.ForceWizard)
        {
            _forceWizard = true;
            _store.ForceWizard = false;
            _store.Save();
        }

        // Consumed on sight: a simulated fresh install is one launch.
        bool simulatingFresh = _store.SimulateFreshInstall;
        if (simulatingFresh) { _store.SimulateFreshInstall = false; _store.Save(); }

        if (IsFirstOnboardingLaunch)
        {
            _store.FirstLaunch = DateTime.UtcNow;
            _store.Save();
            // Someone already using the app is not dragged through a beginner's
            // wizard on upgrade day: everything live so far is marked seen.
            // Skipped when a fresh install was asked for, or a first run would
            // be unreachable on every machine that develops this.
            if (_store.HasPriorAppUse && !simulatingFresh)
            {
                // Only phases that are live: seeding a step nobody can be shown
                // would spend it before it could ever run.
                SetCompleted(OnboardingCatalogue.All
                    .Where(s => _postSetupEnabled || s.Phase is OnboardingPhase.Setup)
                    .Select(s => s.Id));
                _store.LastSeenVersion = VersionTag;
                _store.Save();
                Cohort = OnboardingCohort.ExistingUser;
                _pending = new();
                Changed?.Invoke();
                return;
            }
        }

        if (_store.TourDeclined == true)
        {
            Cohort = OnboardingCohort.Declined;
            _pending = new();
            Changed?.Invoke();
            return;
        }

        var seen = CompletedIds;
        _pending = OnboardingCatalogue.All
            .Where(s => !seen.Contains(s.Id) && s.Applies(device)
                        && (_postSetupEnabled || s.Phase is OnboardingPhase.Setup))
            .ToList();
        Cohort = _pending.Count == 0 ? OnboardingCohort.UpToDate
            : seen.Count == 0 ? OnboardingCohort.NewUser
            : OnboardingCohort.Updater;
        Changed?.Invoke();
    }

    public IReadOnlyList<OnboardingStep> PendingIn<TPhase>() where TPhase : OnboardingPhase =>
        _pending.Where(s => s.Phase is TPhase).ToList();

    /// <summary>The card to show the first time <paramref name="key"/>'s window opens.</summary>
    public OnboardingStep? JustInTimeStep(string key) =>
        _pending.FirstOrDefault(s => s.Phase is OnboardingPhase.JustInTime j && j.Key == key);

    // ── The wizard ──

    /// <summary>Set when the user asks for the wizard (Help › Getting Started),
    /// so it opens even after setup was finished.</summary>
    public bool SetupRequested { get; private set; }

    /// <summary>Whether the wizard replaces the console. For a genuinely new
    /// user, connected or not; never keyed on the device, which may appear
    /// mid-wizard, the very thing several steps wait for.</summary>
    public bool ShouldTakeOverMainWindow()
    {
        if (SetupRequested || _forceWizard) return true;
        return Cohort == OnboardingCohort.NewUser && PendingIn<OnboardingPhase.Setup>().Count > 0;
    }

    /// <summary>Opens the wizard on demand, ending any running tour first.</summary>
    public void RequestSetup()
    {
        if (BasicsTourRunning) EndBasicsTour();
        SetupRequested = true;
        Changed?.Invoke();
    }

    /// <summary>Leaves the wizard, completed or skipped. Both record the setup
    /// steps as seen: a skip that reappears next launch is not a skip.</summary>
    public void FinishSetup()
    {
        SetupRequested = false;
        _forceWizard = false;
        Skip<OnboardingPhase.Setup>();
        Changed?.Invoke();
    }

    // ── The basics tour (state only; no overlay is drawn while it is switched off) ──

    private List<OnboardingStep> _tourSteps = new();
    public IReadOnlyList<OnboardingStep> BasicsTourSteps => _tourSteps;
    public int BasicsTourIndex { get; private set; }
    public bool BasicsTourRunning { get; private set; }
    public OnboardingStep? BasicsTourStep =>
        BasicsTourIndex >= 0 && BasicsTourIndex < _tourSteps.Count ? _tourSteps[BasicsTourIndex] : null;

    public bool CanOfferBasicsTour =>
        _postSetupEnabled && Cohort != OnboardingCohort.Declined && PendingIn<OnboardingPhase.Setup>().Count == 0
        && (Cohort == OnboardingCohort.ExistingUser || PendingIn<OnboardingPhase.Basics>().Count > 0);

    public bool BasicsOfferDismissed { get; private set; }
    public bool ShowsBasicsOffer => CanOfferBasicsTour && !BasicsOfferDismissed;

    /// <summary>Starts the tour, restoring all of it if nothing is pending.
    /// Re-evaluates so a replay cannot resurrect a step for absent hardware.</summary>
    public void StartBasicsTour(IOnboardingDevice device)
    {
        if (!_postSetupEnabled) return;
        if (PendingIn<OnboardingPhase.Basics>().Count == 0)
        {
            SetCompleted(CompletedIds.Except(OnboardingCatalogue.Basics.Select(s => s.Id)));
            Evaluate(device);
        }
        var steps = PendingIn<OnboardingPhase.Basics>().ToList();
        if (steps.Count == 0) return;
        // A window the tour walks through does not also need its first-open card.
        MarkSeen(steps.Select(s => s.Host.JustInTimeKey()).Where(k => k != null).Distinct()
            .Select(k => JustInTimeStep(k!)?.Id).Where(id => id != null).Select(id => id!).ToList());
        _tourSteps = steps;
        BasicsTourIndex = 0;
        BasicsTourRunning = true;
        BasicsOfferDismissed = true;
        Changed?.Invoke();
    }

    public void BasicsTourNext()
    {
        if (!BasicsTourRunning) return;
        // One at a time, so an interrupted tour resumes at the first unread step.
        if (BasicsTourStep is { } step) MarkSeen(step);
        if (BasicsTourIndex + 1 < _tourSteps.Count) { BasicsTourIndex++; Changed?.Invoke(); }
        else EndBasicsTour();
    }

    public void BasicsTourBack()
    {
        if (!BasicsTourRunning || BasicsTourIndex == 0) return;
        BasicsTourIndex--;
        Changed?.Invoke();
    }

    /// <summary>Ends the tour however it ended; skipping records the steps not
    /// reached too, or the skip costs the user the same offer tomorrow.</summary>
    public void EndBasicsTour()
    {
        MarkSeen(_tourSteps.Select(s => s.Id).ToList());
        BasicsTourRunning = false;
        _tourSteps = new();
        BasicsTourIndex = 0;
        BasicsOfferDismissed = true;
        Changed?.Invoke();
    }

    public void DismissBasicsOffer() { BasicsOfferDismissed = true; Changed?.Invoke(); }

    // ── Just-in-time hints ──

    public void MarkJustInTimeSeen(string key)
    {
        if (JustInTimeStep(key) is { } step) MarkSeen(step);
    }

    // ── Recording ──

    public void MarkSeen(OnboardingStep step) => MarkSeen(new[] { step.Id });

    public void MarkSeen(IReadOnlyCollection<string> ids)
    {
        SetCompleted(CompletedIds.Union(ids));
        _store.LastSeenVersion = VersionTag;
        _store.Save();
        _pending.RemoveAll(s => ids.Contains(s.Id));
        if (_pending.Count == 0) Cohort = OnboardingCohort.UpToDate;
        Changed?.Invoke();
    }

    /// <summary>Skipping is as final as completing.</summary>
    public void Skip<TPhase>() where TPhase : OnboardingPhase =>
        MarkSeen(PendingIn<TPhase>().Select(s => s.Id).ToList());

    /// <summary>"Never show me this again." The completed set is left alone, so
    /// a later reset restores the ordinary behaviour.</summary>
    public void DeclineEverything()
    {
        _store.TourDeclined = true;
        _store.Save();
        Cohort = OnboardingCohort.Declined;
        _pending = new();
        Changed?.Invoke();
    }

    // ── Developer ──

    /// <summary>Forgets everything and demands the new-user path next launch;
    /// clearing alone would let the prior-use check seed an existing user.</summary>
    public void ResetAll()
    {
        AbandonBasicsTour();
        _store.CompletedStepIds = null;
        _store.LastSeenVersion = null;
        _store.TourDeclined = null;
        _store.FirstLaunch = null;
        _store.SimulateFreshInstall = true;
        _store.Save();
        Cohort = OnboardingCohort.UpToDate;
        _pending = new();
        Changed?.Invoke();
    }

    /// <summary>Drops a running tour without recording anything (developer resets only).</summary>
    private void AbandonBasicsTour()
    {
        BasicsTourRunning = false;
        _tourSteps = new();
        BasicsTourIndex = 0;
        BasicsOfferDismissed = false;
    }

    public void ReplayBasics()
    {
        AbandonBasicsTour();
        SetCompleted(CompletedIds.Except(OnboardingCatalogue.Basics.Select(s => s.Id)));
        _store.TourDeclined = false;
        _store.Save();
        Changed?.Invoke();
    }

    public void ReplayJustInTime()
    {
        SetCompleted(CompletedIds.Except(OnboardingCatalogue.JustInTime.Select(s => s.Id)));
        Changed?.Invoke();
    }

    /// <summary>Rewrites the persisted state to a requested cohort, once.</summary>
    private void ApplyCohortOverride()
    {
        if (_store.CohortOverride is not { } requested) return;
        _store.CohortOverride = null;
        _store.CompletedStepIds = null;
        _store.LastSeenVersion = null;
        _store.TourDeclined = null;
        _store.FirstLaunch = null;
        switch (requested)
        {
            case "fresh":
                _store.SimulateFreshInstall = true;
                break;
            case "existing":
                _store.SimulatePriorUse();
                break;
            case "declined":
                _store.TourDeclined = true;
                _store.CompletedStepIds = Array.Empty<string>();
                break;
            default:
                if (requested.StartsWith("updater:", StringComparison.Ordinal))
                {
                    string version = requested["updater:".Length..];
                    var from = FirmwareVersion.Parse(version);
                    _store.CompletedStepIds = OnboardingCatalogue.All
                        .Where(s => from is { } f && s.IntroducedIn <= f).Select(s => s.Id)
                        .OrderBy(i => i, StringComparer.Ordinal).ToList();
                    _store.LastSeenVersion = version;
                }
                break;
        }
        _store.Save();
    }
}
