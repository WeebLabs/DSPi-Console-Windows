using System;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Applies device-pushed PARAM_CHANGED notifications for parameters that aren't
/// covered by a dedicated typed handler, plus the discrete hardware-state events.
/// This keeps the UI live when a parameter is changed by something other than this
/// app — a control surface (source=GPIO), the OS, another host, or a UART/I2C
/// controller. HostSet echoes (our own writes) are ignored.
///
/// <para>Scalar fields are applied in place (backing field + PropertyChanged, no
/// device write). Feature blocks reuse the existing Fetch* readers (which already
/// update the VM without echoing), run off the notify thread via Task.Run.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>Wire the generic PARAM_CHANGED + discrete-event notifications.
    /// Called once from the constructor.</summary>
    private void WireParamNotifications()
    {
        _device.ParamChangedNotified += (_, n) =>
        {
            // Our own EP0 writes echo back as HostSet — ignore to avoid loops.
            if (n.Source == ParamSource.HostSet) return;
            OnGenericParamChanged(n);
        };

        _device.InputFormatNotified += (_, _channels) =>
        {
            // The device negotiated a new USB input channel count — re-read the
            // authoritative value from Windows immediately (instead of waiting for
            // the periodic poll).
            Task.Run(RefreshUsbInputChannelCount);
        };

        _device.StatusEventNotified += (_, eventId) =>
        {
            switch (eventId)
            {
                case 0x08: Task.Run(() => { var s = _device.GetAdatStatus(); if (s != null) _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatEnabled))); FetchAdatConfig(); }); break;
                case 0x09: Task.Run(RefreshI2sSlaveStatus); break;
                case 0x0B: Task.Run(RefreshAdatInputStatus); break;
                // Siggen start/stop/completion/reconfigure. The Test Signals window
                // also polls while it believes the generator is running, but the push
                // is what catches a stop nothing is watching for (Identify finishing,
                // preset load, duration exhausted with the window closed).
                case 0x07: RefreshSiggenStatus(); break;
            }
        };
    }

    private void OnGenericParamChanged(ParamChangedNotification n)
    {
        int off = n.Offset;

        // ── Feature blocks: reuse the tested Fetch* readers (off the notify thread).
        // Loudness (global +5..+15, but not +4 bypass).
        if (off >= BulkParamsParser.OffsetGlobal + 5 && off < BulkParamsParser.OffsetCrossfeed)
        { Task.Run(FetchLoudness); return; }
        if (off >= BulkParamsParser.OffsetCrossfeed && off < BulkParamsParser.OffsetLegacy)
        { Task.Run(FetchCrossfeed); return; }
        // The appended sections are matched from the last one down: each range
        // is open-ended, so an earlier one would swallow every later section.
        if (off >= BulkParamsParser.OffsetLimiter)
        { Task.Run(FetchLimiter); return; }
        if (off >= BulkParamsParser.OffsetTube)
        { Task.Run(FetchTube); return; }
        if (off >= BulkParamsParser.OffsetSubharm)
        { Task.Run(FetchSubharm); return; }
        if (off >= BulkParamsParser.OffsetUpmix)
        { Task.Run(FetchUpmix); return; }
        if (off >= BulkParamsParser.OffsetPsybass)
        { Task.Run(FetchPsybass); return; }
        if (off >= BulkParamsParser.OffsetDacHwMute && off < BulkParamsParser.OffsetCrossover)
        { Task.Run(FetchDacHwMute); return; }
        if (off >= BulkParamsParser.OffsetLgSoundSync && off < BulkParamsParser.OffsetUserVolume)
        { Task.Run(FetchLgSoundSync); return; }
        if (off >= BulkParamsParser.OffsetAdat && off < BulkParamsParser.OffsetPsybass)
        { Task.Run(FetchAdatConfig); return; }

        // I2S output-config block (BCK/MCK/clock-pin/slave-BCK).
        if (off >= BulkParamsParser.OffsetI2S && off < BulkParamsParser.OffsetLeveller)
        {
            Task.Run(() =>
            {
                FetchI2SBckPin(); FetchMckEnable(); FetchMckPin(); FetchMckMultiplier();
                FetchI2sClockConfig();
            });
            return;
        }

        // Output GPIO pins (pins section): re-read the changed pin.
        if (off > BulkParamsParser.OffsetPinConfig && off < BulkParamsParser.OffsetEq)
        {
            int idx = off - (BulkParamsParser.OffsetPinConfig + 1);
            if (idx >= 0) { int i = idx; Task.Run(() => FetchOutputPin(i)); }
            return;
        }

        // Input-config block. Input source is handled by the typed
        // InputSourceNotified; the rest map to their existing fetchers.
        if (off >= BulkParamsParser.OffsetInputCfg && off < BulkParamsParser.OffsetLgSoundSync)
        {
            int sub = off - BulkParamsParser.OffsetInputCfg;
            // Fields below spdif_rx_pin_ext shift by one at wire V28 (the array
            // grew for S/PDIF 4), so the tail offsets come from the wire version
            // rather than being hard-coded.
            int tail = BulkParamsParser.InputCfgTailOffset(_device.WireFormatVersion);
            if (sub == tail + 1) Task.Run(FetchI2sClockConfig);             // i2s clock mode
            else if (sub >= tail + 2 && sub <= tail + 4) Task.Run(FetchAdatInputConfig);
            else if (sub == 1 || (sub >= 8 && sub <= tail)) Task.Run(FetchSpdifInputConfig);
            else switch (sub)
            {
                case 2: case 4: case 5: case 6: case 7: Task.Run(FetchI2sInputConfig); break; // I2S pins/channels
                case 3: Task.Run(FetchI2sInputRate); break;                // I2S input rate
                // sub 0 (input_source) never reaches here (typed handler catches it).
            }
            return;
        }

        // ── Scalars / matrix applied in place (no device write) on the UI thread. ──
        _dispatcher.TryEnqueue(() => ApplyScalarParam(n));
    }

    private void ApplyScalarParam(ParamChangedNotification n)
    {
        int off = n.Offset;
        var p = n.Payload;
        bool changed = true;

        // Master volume.
        if (off == BulkParamsParser.OffsetMasterVol && p.Length >= 4)
        {
            _masterVolumeDb = BitConverter.ToSingle(p, 0);
            OnPropertyChanged(nameof(MasterVolumeDb));
        }
        // Global bypass (offset global+4).
        else if (off == BulkParamsParser.OffsetGlobal + 4 && p.Length >= 1)
        {
            _bypass = p[0] != 0;
            OnPropertyChanged(nameof(Bypass));
            BypassChanged?.Invoke(this, EventArgs.Empty);
        }
        // Per-input preamp (first two inputs shown as L/R).
        else if (off >= BulkParamsParser.OffsetPreamp && off < BulkParamsParser.OffsetMasterVol && p.Length >= 4)
        {
            int ch = (off - BulkParamsParser.OffsetPreamp) / 4;
            float v = BitConverter.ToSingle(p, 0);
            if (ch == 0) { _inputPreampLDb = v; OnPropertyChanged(nameof(InputPreampLDb)); }
            else if (ch == 1) { _inputPreampRDb = v; OnPropertyChanged(nameof(InputPreampRDb)); }
            else if (ch < 8) { _inputPreampExtDb[ch - 2] = v; InputPreampExtChanged?.Invoke(ch); }
            else changed = false;
        }
        // Volume leveller (per-field).
        else if (off >= BulkParamsParser.OffsetLeveller && off < BulkParamsParser.OffsetPreamp)
        {
            ApplyLevellerField(off - BulkParamsParser.OffsetLeveller, p);
        }
        // Output channels: enabled / mute / gain / delay per output slot.
        else if (off >= BulkParamsParser.OffsetOutputs && off < BulkParamsParser.OffsetPinConfig)
        {
            ApplyOutputField(off - BulkParamsParser.OffsetOutputs, p);
        }
        // Matrix crosspoints: enabled / invert / gain per (input, output),
        // across the full 8-input wire matrix.
        else if (off >= BulkParamsParser.OffsetCrosspoints && off < BulkParamsParser.OffsetOutputs && p.Length >= 8)
        {
            int flat = (off - BulkParamsParser.OffsetCrosspoints) / 8;
            int inp = flat / 9;
            int outp = flat % 9;
            if (inp < MatrixMaxInputs && outp < 9)
            {
                _matrixRouting[inp, outp] = p[0] != 0;
                _matrixInvert[inp, outp] = p[1] != 0;
                _matrixGain[inp, outp] = BitConverter.ToSingle(p, 4);
                MatrixRouteChanged?.Invoke(inp, outp);
            }
            else changed = false;
        }
        // Per-channel delays. On an output this is the output delay itself (the
        // firmware's output-delay SET writes both and reports both); on an
        // input it is that input's own delay.
        else if (off >= BulkParamsParser.OffsetDelays && off < BulkParamsParser.OffsetCrosspoints && p.Length >= 4)
        {
            int wireCh = (off - BulkParamsParser.OffsetDelays) / 4;
            int appId = ChannelMap.WireToApp(wireCh, NumInputChannels);
            float ms = BitConverter.ToSingle(p, 0);
            if (appId < 0) changed = false;
            else if (IsInputChannelId(appId)) ApplyNotifiedInputChannelDelay(appId, ms);
            else
            {
                _channelDelays[appId] = ms;
                if (appId >= 2 && appId <= 10) MatrixOutputDelayChanged?.Invoke(appId - 2);
            }
        }
        else changed = false;

        if (changed) CheckDirty();
    }

    private void ApplyLevellerField(int sub, byte[] p)
    {
        switch (sub)
        {
            case 0 when p.Length >= 1: _levellerEnabled = p[0] != 0; OnPropertyChanged(nameof(LevellerEnabled)); break;
            case 1 when p.Length >= 1: _levellerSpeed = p[0]; OnPropertyChanged(nameof(LevellerSpeed)); break;
            case 2 when p.Length >= 1: _levellerLookahead = p[0] != 0; OnPropertyChanged(nameof(LevellerLookahead)); break;
            case 4 when p.Length >= 4: _levellerAmount = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerAmount)); break;
            case 8 when p.Length >= 4: _levellerMaxGainDb = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerMaxGainDb)); break;
            case 12 when p.Length >= 4: _levellerGateDb = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerGateDb)); break;
            case 16 when p.Length >= 1: _levellerDetectorMask = p[0]; OnPropertyChanged(nameof(LevellerDetectorMask)); break;
            case 17 when p.Length >= 1: _levellerApplyMask = p[0]; OnPropertyChanged(nameof(LevellerApplyMask)); break;
        }
    }

    private void ApplyOutputField(int rel, byte[] p)
    {
        int outIdx = rel / 12;
        int sub = rel % 12;
        int appId = 2 + outIdx;   // outputs occupy app ids 2..10
        switch (sub)
        {
            case 0 when p.Length >= 1:   // enabled
                bool en = p[0] != 0;
                _outputEnabled[outIdx] = en;
                OutputEnabledChanged?.Invoke(outIdx, en);
                break;
            case 1 when p.Length >= 1:   // mute
                bool muted = p[0] != 0;
                _channelMutes[appId] = muted;
                // Output mute is cached twice: by channel id (main window) and by
                // output index (matrix window). A push has to land in both, or the
                // matrix window keeps showing the pre-push state.
                if (outIdx < _outputMuted.Length) _outputMuted[outIdx] = muted;
                OnPropertyChanged(nameof(ChannelMutes));
                MatrixOutputMuteChanged?.Invoke(outIdx);
                break;
            case 4 when p.Length >= 4:   // gain
                _channelGains[appId] = BitConverter.ToSingle(p, 0);
                MatrixOutputGainChanged?.Invoke(outIdx);
                break;
            case 8 when p.Length >= 4:   // delay
                _channelDelays[appId] = BitConverter.ToSingle(p, 0);
                MatrixOutputDelayChanged?.Invoke(outIdx);
                break;
        }
    }
}
