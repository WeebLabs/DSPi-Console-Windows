using System.Threading.Tasks;

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
    internal void DeviceWrite(Action write)
    {
        lock (_deviceQueueLock)
            _deviceQueue = _deviceQueue.ContinueWith(_ => { try { write(); } catch { } }, TaskScheduler.Default);
    }

    // ── Live sends: device only, for values during a drag ──

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
