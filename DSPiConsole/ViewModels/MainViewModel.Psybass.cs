using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Psychoacoustic bass (psybass, firmware wire V23) state and device orchestration
/// (opcodes 0x30–0x3D). One global parameter set applied per output channel via a
/// 16-bit mask. Values push to the device live on change (like loudness); seeding
/// from a bulk fetch or an explicit fetch is guarded so it doesn't echo writes.
/// </summary>
public partial class MainViewModel
{
    /// <summary>True once the feature is present (bulk wire V23+, or a 0x31 probe
    /// answers). Gates the Psychoacoustic Bass window.</summary>
    [ObservableProperty]
    private bool _psybassSupported;

    [ObservableProperty]
    private bool _psybassEnabled;

    [ObservableProperty]
    private float _psybassCutoffHz = PsybassLimits.CutoffDefaultHz;

    [ObservableProperty]
    private float _psybassHarmonicsDb = PsybassLimits.HarmonicsDefaultDb;

    [ObservableProperty]
    private float _psybassDriveDb = PsybassLimits.DriveDefaultDb;

    [ObservableProperty]
    private float _psybassCharacterPct = PsybassLimits.CharacterDefaultPct;

    [ObservableProperty]
    private float _psybassOriginalDb = PsybassLimits.OriginalDefaultDb;

    /// <summary>Per-output mask; bit k processes output channel k. Held as int to
    /// match the other mask properties' UI binding.</summary>
    [ObservableProperty]
    private int _psybassOutputMask = PsybassLimits.DefaultOutputMask;

    // Set while seeding from bulk / fetch so the change-partials don't re-send the
    // just-read values back to the device.
    private bool _psybassSuppress;

    partial void OnPsybassEnabledChanged(bool value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassEnabled(value));
        CheckDirty();
    }

    partial void OnPsybassCutoffHzChanged(float value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassCutoff(value));
        CheckDirty();
    }

    partial void OnPsybassHarmonicsDbChanged(float value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassHarmonics(value));
        CheckDirty();
    }

    partial void OnPsybassDriveDbChanged(float value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassDrive(value));
        CheckDirty();
    }

    partial void OnPsybassCharacterPctChanged(float value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassCharacter(value));
        CheckDirty();
    }

    partial void OnPsybassOriginalDbChanged(float value)
    {
        if (_psybassSuppress) return;
        DeviceWrite(() => _device.SetPsybassOriginal(value));
        CheckDirty();
    }

    partial void OnPsybassOutputMaskChanged(int value)
    {
        if (_psybassSuppress) return;
        ushort mask = (ushort)value;
        DeviceWrite(() => _device.SetPsybassMask(mask));
        CheckDirty();
    }

    /// <summary>Toggle one output channel in the psybass mask.</summary>
    public void SetPsybassOutputChannel(int output, bool enabled)
    {
        if (output < 0 || output >= 16) return;
        int mask = PsybassOutputMask;
        if (enabled) mask |= (1 << output); else mask &= ~(1 << output);
        PsybassOutputMask = mask;
    }

    /// <summary>Seed all psybass state from a bulk fetch without re-sending.</summary>
    internal void SeedPsybassFromBulk(BulkParams bp)
    {
        PsybassSupported = bp.HasPsybass && bp.FormatVersion >= 23;
        if (!PsybassSupported) return;
        _psybassSuppress = true;
        try
        {
            PsybassEnabled = bp.PsybassEnabled;
            PsybassOutputMask = bp.PsybassOutputMask;
            PsybassCutoffHz = bp.PsybassCutoffHz;
            PsybassHarmonicsDb = bp.PsybassHarmonicsDb;
            PsybassDriveDb = bp.PsybassDriveDb;
            PsybassCharacterPct = bp.PsybassCharacterPct;
            PsybassOriginalDb = bp.PsybassOriginalDb;
        }
        finally { _psybassSuppress = false; }
    }

    /// <summary>Re-read psybass state from the device (0x31–0x3D). Blocking — call
    /// off the UI thread. Clears <see cref="PsybassSupported"/> if 0x31 STALLs.</summary>
    public void FetchPsybass()
    {
        var enabled = _device.GetPsybassEnabled();
        if (enabled == null)
        {
            _dispatcher.TryEnqueue(() => PsybassSupported = false);
            return;
        }
        var cutoff = _device.GetPsybassCutoff();
        var harmonics = _device.GetPsybassHarmonics();
        var drive = _device.GetPsybassDrive();
        var character = _device.GetPsybassCharacter();
        var original = _device.GetPsybassOriginal();
        var mask = _device.GetPsybassMask();

        _dispatcher.TryEnqueue(() =>
        {
            _psybassSuppress = true;
            try
            {
                PsybassSupported = true;
                PsybassEnabled = enabled.Value;
                if (cutoff.HasValue) PsybassCutoffHz = cutoff.Value;
                if (harmonics.HasValue) PsybassHarmonicsDb = harmonics.Value;
                if (drive.HasValue) PsybassDriveDb = drive.Value;
                if (character.HasValue) PsybassCharacterPct = character.Value;
                if (original.HasValue) PsybassOriginalDb = original.Value;
                if (mask.HasValue) PsybassOutputMask = mask.Value;
            }
            finally { _psybassSuppress = false; }
        });
    }
}
