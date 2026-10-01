using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Tube modeller (firmware wire V31) state and device orchestration (0x3E/0x3F).
/// One indexed SET/GET pair carries all fourteen parameters as float32. Values
/// push to the device as they change. The firmware's two cross-field rules are
/// mirrored, since the app never hears its own writes back: picking a type
/// loads that row into the four character values (bias, asymmetry, hardness,
/// sag), and changing one of them drops the type to Custom.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The feature is present (wire V31+). Gates the window.</summary>
    [ObservableProperty]
    private bool _tubeSupported;

    [ObservableProperty]
    private bool _tubeEnabled;

    /// <summary>Per-output mask; bit k processes output channel k.</summary>
    [ObservableProperty]
    private int _tubeOutputMask = TubeLimits.DefaultOutputMask;

    /// <summary>0 = Custom, 1..16 = a row of <see cref="TubeTables.Types"/>.</summary>
    [ObservableProperty]
    private int _tubeType = TubeLimits.DefaultType;

    [ObservableProperty]
    private float _tubeDriveDb = TubeLimits.DefaultDriveDb;

    [ObservableProperty]
    private float _tubeBiasPct = TubeLimits.DefaultBiasPct;

    [ObservableProperty]
    private float _tubeAsymDb = TubeLimits.DefaultAsymDb;

    [ObservableProperty]
    private float _tubeHardnessPct = TubeLimits.DefaultHardnessPct;

    [ObservableProperty]
    private float _tubeSagPct = TubeLimits.DefaultSagPct;

    [ObservableProperty]
    private int _tubeRectifier = TubeLimits.DefaultRectifier;

    [ObservableProperty]
    private bool _tubeXfmrEnabled = TubeLimits.DefaultXfmrEnabled;

    [ObservableProperty]
    private float _tubeXfmrDamping = TubeLimits.DefaultXfmrDamping;

    [ObservableProperty]
    private float _tubeXfmrResHz = TubeLimits.DefaultXfmrResHz;

    [ObservableProperty]
    private float _tubeMixPct = TubeLimits.DefaultMixPct;

    [ObservableProperty]
    private float _tubeTrimDb = TubeLimits.DefaultTrimDb;

    private bool _tubeSuppress;

    partial void OnTubeEnabledChanged(bool value) => TubeSend(TubeParam.Enabled, value ? 1 : 0);
    partial void OnTubeOutputMaskChanged(int value) => TubeSend(TubeParam.OutputMask, (ushort)value);
    partial void OnTubeDriveDbChanged(float value) => TubeSend(TubeParam.DriveDb, value);
    partial void OnTubeRectifierChanged(int value) => TubeSend(TubeParam.Rectifier, value);
    partial void OnTubeXfmrEnabledChanged(bool value) => TubeSend(TubeParam.XfmrEnabled, value ? 1 : 0);
    partial void OnTubeXfmrDampingChanged(float value) => TubeSend(TubeParam.XfmrDamping, value);
    partial void OnTubeXfmrResHzChanged(float value) => TubeSend(TubeParam.XfmrResHz, value);
    partial void OnTubeMixPctChanged(float value) => TubeSend(TubeParam.MixPct, value);
    partial void OnTubeTrimDbChanged(float value) => TubeSend(TubeParam.TrimDb, value);

    partial void OnTubeTypeChanged(int value)
    {
        if (_tubeSuppress) return;
        // The firmware loads the row itself; show it at once without sending
        // the four values, which would each drop the type back to Custom.
        if (value > 0 && value < TubeTables.Types.Count && TubeTables.Types[value] is { } row)
        {
            _tubeSuppress = true;
            try
            {
                TubeBiasPct = row.BiasPct;
                TubeAsymDb = row.AsymDb;
                TubeHardnessPct = row.HardnessPct;
                TubeSagPct = row.SagPct;
            }
            finally { _tubeSuppress = false; }
        }
        TubeSend(TubeParam.TubeType, value);
    }

    partial void OnTubeBiasPctChanged(float value) => TubeCharacterChanged(TubeParam.BiasPct, value);
    partial void OnTubeAsymDbChanged(float value) => TubeCharacterChanged(TubeParam.AsymDb, value);
    partial void OnTubeHardnessPctChanged(float value) => TubeCharacterChanged(TubeParam.HardnessPct, value);
    partial void OnTubeSagPctChanged(float value) => TubeCharacterChanged(TubeParam.SagPct, value);

    /// <summary>A real change to a character value makes the voicing Custom, as
    /// the firmware does; the type itself is not sent.</summary>
    private void TubeCharacterChanged(ushort index, float value)
    {
        if (_tubeSuppress) return;
        _tubeSuppress = true;
        try { TubeType = TubeLimits.TypeCustom; }
        finally { _tubeSuppress = false; }
        TubeSend(index, value);
    }

    private void TubeSend(ushort index, float value)
    {
        if (_tubeSuppress) return;
        Task.Run(() => _device.SetTubeParam(index, value));
        CheckDirty();
    }

    /// <summary>Toggle one output channel in the tube mask.</summary>
    public void SetTubeOutputChannel(int output, bool enabled)
    {
        if (output < 0 || output >= 16) return;
        int mask = TubeOutputMask;
        if (enabled) mask |= 1 << output; else mask &= ~(1 << output);
        TubeOutputMask = mask;
    }

    /// <summary>Seed tube state from a bulk read without re-sending. The image
    /// carries the character values as stored, so a saved Custom voicing stays.</summary>
    internal void SeedTubeFromBulk(BulkParams bp)
    {
        TubeSupported = bp.HasTube;
        if (!TubeSupported) return;
        _tubeSuppress = true;
        try
        {
            TubeEnabled = bp.TubeEnabled;
            TubeOutputMask = bp.TubeOutputMask;
            TubeType = bp.TubeType;
            TubeDriveDb = bp.TubeDriveDb;
            TubeBiasPct = bp.TubeBiasPct;
            TubeAsymDb = bp.TubeAsymDb;
            TubeHardnessPct = bp.TubeHardnessPct;
            TubeSagPct = bp.TubeSagPct;
            TubeRectifier = bp.TubeRectifier;
            TubeXfmrEnabled = bp.TubeXfmrEnabled;
            TubeXfmrDamping = bp.TubeXfmrDamping;
            TubeXfmrResHz = bp.TubeXfmrResHz;
            TubeMixPct = bp.TubeMixPct;
            TubeTrimDb = bp.TubeTrimDb;
        }
        finally { _tubeSuppress = false; }
    }

    /// <summary>Re-read tube state from the device. Blocking: call off the UI
    /// thread. Clears <see cref="TubeSupported"/> if the first GET STALLs.</summary>
    public void FetchTube()
    {
        var values = new float?[TubeParam.Count];
        values[0] = _device.GetTubeParam(TubeParam.Enabled);
        if (values[0] == null)
        {
            _dispatcher.TryEnqueue(() => TubeSupported = false);
            return;
        }
        for (ushort i = 1; i < TubeParam.Count; i++) values[i] = _device.GetTubeParam(i);

        _dispatcher.TryEnqueue(() =>
        {
            _tubeSuppress = true;
            try
            {
                TubeSupported = true;
                float V(ushort i, float fallback) => values[i] ?? fallback;
                TubeEnabled = V(TubeParam.Enabled, 0) != 0;
                TubeOutputMask = (int)V(TubeParam.OutputMask, TubeOutputMask);
                TubeType = (int)Math.Round(V(TubeParam.TubeType, TubeType));
                TubeDriveDb = V(TubeParam.DriveDb, TubeDriveDb);
                TubeBiasPct = V(TubeParam.BiasPct, TubeBiasPct);
                TubeAsymDb = V(TubeParam.AsymDb, TubeAsymDb);
                TubeHardnessPct = V(TubeParam.HardnessPct, TubeHardnessPct);
                TubeSagPct = V(TubeParam.SagPct, TubeSagPct);
                TubeRectifier = (int)Math.Round(V(TubeParam.Rectifier, TubeRectifier));
                TubeXfmrEnabled = V(TubeParam.XfmrEnabled, TubeXfmrEnabled ? 1 : 0) != 0;
                TubeXfmrDamping = V(TubeParam.XfmrDamping, TubeXfmrDamping);
                TubeXfmrResHz = V(TubeParam.XfmrResHz, TubeXfmrResHz);
                TubeMixPct = V(TubeParam.MixPct, TubeMixPct);
                TubeTrimDb = V(TubeParam.TrimDb, TubeTrimDb);
            }
            finally { _tubeSuppress = false; }
            CheckDirty();
        });
    }
}
