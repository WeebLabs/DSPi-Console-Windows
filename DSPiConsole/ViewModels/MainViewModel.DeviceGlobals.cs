using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Device-global settings from firmware 1.1.6 that live in the device's preset
/// directory rather than in any preset: the output latency option, the input
/// source names and the board's own name. None of them is dirty-tracked or
/// carried by presets; Settings stages edits to them like other flash settings.
/// Port of the macOS Console's fetchOutputLatency / fetchInputSourceNames /
/// fetchDeviceName and their setters.
/// </summary>
public partial class MainViewModel
{
    // ── Output latency ────────────────────────────────────────────────────

    /// <summary>False on firmware that STALLs REQ_OUTPUT_LATENCY, which hides
    /// the Settings section.</summary>
    [ObservableProperty]
    private bool _outputLatencySupported;

    [ObservableProperty]
    private OutputLatencyStatus _outputLatencyStatus;

    /// <summary>The device's option table. Which options run as themselves
    /// depends on the input source and rate, so it is re-read whenever those
    /// may have moved.</summary>
    [ObservableProperty]
    private IReadOnlyList<OutputLatencyOption> _outputLatencyOptions = Array.Empty<OutputLatencyOption>();

    /// <summary>Read the latency status and option table. Blocks: call off the
    /// UI thread. Only the full fetch's read (<paramref name="probe"/>) decides
    /// support: a refresh while the outputs restart (after a SET, or a source
    /// switch) can fail without the firmware lacking the setting, and must not
    /// hide it.</summary>
    private void FetchOutputLatency(bool probe = false)
    {
        var serial = _device.SelectedDeviceInfo?.Serial;
        if (_device.GetOutputLatencyStatus() is not { } status)
        {
            if (probe)
                _dispatcher.TryEnqueue(() =>
                {
                    if (_device.SelectedDeviceInfo?.Serial == serial) OutputLatencySupported = false;
                });
            return;
        }
        var options = _device.GetOutputLatencyOptions(status.OptionCount);
        _dispatcher.TryEnqueue(() =>
        {
            // A late reply from the previous device must not land on the new one.
            if (_device.SelectedDeviceInfo?.Serial != serial) return;
            OutputLatencyStatus = status;
            if (options != null) OutputLatencyOptions = options;
            OutputLatencySupported = true;
        });
    }

    /// <summary>Select a latency option. The firmware stores it and restarts
    /// the outputs inside a flash write on its main loop, so the status reports
    /// pending until then: poll for that (reads can fail during the flash
    /// window), then re-read the table, whose effective values follow the new
    /// option.</summary>
    public Task<bool> SetOutputLatencyAsync(byte optionId) => Task.Run(() =>
    {
        if (!OutputLatencySupported) return false;
        var serial = _device.SelectedDeviceInfo?.Serial;
        if (!_device.SetOutputLatency(optionId)) return false;
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(100);
            if (_device.SelectedDeviceInfo?.Serial != serial) return false;
            if (_device.GetOutputLatencyStatus() is { Pending: false }) break;
        }
        FetchOutputLatency();
        return true;
    });

    /// <summary>The label for a latency option ID: the device's nominal target
    /// when the table has it, otherwise the fallback list.</summary>
    public string OutputLatencyLabel(int optionId)
    {
        foreach (var o in OutputLatencyOptions)
            if (o.Id == optionId) return o.Label;
        int i = optionId - 1;
        return i >= 0 && i < OutputLatency.FallbackLabels.Length
            ? OutputLatency.FallbackLabels[i]
            : $"Option {optionId}";
    }

    // ── Input source names ────────────────────────────────────────────────

    private readonly string[] _inputSourceNames = Enumerable.Repeat("", DeviceNameText.SourceSlots).ToArray();

    /// <summary>Stays false on firmware that predates the names (the probe in
    /// <see cref="FetchInputSourceNames"/>).</summary>
    [ObservableProperty]
    private bool _inputSourceNamesSupported;

    /// <summary>User names per source ID; empty means unnamed.</summary>
    public IReadOnlyList<string> InputSourceNames => _inputSourceNames;

    /// <summary>A source's name changed (from this app, another host or a
    /// fresh read). Raised on the UI thread.</summary>
    public event Action? InputSourceNamesChanged;

    /// <summary>What the app calls a source: the user's name when one is set,
    /// otherwise the built-in label. The source picker, Control Surfaces value
    /// text and the unsaved-changes list go through this; the Interrupt Monitor
    /// keeps the raw built-in labels on purpose.</summary>
    public string InputSourceTitle(int source) =>
        source >= 0 && source < _inputSourceNames.Length && _inputSourceNames[source].Length > 0
            ? _inputSourceNames[source]
            : BuiltInInputSourceTitle(source);

    public string InputSourceTitle(InputSource source) => InputSourceTitle((int)source);

    /// <summary>The built-in label. The first S/PDIF input is numbered once a
    /// second one is enabled.</summary>
    public string BuiltInInputSourceTitle(int source) => (InputSource)source switch
    {
        InputSource.Usb => "USB",
        InputSource.Spdif => (MultiSpdifSupported ? SpdifEnabledCount : 1) > 1 ? "S/PDIF 1" : "S/PDIF",
        InputSource.Spdif2 => "S/PDIF 2",
        InputSource.Spdif3 => "S/PDIF 3",
        InputSource.Spdif4 => "S/PDIF 4",
        InputSource.I2s => "I2S",
        InputSource.Adat => "ADAT",
        _ => $"Source {source}",
    };

    /// <summary>The selectable sources for the current device configuration, in
    /// menu order: USB, S/PDIF 1, any enabled optional S/PDIF inputs, I2S, and
    /// ADAT once it is enabled with a pin. After the macOS Console's
    /// inputSourceOptions.</summary>
    public IReadOnlyList<InputSource> InputSourceOptions
    {
        get
        {
            var list = new List<InputSource> { InputSource.Usb, InputSource.Spdif };
            if (MultiSpdifSupported)
                for (int i = 1; i < SpdifInputCount && i <= 3; i++)
                    if (SpdifInputEnabled(i)) list.Add((InputSource)((int)InputSource.Spdif2 + i - 1));
            if (InputI2sSupported) list.Add(InputSource.I2s);
            if (AdatInputSelectable) list.Add(InputSource.Adat);
            return list;
        }
    }

    /// <summary>Where a source physically comes in, so similar sources can be
    /// told apart in Settings.</summary>
    public string InputSourceDetail(InputSource source) => source switch
    {
        InputSource.Usb => "Audio from the computer",
        InputSource.I2s => "External I2S device",
        InputSource.Adat => $"Lightpipe on GPIO {AdatInputPin}",
        InputSource.Spdif => SpdifDetail(0),
        InputSource.Spdif2 => SpdifDetail(1),
        InputSource.Spdif3 => SpdifDetail(2),
        InputSource.Spdif4 => SpdifDetail(3),
        _ => "",
    };

    private string SpdifDetail(int index) =>
        SpdifRxPinAt(index) is var pin && pin != 0 ? $"Receiver on GPIO {pin}" : "S/PDIF receiver";

    /// <summary>Read every source name. Older firmware answers source 0 with
    /// its 4-byte legacy gain, so only that probe decides support; a later
    /// failed read just leaves that source on its built-in label. Unconditional
    /// on every full fetch, so a device switch never keeps the last device's
    /// names. Blocks: call off the UI thread.</summary>
    private void FetchInputSourceNames()
    {
        var serial = _device.SelectedDeviceInfo?.Serial;
        var names = Enumerable.Repeat("", DeviceNameText.SourceSlots).ToArray();
        bool supported = false;
        for (int source = 0; source <= DeviceNameText.MaxSourceId; source++)
        {
            var name = _device.GetInputSourceName(source);
            if (source == 0)
            {
                if (name == null) break;
                supported = true;
            }
            if (name != null) names[source] = name;
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (_device.SelectedDeviceInfo?.Serial != serial) return;
            InputSourceNamesSupported = supported;
            names.CopyTo(_inputSourceNames, 0);
            InputSourceNamesChanged?.Invoke();
        });
    }

    /// <summary>Store a source name; empty restores the built-in label. The
    /// device writes flash on its main loop and echoes the stored name back as
    /// NOTIFY_EVT_INPUT_SOURCE_NAME.</summary>
    /// <para>The name shows at once; a transfer the device refuses puts the
    /// previous name back, so the app never claims a name the board lacks.</para>
    public async Task<bool> SetInputSourceNameAsync(int source, string name)
    {
        if (!InputSourceNamesSupported || source is < 0 or > DeviceNameText.MaxSourceId)
            return false;
        var clean = DeviceNameText.Sanitize(name);
        var previous = _inputSourceNames[source];
        _inputSourceNames[source] = clean;
        InputSourceNamesChanged?.Invoke();
        if (await Task.Run(() => _device.SetInputSourceName(source, clean))) return true;
        if (_inputSourceNames[source] == clean)
        {
            _inputSourceNames[source] = previous;
            InputSourceNamesChanged?.Invoke();
        }
        return false;
    }

    // ── The board's own name ──────────────────────────────────────────────

    /// <summary>Stays false on firmware without REQ_GET_DEVICE_NAME.</summary>
    [ObservableProperty]
    private bool _deviceNameSupported;

    /// <summary>The board's own name; empty means unnamed.</summary>
    [ObservableProperty]
    private string _deviceName = "";

    /// <summary>The last name read from each board this session, so the
    /// picker can name boards that are not the connected one.</summary>
    private readonly Dictionary<string, string> _knownDeviceNames = new();

    /// <summary>A board's name was learned or changed. Raised on the UI thread.</summary>
    public event Action? DeviceNamesChanged;

    /// <summary>Note what a board is called, for the picker. Recorded where a
    /// name is learned rather than on DeviceName's change, which a second board
    /// with the same name would not trigger. UI thread.</summary>
    private void RememberDeviceName(string? serial, string name)
    {
        if (serial == null) return;
        if (_knownDeviceNames.TryGetValue(serial, out var known) && known == name) return;
        _knownDeviceNames[serial] = name;
        DeviceNamesChanged?.Invoke();
    }

    /// <summary>The device's name without the hub it is reached through: the
    /// heading of the picker's submenu for a device on several hubs.</summary>
    public string DeviceNameOnly(DSPiDeviceInfo device)
    {
        if (_knownDeviceNames.TryGetValue(device.Serial, out var name) && name.Length > 0) return name;
        if (!string.IsNullOrWhiteSpace(device.RemoteName)) return device.RemoteName;
        return device.Serial.Length >= 8 ? $"DSPi ({device.Serial[^8..]})" : "DSPi";
    }

    /// <summary>Picker title: the board's own name once known, otherwise the
    /// serial-based default.</summary>
    public string DeviceTitle(DSPiDeviceInfo device)
    {
        if (!_knownDeviceNames.TryGetValue(device.Serial, out var name) || name.Length == 0) return device.DisplayName;
        // A device on another hub says which hub, as the picker lists it.
        return device.Kind == DeviceLinkKind.Remote ? $"{name} via {device.HubName ?? "a hub"}" : name;
    }

    /// <summary>Read the board name. The probe is this GET alone: a STALL or a
    /// reply shorter than 32 bytes means no board name. Blocks: call off the UI
    /// thread.</summary>
    private void FetchDeviceName()
    {
        var serial = _device.SelectedDeviceInfo?.Serial;
        var name = _device.GetDeviceName();
        _dispatcher.TryEnqueue(() =>
        {
            if (_device.SelectedDeviceInfo?.Serial != serial) return;
            DeviceNameSupported = name != null;
            DeviceName = name ?? "";
            if (name != null) RememberDeviceName(serial, name);
        });
    }

    /// <summary>Store the board name; empty clears it. The device writes flash
    /// on its main loop and echoes the stored name as NOTIFY_EVT_DEVICE_NAME.</summary>
    /// <para>A transfer the device refuses puts the previous name back.</para>
    public async Task<bool> SetDeviceNameAsync(string name)
    {
        if (!DeviceNameSupported) return false;
        var serial = _device.SelectedDeviceInfo?.Serial;
        var clean = DeviceNameText.Sanitize(name);
        var previous = DeviceName;
        DeviceName = clean;
        RememberDeviceName(serial, clean);
        if (await Task.Run(() => _device.SetDeviceName(clean))) return true;
        if (DeviceName == clean && _device.SelectedDeviceInfo?.Serial == serial)
        {
            DeviceName = previous;
            RememberDeviceName(serial, previous);
        }
        return false;
    }

    // ── Wiring ────────────────────────────────────────────────────────────

    /// <summary>Everything above, read on each full fetch.</summary>
    private void FetchDeviceGlobals()
    {
        FetchOutputLatency(probe: true);
        FetchInputSourceNames();
        FetchDeviceName();
    }

    /// <summary>Forget the last device's support and names when it goes away or
    /// another is chosen, so nothing offers a rename (a SET that older firmware
    /// would take as a legacy gain or mute) before the next device's probe has
    /// run. The picker's per-serial names are kept. UI thread.</summary>
    private void ResetDeviceGlobals()
    {
        OutputLatencySupported = false;
        OutputLatencyOptions = Array.Empty<OutputLatencyOption>();
        InputSourceNamesSupported = false;
        if (_inputSourceNames.Any(n => n.Length > 0))
        {
            Array.Fill(_inputSourceNames, "");
            InputSourceNamesChanged?.Invoke();
        }
        DeviceNameSupported = false;
        DeviceName = "";
    }

    /// <summary>Hook the device's name events; called once from the constructor.
    /// A name event carries the stored value, so it applies straight away
    /// whoever caused it, this app's own write included.</summary>
    private void SubscribeDeviceGlobals()
    {
        _device.InputSourceNameNotified += (_, n) => _dispatcher.TryEnqueue(() =>
        {
            if (n.SourceId < 0 || n.SourceId >= _inputSourceNames.Length) return;
            if (_inputSourceNames[n.SourceId] == n.Name) return;
            _inputSourceNames[n.SourceId] = n.Name;
            InputSourceNamesChanged?.Invoke();
        });
        _device.DeviceNameNotified += (_, n) => _dispatcher.TryEnqueue(() =>
        {
            if (DeviceName != n.Name) DeviceName = n.Name;
            RememberDeviceName(_device.SelectedDeviceInfo?.Serial, n.Name);
        });
        // Which latency options run as themselves depends on the source.
        InputSourceChanged += (_, _) =>
        {
            if (OutputLatencySupported) Task.Run(() => FetchOutputLatency());
        };
    }
}
