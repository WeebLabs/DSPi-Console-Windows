using DSPiConsole.Core.Models;
using DSPiConsole.Core.Onboarding;

namespace DSPiConsole.Core.Tests;

/// <summary>The onboarding coordinator and What's New, after the macOS
/// Console's OnboardingCoordinatorTests.</summary>
public class OnboardingCoordinatorTests
{
    private sealed class Store : IOnboardingStore
    {
        public IReadOnlyCollection<string>? CompletedStepIds { get; set; }
        public string? LastSeenVersion { get; set; }
        public bool? TourDeclined { get; set; }
        public DateTime? FirstLaunch { get; set; }
        public bool SimulateFreshInstall { get; set; }
        public bool HasPriorAppUse { get; set; }
        public string? CohortOverride { get; set; }
        public bool ForceWizard { get; set; }
        public void SimulatePriorUse() => HasPriorAppUse = true;
        public void Save() { }
    }

    private sealed class Device : IOnboardingDevice
    {
        public bool ControlSurfacesSupported { get; set; } = true;
        public bool CsAuxSupported { get; set; } = true;
        public bool AdatSupported { get; set; } = true;
        public bool I2sInputSupported { get; set; } = true;
        public string Platform { get; set; } = "RP2350";
        public bool SubharmSupported { get; set; } = true;
        public bool TubeSupported { get; set; } = true;
        public bool LimiterSupported { get; set; } = true;
    }

    private static readonly FirmwareVersion App = new(1, 1, 6, 4);

    private static OnboardingCoordinator Make(Store store, bool postSetup = true) => new(store, App, postSetup);

    [Fact]
    public void NewUserGetsEveryApplicableStepAndTheWizard()
    {
        var c = Make(new Store());
        c.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.NewUser, c.Cohort);
        Assert.Equal(OnboardingCatalogue.All.Count, c.Pending.Count);
        Assert.True(c.ShouldTakeOverMainWindow());
    }

    [Fact]
    public void ExistingUserIsSeededRatherThanOnboardedOnce()
    {
        var store = new Store { HasPriorAppUse = true };
        var c = Make(store);
        c.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.ExistingUser, c.Cohort);
        Assert.Empty(c.Pending);
        Assert.False(c.ShouldTakeOverMainWindow());
        Assert.True(c.CanOfferBasicsTour);
        // Second launch: not seeded again, simply up to date.
        c.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.UpToDate, c.Cohort);
    }

    [Fact]
    public void WithThePostSetupHalfOffOnlySetupIsLiveAndOnlySetupIsSeeded()
    {
        var c = Make(new Store(), postSetup: false);
        c.Evaluate(new Device());
        Assert.All(c.Pending, s => Assert.IsType<OnboardingPhase.Setup>(s.Phase));
        Assert.False(c.CanOfferBasicsTour);
        Assert.Null(c.JustInTimeStep("crossfeed"));

        var existing = new Store { HasPriorAppUse = true };
        Make(existing, postSetup: false).Evaluate(new Device());
        Assert.Equal(OnboardingCatalogue.Setup.Select(s => s.Id).OrderBy(i => i, StringComparer.Ordinal), existing.CompletedStepIds);
    }

    [Fact]
    public void UpdaterGetsOnlyStepsNotSeen()
    {
        var store = new Store { CompletedStepIds = OnboardingCatalogue.Setup.Concat(OnboardingCatalogue.Basics).Select(s => s.Id).ToList() };
        var c = Make(store);
        c.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.Updater, c.Cohort);
        Assert.All(c.Pending, s => Assert.IsType<OnboardingPhase.JustInTime>(s.Phase));
        Assert.False(c.ShouldTakeOverMainWindow());
    }

    [Fact]
    public void StepsAreGatedOnDeviceCapability()
    {
        var c = Make(new Store());
        c.Evaluate(new Device { Platform = "RP2040", CsAuxSupported = false });
        Assert.Null(c.JustInTimeStep("upmixer"));
        Assert.Null(c.JustInTimeStep("aux-outputs"));
        Assert.NotNull(c.JustInTimeStep("crossfeed"));
    }

    [Fact]
    public void FinishingSetupReleasesTheWindowForGoodAndLeavesTheTourPending()
    {
        var store = new Store();
        var c = Make(store);
        c.Evaluate(new Device());
        c.FinishSetup();
        Assert.False(c.ShouldTakeOverMainWindow());
        Assert.NotEmpty(c.PendingIn<OnboardingPhase.Basics>());
        Make(store).Evaluate(new Device());
        var again = Make(store);
        again.Evaluate(new Device());
        Assert.False(again.ShouldTakeOverMainWindow());
    }

    [Fact]
    public void RequestingSetupOpensItForAnyone()
    {
        var c = Make(new Store { HasPriorAppUse = true });
        c.Evaluate(new Device());
        Assert.False(c.ShouldTakeOverMainWindow());
        c.RequestSetup();
        Assert.True(c.ShouldTakeOverMainWindow());
        c.FinishSetup();
        Assert.False(c.ShouldTakeOverMainWindow());
    }

    [Fact]
    public void DecliningSilencesEverything()
    {
        var store = new Store();
        var c = Make(store);
        c.Evaluate(new Device());
        c.DeclineEverything();
        var next = Make(store);
        next.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.Declined, next.Cohort);
        Assert.Empty(next.Pending);
    }

    [Fact]
    public void JustInTimeCardIsOfferedOnceThenNeverAgain()
    {
        var store = new Store();
        var c = Make(store);
        c.Evaluate(new Device());
        Assert.NotNull(c.JustInTimeStep("stats"));
        c.MarkJustInTimeSeen("stats");
        Assert.Null(c.JustInTimeStep("stats"));
        var next = Make(store);
        next.Evaluate(new Device());
        Assert.Null(next.JustInTimeStep("stats"));
    }

    [Fact]
    public void ResetReachesAFirstRunOnAMachineThatHasUsedTheAppOnce()
    {
        var store = new Store { HasPriorAppUse = true };
        var c = Make(store);
        c.Evaluate(new Device());
        c.ResetAll();
        var next = Make(store);
        next.Evaluate(new Device());
        Assert.Equal(OnboardingCohort.NewUser, next.Cohort);
        // The simulated fresh install is one launch, not a mode.
        Assert.False(store.SimulateFreshInstall);
    }

    [Fact]
    public void UpdaterOverrideMarksEverythingFromThatReleaseSeenAndIsConsumed()
    {
        var store = new Store { CohortOverride = "updater:1.1.6-beta3" };
        var c = Make(store);
        c.Evaluate(new Device());
        Assert.Null(store.CohortOverride);
        Assert.Equal(OnboardingCohort.Updater, c.Cohort);
        Assert.All(c.Pending, s => Assert.True(s.IntroducedIn > new FirmwareVersion(1, 1, 6, 3)));
        Assert.NotNull(c.JustInTimeStep("tube"));
    }

    [Fact]
    public void TourRunsPendingStepsAndSkippingRecordsTheRest()
    {
        var store = new Store();
        var c = Make(store);
        c.Evaluate(new Device());
        c.FinishSetup();
        c.StartBasicsTour(new Device());
        int total = c.BasicsTourSteps.Count;
        Assert.Equal(OnboardingCatalogue.Basics.Count, total);
        // The matrix window's first-open card is spent by the tour that explains it.
        Assert.Null(c.JustInTimeStep("matrix-mixer"));
        c.BasicsTourNext();
        Assert.Equal(1, c.BasicsTourIndex);
        Assert.Equal(total, c.BasicsTourSteps.Count);
        c.BasicsTourBack();
        Assert.Equal(0, c.BasicsTourIndex);
        c.EndBasicsTour();
        Assert.False(c.BasicsTourRunning);
        Assert.Empty(c.PendingIn<OnboardingPhase.Basics>());
    }

    [Fact]
    public void ForcedWizardHoldsForOneLaunchAndTheVersionIsStoredParseably()
    {
        var store = new Store { HasPriorAppUse = true, ForceWizard = true };
        var c = Make(store);
        c.Evaluate(new Device());
        Assert.True(c.ShouldTakeOverMainWindow());
        Assert.False(store.ForceWizard);
        c.FinishSetup();
        Assert.False(c.ShouldTakeOverMainWindow());
        Assert.Equal(App, FirmwareVersion.Parse(store.LastSeenVersion));
    }

    [Fact]
    public void CatalogueIdsAndKeysAreUniqueAndNothingPredatesOnboarding()
    {
        var all = OnboardingCatalogue.All;
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        var keys = all.Select(s => s.Phase).OfType<OnboardingPhase.JustInTime>().Select(j => j.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(all, s => Assert.True(s.IntroducedIn >= OnboardingCatalogue.FirstRelease));
    }

    [Fact]
    public void WhatsNewShowsOnlyUnreadNotesUpToThisVersion()
    {
        var notes = WhatsNew.Parse("""
            [ { "version": "1.1.6-beta3", "headline": "a", "items": ["x"] },
              { "version": "1.1.7", "headline": "future", "items": [] },
              { "version": "1.1.6-beta4", "headline": "b", "items": ["y", "z"] } ]
            """);
        Assert.Equal("1.1.7", notes[0].Version);
        // A first run has nothing to catch up on.
        Assert.Empty(WhatsNew.Unread(notes, null, App));
        var unread = WhatsNew.Unread(notes, "1.1.6-beta2", App);
        Assert.Equal(new[] { "1.1.6-beta4", "1.1.6-beta3" }, unread.Select(r => r.Version));
        Assert.Empty(WhatsNew.Unread(notes, "1.1.6-beta4", App));
        Assert.Empty(WhatsNew.Parse("not json"));
    }
}
