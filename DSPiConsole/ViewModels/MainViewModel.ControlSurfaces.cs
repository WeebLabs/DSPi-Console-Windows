using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Control Surfaces + IR remote state and device orchestration (firmware
/// 0x84–0x8F, 0x9D/0x9E, 0x20–0x26 for caps-v9 groups and macros, and 0x27–0x2B
/// for the caps-v10 I2C display). The whole editor is caps-driven: we probe the
/// caps header + per-noun descriptors once, then read the 16 binding slots, 16
/// slot names, up to 16 IR command sub-slots, the 8 target groups and 8 macros,
/// and the display config and its page table — every count from the device.
///
/// <para>Three-tier persistence: every SET is a live-only <b>preview</b> that
/// applies immediately (device <c>Dirty</c>=true) but is RAM-only; <see cref="CsSave"/>
/// writes the whole config to flash; <see cref="CsRevert"/> discards the preview.
/// A local clean baseline gives net-zero dirty suppression so add-then-remove
/// doesn't strand the Save bar.</para>
///
/// <para>These methods block (they poll the deferred-apply status) — call them
/// off the UI thread (the window wraps them in <c>Task.Run</c>, matching the
/// ADAT settings page).</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>True once the caps probe (0x86) answers. Older firmware STALLs,
    /// which hides the whole feature.</summary>
    [ObservableProperty]
    private bool _controlSurfacesSupported;

    private CsCapsHeader? _csCaps;
    private CsNounDesc?[] _csNounDescs = Array.Empty<CsNounDesc?>();
    private CsBinding[] _csBindings = NewClearedBindings();
    private string[] _csNames = NewEmptyNames();
    private IrCommand[] _csIrCommands = NewEmptyIrCommands();
    private CsGroup[] _csGroups = NewEmptyGroups();
    private CsMacro[] _csMacros = NewEmptyMacros();
    private CsStatusPacket? _csStatus;
    private CsExtStatusPacket? _csExtStatus;

    // I2C display (caps v10). The panel's component lives in the ordinary binding
    // table; what it shows is this config plus a page table, both device-global
    // and under the same preview / Save / Revert as everything else here.
    private CsDisplayCfg _csDisplayCfg = new();
    private CsDisplayPage[] _csDisplayPages = NewEmptyDisplayPages();
    private CsDisplayStatus _csDisplayStatus = new();
    private byte _csDisplayMaxPages;
    private byte _csDisplayModelCount;

    // Local clean baseline (wire bytes) captured whenever the device reports a
    // non-dirty state — used for net-zero dirty suppression.
    private CsBinding[]? _csCleanBindings;
    private string[]? _csCleanNames;
    private IrCommand[]? _csCleanIrCommands;
    private CsGroup[]? _csCleanGroups;
    private CsMacro[]? _csCleanMacros;
    private CsDisplayCfg? _csCleanDisplayCfg;
    private CsDisplayPage[]? _csCleanDisplayPages;

    /// <summary>Serializes the deferred CS writes. Individual control transfers are
    /// already locked, but a deferred SET is a <i>sequence</i> — the OUT, then a
    /// poll of the shared <c>LastStatus</c>/<c>LastSlot</c> pair until it names the
    /// op. Two sequences interleaving (Apply on a binding while a group Apply is
    /// still polling, say) let one overwrite the verdict the other is waiting for,
    /// which surfaces as a spurious "Applying…" or BUSY. One writer at a time.</summary>
    private readonly object _csWriteLock = new();

    public CsCapsHeader? CsCaps => _csCaps;
    public IReadOnlyList<CsNounDesc?> CsNounDescs => _csNounDescs;
    public IReadOnlyList<CsBinding> CsBindings => _csBindings;
    public IReadOnlyList<string> CsNames => _csNames;
    public IReadOnlyList<IrCommand> CsIrCommands => _csIrCommands;
    public IReadOnlyList<CsGroup> CsGroups => _csGroups;
    public IReadOnlyList<CsMacro> CsMacros => _csMacros;
    public CsStatusPacket? CsStatus => _csStatus;
    public CsExtStatusPacket? CsExtStatus => _csExtStatus;
    public CsDisplayCfg CsDisplayCfg => _csDisplayCfg;
    public IReadOnlyList<CsDisplayPage> CsDisplayPages => _csDisplayPages;
    public CsDisplayStatus CsDisplayStatus => _csDisplayStatus;

    /// <summary>Outcome of the last display config or page write. Those apply as
    /// they are edited, with no Apply button to report against, so without this a
    /// rejection would just make the row snap back with nothing said.</summary>
    [ObservableProperty]
    private byte _csDisplayLastStatus = DSPiConsole.Core.Models.CsStatus.Success;

    /// <summary>Number of usable binding slots (min of caps + local cap of 16).</summary>
    public int CsSlotCount => Math.Min((int)(_csCaps?.MaxBindings ?? CsLimits.MaxBindings), CsLimits.MaxBindings);

    /// <summary>Number of usable IR command sub-slots.</summary>
    public int CsIrMax => Math.Min((int)(_csCaps?.MaxIrCommands ?? 0), CsLimits.MaxIrCommands);

    /// <summary>Whether this firmware serves target groups and macros (caps v9).
    /// A pre-v9 header reports 0 in those bytes, which hides both sections.</summary>
    public bool CsGroupsSupported => _csCaps?.HasGroupsAndMacros == true;

    /// <summary>Number of usable group slots.</summary>
    public int CsGroupMax => Math.Min((int)(_csCaps?.MaxGroups ?? 0), CsLimits.MaxGroups);

    /// <summary>Number of usable macro slots.</summary>
    public int CsMacroMax => Math.Min((int)(_csCaps?.MaxMacros ?? 0), CsLimits.MaxMacros);

    /// <summary>Steps a macro may hold.</summary>
    public int CsMacroStepMax => Math.Min((int)(_csCaps?.MaxMacroSteps ?? 0), CsLimits.MaxMacroSteps);

    /// <summary>Macro currently running on the device, or null when idle.</summary>
    public int? CsRunningMacro =>
        _csExtStatus is { IsMacroRunning: true } e ? e.MacroRunning : null;

    /// <summary>Whether IR remote commands are available (a receiver type exists
    /// and the firmware advertises IR command slots).</summary>
    public bool CsIrSupported =>
        _csCaps != null && _csCaps.MaxIrCommands > 0 && _csCaps.TypeCount > (int)CsType.Ir;

    /// <summary>Whether this firmware offers the I2C display component (caps v10).
    /// Read from the caps type table, not the version — the same self-describing
    /// rule the rest of the editor follows.</summary>
    public bool CsDisplaySupported => _csCaps?.HasDisplay == true;

    /// <summary>Whether an IR command may address a group (caps v10). Bundled with
    /// the display component, so the type table answers for it too: a pre-v10
    /// device rejects a grouped IR record at validation.</summary>
    public bool CsIrGroupsSupported => CsDisplaySupported && CsGroupsSupported;

    /// <summary>Whether the panel honours the per-line alignment fields (caps
    /// v11). Nothing about the record changes, so the version is the only signal —
    /// a v10 device rejects a config carrying either field set.</summary>
    public bool CsDisplayAlignSupported => CsDisplaySupported && _csCaps?.CapsVersion >= 11;

    /// <summary>Whether a PWM LED may carry a brightness ceiling (caps v12). The
    /// byte was reserved before, and a pre-v12 device rejects a binding with
    /// anything in it, so the field stays hidden — and zero — below v12.</summary>
    public bool CsLedBrightnessSupported => _csCaps?.CapsVersion >= 12;

    /// <summary>Whether a display page may carry a level bar (caps v13), by the
    /// same reasoning as the alignment fields.</summary>
    public bool CsDisplayBarSupported => CsDisplaySupported && _csCaps?.CapsVersion >= 13;

    /// <summary>Page slots to show — the device's own count, clamped to what the
    /// app allocates.</summary>
    public int CsDisplayPageCount =>
        _csDisplayMaxPages > 0
            ? Math.Min((int)_csDisplayMaxPages, CsLimits.MaxDisplayPages)
            : CsLimits.MaxDisplayPages;

    /// <summary>Panel models the device serves, clamped to the ones this build
    /// knows names for.</summary>
    public int CsDisplayModelCount =>
        _csDisplayModelCount > 0
            ? Math.Min((int)_csDisplayModelCount, CsDisplayModels.Count)
            : CsDisplayModels.Count;

    /// <summary>The binding slot holding the display component, or null when none
    /// is configured. One panel per device, so the first match is the answer.</summary>
    public int? CsDisplaySlot
    {
        get
        {
            for (int s = 0; s < CsSlotCount && s < _csBindings.Length; s++)
                if (_csBindings[s].Type == CsType.Display) return s;
            return null;
        }
    }

    /// <summary>Per-noun descriptor, or null if the noun index is out of range /
    /// unavailable on this platform.</summary>
    public CsNounDesc? CsNounDescFor(int noun) =>
        noun >= 0 && noun < _csNounDescs.Length ? _csNounDescs[noun] : null;

    /// <summary>Unsaved-changes flag: the firmware's sticky dirty bit AND an actual
    /// difference from the local clean baseline (so add-then-remove nets to clean).</summary>
    public bool CsDirty => _csStatus?.Dirty == true && !MatchesCleanBaseline();

    /// <summary>Raised (on the dispatcher) after the whole config is (re)loaded from
    /// the device — the window rebuilds its cards in response.</summary>
    public event Action? ControlSurfacesReloaded;

    private static CsBinding[] NewClearedBindings()
    {
        var a = new CsBinding[CsLimits.MaxBindings];
        for (int i = 0; i < a.Length; i++) a[i] = CsBinding.Cleared();
        return a;
    }

    private static string[] NewEmptyNames()
    {
        var a = new string[CsLimits.MaxBindings];
        for (int i = 0; i < a.Length; i++) a[i] = "";
        return a;
    }

    private static IrCommand[] NewEmptyIrCommands()
    {
        var a = new IrCommand[CsLimits.MaxIrCommands];
        for (int i = 0; i < a.Length; i++) a[i] = new IrCommand();
        return a;
    }

    private static CsGroup[] NewEmptyGroups()
    {
        var a = new CsGroup[CsLimits.MaxGroups];
        for (int i = 0; i < a.Length; i++) a[i] = CsGroup.Cleared();
        return a;
    }

    private static CsMacro[] NewEmptyMacros()
    {
        var a = new CsMacro[CsLimits.MaxMacros];
        for (int i = 0; i < a.Length; i++) a[i] = new CsMacro();
        return a;
    }

    private static CsDisplayPage[] NewEmptyDisplayPages()
    {
        var a = new CsDisplayPage[CsLimits.MaxDisplayPages];
        for (int i = 0; i < a.Length; i++) a[i] = new CsDisplayPage();
        return a;
    }

    /// <summary>Channels addressable by a group of this kind — the widest
    /// <c>target_count</c> any noun advertises for that space, which is exactly
    /// what the firmware bounds a group's member mask against.</summary>
    public int CsChannelCount(CsTarget kind)
    {
        int max = 0;
        foreach (var nd in _csNounDescs)
        {
            if (nd == null || !nd.IsAvailable) continue;
            // DSP_BAND nouns address the same channel space as DSP_CH.
            var k = nd.TargetKind == CsTarget.DspBand ? CsTarget.DspCh : nd.TargetKind;
            if (k == kind && nd.TargetCount > max) max = nd.TargetCount;
        }
        return max;
    }

    /// <summary>Probe the feature and read the whole live config: caps header,
    /// per-noun descriptors, all binding slots + names, and IR command sub-slots.
    /// Sets <see cref="ControlSurfacesSupported"/> (false if the firmware STALLs).
    /// Blocking — call off the UI thread.</summary>
    public void FetchControlSurfaces()
    {
        var caps = _device.GetCsCaps();
        if (caps == null || !caps.IsValid)
        {
            _dispatcher.TryEnqueue(() => ControlSurfacesSupported = false);
            return;
        }

        var descs = new CsNounDesc?[caps.NounCount];
        for (int n = 0; n < caps.NounCount; n++)
            descs[n] = _device.GetCsNounDesc(n);

        int slots = Math.Min((int)caps.MaxBindings, CsLimits.MaxBindings);
        var bindings = NewClearedBindings();
        var names = NewEmptyNames();
        for (int s = 0; s < slots; s++)
        {
            bindings[s] = _device.GetCsBinding(s) ?? CsBinding.Cleared();
            names[s] = _device.GetCsName(s);
        }

        var irCmds = NewEmptyIrCommands();
        int irMax = Math.Min((int)caps.MaxIrCommands, CsLimits.MaxIrCommands);
        for (int i = 0; i < irMax; i++)
            irCmds[i] = _device.GetCsIrCommand(i) ?? new IrCommand();

        // Groups and macros (caps v9). A pre-v9 firmware advertises none and
        // STALLs 0x20-0x26, so the tables stay empty and the UI hides them.
        var groups = NewEmptyGroups();
        var macros = NewEmptyMacros();
        CsExtStatusPacket? ext = null;
        if (caps.HasGroupsAndMacros)
        {
            int groupMax = Math.Min((int)caps.MaxGroups, CsLimits.MaxGroups);
            for (int g = 0; g < groupMax; g++)
                groups[g] = _device.GetCsGroup(g) ?? CsGroup.Cleared();
            int macroMax = Math.Min((int)caps.MaxMacros, CsLimits.MaxMacros);
            for (int m = 0; m < macroMax; m++)
                macros[m] = _device.GetCsMacro(m) ?? new CsMacro();
            ext = _device.GetCsExtStatus();
        }

        // I2C display (caps v10). The config GET carries the page limit, so it
        // has to land before the page loop that reads against it.
        var displayCfg = new CsDisplayCfg();
        var displayPages = NewEmptyDisplayPages();
        var displayStatus = new CsDisplayStatus();
        byte displayMaxPages = 0, displayModelCount = 0;
        if (caps.HasDisplay)
        {
            var read = _device.GetCsDisplayCfg();
            if (read != null)
            {
                displayCfg = read.Cfg;
                displayMaxPages = read.MaxPages;
                displayModelCount = read.ModelCount;
                int pages = Math.Min((int)read.MaxPages, CsLimits.MaxDisplayPages);
                for (int i = 0; i < pages; i++)
                    displayPages[i] = _device.GetCsDisplayPage(i) ?? new CsDisplayPage();
            }
            displayStatus = _device.GetCsDisplayStatus() ?? displayStatus;
        }

        var status = _device.GetCsStatus();

        _csCaps = caps;
        _csNounDescs = descs;
        _csBindings = bindings;
        _csNames = names;
        _csIrCommands = irCmds;
        _csGroups = groups;
        _csMacros = macros;
        _csExtStatus = ext;
        _csDisplayCfg = displayCfg;
        _csDisplayPages = displayPages;
        _csDisplayStatus = displayStatus;
        _csDisplayMaxPages = displayMaxPages;
        _csDisplayModelCount = displayModelCount;
        _csStatus = status;
        if (status != null && !status.Dirty) CaptureCsCleanBaseline();

        _dispatcher.TryEnqueue(() =>
        {
            ControlSurfacesSupported = true;
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsExtStatus));
            OnPropertyChanged(nameof(CsDisplayCfg));
            OnPropertyChanged(nameof(CsDisplayStatus));
            // A refusal reported against the last device doesn't describe this one.
            CsDisplayLastStatus = DSPiConsole.Core.Models.CsStatus.Success;
            OnPropertyChanged(nameof(CsDirty));
            ControlSurfacesReloaded?.Invoke();
        });
    }

    /// <summary>Re-read the status packet (active/dirty masks, per-slot
    /// health) into <see cref="CsStatus"/>.</summary>
    public void RefreshCsStatus()
    {
        var status = _device.GetCsStatus();
        if (status == null) return;
        _csStatus = status;
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsDirty));
        });
    }

    /// <summary>Stage a binding (live preview) and re-read the slot + status.
    /// Returns the firmware CS status byte.</summary>
    public byte SetCsBinding(int slot, CsBinding binding)
    {
        lock (_csWriteLock)
        {
            byte result = _device.SetCsBinding(slot, binding);
            _csBindings[slot] = _device.GetCsBinding(slot) ?? CsBinding.Cleared();
            RefreshCsStatus();
            return result;
        }
    }

    /// <summary>Stage a slot name (live preview). Empty string clears it.</summary>
    public byte SetCsName(int slot, string name)
    {
        lock (_csWriteLock)
        {
            byte result = _device.SetCsName(slot, name ?? "");
            _csNames[slot] = _device.GetCsName(slot);
            RefreshCsStatus();
            return result;
        }
    }

    /// <summary>Stage an IR command sub-slot (live preview) and re-read it.</summary>
    public byte SetCsIrCommand(int sub, IrCommand cmd)
    {
        lock (_csWriteLock)
        {
            byte result = _device.SetCsIrCommand(sub, cmd);
            _csIrCommands[sub] = _device.GetCsIrCommand(sub) ?? new IrCommand();
            RefreshCsStatus();
            return result;
        }
    }

    /// <summary>Re-read the live panel state (came up / starting / not responding,
    /// the page on screen, the I2C abort count). False if the device didn't answer,
    /// so a caller polling the on-screen page can give up rather than keep polling
    /// a device that has gone.</summary>
    public bool RefreshCsDisplayStatus()
    {
        if (!CsDisplaySupported || !IsDeviceConnected) return false;
        var st = _device.GetCsDisplayStatus();
        if (st == null) return false;
        _csDisplayStatus = st;
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CsDisplayStatus)));
        return true;
    }

    /// <summary>Re-read the display config, its whole page table and the panel
    /// state. The firmware seeds a fresh page table (volume, preset, input source,
    /// sample rate) the first time a display attaches, so what the app holds is out
    /// of date the moment a display binding is applied — and the panel's state
    /// moves when one is removed.</summary>
    public void RefreshCsDisplay()
    {
        if (!CsDisplaySupported || !IsDeviceConnected) return;
        lock (_csWriteLock)
        {
            var read = _device.GetCsDisplayCfg();
            if (read != null)
            {
                _csDisplayCfg = read.Cfg;
                _csDisplayMaxPages = read.MaxPages;
                _csDisplayModelCount = read.ModelCount;
            }
            for (int i = 0; i < CsDisplayPageCount; i++)
                _csDisplayPages[i] = _device.GetCsDisplayPage(i) ?? new CsDisplayPage();
            _csDisplayStatus = _device.GetCsDisplayStatus() ?? _csDisplayStatus;
        }
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CsDisplayCfg));
            OnPropertyChanged(nameof(CsDisplayStatus));
            OnPropertyChanged(nameof(CsDirty));
        });
    }

    /// <summary>Stage the display config (live preview) and re-read it. Shares the
    /// write lock with every other deferred CS write: the firmware tags the config
    /// bare and pages 0x50 | page, so the config and page 0 report under the same
    /// byte and only one display write may be in flight at a time.</summary>
    public byte SetCsDisplayCfg(CsDisplayCfg cfg)
    {
        lock (_csWriteLock)
        {
            byte result = _device.SetCsDisplayCfg(cfg);
            var read = _device.GetCsDisplayCfg();
            if (read != null)
            {
                _csDisplayCfg = read.Cfg;
                _csDisplayMaxPages = read.MaxPages;
                _csDisplayModelCount = read.ModelCount;
            }
            RefreshCsStatus();
            RefreshCsDisplayStatus();
            PublishDisplayResult(result);
            return result;
        }
    }

    /// <summary>Stage one display page (live preview); an all-zero record clears
    /// the slot. Re-reads the page the device actually kept.</summary>
    public byte SetCsDisplayPage(int index, CsDisplayPage page)
    {
        if (index < 0 || index >= _csDisplayPages.Length)
            return DSPiConsole.Core.Models.CsStatus.InvalidPage;
        lock (_csWriteLock)
        {
            byte result = _device.SetCsDisplayPage(index, page);
            _csDisplayPages[index] = _device.GetCsDisplayPage(index) ?? new CsDisplayPage();
            RefreshCsStatus();
            RefreshCsDisplayStatus();
            PublishDisplayResult(result);
            return result;
        }
    }

    private void PublishDisplayResult(byte result) =>
        _dispatcher.TryEnqueue(() =>
        {
            CsDisplayLastStatus = result;
            OnPropertyChanged(nameof(CsDisplayCfg));
            OnPropertyChanged(nameof(CsDirty));
        });

    /// <summary>Re-read the group/macro status packet (table limits, per-slot
    /// validity, the running macro). False if the device didn't answer — a caller
    /// polling macro progress uses that to give up rather than poll a dead or
    /// unplugged device forever.</summary>
    public bool RefreshCsExtStatus()
    {
        if (!CsGroupsSupported || !IsDeviceConnected) return false;
        var ext = _device.GetCsExtStatus();
        if (ext == null) return false;
        _csExtStatus = ext;
        _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CsExtStatus)));
        return true;
    }

    /// <summary>Stage a target group (live preview) and re-read it. Applying a
    /// group re-validates every binding that references one, so the status packet
    /// (and any slot health it changed) is re-read too.</summary>
    public byte SetCsGroup(int idx, CsGroup group)
    {
        lock (_csWriteLock)
        {
            byte result = _device.SetCsGroup(idx, group);
            _csGroups[idx] = _device.GetCsGroup(idx) ?? CsGroup.Cleared();
            RefreshCsStatus();
            RefreshCsExtStatus();
            return result;
        }
    }

    /// <summary>Stage a whole macro: the changed steps first, then the header
    /// carrying the final step count, so a concurrently-fired macro never sees a
    /// count that exceeds its written steps (spec s3). Only records that differ
    /// from the live macro go over the wire, and the first failure stops the write
    /// and is returned.</summary>
    public byte SetCsMacro(int idx, CsMacro macro)
    {
        lock (_csWriteLock)
        {
            byte result = DSPiConsole.Core.Models.CsStatus.Success;
            var live = _csMacros[idx];
            var empty = new CsMacroStep();

            for (int s = 0; s < CsMacroStepMax; s++)
            {
                // Steps past the new count are cleared, so shortening a macro
                // leaves no stale tail behind.
                var step = s < macro.StepCount ? macro.Steps[s] : empty;
                if (s < live.Steps.Length && step.WireEquals(live.Steps[s])) continue;
                result = _device.SetCsMacroStep(idx, s, step);
                if (result != DSPiConsole.Core.Models.CsStatus.Success) break;
            }
            if (result == DSPiConsole.Core.Models.CsStatus.Success
                && (live.StepCount != macro.StepCount
                    || !string.Equals(live.Name, macro.Name, StringComparison.Ordinal)))
                result = _device.SetCsMacroHeader(idx, macro);

            _csMacros[idx] = _device.GetCsMacro(idx) ?? new CsMacro();
            RefreshCsStatus();
            RefreshCsExtStatus();
            return result;
        }
    }

    /// <summary>Fire a macro on the device. False if the firmware rejected it
    /// (bad index or step count) — the reason lands in the status packet.</summary>
    public bool CsMacroFire(int idx)
    {
        // A rejected fire writes the shared LastStatus/LastSlot pair, so it queues
        // behind any deferred write that is still polling for its own verdict.
        lock (_csWriteLock)
        {
            bool ok = _device.CsMacroFire(idx);
            RefreshCsStatus();
            RefreshCsExtStatus();
            return ok;
        }
    }

    /// <summary>Cancel the running macro. Steps already dispatched stand.</summary>
    public void CsMacroCancel()
    {
        lock (_csWriteLock)
        {
            _device.CsMacroCancel();
            RefreshCsExtStatus();
        }
    }

    /// <summary>Persist the whole live config to flash. On success re-captures the
    /// clean baseline so the pending-changes prompt clears.</summary>
    public byte CsSave()
    {
        // Nothing outstanding → no-op, so the batched save the settings prompt
        // issues once per staged entry only writes flash on the first call.
        if (!CsDirty) return DSPiConsole.Core.Models.CsStatus.Success;
        lock (_csWriteLock)
        {
            byte result = _device.CsSave();
            RefreshCsStatus();
            if (result == DSPiConsole.Core.Models.CsStatus.Success) CaptureCsCleanBaseline();
            _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(CsDirty)));
            return result;
        }
    }

    /// <summary>Discard the live preview, reload the stored config from flash, and
    /// re-read every slot / IR command / name.</summary>
    public byte CsRevert()
    {
        byte result;
        lock (_csWriteLock)
        {
            result = _device.CsRevert();

            int slots = CsSlotCount;
            for (int s = 0; s < slots; s++)
            {
                _csBindings[s] = _device.GetCsBinding(s) ?? CsBinding.Cleared();
                _csNames[s] = _device.GetCsName(s);
            }
            int irMax = CsIrMax;
            for (int i = 0; i < irMax; i++)
                _csIrCommands[i] = _device.GetCsIrCommand(i) ?? new IrCommand();
            for (int g = 0; g < CsGroupMax; g++)
                _csGroups[g] = _device.GetCsGroup(g) ?? CsGroup.Cleared();
            for (int m = 0; m < CsMacroMax; m++)
                _csMacros[m] = _device.GetCsMacro(m) ?? new CsMacro();
            if (CsGroupsSupported) _csExtStatus = _device.GetCsExtStatus() ?? _csExtStatus;
            // The display config and its pages ride the same revert.
            if (CsDisplaySupported)
            {
                var read = _device.GetCsDisplayCfg();
                if (read != null)
                {
                    _csDisplayCfg = read.Cfg;
                    _csDisplayMaxPages = read.MaxPages;
                    _csDisplayModelCount = read.ModelCount;
                }
                for (int i = 0; i < CsDisplayPageCount; i++)
                    _csDisplayPages[i] = _device.GetCsDisplayPage(i) ?? new CsDisplayPage();
                _csDisplayStatus = _device.GetCsDisplayStatus() ?? _csDisplayStatus;
            }

            var status = _device.GetCsStatus();
            _csStatus = status;
            if (status != null && !status.Dirty) CaptureCsCleanBaseline();
        }

        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(CsStatus));
            OnPropertyChanged(nameof(CsExtStatus));
            OnPropertyChanged(nameof(CsDisplayCfg));
            OnPropertyChanged(nameof(CsDisplayStatus));
            CsDisplayLastStatus = DSPiConsole.Core.Models.CsStatus.Success;
            OnPropertyChanged(nameof(CsDirty));
            ControlSurfacesReloaded?.Invoke();
        });
        return result;
    }

    /// <summary>Display name for a binding's channel target, using the same
    /// user-editable names the sidebar shows (so a rename is reflected here).
    /// Falls back to a plain positional label if the firmware advertises a
    /// target the app's channel model doesn't cover.</summary>
    public string CsTargetLabel(CsTarget kind, int index)
    {
        var ch = CsTargetChannel(kind, index);
        if (ch != null) return GetChannelName(ch);
        return kind switch
        {
            CsTarget.InputCh => $"Input {index + 1}",
            CsTarget.OutputCh => $"Output {index + 1}",
            CsTarget.Aux => $"Aux {index + 1}",
            _ => $"Channel {index + 1}",
        };
    }

    /// <summary>Display name for a group slot — its user-set name, or a positional
    /// fallback while it is unnamed.</summary>
    public string CsGroupLabel(int idx)
    {
        if (idx < 0 || idx >= _csGroups.Length) return $"Group {idx + 1}";
        var name = _csGroups[idx].Name;
        return string.IsNullOrWhiteSpace(name) ? $"Group {idx + 1}" : name;
    }

    /// <summary>Display name for a macro slot — its user-set name, or a positional
    /// fallback while it is unnamed.</summary>
    public string CsMacroLabel(int idx)
    {
        if (idx < 0 || idx >= _csMacros.Length) return $"Macro {idx + 1}";
        var name = _csMacros[idx].Name;
        return string.IsNullOrWhiteSpace(name) ? $"Macro {idx + 1}" : name;
    }

    /// <summary>What one display page shows, in the words the panel itself uses:
    /// the item's name, prefixed by its channel or group when it has one.</summary>
    public string CsDisplayPageSummary(CsDisplayPage page)
    {
        if (page == null || !page.IsActive) return "Empty";
        string noun = CsNounInfo.Name(page.Noun);
        var nd = CsNounDescFor(page.Noun);
        if (nd == null || !nd.IsTargeted) return noun;
        string where = page.IsGrouped
            ? CsGroupLabel(page.Target)
            : CsTargetLabel(nd.TargetKind, page.Target);
        return $"{where} {noun}";
    }

    /// <summary>Comma-separated member names for a group, e.g. "Front L, Front R".</summary>
    public string CsGroupMembersLabel(CsGroup group)
    {
        if (group == null || !group.IsConfigured) return "No channels";
        var parts = new List<string>();
        for (int ch = 0; ch < 32; ch++)
            if ((group.MemberMask & (1u << ch)) != 0) parts.Add(CsTargetLabel(group.Kind, ch));
        return parts.Count > 0 ? string.Join(", ", parts) : "No channels";
    }

    /// <summary>The app channel a CS target addresses, or null if out of range.
    /// INPUT_CH indexes the wire input region, OUTPUT_CH the output position, and
    /// DSP_CH / DSP_BAND the unified channel space (inputs then outputs).</summary>
    private Channel? CsTargetChannel(CsTarget kind, int index)
    {
        if (index < 0) return null;
        return kind switch
        {
            CsTarget.InputCh => index < Channel.AllInputs.Count ? Channel.AllInputs[index] : null,
            CsTarget.OutputCh => index < ActiveOutputs.Count ? ActiveOutputs[index] : null,
            CsTarget.DspCh or CsTarget.DspBand =>
                ChannelForAppId(ChannelMap.WireToApp(index, _device.NumInputChannels)),
            _ => null,
        };
    }

    /// <summary>App channel id → its <see cref="Channel"/>. Outputs resolve through
    /// <see cref="ActiveOutputs"/> so RP2040 gets its own PDM entry rather than the
    /// SPDIF 3 L channel that shares the id.</summary>
    private Channel? ChannelForAppId(int appId)
    {
        if (appId < 0) return null;
        if (appId < ChannelMap.AppInputCount) return Channel.AllInputs[appId];
        if (appId >= ChannelMap.ExtraInputFirstId)
        {
            int i = ChannelMap.AppInputCount + (appId - ChannelMap.ExtraInputFirstId);
            return i < Channel.AllInputs.Count ? Channel.AllInputs[i] : null;
        }
        int pos = appId - ChannelMap.AppInputCount;
        return pos < ActiveOutputs.Count ? ActiveOutputs[pos] : null;
    }

    /// <summary>Arm IR learn (10 s window). False if no live IR receiver.</summary>
    public bool CsIrLearnArm() => _device.CsIrLearnArm();

    /// <summary>Cancel a pending IR learn.</summary>
    public void CsIrLearnCancel() => _device.CsIrLearnCancel();

    /// <summary>Poll the IR-learn result.</summary>
    public CsIrLearnResult? CsIrLearnRead() => _device.CsIrLearnRead();

    private void CaptureCsCleanBaseline()
    {
        _csCleanBindings = new CsBinding[_csBindings.Length];
        for (int i = 0; i < _csBindings.Length; i++) _csCleanBindings[i] = _csBindings[i].Clone();
        _csCleanNames = (string[])_csNames.Clone();
        _csCleanIrCommands = new IrCommand[_csIrCommands.Length];
        for (int i = 0; i < _csIrCommands.Length; i++) _csCleanIrCommands[i] = _csIrCommands[i].Clone();
        _csCleanGroups = new CsGroup[_csGroups.Length];
        for (int i = 0; i < _csGroups.Length; i++) _csCleanGroups[i] = _csGroups[i].Clone();
        _csCleanMacros = new CsMacro[_csMacros.Length];
        for (int i = 0; i < _csMacros.Length; i++) _csCleanMacros[i] = _csMacros[i].Clone();
        _csCleanDisplayCfg = _csDisplayCfg.Clone();
        _csCleanDisplayPages = new CsDisplayPage[_csDisplayPages.Length];
        for (int i = 0; i < _csDisplayPages.Length; i++) _csCleanDisplayPages[i] = _csDisplayPages[i].Clone();
    }

    /// <summary>
    /// Per-item diff of the live control-surface config against the last-saved
    /// baseline — the same shape the IO block produces, so the Settings window can
    /// stage control-surface edits in its pending-changes prompt instead of the
    /// editor carrying a second Save/Revert bar of its own.
    ///
    /// <para>One entry per control, group or macro rather than one aggregate entry,
    /// so the prompt's count is a real count and its sidebar dot lands on the page
    /// that owns the change. A slot's binding and its name fold into one entry: to
    /// the user, renaming a control and re-targeting it are one edit to one thing.
    /// Empty while the firmware reports clean.</para>
    ///
    /// <para>The baseline only gets captured when the device is seen clean, so
    /// opening the app onto an already-dirty device leaves us with nothing to diff
    /// against. That must still raise the prompt — the edits are real and unsaved,
    /// we just can't itemise them — so it falls back to one aggregate entry rather
    /// than reporting no changes.</para>
    /// </summary>
    public IReadOnlyList<PresetDiff.IoChange> GetControlSurfaceChanges()
    {
        var changes = new List<PresetDiff.IoChange>();
        if (!CsDirty) return changes;

        if (_csCleanBindings == null || _csCleanNames == null || _csCleanIrCommands == null
            || _csCleanGroups == null || _csCleanMacros == null
            || _csCleanDisplayCfg == null || _csCleanDisplayPages == null)
        {
            changes.Add(new("cs.all", "Control surfaces", "saved", "edited"));
            return changes;
        }

        for (int i = 0; i < _csBindings.Length; i++)
        {
            bool bindingSame = _csBindings[i].WireEquals(_csCleanBindings[i]);
            bool nameSame = string.Equals(_csNames[i], _csCleanNames[i], StringComparison.Ordinal);
            if (bindingSame && nameSame) continue;
            string label = string.IsNullOrWhiteSpace(_csNames[i]) ? $"Control {i + 1}" : _csNames[i];
            var d = Delta(_csCleanBindings[i].IsConfigured, _csBindings[i].IsConfigured);
            changes.Add(new($"cs.slot.{i}", label, d.Old, d.New));
        }
        for (int i = 0; i < _csIrCommands.Length; i++)
        {
            if (_csIrCommands[i].WireEquals(_csCleanIrCommands[i])) continue;
            var d = Delta(_csCleanIrCommands[i].IsConfigured, _csIrCommands[i].IsConfigured);
            changes.Add(new($"cs.ir.{i}", $"Remote button {i + 1}", d.Old, d.New));
        }
        for (int i = 0; i < _csGroups.Length; i++)
        {
            if (_csGroups[i].WireEquals(_csCleanGroups[i])) continue;
            string label = string.IsNullOrWhiteSpace(_csGroups[i].Name)
                ? $"Group {i + 1}" : _csGroups[i].Name;
            var d = Delta(_csCleanGroups[i].IsConfigured, _csGroups[i].IsConfigured);
            changes.Add(new($"cs.group.{i}", label, d.Old, d.New));
        }
        for (int i = 0; i < _csMacros.Length; i++)
        {
            if (_csMacros[i].WireEquals(_csCleanMacros[i])) continue;
            string label = string.IsNullOrWhiteSpace(_csMacros[i].Name)
                ? $"Macro {i + 1}" : _csMacros[i].Name;
            var d = Delta(_csCleanMacros[i].IsConfigured, _csMacros[i].IsConfigured);
            changes.Add(new($"cs.macro.{i}", label, d.Old, d.New));
        }
        // The display's own settings are one item; each page is its own, named by
        // what it shows so the prompt reads as a list of pages rather than a count.
        if (!_csDisplayCfg.WireEquals(_csCleanDisplayCfg))
            changes.Add(new("cs.display.cfg", "Display settings", "saved", "edited"));
        for (int i = 0; i < _csDisplayPages.Length; i++)
        {
            if (_csDisplayPages[i].WireEquals(_csCleanDisplayPages[i])) continue;
            var page = _csDisplayPages[i].IsActive ? _csDisplayPages[i] : _csCleanDisplayPages[i];
            string label = page.IsActive
                ? $"Page {i + 1}: {CsDisplayPageSummary(page)}" : $"Page {i + 1}";
            var d = Delta(_csCleanDisplayPages[i].IsActive, _csDisplayPages[i].IsActive);
            changes.Add(new($"cs.display.page.{i}", label, d.Old, d.New));
        }
        return changes;

        // These are whole records, not single fields, so the prompt says what
        // happened to the item rather than a before/after value it has no room for.
        static (string Old, string New) Delta(bool before, bool after) =>
            !before && after ? ("not set", "added")
            : before && !after ? ("set", "removed")
            : ("saved", "edited");
    }

    private bool MatchesCleanBaseline()
    {
        if (_csCleanBindings == null || _csCleanNames == null || _csCleanIrCommands == null
            || _csCleanGroups == null || _csCleanMacros == null
            || _csCleanDisplayCfg == null || _csCleanDisplayPages == null)
            return false; // no baseline yet → treat firmware dirty as authoritative
        for (int i = 0; i < _csBindings.Length; i++)
            if (!_csBindings[i].WireEquals(_csCleanBindings[i])) return false;
        for (int i = 0; i < _csNames.Length; i++)
            if (!string.Equals(_csNames[i], _csCleanNames[i], StringComparison.Ordinal)) return false;
        for (int i = 0; i < _csIrCommands.Length; i++)
            if (!_csIrCommands[i].WireEquals(_csCleanIrCommands[i])) return false;
        for (int i = 0; i < _csGroups.Length; i++)
            if (!_csGroups[i].WireEquals(_csCleanGroups[i])) return false;
        for (int i = 0; i < _csMacros.Length; i++)
            if (!_csMacros[i].WireEquals(_csCleanMacros[i])) return false;
        if (!_csDisplayCfg.WireEquals(_csCleanDisplayCfg)) return false;
        for (int i = 0; i < _csDisplayPages.Length; i++)
            if (!_csDisplayPages[i].WireEquals(_csCleanDisplayPages[i])) return false;
        return true;
    }
}
