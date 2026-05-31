using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Handles low-level USB communication for the DSPi device.
/// </summary>
public class DspiUsb : IDspiTransfer
{
    // Device identification
    public const int VendorId = 0x2E8B;
    public const int ProductId = 0xFEAA;

    // Interface 2 is the vendor-specific control interface
    public const int VendorInterfaceNumber = 2;

    // USB Request Types
    private const byte RequestTypeOut = 0x41;
    private const byte RequestTypeIn = 0xC1;

    private readonly UsbContext _context = new();
    private IUsbDevice? _device;
    private bool _interfaceClaimed;
    private byte _openBusNumber;
    private byte _openAddress;
    private string? _openDeviceSerial;
    private readonly object _lock = new();

    private readonly System.Timers.Timer _pollTimer;
    private readonly System.Timers.Timer _statusPollTimer;
    private readonly Dictionary<(byte bus, byte addr), string> _serialCache = new();
    private List<DSPiDeviceInfo> _availableDevices = new();
    private DSPiDeviceInfo? _selectedDeviceInfo;
    private string? _lastSelectedSerial;

    // Notification endpoint state
    private UsbEndpointReader? _notifyReader;
    private Thread? _notifyThread;
    private volatile bool _notifyStop;
    private const int NotifyPacketSize = 64;

    public string DeviceType => "USB";
    public bool IsConnected => _device != null;
    public string? OpenDeviceSerial => _openDeviceSerial;
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices => _availableDevices;
    public DSPiDeviceInfo? SelectedDeviceInfo => _selectedDeviceInfo;

    public event EventHandler<byte[]>? NotifyPacketReceived;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler? StatusPollRequested;

    public DspiUsb()
    {
        // Poll for devices every 500ms
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) => ScanDevices();
        _pollTimer.AutoReset = true;

        // Poll for status every 100ms when connected
        _statusPollTimer = new System.Timers.Timer(100);
        _statusPollTimer.Elapsed += (_, _) => StatusPollRequested?.Invoke(this, EventArgs.Empty);
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        _pollTimer.Start();
        ScanDevices();
    }

    public void StopMonitoring()
    {
        _pollTimer.Stop();
        _statusPollTimer.Stop();
    }

    public void ScanDevices()
    {
        try
        {
            var matching = ListMatchingDevices().ToList();

            // Drop cache entries for devices that have unplugged.
            var liveKeys = matching.Select(GetBusAddr).ToHashSet();
            lock (_lock)
            {
                foreach (var stale in _serialCache.Keys.Where(k => !liveKeys.Contains(k)).ToList())
                    _serialCache.Remove(stale);
            }

            // Build the current device list. For the device we already have open,
            // skip the open/claim/read cycle entirely — we know its serial.
            var currentDevices = new List<DSPiDeviceInfo>();
            (byte bus, byte addr) openKey = OpenBusAddr;
            bool weHaveOpen = IsConnected && _selectedDeviceInfo != null;

            foreach (var dev in matching)
            {
                var key = GetBusAddr(dev);

                if (weHaveOpen && key.bus == openKey.bus && key.addr == openKey.addr)
                {
                    currentDevices.Add(_selectedDeviceInfo!);
                    continue;
                }

                bool foundInCache = false;
                string? cachedSerial = null;
                lock (_lock)
                {
                    foundInCache = _serialCache.TryGetValue(key, out cachedSerial);
                }

                if (foundInCache)
                {
                    currentDevices.Add(new DSPiDeviceInfo(cachedSerial!,
                        $"vid_{VendorId:X4}&pid_{ProductId:X4}#{cachedSerial}"));
                    continue;
                }

                // Unknown device — open briefly to read serial, then close.
                try
                {
                    if (!dev.TryOpen()) continue;
                    try
                    {
                        if (!ConfigureAndClaim(dev)) continue;
                        var serial = ReadSerialFromDevice(dev);
                        if (!string.IsNullOrEmpty(serial))
                        {
                            lock (_lock)
                            {
                                _serialCache[key] = serial!;
                            }
                            currentDevices.Add(new DSPiDeviceInfo(serial!,
                                $"vid_{VendorId:X4}&pid_{ProductId:X4}#{serial}"));
                        }
                    }
                    finally
                    {
                        try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }
                catch { }
            }

            var oldSerials = _availableDevices.Select(d => d.Serial).ToHashSet();
            var newSerials = currentDevices.Select(d => d.Serial).ToHashSet();
            if (!oldSerials.SetEquals(newSerials))
            {
                _availableDevices = currentDevices;
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_selectedDeviceInfo != null && !newSerials.Contains(_selectedDeviceInfo.Serial))
            {
                Close();
            }

            if (IsConnected)
            {
                if (matching.Count == 0)
                {
                    Close();
                }
                return;
            }

            if (!IsConnected && currentDevices.Count > 0)
            {
                var reconnectTarget = _lastSelectedSerial != null
                    ? currentDevices.FirstOrDefault(d => d.Serial == _lastSelectedSerial)
                    : null;
                OpenDevice(reconnectTarget ?? currentDevices[0]);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
        }
    }

    public void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        lock (_lock)
        {
            try
            {
                if (IsConnected)
                {
                    Close();
                }

                List<IUsbDevice> candidates = new();
                foreach (var d in ListMatchingDevices())
                {
                    candidates.Add(d.Clone());
                }

                IUsbDevice? opened = null;
                foreach (var dev in candidates)
                {
                    if (opened != null)
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    if (!dev.TryOpen())
                    {
                        try { dev.Close(); } catch { }
                        continue;
                    }

                    bool claimed = false;
                    try
                    {
                        if (!ConfigureAndClaim(dev))
                        {
                            try { dev.Close(); } catch { }
                            continue;
                        }
                        claimed = true;

                        var serial = ReadSerialFromDevice(dev);
                        if (serial == deviceInfo.Serial)
                        {
                            opened = dev;
                        }
                        else
                        {
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                            try { dev.Close(); } catch { }
                        }
                    }
                    catch
                    {
                        if (claimed)
                            try { dev.ReleaseInterface(VendorInterfaceNumber); } catch { }
                        try { dev.Close(); } catch { }
                    }
                }

                if (opened == null)
                {
                    return;
                }

                if (!Open(opened, deviceInfo.Serial))
                {
                    return;
                }

                _selectedDeviceInfo = deviceInfo;
                _lastSelectedSerial = deviceInfo.Serial;
                
                _statusPollTimer.Start();
                DeviceConnected?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenDevice error: {ex.Message}");
                Close();
            }
        }
    }

    public IEnumerable<IUsbDevice> ListMatchingDevices()
    {
        return _context.List().Where(d => d.VendorId == VendorId && d.ProductId == ProductId);
    }

    public (byte bus, byte addr) GetBusAddr(IUsbDevice dev)
    {
        if (dev is UsbDevice ud) return (ud.BusNumber, ud.Address);
        return (0, 0);
    }

    public bool ConfigureAndClaim(IUsbDevice dev)
    {
        try { dev.SetConfiguration(1); }
        catch { /* already configured */ }
        return dev.ClaimInterface(VendorInterfaceNumber);
    }

    public string? ReadSerialFromDevice(IUsbDevice tempDevice)
    {
        var setupPacket = new UsbSetupPacket(RequestTypeIn, VendorCommands.GetSerial, 0, VendorInterfaceNumber, 16);
        var buffer = new byte[16];
        int transferred = tempDevice.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        if (transferred > 0)
            return System.Text.Encoding.ASCII.GetString(buffer, 0, transferred).TrimEnd('\0');
        return null;
    }

    public bool Open(IUsbDevice dev, string serial)
    {
        lock (_lock)
        {
            Close();
            _device = dev;
            var key = GetBusAddr(dev);
            _openBusNumber = key.bus;
            _openAddress = key.addr;
            _openDeviceSerial = serial;
            _interfaceClaimed = true; // Assumed claimed before calling Open or by Open logic
            
            StartNotifyListener(_device);
            return true;
        }
    }

    private void HandleDisconnect()
    {
        Close();
    }

    public void Disconnect()
    {
        HandleDisconnect();
    }

    public void Reconnect()
    {
        HandleDisconnect();
        ScanDevices();
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_device == null) return;
            StopNotifyListener();
            _statusPollTimer.Stop();
            if (_interfaceClaimed)
            {
                try { _device.ReleaseInterface(VendorInterfaceNumber); } catch { }
            }
            try { _device.Close(); } catch { }
            _device = null;
            _interfaceClaimed = false;
            _openBusNumber = 0;
            _openAddress = 0;
            _openDeviceSerial = null;
            _selectedDeviceInfo = null;
            DeviceDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        lock (_lock)
        {
            if (_device == null) return false;

            var buffer = data ?? Array.Empty<byte>();
            var setupPacket = new UsbSetupPacket(
                RequestTypeOut,
                request,
                value,
                VendorInterfaceNumber,
                buffer.Length);

            int transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);
            return transferred >= 0;
        }
    }

    public byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        lock (_lock)
        {
            if (_device == null) return null;

            var setupPacket = new UsbSetupPacket(
                RequestTypeIn,
                request,
                value,
                VendorInterfaceNumber,
                length);

            var buffer = new byte[length];
            int transferred = _device.ControlTransfer(setupPacket, buffer, 0, buffer.Length);

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

            return null;
        }
    }

    private void StartNotifyListener(IUsbDevice dev)
    {
        try
        {
            _notifyReader = dev.OpenEndpointReader(ReadEndpointID.Ep03, NotifyPacketSize, EndpointType.Bulk);
        }
        catch
        {
            _notifyReader = null;
            return;
        }

        _notifyStop = false;
        _notifyThread = new Thread(NotifyReadLoop)
        {
            IsBackground = true,
            Name = "DSPi notify"
        };
        _notifyThread.Start();
    }

    private void StopNotifyListener()
    {
        _notifyStop = true;
        var thread = _notifyThread;
        _notifyThread = null;
        _notifyReader = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(500); } catch { }
        }
    }

    private void NotifyReadLoop()
    {
        var buf = new byte[NotifyPacketSize];
        while (!_notifyStop)
        {
            UsbEndpointReader? reader = _notifyReader;
            if (reader == null) break;

            int len;
            LibUsbDotNet.Error err;
            try
            {
                err = reader.Read(buf, 1000, out len);
            }
            catch
            {
                break;
            }

            if (_notifyStop) break;

            if (err == LibUsbDotNet.Error.Timeout || err == LibUsbDotNet.Error.Interrupted)
                continue;
            if (err == LibUsbDotNet.Error.NoDevice || err == LibUsbDotNet.Error.NotFound)
                break;
            if (err != LibUsbDotNet.Error.Success || len <= 0)
                continue;

            var copy = new byte[len];
            Buffer.BlockCopy(buf, 0, copy, 0, len);
            NotifyPacketReceived?.Invoke(this, copy);
        }
    }

    public (byte bus, byte addr) OpenBusAddr => (_openBusNumber, _openAddress);

    public void Dispose()
    {
        Close();
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _statusPollTimer.Stop();
        _statusPollTimer.Dispose();
        Disconnect();
        _context.Dispose();
    }
}
