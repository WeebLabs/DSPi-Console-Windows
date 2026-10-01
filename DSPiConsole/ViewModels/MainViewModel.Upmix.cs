using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Stereo upmixer (firmware wire V27, RP2350 only) state and device orchestration
/// (opcodes 0x4A-0x4E). Derives Centre / Ls / Rs matrix source rows 2-4 from a
/// stereo input. Values push live per-param (0x4C, no read-modify-write races);
/// seeding from bulk or an explicit 0x4B fetch is guarded against write echo.
/// </summary>
public partial class MainViewModel
{
    /// <summary>True when the firmware carries the upmix bulk section (wire V25+)
    /// on an RP2350. Gates the Upmixer window and the derived matrix rows.</summary>
    [ObservableProperty]
    private bool _upmixSupported;

    [ObservableProperty]
    private bool _upmixEnabled;

    // Mode properties are plain ints matching the wire bytes; the enum type
    // names are shadowed by these property names inside the class, so literals
    // are used here. 1 = ADAPTIVE centre, 2 = ADAPTIVE surround (spec defaults).

    /// <summary>0 = Passive, 1 = Adaptive, 2 = Off (matches the wire byte; Off is
    /// last because the firmware appended it at V27 rather than renumbering).</summary>
    [ObservableProperty]
    private int _upmixCenterMode = 1;

    /// <summary>0 = Off, 1 = Passive, 2 = Adaptive (matches the wire byte).</summary>
    [ObservableProperty]
    private int _upmixSurroundMode = 2;

    [ObservableProperty] private float _upmixStrengthPct = UpmixLimits.StrengthDefaultPct;
    [ObservableProperty] private float _upmixCenterWidthPct = UpmixLimits.WidthDefaultPct;
    [ObservableProperty] private float _upmixThresholdPct = UpmixLimits.ThresholdDefaultPct;
    [ObservableProperty] private float _upmixAttackMs = UpmixLimits.AttackDefaultMs;
    [ObservableProperty] private float _upmixReleaseMs = UpmixLimits.ReleaseDefaultMs;
    [ObservableProperty] private float _upmixDetectorHpfHz = UpmixLimits.DetHpfDefaultHz;
    [ObservableProperty] private float _upmixSurroundDelayMs = UpmixLimits.SurDelayDefaultMs;
    [ObservableProperty] private float _upmixSurroundHpfHz = UpmixLimits.SurHpfDefaultHz;
    [ObservableProperty] private float _upmixSurroundLpfHz = UpmixLimits.SurLpfDefaultHz;
    [ObservableProperty] private float _upmixDecorrPct = UpmixLimits.DecorrDefaultPct;
    [ObservableProperty] private float _upmixPresenceDb = UpmixLimits.PresenceDefaultDb;

    /// <summary>Latest 0x4E telemetry snapshot (null before the first poll).</summary>
    [ObservableProperty]
    private UpmixStatus? _upmixStatus;

    // Set while seeding from bulk / fetch so the change-partials don't re-send
    // the just-read values back to the device.
    private bool _upmixSuppress;

    /// <summary>Rows 2-4 of the matrix are upmix-derived (C/Ls/Rs) rather than
    /// multichannel inputs 3-5: the upmixer only runs on a plain stereo input.</summary>
    public bool UpmixRowsActive =>
        UpmixSupported && UpmixEnabled && ActiveInputChannelCount == 2;

    /// <summary>Ls/Rs rows (3-4) are exposed only while the surround engine is on.</summary>
    public bool UpmixSurroundRowsActive =>
        UpmixRowsActive && UpmixSurroundMode != 0;   // 0 = surround OFF

    /// <summary>Row 2 (C) stays exposed with the centre engine off — withdrawing it
    /// would renumber Ls/Rs and silently repoint existing routing — but it carries
    /// no signal, so the matrix labels it rather than hiding it (spec section 3).</summary>
    public bool UpmixCenterRowSilent =>
        UpmixRowsActive && UpmixCenterMode == 2;     // 2 = centre OFF

    private void RaiseUpmixRowsChanged()
    {
        OnPropertyChanged(nameof(UpmixRowsActive));
        OnPropertyChanged(nameof(UpmixSurroundRowsActive));
        OnPropertyChanged(nameof(UpmixCenterRowSilent));
    }

    private void PushUpmixParam(ushort id, float value)
    {
        if (_upmixSuppress) return;
        // The ordered queue, which the window's live sends share.
        DeviceWrite(() => _device.SetUpmixParam(id, value));
        CheckDirty();
    }

    /// <summary>An upmix parameter during a drag (device only); the property
    /// commits it.</summary>
    public void SendUpmixLive(ushort id, float value) => DeviceWrite(() => _device.SetUpmixParam(id, value));

    partial void OnUpmixEnabledChanged(bool value)
    {
        PushUpmixParam(UpmixParam.Enabled, value ? 1f : 0f);
        RaiseUpmixRowsChanged();
    }

    partial void OnUpmixCenterModeChanged(int value)
    {
        PushUpmixParam(UpmixParam.CenterMode, value);
        OnPropertyChanged(nameof(UpmixCenterRowSilent));
    }

    partial void OnUpmixSurroundModeChanged(int value)
    {
        PushUpmixParam(UpmixParam.SurroundMode, value);
        RaiseUpmixRowsChanged();
    }

    partial void OnUpmixStrengthPctChanged(float value) => PushUpmixParam(UpmixParam.Strength, value);
    partial void OnUpmixCenterWidthPctChanged(float value) => PushUpmixParam(UpmixParam.CenterWidth, value);
    partial void OnUpmixThresholdPctChanged(float value) => PushUpmixParam(UpmixParam.Threshold, value);
    partial void OnUpmixAttackMsChanged(float value) => PushUpmixParam(UpmixParam.Attack, value);
    partial void OnUpmixReleaseMsChanged(float value) => PushUpmixParam(UpmixParam.Release, value);
    partial void OnUpmixDetectorHpfHzChanged(float value) => PushUpmixParam(UpmixParam.DetectorHpf, value);
    partial void OnUpmixSurroundDelayMsChanged(float value) => PushUpmixParam(UpmixParam.SurroundDelay, value);
    partial void OnUpmixSurroundHpfHzChanged(float value) => PushUpmixParam(UpmixParam.SurroundHpf, value);
    partial void OnUpmixSurroundLpfHzChanged(float value) => PushUpmixParam(UpmixParam.SurroundLpf, value);
    partial void OnUpmixDecorrPctChanged(float value) => PushUpmixParam(UpmixParam.Decorr, value);
    partial void OnUpmixPresenceDbChanged(float value) => PushUpmixParam(UpmixParam.Presence, value);

    /// <summary>Apply a parsed config into the observable state without echoing
    /// writes back to the device. UI-thread only.</summary>
    private void ApplyUpmixConfig(UpmixConfig cfg)
    {
        _upmixSuppress = true;
        try
        {
            UpmixEnabled = cfg.Enabled;
            UpmixCenterMode = cfg.CenterMode;
            UpmixSurroundMode = cfg.SurroundMode;
            UpmixStrengthPct = cfg.StrengthPct;
            UpmixCenterWidthPct = cfg.CenterWidthPct;
            UpmixThresholdPct = cfg.CorrThresholdPct;
            UpmixAttackMs = cfg.AttackMs;
            UpmixReleaseMs = cfg.ReleaseMs;
            UpmixDetectorHpfHz = cfg.DetectorHpfHz;
            UpmixSurroundDelayMs = cfg.SurroundDelayMs;
            UpmixSurroundHpfHz = cfg.SurroundHpfHz;
            UpmixSurroundLpfHz = cfg.SurroundLpfHz;
            UpmixDecorrPct = cfg.DecorrPct;
            UpmixPresenceDb = cfg.PresenceDb;
        }
        finally { _upmixSuppress = false; }
        RaiseUpmixRowsChanged();
    }

    /// <summary>Seed upmix state from a bulk fetch (UI-thread dispatcher block).
    /// RP2040 carries the section as zeros, so also gate on the platform.</summary>
    internal void SeedUpmixFromBulk(BulkParams bp)
    {
        // The app is a V26 client of the upmixer (the presence control).
        UpmixSupported = bp.HasUpmix && bp.FormatVersion >= 26 && Platform == "RP2350";
        if (!UpmixSupported || bp.Upmix == null) return;
        ApplyUpmixConfig(bp.Upmix);
    }

    /// <summary>Re-read the applied upmix config from the device (0x4B). Blocking —
    /// call off the UI thread. The spec recommends this after preset load / bulk SET.</summary>
    public void FetchUpmix()
    {
        var cfg = _device.GetUpmixConfig();
        if (cfg == null) return;   // STALL: leave UpmixSupported to the bulk seed
        _dispatcher.TryEnqueue(() =>
        {
            if (UpmixSupported) ApplyUpmixConfig(cfg);
        });
    }

    /// <summary>Poll live telemetry (0x4E) into <see cref="UpmixStatus"/>.</summary>
    public async Task PollUpmixStatusAsync()
    {
        var status = await Task.Run(() => _device.GetUpmixStatus());
        if (status != null) UpmixStatus = status;
    }

    /// <summary>Current upmix state as a wire config (preset dirty tracking).</summary>
    internal UpmixConfig CaptureUpmixConfig() => new()
    {
        Enabled = UpmixEnabled,
        CenterMode = (byte)UpmixCenterMode,
        SurroundMode = (byte)UpmixSurroundMode,
        StrengthPct = UpmixStrengthPct,
        CenterWidthPct = UpmixCenterWidthPct,
        CorrThresholdPct = UpmixThresholdPct,
        AttackMs = UpmixAttackMs,
        ReleaseMs = UpmixReleaseMs,
        DetectorHpfHz = UpmixDetectorHpfHz,
        SurroundDelayMs = UpmixSurroundDelayMs,
        SurroundHpfHz = UpmixSurroundHpfHz,
        SurroundLpfHz = UpmixSurroundLpfHz,
        DecorrPct = UpmixDecorrPct,
        PresenceDb = UpmixPresenceDb,
    };
}
