using System;
using System.Collections.Generic;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;

namespace DSPiConsole.Settings;

/// <summary>
/// Cross-page pin-coordination helpers for the Hardware category.
///
/// <para>
/// Each Hardware page hosts its own pin pickers (output GPIOs, BCK/MCK,
/// SPDIF RX, DAC mute) but pins are claimed across the whole device —
/// so any one page's edit must refresh every other page's "disabled
/// because in use" labels. In the legacy <c>SettingsDialog</c> this
/// was a private method walking every picker in the same dialog;
/// splitting the dialog into per-feature pages requires a small shared
/// surface.
/// </para>
///
/// <para>
/// <see cref="BuildOwnerMap"/> is the single source of truth: it
/// queries the ViewModel for every claimed pin and labels the
/// claimant. Pages call it whenever they need to refresh their
/// combo-box conflict state.
/// </para>
///
/// <para>
/// <see cref="PinAssignmentsChanged"/> is the broadcast: a page raises
/// it after committing a successful pin change; every Hardware page
/// subscribes to refresh its pickers in response. The static event is
/// fine here because the Settings window is single-instance — there is
/// at most one subscriber set at a time, and subscribers detach in
/// their Unloaded handlers.
/// </para>
/// </summary>
/// <summary>
/// What a claimed GPIO is doing. The Overview map colours by this, and the
/// order is the order it lists the roles in.
/// </summary>
internal enum PinRole
{
    Output,   // an audio output: I2S/SPDIF data, PDM, ADAT out
    Clock,    // BCK, LRCK, MCK — the pins that carry timing rather than audio
    Input,    // an audio input: S/PDIF RX, I2S RX, ADAT in
    Control,  // something driving the device: control surfaces, UART, I2C
    Utility,  // everything else the board holds, e.g. the DAC mute line
}

/// <summary>One GPIO, the feature holding it, and the page that feature is set
/// on. <see cref="Label"/> is the same owner name the pin pickers have always
/// shown in their conflict messages, so the Overview and a picker cannot
/// disagree about a pin.
///
/// <para><see cref="PageId"/> is carried here rather than worked out again by
/// whoever wants to navigate: the code that knows a pin is BCK is the same code
/// that knows BCK is set on the I2S page, and a second table mapping one to the
/// other would be free to drift from this one.</para></summary>
internal readonly record struct PinAssignment(byte Pin, string Label, PinRole Role, string PageId);

internal static class HardwarePins
{
    /// <summary>The GPIO pins exposed to audio-routing UI on the
    /// supported RP2040 / RP2350 boards. The legacy dialog duplicated
    /// this constant in two files; centralising it here removes the
    /// drift risk.</summary>
    public static readonly byte[] ValidPins =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
        13, 14, 15, 16, 17, 18, 19, 20, 21, 22,
        26, 27, 28
    ];

    /// <summary>Whether a GPIO is one of the audio-capable pins
    /// exposed by this board's mux (i.e. is in <see cref="ValidPins"/>).
    /// Used to validate constraints like "BCK pin must have an
    /// audio-capable neighbour at pin+1 to host LRCK".</summary>
    public static bool IsAudioCapable(byte pin) =>
        System.Array.IndexOf(ValidPins, pin) >= 0;

    /// <summary>
    /// GPIO pins that can drive MCK. MCK uses the RP2040/RP2350's
    /// hardware <c>clk_gpout</c> output, which is only wired to a
    /// small subset of GPIOs. The firmware rejects any other pin
    /// with <c>PIN_CONFIG_INVALID_PIN</c> (see
    /// <c>REQ_SET_MCK_PIN</c> in vendor_commands.c).
    /// <list type="bullet">
    /// <item><b>RP2040:</b> GPIO 21 only (23–25 exist on chip but are
    /// board-reserved and not in <see cref="ValidPins"/>).</item>
    /// <item><b>RP2350:</b> GPIO 13, 15, and 21.</item>
    /// </list>
    /// </summary>
    public static byte[] McKCapablePins(string platform) =>
        platform == "RP2350"
            ? new byte[] { 13, 15, 21 }
            : new byte[] { 21 };

    // The pages that own a GPIO, by the id each registers with the settings
    // registry. Named here so a claim above reads as "who holds it and where you
    // change it" on one line.
    private const string PageOutputs = "hardware.output-assignment";
    private const string PageMasterClock = "hardware.master-clock";
    private const string PageI2s = "hardware.i2s";
    private const string PageAdat = "hardware.adat";
    private const string PageSpdif = "hardware.spdif-input";
    private const string PageControlInterfaces = "hardware.control-interfaces";
    private const string PageDacMute = "hardware.dac-mute";
    private const string PageControlSurfaces = "control.surfaces";
    private const string PageControlAux = "control.aux";

    /// <summary>Raised when any Hardware page commits a pin change.
    /// Subscribers (other Hardware pages) call <see cref="BuildOwnerMap"/>
    /// and refresh their own pin combos.</summary>
    public static event Action? PinAssignmentsChanged;

    /// <summary>Notify all pages that the pin map changed. Called by
    /// the originating page after a successful flash write.</summary>
    public static void RaisePinAssignmentsChanged() =>
        PinAssignmentsChanged?.Invoke();

    /// <summary>
    /// Build the current map of pin → owner-label. Used by pin pickers
    /// to grey-out items that another feature already claims.
    /// </summary>
    /// <param name="vm">The application ViewModel; provides every
    /// claimed pin and the labels for each owner.</param>
    /// <param name="excludeOutputId">For output rows: pass the row's
    /// own output id so the picker doesn't grey-out its current pin.
    /// Pass -1 to exclude nothing.</param>
    /// <param name="excludeMckSelf">For the MCK pin combo: true makes
    /// the MCK self-entry omitted so MCK's own current pin stays
    /// selectable; equivalent for SPDIF RX via
    /// <paramref name="excludeSpdifRxSelf"/>.</param>
    /// <param name="excludeSpdifRxSelf">See above.</param>
    /// <param name="excludeDacMuteSelf">See above.</param>
    public static IReadOnlyDictionary<byte, string> BuildOwnerMap(
        MainViewModel vm,
        int excludeOutputId = -1,
        bool excludeMckSelf = false,
        bool excludeSpdifRxSelf = false,
        bool excludeDacMuteSelf = false,
        bool excludeI2sRxSelf = false,
        int excludeSpdifRxIndex = -1,
        int excludeI2sRxPair = -1,
        bool excludeAdatSelf = false,
        int excludeCsSlot = -1,
        bool excludeUartSelf = false,
        bool excludeI2cSelf = false,
        bool excludeAdatInputSelf = false,
        bool excludeI2sBckSlaveSelf = false)
    {
        var claims = BuildAssignmentMap(vm, excludeOutputId, excludeMckSelf, excludeSpdifRxSelf,
            excludeDacMuteSelf, excludeI2sRxSelf, excludeSpdifRxIndex, excludeI2sRxPair,
            excludeAdatSelf, excludeCsSlot, excludeUartSelf, excludeI2cSelf,
            excludeAdatInputSelf, excludeI2sBckSlaveSelf);
        var owners = new Dictionary<byte, string>(claims.Count);
        foreach (var (pin, claim) in claims) owners[pin] = claim.Label;
        return owners;
    }

    /// <summary>
    /// The same map, with each claim's role alongside its label. This is where
    /// the work is done; <see cref="BuildOwnerMap"/> is the label-only view of
    /// it that the pin pickers use. One authority, so the Overview cannot
    /// report a pin free that a picker will refuse, or the other way about.
    /// </summary>
    public static IReadOnlyDictionary<byte, PinAssignment> BuildAssignmentMap(
        MainViewModel vm,
        int excludeOutputId = -1,
        bool excludeMckSelf = false,
        bool excludeSpdifRxSelf = false,
        bool excludeDacMuteSelf = false,
        bool excludeI2sRxSelf = false,
        int excludeSpdifRxIndex = -1,
        int excludeI2sRxPair = -1,
        bool excludeAdatSelf = false,
        int excludeCsSlot = -1,
        bool excludeUartSelf = false,
        bool excludeI2cSelf = false,
        bool excludeAdatInputSelf = false,
        bool excludeI2sBckSlaveSelf = false)
    {
        var owners = new Dictionary<byte, PinAssignment>();
        // Later claims overwrite earlier ones on the same pin, which is how the
        // ADAT input's deliberate sharing of the output pin is reported.
        void Claim(byte pin, string label, PinRole role, string pageId) =>
            owners[pin] = new PinAssignment(pin, label, role, pageId);

        // Output GPIO pins. Output id 0..3 map to S/PDIF L of each pair
        // (or I²S DOUT); id 4 (RP2350) / id 2 (RP2040) is PDM. The
        // excludeOutputId guard keeps a row's own pin pickable on its
        // own combo.
        //
        // Use Detail ("OUT 1/2", "SUB OUT", …) rather than Name
        // ("Output 1", "PDM") because every consumer of this map shows
        // the value in a pin-conflict label — and Detail names the
        // actual signal that's on the GPIO, which is what the user
        // needs to know to resolve the conflict. Name is the row
        // header in the Output Assignment editor only.
        var outputs = AllPinOutputs(vm.Platform);
        foreach (var o in outputs)
        {
            if (o.Id == excludeOutputId) continue;
            Claim(vm.GetOutputPinValue(o.Id), o.Detail, PinRole.Output, PageOutputs);
        }

        // I²S clock pins: BCK reserves both pin and pin+1 (LRCK).
        Claim(vm.I2SBckPin, "BCK", PinRole.Clock, PageI2s);
        Claim((byte)(vm.I2SBckPin + 1), "LRCK", PinRole.Clock, PageI2s);

        // Slave-pair BCK/LRCK — reserved only in SPLIT clock-pin mode.
        if (vm.I2sClockSplit && !excludeI2sBckSlaveSelf)
        {
            Claim(vm.I2sBckPinSlave, "Slave BCK", PinRole.Clock, PageI2s);
            Claim((byte)(vm.I2sBckPinSlave + 1), "Slave LRCK", PinRole.Clock, PageI2s);
        }

        if (vm.MckEnabled && !excludeMckSelf)
            Claim(vm.MckPin, "MCK", PinRole.Clock, PageMasterClock);

        // S/PDIF RX input pins. With multiple selectable inputs, only the
        // ENABLED inputs actually claim a GPIO (a disabled input's pin is just a
        // stored preference). excludeSpdifRxIndex keeps a row's own pin pickable.
        if (vm.InputSourceSupported)
        {
            if (vm.MultiSpdifSupported)
            {
                for (int i = 0; i < vm.SpdifInputCount; i++)
                {
                    if (i == excludeSpdifRxIndex) continue;
                    if (excludeSpdifRxSelf && i == 0) continue;
                    if (!vm.SpdifInputEnabled(i)) continue;
                    Claim(vm.SpdifRxPinAt(i), $"SPDIF {i + 1}", PinRole.Input, PageSpdif);
                }
            }
            else if (!excludeSpdifRxSelf)
            {
                Claim(vm.SpdifRxPin, "SPDIF RX", PinRole.Input, PageSpdif);
            }
        }

        // I2S input data pins (V12+). One pin per ACTIVE stereo pair; higher
        // pairs reserve no GPIO until the channel count grows to reach them.
        if (vm.InputI2sSupported)
        {
            // The firmware has two tiers here (vendor_commands.c), and so does
            // this. Assigning an I2S RX pin must avoid EVERY configured pair,
            // active or not: i2s_rx_pair_pin_taken keeps all pair pins mutually
            // distinct so that raising the channel count can never bring two state
            // machines up on one GPIO. Every other consumer sees only the active
            // pairs, so an inactive pair's placeholder pin never locks a GPIO away
            // from anything else.
            //
            // Modelling only the second tier is what let an inactive pair's pin
            // read as free on every page while I2S alone refused it, with nothing
            // to point at when it did.
            bool assigningRxPin = excludeI2sRxPair >= 0;
            int pairs = assigningRxPin ? vm.I2sMaxPairs : vm.I2sActivePairs;
            // Numbered on a part that has more than one pair, whether or not the
            // rest are switched on yet. Numbering by the active count instead read
            // as a bare "I2S RX" for pair 1 right up until a second pair came up —
            // which is exactly when a conflict names it and the number matters.
            bool numbered = vm.I2sMaxPairs > 1;
            for (int p = 0; p < pairs; p++)
            {
                if (p == excludeI2sRxPair) continue;
                if (excludeI2sRxSelf && p == 0) continue;
                Claim(vm.I2sRxPinAt(p), numbered ? $"I2S RX {p + 1}" : "I2S RX", PinRole.Input, PageI2s);
            }
        }

        // ADAT bulk output pin (V17+, RP2350): only claims a GPIO while the
        // optical output is actually enabled (a disabled ADAT's pin is just a
        // stored preference). excludeAdatSelf keeps it pickable on its own combo.
        // "ADAT Out" rather than "ADAT" so the conflict label pairs with "ADAT In"
        // — both directions live on one page now and either can own a GPIO.
        if (vm.AdatSupported && vm.AdatEnabled && !excludeAdatSelf)
            Claim(vm.AdatPin, "ADAT Out", PinRole.Output, PageAdat);

        // ADAT optical input pin (V24+, RP2350): claims a GPIO only while enabled.
        // May legitimately share the ADAT-output pin (one-directional loopback), so
        // it's claimed last — its own combo passes excludeAdatInputSelf.
        if (vm.AdatInputSupported && vm.AdatInputEnabled && !excludeAdatInputSelf
            && vm.AdatInputPin != MainViewModel.AdatInputPinUnset)
            Claim(vm.AdatInputPin, "ADAT In", PinRole.Input, PageAdat);

        // Control-surface GPIOs: only LIVE bindings (CsStatus.IsSlotActive) actually
        // hold a pin. Encoders claim both gpio0 and gpio1; single-pin types leave
        // gpio1 = 0xFF. excludeCsSlot keeps a slot's own pins pickable on its card.
        if (vm.ControlSurfacesSupported && vm.CsStatus is { } csStatus)
        {
            int slots = vm.CsSlotCount;
            for (int s = 0; s < slots; s++)
            {
                if (s == excludeCsSlot) continue;
                if (!csStatus.IsSlotActive(s)) continue;
                var b = vm.CsBindings[s];
                if (!b.IsConfigured) continue;
                string label = !string.IsNullOrWhiteSpace(vm.CsNames[s])
                    ? vm.CsNames[s]
                    : $"Ctrl {s + 1}";
                // An aux output lives on its own page.
                string page = b.IsAux ? PageControlAux : PageControlSurfaces;
                Claim(b.Gpio0, label, PinRole.Control, page);
                if (b.Gpio1 != CsLimits.GpioUnused) Claim(b.Gpio1, label, PinRole.Control, page);
            }
        }

        // UART / I2C control-interface GPIOs: an interface reserves its pins only
        // while it is actually LIVE (a disabled or boot-collided interface holds
        // none). excludeUartSelf/excludeI2cSelf keep a section's own pins pickable.
        if (vm.ControlInterfacesSupported && vm.CtrlIfaceStatus is { } ci)
        {
            if (ci.UartLive && !excludeUartSelf)
            {
                Claim(vm.UartCtrlConfig.TxPin, "UART TX", PinRole.Control, PageControlInterfaces);
                Claim(vm.UartCtrlConfig.RxPin, "UART RX", PinRole.Control, PageControlInterfaces);
            }
            if (ci.I2cLive && !excludeI2cSelf)
            {
                Claim(vm.I2cCtrlConfig.SdaPin, "I2C SDA", PinRole.Control, PageControlInterfaces);
                Claim(vm.I2cCtrlConfig.SclPin, "I2C SCL", PinRole.Control, PageControlInterfaces);
            }
        }

        // External DAC mute pin (V10+): only claim when supported AND
        // configured with a real pin. The "No Pin" sentinel disables
        // the feature without burning a GPIO.
        if (vm.DacHwMuteSupported
            && !excludeDacMuteSelf
            && vm.DacHwMute.Pin != DacHwMuteConfig.PinNone)
            Claim(vm.DacHwMute.Pin, "DAC Mute", PinRole.Utility, PageDacMute);

        return owners;
    }

    /// <summary>Every claimed GPIO, in pin order. Walks the same valid-pin list
    /// the pickers offer and asks the one authority about each.</summary>
    public static IReadOnlyList<PinAssignment> ActiveAssignments(MainViewModel vm)
    {
        var claims = BuildAssignmentMap(vm);
        var rows = new List<PinAssignment>();
        foreach (byte pin in ValidPins)
            if (claims.TryGetValue(pin, out var claim)) rows.Add(claim);
        return rows;
    }

    /// <summary>Output-row metadata. Each row knows its slot index
    /// (-1 for PDM), its visible name and detail label, the colour
    /// used for the row's dot, and its factory-default pin.</summary>
    public sealed record PinOutput(
        int Id, string Name, string Detail, string Icon,
        byte DefaultPin, Windows.UI.Color Color, int SlotIndex);

    private static readonly PinOutput[] OutputsRp2350 =
    [
        new(0, "Output 1", "OUT 1/2", "", 6,
            Windows.UI.Color.FromArgb(255, 69, 194, 163), 0),
        new(1, "Output 2", "OUT 3/4", "", 7,
            Windows.UI.Color.FromArgb(255, 240, 196, 89), 1),
        new(2, "Output 3", "OUT 5/6", "", 8,
            Windows.UI.Color.FromArgb(255, 89, 140, 242), 2),
        new(3, "Output 4", "OUT 7/8", "", 9,
            Windows.UI.Color.FromArgb(255, 217, 115, 140), 3),
        new(4, "PDM",      "SUB OUT", "", 10,
            Windows.UI.Color.FromArgb(255, 186, 135, 243), -1),
    ];

    private static readonly PinOutput[] OutputsRp2040 =
    [
        new(0, "Output 1", "OUT 1/2", "", 6,
            Windows.UI.Color.FromArgb(255, 69, 194, 163), 0),
        new(1, "Output 2", "OUT 3/4", "", 7,
            Windows.UI.Color.FromArgb(255, 240, 196, 89), 1),
        new(2, "PDM",      "SUB OUT", "", 10,
            Windows.UI.Color.FromArgb(255, 186, 135, 243), -1),
    ];

    /// <summary>Get the platform-correct list of audio output rows.</summary>
    public static IReadOnlyList<PinOutput> AllPinOutputs(string platform) =>
        platform == "RP2350" ? OutputsRp2350 : OutputsRp2040;
}
