using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// The per-channel delay (REQ_SET_DELAY 0x48, WireChannelDelays), one value per
/// wire channel. On an output it is the same value as the output delay: the
/// firmware's REQ_SET_OUTPUT_DELAY (0x78), which the output Delay control
/// sends, writes both and the DSP reads this one. On an input it is a delay of
/// its own, ahead of the matrix, which the app had no support for. The app has
/// no control for it yet, but it is preset state and the macOS Console edits
/// it, so it is read from the bulk image, tracked for unsaved changes, and
/// carried by preset files.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Input channels' delays, by app channel id.</summary>
    private readonly Dictionary<int, float> _inputChannelDelays = new();

    private static bool IsInputChannelId(int channelId) =>
        Channel.FromId((ChannelId)channelId) is { IsOutput: false };

    /// <summary>A channel's delay (ms) by app channel id: an input's own, or an
    /// output's output delay.</summary>
    public float GetPreMatrixDelay(int channelId) =>
        IsInputChannelId(channelId)
            ? (_inputChannelDelays.TryGetValue(channelId, out var d) ? d : 0f)
            : (_channelDelays.TryGetValue(channelId, out var o) ? o : 0f);

    /// <summary>Sets an input's delay. An output's goes through
    /// <see cref="SetDelay"/>, which sets the value the DSP reads.</summary>
    public void SetPreMatrixDelay(int channelId, float ms)
    {
        if (!IsInputChannelId(channelId))
        {
            SetDelay(channelId, ms);
            return;
        }
        ms = MathF.Round(Math.Clamp(ms, 0f, MaxOutputDelayMs), 4);
        if (Math.Abs(GetPreMatrixDelay(channelId) - ms) < 0.00005f) return;
        _inputChannelDelays[channelId] = ms;
        Task.Run(() => _device.SetDelay(channelId, ms));
        CheckDirty();
    }

    private void SeedInputChannelDelaysFromBulk(BulkParams bp)
    {
        _inputChannelDelays.Clear();
        for (int wire = 0; wire < Math.Min(bp.Delays.Length, bp.NumInputChannels); wire++)
        {
            int id = ChannelMap.WireToApp(wire, bp.NumInputChannels);
            if (id >= 0) _inputChannelDelays[id] = bp.Delays[wire];
        }
    }

    /// <summary>A device-pushed change to an input's delay.</summary>
    private void ApplyNotifiedInputChannelDelay(int appId, float ms) => _inputChannelDelays[appId] = ms;
}
