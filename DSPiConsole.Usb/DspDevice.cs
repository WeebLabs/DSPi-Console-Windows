using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Core.Models;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace DSPiConsole.Usb;

/// <summary>
/// Command IDs for the vendor interface (matching firmware REQ_* defines)
/// These are sent as bRequest in USB control transfers to Interface 2.
/// </summary>
public static class VendorCommands
{
    public const byte SetEqParam = 0x42;
    public const byte GetEqParam = 0x43;
    public const byte SetPreamp = 0x44;
    public const byte GetPreamp = 0x45;
    public const byte SetBypass = 0x46;
    public const byte GetBypass = 0x47;
    public const byte SetDelay = 0x48;
    public const byte GetDelay = 0x49;
    public const byte GetStatus = 0x50;
    public const byte SaveParams = 0x51;
    public const byte LoadParams = 0x52;
    public const byte FactoryReset = 0x53;
    public const byte SetChannelGain = 0x54;
    public const byte GetChannelGain = 0x55;
    public const byte SetChannelMute = 0x56;
    public const byte GetChannelMute = 0x57;
    public const byte SetLoudnessEnabled = 0x58;
    public const byte GetLoudnessEnabled = 0x59;
    public const byte SetLoudnessRefSPL = 0x5A;
    public const byte GetLoudnessRefSPL = 0x5B;
    public const byte SetLoudnessIntensity = 0x5C;
    public const byte GetLoudnessIntensity = 0x5D;
}

/// <summary>
/// Flash operation result codes from firmware.
/// </summary>
public static class FlashResult
{
    public const byte Ok = 0;
    public const byte ErrWrite = 1;
    public const byte ErrNoData = 2;
    public const byte ErrCrc = 3;
}

/// <summary>
/// EQ parameter packet structure matching firmware EqParamPacket.
/// Used for control transfer data payload (13 bytes).
/// Channel and band are specified in wValue/wIndex of the setup packet.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EqParamPacket
{
    public byte Type;
    public float Frequency;
    public float Q;
    public float GainDb;

    public const int Size = 13; // 1 + 4 + 4 + 4

    public static EqParamPacket FromFilterParams(FilterParams p) => new()
    {
        Type = (byte)p.Type,
        Frequency = p.Frequency,
        Q = p.Q,
        GainDb = p.Gain
    };

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        bytes[0] = Type;
        BitConverter.GetBytes(Frequency).CopyTo(bytes, 1);
        BitConverter.GetBytes(Q).CopyTo(bytes, 5);
        BitConverter.GetBytes(GainDb).CopyTo(bytes, 9);
        return bytes;
    }

    public static EqParamPacket FromBytes(byte[] data, int offset = 0)
    {
        return new EqParamPacket
        {
            Type = data[offset + 0],
            Frequency = BitConverter.ToSingle(data, offset + 1),
            Q = BitConverter.ToSingle(data, offset + 5),
            GainDb = BitConverter.ToSingle(data, offset + 9)
        };
    }

    public FilterParams ToFilterParams() => new()
    {
        Type = (FilterType)Type,
        Frequency = Frequency,
        Q = Q,
        Gain = GainDb
    };
}

/// <summary>
/// Manages USB communication with the DSPi device using LibUsbDotNet.
/// Uses USB Control Transfers on Interface 2 (vendor-specific, control-only).
/// </summary>
public partial class DspDevice : ObservableObject, IDisposable
{
    // Device identification
    private const int VendorId = 0x2E8A;
    private const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    private const int VendorInterfaceNumber = 2;

    // USB Request Types (matching Python script)
    // 0x41 = 01000001 (Dir: Host-to-Device | Type: Vendor | Recipient: Interface)
    // 0xC1 = 11000001 (Dir: Device-to-Host | Type: Vendor | Recipient: Interface)
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    private UsbDevice? _device;
    private readonly object _lock = new();
    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _statusPollTimer;
    private bool _disposed;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus? _currentStatus;

    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler<SystemStatus>? StatusUpdated;

    public DspDevice()
    {
        // Poll for device every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => CheckForDevice();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => PollStatus();
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        CheckForDevice();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
    }

    private void CheckForDevice()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_device != null && IsConnected)
            {
                // Already connected, verify still valid
                return;
            }

            try
            {
                // First try the standard method
                var finder = new UsbDeviceFinder(VendorId, ProductId);
                _device = UsbDevice.OpenUsbDevice(finder);

                // If that fails, try opening via registry
                if (_device == null)
                {
                    foreach (UsbRegistry reg in UsbDevice.AllDevices)
                    {
                        if (reg.Vid == VendorId && reg.Pid == ProductId)
                        {
                            // Try to open via registry entry
                            if (reg.Open(out _device))
                            {
                                break;
                            }
                            else
                            {
                                // Get more info about why it failed
                                var deviceType = reg.GetType().Name;
                                ErrorMessage = $"Device found ({deviceType}) but Open() failed. Run Zadig, select Interface 2, install WinUSB.";
                            }
                        }
                    }
                }

                if (_device == null)
                {
                    var allDevices = UsbDevice.AllDevices;
                    if (allDevices.Count == 0)
                    {
                        ErrorMessage = "No USB devices visible to LibUsbDotNet. Install libusb-win32 filter driver.";
                    }
                    else
                    {
                        bool found = allDevices.Any(r => r.Vid == VendorId && r.Pid == ProductId);
                        if (!found)
                        {
                            ErrorMessage = $"Device VID:{VendorId:X4} PID:{ProductId:X4} not found. {allDevices.Count} other device(s) visible.";
                        }
                        // else ErrorMessage was already set above
                    }

                    if (IsConnected)
                    {
                        HandleDisconnect();
                    }
                    return;
                }

                // For whole USB devices, set configuration
                if (_device is IUsbDevice wholeDevice)
                {
                    wholeDevice.SetConfiguration(1);
                    wholeDevice.ClaimInterface(VendorInterfaceNumber);
                }

                IsConnected = true;
                ErrorMessage = null;

                // Start status polling
                _statusPollTimer.Start();

                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
                _device?.Close();
                _device = null;
            }
        }
    }

    /// <summary>
    /// Poll status using control transfer. Called by timer.
    /// </summary>
    private void PollStatus()
    {
        if (_disposed || !IsConnected) return;

        try
        {
            var status = GetStatus();
            if (status != null)
            {
                CurrentStatus = status;
                StatusUpdated?.Invoke(this, status);
            }
        }
        catch
        {
            // Ignore polling errors
        }
    }

    private SystemStatus ParseStatusResponse(byte[] buffer)
    {
        // Status response (wValue=9): 12 bytes
        // peaks[5] as uint16 (10 bytes) + cpu0_load(1) + cpu1_load(1)
        return new SystemStatus
        {
            Peaks = new[]
            {
                BitConverter.ToUInt16(buffer, 0) / 65535.0f,
                BitConverter.ToUInt16(buffer, 2) / 65535.0f,
                BitConverter.ToUInt16(buffer, 4) / 65535.0f,
                BitConverter.ToUInt16(buffer, 6) / 65535.0f,
                BitConverter.ToUInt16(buffer, 8) / 65535.0f
            },
            Cpu0Load = buffer[10],
            Cpu1Load = buffer[11]
        };
    }

    private void HandleDisconnect()
    {
        _statusPollTimer.Stop();

        var wasConnected = IsConnected;

        _device?.Close();
        _device = null;

        IsConnected = false;

        // Only say "Device Removed" if we were previously connected
        if (wasConnected)
        {
            ErrorMessage = "Device Removed";
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
    }

    public void Reconnect()
    {
        lock (_lock)
        {
            HandleDisconnect();
        }
        CheckForDevice();
    }

    /// <summary>
    /// Send a vendor control OUT transfer (host to device)
    /// </summary>
    private bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var setupPacket = new UsbSetupPacket(
                RequestTypeOut,
                request,
                (short)value,
                VendorInterfaceNumber,
                (short)(data?.Length ?? 0));

            int transferred;
            var buffer = data ?? Array.Empty<byte>();

            return _device.ControlTransfer(ref setupPacket, buffer, buffer.Length, out transferred);
        }
    }

    /// <summary>
    /// Send a vendor control IN transfer (device to host)
    /// </summary>
    private byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        lock (_lock)
        {
            if (_device == null) return null;

            var setupPacket = new UsbSetupPacket(
                RequestTypeIn,
                request,
                (short)value,
                VendorInterfaceNumber,
                (short)length);

            var buffer = new byte[length];
            int transferred;

            if (_device.ControlTransfer(ref setupPacket, buffer, buffer.Length, out transferred))
            {
                if (transferred > 0)
                {
                    if (transferred < length)
                    {
                        var result = new byte[transferred];
                        Array.Copy(buffer, result, transferred);
                        return result;
                    }
                    return buffer;
                }
            }

            return null;
        }
    }

    #region High-Level Commands

    // EQ parameter indices for wValue encoding
    private const int EqParamType = 0;
    private const int EqParamFreq = 1;
    private const int EqParamQ = 2;
    private const int EqParamGain = 3;

    /// <summary>
    /// Encode wValue for EQ parameter access: (channel &lt;&lt; 8) | (band &lt;&lt; 4) | param
    /// </summary>
    private static ushort EncodeEqValue(int channel, int band, int param = 0)
    {
        return (ushort)((channel << 8) | (band << 4) | param);
    }

    /// <summary>
    /// Set EQ filter parameters for a specific channel and band.
    /// Sends 16-byte packet: channel(1), band(1), type(1), reserved(1), freq(4), Q(4), gain(4)
    /// </summary>
    public bool SetFilter(int channel, int band, FilterParams p)
    {
        var data = new byte[16];
        data[0] = (byte)channel;
        data[1] = (byte)band;
        data[2] = (byte)p.Type;
        data[3] = 0; // reserved
        BitConverter.GetBytes(p.Frequency).CopyTo(data, 4);
        BitConverter.GetBytes(p.Q).CopyTo(data, 8);
        BitConverter.GetBytes(p.Gain).CopyTo(data, 12);

        return ControlTransferOut(VendorCommands.SetEqParam, 0, data);
    }

    /// <summary>
    /// Get EQ filter parameters for a specific channel and band.
    /// Reads each parameter individually (4 bytes each) like the Python script.
    /// </summary>
    public FilterParams? GetFilter(int channel, int band)
    {
        // Read type (returned as uint32)
        var typeData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamType), 4);
        if (typeData == null || typeData.Length < 4) return null;
        var type = BitConverter.ToUInt32(typeData, 0);

        // Read frequency (float)
        var freqData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamFreq), 4);
        if (freqData == null || freqData.Length < 4) return null;
        var freq = BitConverter.ToSingle(freqData, 0);

        // Read Q (float)
        var qData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamQ), 4);
        if (qData == null || qData.Length < 4) return null;
        var q = BitConverter.ToSingle(qData, 0);

        // Read gain (float)
        var gainData = ControlTransferIn(VendorCommands.GetEqParam, EncodeEqValue(channel, band, EqParamGain), 4);
        if (gainData == null || gainData.Length < 4) return null;
        var gain = BitConverter.ToSingle(gainData, 0);

        return new FilterParams
        {
            Type = (FilterType)type,
            Frequency = freq,
            Q = q,
            Gain = gain
        };
    }

    /// <summary>
    /// Set master preamp gain in dB.
    /// </summary>
    public bool SetPreamp(float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetPreamp, 0, data);
    }

    /// <summary>
    /// Get current master preamp gain in dB.
    /// </summary>
    public float? GetPreamp()
    {
        var response = ControlTransferIn(VendorCommands.GetPreamp, 0, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Enable or disable master EQ bypass.
    /// </summary>
    public bool SetBypass(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetBypass, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get current bypass state.
    /// </summary>
    public bool? GetBypass()
    {
        var response = ControlTransferIn(VendorCommands.GetBypass, 0, 1);

        if (response == null || response.Length < 1)
            return null;

        return response[0] != 0;
    }

    /// <summary>
    /// Set delay for a specific channel in milliseconds.
    /// Channel is encoded in wValue.
    /// </summary>
    public bool SetDelay(int channel, float ms)
    {
        var data = BitConverter.GetBytes(ms);
        return ControlTransferOut(VendorCommands.SetDelay, (ushort)channel, data);
    }

    /// <summary>
    /// Get delay for a specific channel in milliseconds.
    /// </summary>
    public float? GetDelay(int channel)
    {
        var response = ControlTransferIn(VendorCommands.GetDelay, (ushort)channel, 4);

        if (response == null || response.Length < 4)
            return null;

        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Get system status (peak levels and CPU load).
    /// wValue=9 requests full status (12 bytes: 5 peaks + 2 CPU loads)
    /// </summary>
    public SystemStatus? GetStatus()
    {
        // wValue=9 requests all status data
        var response = ControlTransferIn(VendorCommands.GetStatus, 9, 12);

        if (response == null || response.Length < 12)
            return null;

        return ParseStatusResponse(response);
    }

    /// <summary>
    /// Save current parameters to flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte SaveParams()
    {
        var response = ControlTransferIn(VendorCommands.SaveParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Load parameters from flash memory.
    /// Returns FlashResult code.
    /// </summary>
    public byte LoadParams()
    {
        var response = ControlTransferIn(VendorCommands.LoadParams, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Reset all parameters to factory defaults.
    /// Returns FlashResult code.
    /// </summary>
    public byte FactoryReset()
    {
        var response = ControlTransferIn(VendorCommands.FactoryReset, 0, 1);
        return response != null && response.Length >= 1 ? response[0] : FlashResult.ErrWrite;
    }

    /// <summary>
    /// Set output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelGain(int outputChannel, float db)
    {
        var data = BitConverter.GetBytes(db);
        return ControlTransferOut(VendorCommands.SetChannelGain, (ushort)outputChannel, data);
    }

    /// <summary>
    /// Get output channel gain in dB. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public float? GetChannelGain(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelGain, (ushort)outputChannel, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool SetChannelMute(int outputChannel, bool muted)
    {
        return ControlTransferOut(VendorCommands.SetChannelMute, (ushort)outputChannel, new[] { (byte)(muted ? 1 : 0) });
    }

    /// <summary>
    /// Get output channel mute state. wValue = output index (0=OutL, 1=OutR, 2=Sub).
    /// </summary>
    public bool? GetChannelMute(int outputChannel)
    {
        var response = ControlTransferIn(VendorCommands.GetChannelMute, (ushort)outputChannel, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness compensation enabled state.
    /// </summary>
    public bool SetLoudnessEnabled(bool enabled)
    {
        return ControlTransferOut(VendorCommands.SetLoudnessEnabled, 0, new[] { (byte)(enabled ? 1 : 0) });
    }

    /// <summary>
    /// Get loudness compensation enabled state.
    /// </summary>
    public bool? GetLoudnessEnabled()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessEnabled, 0, 1);
        if (response == null || response.Length < 1) return null;
        return response[0] != 0;
    }

    /// <summary>
    /// Set loudness reference SPL (40-100 dB, default 83).
    /// </summary>
    public bool SetLoudnessRefSPL(float spl)
    {
        var data = BitConverter.GetBytes(spl);
        return ControlTransferOut(VendorCommands.SetLoudnessRefSPL, 0, data);
    }

    /// <summary>
    /// Get loudness reference SPL.
    /// </summary>
    public float? GetLoudnessRefSPL()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessRefSPL, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Set loudness intensity (0-200%, default 100).
    /// </summary>
    public bool SetLoudnessIntensity(float intensity)
    {
        var data = BitConverter.GetBytes(intensity);
        return ControlTransferOut(VendorCommands.SetLoudnessIntensity, 0, data);
    }

    /// <summary>
    /// Get loudness intensity.
    /// </summary>
    public float? GetLoudnessIntensity()
    {
        var response = ControlTransferIn(VendorCommands.GetLoudnessIntensity, 0, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToSingle(response, 0);
    }

    /// <summary>
    /// Get a 4-byte unsigned status value. wValue selects the stat type.
    /// </summary>
    public uint? GetStatusUInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToUInt32(response, 0);
    }

    /// <summary>
    /// Get a 4-byte signed status value. wValue selects the stat type.
    /// </summary>
    public int? GetStatusInt32(ushort wValue)
    {
        var response = ControlTransferIn(VendorCommands.GetStatus, wValue, 4);
        if (response == null || response.Length < 4) return null;
        return BitConverter.ToInt32(response, 0);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _statusPollTimer.Stop();
        _statusPollTimer.Dispose();
        Disconnect();

        GC.SuppressFinalize(this);
    }
}
