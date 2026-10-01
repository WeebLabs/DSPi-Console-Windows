using System.Threading.Tasks;
using DSPiConsole.Core.Models;

namespace DSPiConsole.ViewModels;

/// <summary>
/// One ordered queue for parameter writes, and the device-only "live" sends a
/// slider drag makes (see Controls/SliderDrag). Writes used to go out through a
/// Task.Run each, which the thread pool may run in any order, so a drag's last
/// live value could land after the commit that followed it and leave the device
/// on a stale value. Live sends and the commits of the same parameters share
/// this queue, so they reach the device in the order they were made.
/// </summary>
public partial class MainViewModel
{
    private readonly object _deviceQueueLock = new();
    private Task _deviceQueue = Task.CompletedTask;

    /// <summary>Runs a device write after every write queued before it.</summary>
    internal void DeviceWrite(Action write) => DeviceWriteAsync(write);

    /// <summary>As <see cref="DeviceWrite"/>, for a caller that waits for the
    /// write; the task completes once it has run, failed or not.</summary>
    internal Task DeviceWriteAsync(Action write)
    {
        lock (_deviceQueueLock)
            return _deviceQueue = _deviceQueue.ContinueWith(_ => { try { write(); } catch { } }, TaskScheduler.Default);
    }

    // ── Live sends: device only, for values during a drag ──

    public enum PsybassField { Cutoff, Harmonics, Drive, Character, Original }

    /// <summary>A psychoacoustic bass value during a drag, clamped to its
    /// range; the properties commit it.</summary>
    public void SendPsybassLive(PsybassField field, float value)
    {
        switch (field)
        {
            case PsybassField.Cutoff: { float v = Math.Clamp(value, PsybassLimits.CutoffMinHz, PsybassLimits.CutoffMaxHz); DeviceWrite(() => _device.SetPsybassCutoff(v)); break; }
            case PsybassField.Harmonics: { float v = Math.Clamp(value, PsybassLimits.HarmonicsMinDb, PsybassLimits.HarmonicsMaxDb); DeviceWrite(() => _device.SetPsybassHarmonics(v)); break; }
            case PsybassField.Drive: { float v = Math.Clamp(value, PsybassLimits.DriveMinDb, PsybassLimits.DriveMaxDb); DeviceWrite(() => _device.SetPsybassDrive(v)); break; }
            case PsybassField.Character: { float v = Math.Clamp(value, PsybassLimits.CharacterMinPct, PsybassLimits.CharacterMaxPct); DeviceWrite(() => _device.SetPsybassCharacter(v)); break; }
            default: { float v = Math.Clamp(value, PsybassLimits.OriginalMinDb, PsybassLimits.OriginalMaxDb); DeviceWrite(() => _device.SetPsybassOriginal(v)); break; }
        }
    }

    public enum LevellerField { Amount, MaxGain, Gate }

    /// <summary>A leveller value during a drag, clamped as the firmware does;
    /// the properties commit it.</summary>
    public void SendLevellerLive(LevellerField field, float value)
    {
        switch (field)
        {
            case LevellerField.Amount: { float v = Math.Clamp(value, 0, 100); DeviceWrite(() => _device.SetLevellerAmount(v)); break; }
            case LevellerField.MaxGain: { float v = Math.Clamp(value, 0, 35); DeviceWrite(() => _device.SetLevellerMaxGain(v)); break; }
            default: { float v = Math.Clamp(value, -96, 0); DeviceWrite(() => _device.SetLevellerGate(v)); break; }
        }
    }

    /// <summary>Crossfeed cutoff (500-2000 Hz) or feed (0-15 dB) during a
    /// drag; the properties commit them.</summary>
    public void SendCrossfeedLive(bool freq, float value)
    {
        if (freq) { float v = Math.Clamp(value, 500, 2000); DeviceWrite(() => _device.SetCrossfeedFreq(v)); }
        else { float v = Math.Clamp(value, 0, 15); DeviceWrite(() => _device.SetCrossfeedFeed(v)); }
    }

    /// <summary>Loudness reference SPL (40-100 dB) or intensity (0-200 %)
    /// during a drag; the properties commit them.</summary>
    public void SendLoudnessLive(bool refSpl, float value)
    {
        if (refSpl) { float v = Math.Clamp(value, 40, 100); DeviceWrite(() => _device.SetLoudnessRefSPL(v)); }
        else { float v = Math.Clamp(value, 0, 200); DeviceWrite(() => _device.SetLoudnessIntensity(v)); }
    }

    /// <summary>An output's gain during a drag; <see cref="SetChannelGain"/>
    /// commits it.</summary>
    public void SendChannelGainLive(int channelId, float db)
    {
        int output = GetOutputIndex(channelId);
        if (output < 0) return;
        db = MathF.Round(db, 2);
        DeviceWrite(() => _device.SetOutputGain(output, db));
    }

    /// <summary>An output's delay during a drag; <see cref="SetDelay"/> commits it.</summary>
    public void SendDelayLive(int channelId, float ms)
    {
        int output = GetOutputIndex(channelId);
        if (output < 0) return;
        ms = MathF.Round(Math.Clamp(ms, 0f, MaxOutputDelayMs), 4);
        DeviceWrite(() => _device.SetOutputDelay(output, ms));
    }

    /// <summary>An input's preamp during a drag, mirrored to its linked partner
    /// as the commit is; <see cref="SetInputPreampAt"/> commits it.</summary>
    public void SendInputPreampLive(int wireInput, float db)
    {
        if (wireInput is < 0 or > 7) return;
        db = MathF.Round(db, 1);
        DeviceWrite(() => _device.SetInputPreamp(wireInput, db));
        bool linked = wireInput < 2 ? _masterPeqLinked : GetInputPairLinked(1 + (wireInput - 2) / 2);
        if (linked)
        {
            int partner = wireInput ^ 1;
            DeviceWrite(() => _device.SetInputPreamp(partner, db));
        }
    }
}
