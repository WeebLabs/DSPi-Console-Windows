using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Onboarding;

/// <summary>Where a step is shown.</summary>
public abstract record OnboardingPhase
{
    private OnboardingPhase() { }

    /// <summary>The Getting Started wizard: a linear run from a blank board to
    /// verified firmware.</summary>
    public sealed record Setup : OnboardingPhase;
    /// <summary>Coach marks over the real UI, once, after setup.</summary>
    public sealed record Basics : OnboardingPhase;
    /// <summary>One card the first time a specialist window is opened.</summary>
    public sealed record JustInTime(string Key) : OnboardingPhase;
}

/// <summary>Which window a step is shown in.</summary>
public enum OnboardingHost
{
    MainWindow,
    MatrixMixer,
}

public static class OnboardingHosts
{
    /// <summary>The first-open card this window's tour steps replace: a window
    /// the tour walks through has already been explained.</summary>
    public static string? JustInTimeKey(this OnboardingHost host) =>
        host == OnboardingHost.MatrixMixer ? "matrix-mixer" : null;
}

/// <summary>What onboarding needs to know about the connected device to
/// decide whether a step means anything for it.</summary>
public interface IOnboardingDevice
{
    bool ControlSurfacesSupported { get; }
    bool CsAuxSupported { get; }
    bool AdatSupported { get; }
    bool I2sInputSupported { get; }
    string Platform { get; }
    bool SubharmSupported { get; }
    bool TubeSupported { get; }
    bool LimiterSupported { get; }
}

/// <summary>
/// One thing onboarding can teach. Data rather than a view, so what to show
/// can be tested without rendering anything. Equal by id. Port of the macOS
/// Console's OnboardingStep.
/// </summary>
public sealed record OnboardingStep(
    // Stable forever: persisted in the completed set.
    string Id,
    // The release this step first shipped in.
    FirmwareVersion IntroducedIn,
    OnboardingPhase Phase,
    string Title,
    Func<IOnboardingDevice, bool> Applies,
    string Message = "",
    OnboardingHost Host = OnboardingHost.MainWindow,
    // For a basics step, the anchor id it points at.
    string? Anchor = null,
    // The target only exists once a channel is selected.
    bool NeedsChannelDetail = false,
    // The step invites typing into its target, so Next gives up Enter.
    bool InvitesTyping = false)
{
    public bool Equals(OnboardingStep? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// Every onboarding step, in the order they are offered: setup top to bottom,
/// the basics tour along the path a new user takes, then the first-open cards.
/// Copy is the macOS Console's, word for word. Port of OnboardingCatalogue.
/// </summary>
public static class OnboardingCatalogue
{
    /// <summary>The release onboarding first shipped in. Nothing may be dated
    /// earlier: an updater has been offered every step from before their version.</summary>
    public static readonly FirmwareVersion FirstRelease = new(1, 1, 6, 3);

    private static readonly FirmwareVersion V3 = new(1, 1, 6, 3);
    private static readonly FirmwareVersion V4 = new(1, 1, 6, 4);

    private static bool Always(IOnboardingDevice _) => true;

    /// <summary>The wizard. Finishing or skipping it marks all of these seen.</summary>
    public static readonly IReadOnlyList<OnboardingStep> Setup = new[]
    {
        new OnboardingStep("setup.welcome", V3, new OnboardingPhase.Setup(), "Welcome", Always),
        new OnboardingStep("setup.board", V3, new OnboardingPhase.Setup(), "Get your board running", Always),
        new OnboardingStep("setup.finished", V3, new OnboardingPhase.Setup(), "You are set up", Always),
    };

    public static readonly IReadOnlyList<OnboardingStep> Basics = new[]
    {
        new OnboardingStep("basics.sidebar", V3, new OnboardingPhase.Basics(), "Inputs and outputs", Always,
            "Every channel lives here: inputs are what arrives from your computer, outputs are what leaves for your speakers, and each row's meter shows what is reaching it. Click a channel's name or meter to open its page and edit its filters. The small coloured tag at the end of the row is a separate control - it shows or hides that channel's curve on the graph, and leaves whichever page you have open alone.",
            Anchor: "basics.sidebar"),
        new OnboardingStep("basics.routing", V3, new OnboardingPhase.Basics(), "The Matrix Mixer", Always,
            "The Matrix Mixer decides which sound reaches which output, and it is the one screen standing between you and working audio: to begin with your left and right channels reach the first pair of outputs and nothing else is connected, so everything past plain stereo starts here. This button opens it, and it is worth remembering where it is. Next opens it for you.",
            Anchor: "basics.routing"),
        new OnboardingStep("basics.matrix-grid", V3, new OnboardingPhase.Basics(), "Connecting an input to an output", Always,
            "Every input has a row and every output has a column. The circle where a row meets a column is the connection: click one and that input plays through that output. A connected circle grows a level field above it and an INV switch below, which flips its polarity for a driver wired backwards. Try one now. An input can feed several outputs at once, which is how you send bass to a subwoofer while the main speakers carry the rest.",
            Host: OnboardingHost.MatrixMixer, Anchor: "matrix.grid", InvitesTyping: true),
        new OnboardingStep("basics.matrix-outputs", V3, new OnboardingPhase.Basics(), "What each output does", Always,
            "These rows act on a whole output rather than on one connection. ENABLE switches an output off and gives its processing time back to the device, GAIN and DELAY set its level and time it against your other speakers, and MUTE silences it while you work. Each output also has its own filters, which is how a crossover is built: send the same input to two outputs and filter each one differently.",
            Host: OnboardingHost.MatrixMixer, Anchor: "matrix.outputs"),
        new OnboardingStep("basics.graph", V3, new OnboardingPhase.Basics(), "The response graph", Always,
            "This draws what your filters do to the sound. Each curve is the result of every filter on that channel combined, so you can see the shape you are building as you build it. The coloured tags in the sidebar decide which curves are drawn, so you can compare a few channels or narrow it down to one.",
            Anchor: "basics.graph"),
        new OnboardingStep("basics.add-filter", V3, new OnboardingPhase.Basics(), "Add a filter", Always,
            "Each channel has ten filter slots, empty until you give one a type. Try it now: set a slot to Peaking, then give it a frequency, a gain and a Q. Q is how wide the filter reaches around its frequency - low Q is broad and gentle, high Q is narrow. The graph redraws as you type, and so does the device.",
            Anchor: "basics.add-filter", NeedsChannelDetail: true, InvitesTyping: true),
        new OnboardingStep("basics.volume-controls", V3, new OnboardingPhase.Basics(), "Volume Controls", Always,
            "Two volume controls share this spot and clicking the label above the slider enables you to switch between them. User Volume is the everyday control and chooses the amount by which your input source will be attenuated. Master Volume is stored on DSPi and has the final word on the highest volume that will actually come out of your speakers or headphones.",
            Anchor: "basics.volume"),
        new OnboardingStep("basics.saving", V3, new OnboardingPhase.Basics(), "Saving to the device", Always,
            "This is the one thing worth reading twice. Changes take effect on the device immediately, but they live in memory until you commit them, and a power cycle loses anything uncommitted. Commit Parameters and Revert to Saved are both in the Tools menu. An asterisk beside the preset name means there is uncommitted work."),
        new OnboardingStep("basics.presets", V3, new OnboardingPhase.Basics(), "Presets", Always,
            "The device holds ten named presets, each a complete configuration you can name and switch between. Switching discards anything uncommitted, so commit first if you want to keep what you have been working on.",
            Anchor: "basics.presets"),
        new OnboardingStep("basics.where-things-live", V3, new OnboardingPhase.Basics(), "Where everything else lives", Always,
            "That is the whole of the everyday interface. Everything else lives in these icons and in the Tools menu, including crossfeed, loudness compensation, upmixing and test signals. Each one explains itself the first time you open it, so there is nothing to learn in advance.",
            Anchor: "basics.tools"),
    };

    public static readonly IReadOnlyList<OnboardingStep> JustInTime = new[]
    {
        Jit("matrix-mixer", "Matrix Mixer",
            "Every input has a row and every output has a column. Switch on the square where they meet and that input plays through that output. Each connection carries its own level, so you can blend several inputs into one output without overloading it, and its own polarity switch for a driver that is wired backwards. The controls above the grid set the level, delay and mute for each output as a whole."),
        Jit("control-surfaces", "Control Surfaces",
            "Wire real buttons, knobs, switches or an infrared remote to the Pico and bind them to anything the device can change. Each slot is one physical control, one pin and one thing it acts on.",
            d => d.ControlSurfacesSupported),
        Jit("control-interfaces", "Control Interfaces",
            "Lets another device drive the DSPi over UART or I2C - a microcontroller, a home automation box, anything that can send bytes. Changes here are prepared and then applied together, so a half-typed setting never reaches the hardware."),
        Jit("macros", "Macros",
            "One control, several actions. A macro runs a short list of steps in order, with optional delays, so a single button press can change volume, switch a preset and mute an output together.",
            d => d.ControlSurfacesSupported),
        Jit("aux-outputs", "Auxiliary Outputs",
            "A GPIO the DSPi switches or dims for you and never reads itself: an amplifier trigger, a speaker relay, a panel lamp, a fan. Add one here on a spare pin, then point a button, knob, remote key or macro at it from the Control Surfaces page.",
            d => d.CsAuxSupported),
        Jit("channel-groups", "Channel Groups",
            "Groups let one control move several channels at once, keeping their relative levels or setting them all to the same value. Useful when a pair of speakers should always track together.",
            d => d.ControlSurfacesSupported),
        Jit("adat", "ADAT",
            "ADAT carries eight channels of audio down one optical cable, so the DSPi can reach an interface or a mixer without eight separate leads. Switch it on here to send all eight output channels; receiving eight channels in is set up on the Inputs page.",
            d => d.AdatSupported),
        Jit("i2s-input", "I2S Input",
            "I2S brings audio in directly from an ADC or another digital source over a few wires, instead of over USB. The important choice is which side generates the clock; everything else follows from it.",
            d => d.I2sInputSupported),
        Jit("upmixer", "Stereo Upmixer",
            "Derives centre and surround channels from an ordinary stereo recording, so a two-channel source can drive more speakers. Nothing is invented: the extra channels are pulled out of what the stereo pair already contains.",
            d => d.Platform == "RP2350"),
        Jit("crossfeed", "Headphone Crossfeed",
            "On headphones each ear hears only its own channel, which is not how speakers in a room work and is why some recordings feel oddly wide. Crossfeed blends a little of each channel into the other, with a short delay, to relax that effect."),
        Jit("loudness", "Loudness Compensation",
            "Ears lose sensitivity to bass and treble as things get quieter, so music thins out at low volume. This adds back what quiet listening takes away, tracking your volume setting so the balance stays even as you turn it down."),
        Jit("leveller", "Volume Leveller",
            "Evens out material that swings between quiet and loud - late-night listening, mixed playlists, films with whispered dialogue and loud effects. It watches the level and applies gentle gain, rather than squashing the peaks."),
        Jit("psybass", "Psychoacoustic Bass",
            "Small speakers cannot reproduce the lowest notes, but the ear will still hear a note whose harmonics are present even when the fundamental is missing. This synthesises those harmonics, so bass reads as deeper without asking the driver for anything it cannot do."),
        Jit("subharm", "Subharmonic Synthesizer",
            "The opposite of psychoacoustic bass: instead of implying a low note a speaker cannot play, this synthesises a real one an octave below the bass already in the music. Only worth switching on for an output that can reproduce 24 to 80 Hz - a subwoofer, or a large full-range system.",
            d => d.SubharmSupported),
        Jit("tube", "Tube Modeller",
            "Adds the harmonic colour, gentle compression and transformer weight of a valve amplifier. Pick a tube to load its character, then use drive to decide how hard the stage works: a few dB is warmth, a lot is overdrive.",
            d => d.TubeSupported, V4),
        Jit("limiter", "Output Limiter",
            "Holds this output under the ceiling you set, so a loud track or a slip of the volume cannot drive an amplifier or a tweeter into clipping. Switch it on only where you need it: while any limiter is on, every output is delayed by a fraction of a millisecond.",
            d => d.LimiterSupported, V4),
        Jit("autoeq", "AutoEQ",
            "A library of measured headphone corrections. Find your model, load its filters, and the DSPi applies the correction that measurement suggests - a good starting point to adjust by ear afterwards."),
        Jit("test-signals", "Signal Generator",
            "Generates tones, sweeps and noise on the device itself, so you can check wiring, identify a channel or take a measurement without needing a source playing. Start quiet: test signals are far more consistent than music and will happily drive a speaker hard."),
        Jit("stats", "Stats",
            "Live diagnostics from the device: processor load, sample rates, clock lock state and the health of each input. The first place to look when something sounds wrong or a source will not lock."),
    };

    public static readonly IReadOnlyList<OnboardingStep> All = Setup.Concat(Basics).Concat(JustInTime).ToList();

    private static OnboardingStep Jit(string key, string title, string message,
        Func<IOnboardingDevice, bool>? applies = null, FirmwareVersion? introducedIn = null) =>
        new($"jit.{key}", introducedIn ?? V3, new OnboardingPhase.JustInTime(key), title, applies ?? Always, message);
}
