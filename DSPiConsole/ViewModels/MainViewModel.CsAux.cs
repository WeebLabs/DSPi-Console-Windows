using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Control Surfaces auxiliary outputs (caps v18): the live on/off flag and level
/// of each slot holding an aux output. These are runtime values, never part of
/// the preview or of Save (the boot values live in the binding), read with the
/// rest of the config and kept current by NOTIFY_EVT_CS_AUX, which carries both
/// values whatever moved them: the editor, a bound control, a macro.
/// </summary>
public partial class MainViewModel
{
    private readonly bool[] _csAuxOn = new bool[CsLimits.MaxBindings];
    private readonly ushort[] _csAuxLevelQ8 = new ushort[CsLimits.MaxBindings];

    /// <summary>Full scale of an aux level: 100 % in 8.8 fixed point.</summary>
    public const ushort CsAuxLevelMaxQ8 = 25600;

    public bool CsAuxSupported => _csCaps?.HasAux == true;
    public IReadOnlyList<bool> CsAuxOn => _csAuxOn;
    public IReadOnlyList<ushort> CsAuxLevelQ8 => _csAuxLevelQ8;

    /// <summary>An aux output's name: its slot name, else "Aux n".</summary>
    public string CsAuxName(int slot) =>
        slot >= 0 && slot < _csNames.Length && !string.IsNullOrWhiteSpace(_csNames[slot])
            ? _csNames[slot].Trim() : $"Aux {slot + 1}";

    /// <summary>A slot's live aux state changed (argument: the slot); raised on
    /// the UI thread.</summary>
    public event Action<int>? CsAuxChanged;

    /// <summary>Slots whose stored binding a save rewrote (an aux output's
    /// boot fields); raised on the UI thread.</summary>
    public event Action<IReadOnlyList<int>>? CsBindingsReread;

    private void HookCsAuxNotifications()
    {
        _device.CsAuxNotified += (_, n) => _dispatcher.TryEnqueue(() =>
        {
            if (n.Slot >= CsLimits.MaxBindings) return;
            _csAuxOn[n.Slot] = n.On;
            _csAuxLevelQ8[n.Slot] = n.LevelQ8;
            CsAuxChanged?.Invoke(n.Slot);
        });
    }

    /// <summary>Re-read every slot's live aux state. Blocking; call off the UI thread.</summary>
    public void RefreshCsAux()
    {
        if (!CsAuxSupported || _device.GetCsAuxStates() is not { } read) return;
        _dispatcher.TryEnqueue(() =>
        {
            for (int i = 0; i < CsLimits.MaxBindings; i++)
            {
                bool changed = _csAuxOn[i] != read.On[i] || _csAuxLevelQ8[i] != read.LevelQ8[i];
                _csAuxOn[i] = read.On[i];
                _csAuxLevelQ8[i] = read.LevelQ8[i];
                if (changed) CsAuxChanged?.Invoke(i);
            }
        });
    }

    /// <summary>Switch an aux output now. Runtime only: saving folds it into the
    /// boot state only for a slot set to remember it.</summary>
    public void SetCsAuxOn(int slot, bool on)
    {
        if (slot < 0 || slot >= CsLimits.MaxBindings) return;
        _csAuxOn[slot] = on;
        DeviceWrite(() => _device.SetCsAuxState(slot, on));
    }

    /// <summary>Set a dimmable aux output's level now, 8.8 percent. Also the
    /// live send of a level slider's drag.</summary>
    public void SetCsAuxLevel(int slot, ushort levelQ8)
    {
        if (slot < 0 || slot >= CsLimits.MaxBindings) return;
        levelQ8 = Math.Min(levelQ8, CsAuxLevelMaxQ8);
        _csAuxLevelQ8[slot] = levelQ8;
        DeviceWrite(() => _device.SetCsAuxLevel(slot, levelQ8));
    }
}
