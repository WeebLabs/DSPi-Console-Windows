using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Subharmonic synthesizer (subharm, firmware wire V29, extended V30) state and
/// device orchestration (0x10-0x1F, 0x2C-0x2F, 0xA9-0xAE). One global parameter
/// set applied per output channel via a 16-bit mask, like psybass. Values push
/// to the device as they change; seeding from a bulk read or a fetch is guarded
/// so it doesn't echo writes. The effect adds gain, and the firmware computes an
/// exact worst-case figure on every headroom GET, so each change that can move
/// it is followed by a re-read rather than an estimate.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The feature is present (wire V29+). Gates the window.</summary>
    [ObservableProperty]
    private bool _subharmSupported;

    /// <summary>The V30 controls: third band, selectivity, ceiling, pair link,
    /// solo and the meter. Their GETs STALL on V29 firmware.</summary>
    [ObservableProperty]
    private bool _subharmExtendedSupported;

    [ObservableProperty]
    private bool _subharmEnabled;

    [ObservableProperty]
    private float _subharmLowDb = SubharmLimits.DefaultLowDb;

    [ObservableProperty]
    private float _subharmHighDb = SubharmLimits.DefaultHighDb;

    [ObservableProperty]
    private float _subharmTopDb = SubharmLimits.DefaultTopDb;

    [ObservableProperty]
    private float _subharmBoostDb = SubharmLimits.DefaultBoostDb;

    /// <summary>Per-output mask; bit k processes output channel k.</summary>
    [ObservableProperty]
    private int _subharmOutputMask = SubharmLimits.DefaultOutputMask;

    [ObservableProperty]
    private int _subharmSelectMode = SubharmLimits.DefaultSelectMode;

    [ObservableProperty]
    private float _subharmSelectDepthPct = SubharmLimits.DefaultDepthPct;

    [ObservableProperty]
    private float _subharmSelectHoldMs = SubharmLimits.DefaultHoldMs;

    /// <summary>Soft limit on the sub before it is mixed in (dBFS); 0 = off.</summary>
    [ObservableProperty]
    private float _subharmCeilingDb = SubharmLimits.DefaultCeilingDb;

    [ObservableProperty]
    private bool _subharmLinkPairs = SubharmLimits.DefaultLinkPairs;

    /// <summary>Runtime-only monitor: masked outputs carry the sub alone. Never
    /// saved and not in the bulk image, so it is read back with its own GET and
    /// should be cleared when the window closes.</summary>
    [ObservableProperty]
    private bool _subharmSolo;

    /// <summary>Worst-case gain of the live configuration (dB), read back after
    /// every change that can move it. Derived, so not part of a preset.</summary>
    [ObservableProperty]
    private float _subharmHeadroomDb;

    private bool _subharmSuppress;

    partial void OnSubharmEnabledChanged(bool value) => SubharmSend(() => _device.SetSubharmEnabled(value), headroom: true);
    partial void OnSubharmLowDbChanged(float value) => SubharmSend(() => _device.SetSubharmLow(value), headroom: true);
    partial void OnSubharmHighDbChanged(float value) => SubharmSend(() => _device.SetSubharmHigh(value), headroom: true);
    partial void OnSubharmTopDbChanged(float value) => SubharmSend(() => _device.SetSubharmTop(value), headroom: true);
    partial void OnSubharmBoostDbChanged(float value) => SubharmSend(() => _device.SetSubharmBoost(value), headroom: true);
    partial void OnSubharmSelectModeChanged(int value) => SubharmSend(() => _device.SetSubharmSelectMode(value), headroom: true);
    partial void OnSubharmSelectDepthPctChanged(float value) => SubharmSend(() => _device.SetSubharmSelectDepth(value), headroom: true);
    partial void OnSubharmSelectHoldMsChanged(float value) => SubharmSend(() => _device.SetSubharmSelectHold(value), headroom: true);
    partial void OnSubharmCeilingDbChanged(float value) => SubharmSend(() => _device.SetSubharmCeiling(value), headroom: true);
    // The mask and the pair link are read live per packet and cannot change the
    // worst-case gain (a mono sum is never louder than the louder channel).
    partial void OnSubharmOutputMaskChanged(int value) => SubharmSend(() => _device.SetSubharmMask((ushort)value), headroom: false);
    partial void OnSubharmLinkPairsChanged(bool value) => SubharmSend(() => _device.SetSubharmLinkPairs(value), headroom: false);

    partial void OnSubharmSoloChanged(bool value)
    {
        if (_subharmSuppress) return;
        Task.Run(() => _device.SetSubharmSolo(value));
    }

    private void SubharmSend(Func<bool> send, bool headroom)
    {
        if (_subharmSuppress) return;
        Task.Run(() =>
        {
            send();
            // The GET is ordered behind the SET on the bus, so it reflects it.
            if (headroom) FetchSubharmHeadroom();
        });
        CheckDirty();
    }

    /// <summary>Toggle one output channel in the subharm mask.</summary>
    public void SetSubharmOutputChannel(int output, bool enabled)
    {
        if (output < 0 || output >= 16) return;
        int mask = SubharmOutputMask;
        if (enabled) mask |= 1 << output; else mask &= ~(1 << output);
        SubharmOutputMask = mask;
    }

    /// <summary>Seed subharm state from a bulk read without re-sending.</summary>
    internal void SeedSubharmFromBulk(BulkParams bp)
    {
        SubharmSupported = bp.HasSubharm;
        SubharmExtendedSupported = bp.HasSubharmExtended;
        if (!SubharmSupported) return;
        _subharmSuppress = true;
        try
        {
            SubharmEnabled = bp.SubharmEnabled;
            SubharmOutputMask = bp.SubharmOutputMask;
            SubharmLowDb = bp.SubharmLowDb;
            SubharmHighDb = bp.SubharmHighDb;
            SubharmBoostDb = bp.SubharmBoostDb;
            if (SubharmExtendedSupported)
            {
                SubharmTopDb = bp.SubharmTopDb;
                SubharmSelectDepthPct = bp.SubharmSelectDepthPct;
                SubharmSelectHoldMs = bp.SubharmSelectHoldMs;
                SubharmCeilingDb = bp.SubharmCeilingDb;
                SubharmSelectMode = bp.SubharmSelectMode;
                SubharmLinkPairs = bp.SubharmLinkPairs;
            }
        }
        finally { _subharmSuppress = false; }
    }

    /// <summary>Re-read subharm state from the device. Blocking: call off the UI
    /// thread. Clears <see cref="SubharmSupported"/> if the enable GET STALLs.</summary>
    public void FetchSubharm()
    {
        var enabled = _device.GetSubharmEnabled();
        if (enabled == null)
        {
            _dispatcher.TryEnqueue(() => { SubharmSupported = false; SubharmExtendedSupported = false; });
            return;
        }
        var low = _device.GetSubharmLow();
        var high = _device.GetSubharmHigh();
        var boost = _device.GetSubharmBoost();
        var mask = _device.GetSubharmMask();
        var top = _device.GetSubharmTop();
        bool extended = top != null;
        var mode = extended ? _device.GetSubharmSelectMode() : null;
        var depth = extended ? _device.GetSubharmSelectDepth() : null;
        var hold = extended ? _device.GetSubharmSelectHold() : null;
        var ceiling = extended ? _device.GetSubharmCeiling() : null;
        var link = extended ? _device.GetSubharmLinkPairs() : null;
        var headroom = _device.GetSubharmHeadroom();

        _dispatcher.TryEnqueue(() =>
        {
            _subharmSuppress = true;
            try
            {
                SubharmSupported = true;
                SubharmExtendedSupported = extended;
                SubharmEnabled = enabled.Value;
                if (low.HasValue) SubharmLowDb = low.Value;
                if (high.HasValue) SubharmHighDb = high.Value;
                if (boost.HasValue) SubharmBoostDb = boost.Value;
                if (mask.HasValue) SubharmOutputMask = mask.Value;
                if (top.HasValue) SubharmTopDb = top.Value;
                if (mode.HasValue) SubharmSelectMode = mode.Value;
                if (depth.HasValue) SubharmSelectDepthPct = depth.Value;
                if (hold.HasValue) SubharmSelectHoldMs = hold.Value;
                if (ceiling.HasValue) SubharmCeilingDb = ceiling.Value;
                if (link.HasValue) SubharmLinkPairs = link.Value;
                if (headroom.HasValue) SubharmHeadroomDb = headroom.Value;
            }
            finally { _subharmSuppress = false; }
            CheckDirty();
        });
    }

    /// <summary>Re-read the derived headroom figure. Blocking.</summary>
    public void FetchSubharmHeadroom()
    {
        if (_device.GetSubharmHeadroom() is { } db)
            _dispatcher.TryEnqueue(() => SubharmHeadroomDb = db);
    }

    /// <summary>Read back the two values the bulk image does not carry: solo
    /// (runtime only) and the headroom (derived). For when the window opens.
    /// Blocking.</summary>
    public void FetchSubharmRuntimeState()
    {
        if (!SubharmSupported) return;
        FetchSubharmHeadroom();
        if (SubharmExtendedSupported && _device.GetSubharmSolo() is { } solo)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _subharmSuppress = true;
                try { SubharmSolo = solo; }
                finally { _subharmSuppress = false; }
            });
        }
    }
}
