using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Implements IDspiTransfer by communicating with a remote DSPiCliServer over TCP.
/// </summary>
public class DspiRemote : IDspiTransfer
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly object _lock = new();
    
    private bool _isConnected;
    private List<DSPiDeviceInfo> _availableDevices = new();
    private DSPiDeviceInfo? _selectedDeviceInfo;
    private string? _openDeviceSerial;

    private readonly System.Timers.Timer _scanTimer;
    private readonly System.Timers.Timer _statusPollTimer;

    public string DeviceType => "Remote";
    public bool IsConnected => _isConnected;
    public string? OpenDeviceSerial => _openDeviceSerial;
    public IReadOnlyList<DSPiDeviceInfo> AvailableDevices => _availableDevices;
    public DSPiDeviceInfo? SelectedDeviceInfo => _selectedDeviceInfo;

    public event EventHandler<byte[]>? NotifyPacketReceived;
    public event EventHandler? AvailableDevicesChanged;
    public event EventHandler? DeviceConnected;
    public event EventHandler? DeviceDisconnected;
    public event EventHandler? StatusPollRequested;

    public DspiRemote(string host = "localhost", int port = 8084)
    {
        _host = host;
        _port = port;

        _scanTimer = new System.Timers.Timer(10000);
        _scanTimer.Elapsed += (_, _) => ScanDevices();
        _scanTimer.AutoReset = true;

        _statusPollTimer = new System.Timers.Timer(2000);
        _statusPollTimer.Elapsed += (_, _) => StatusPollRequested?.Invoke(this, EventArgs.Empty);
        _statusPollTimer.AutoReset = true;
    }

    public void StartMonitoring()
    {
        Task.Run(() => ConnectAsync());
        _scanTimer.Start();
    }

    public void StopMonitoring()
    {
        _scanTimer.Stop();
        _statusPollTimer.Stop();
        Disconnect();
    }

    private async Task ConnectAsync()
    {
        lock (_lock)
        {
            if (_client != null) return;
        }

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_host, _port);
            
            lock (_lock)
            {
                _client = client;
                _stream = client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
            }
            
            // Start a background task to read from the server if it ever pushes data
            // For now, we mainly do request-response.
            _ = Task.Run(ReadLoop);

            ScanDevices();
        }
        catch
        {
            // Failed to connect, will retry via ScanDevices or similar if needed
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                lock (_lock)
                {
                    if (_reader == null) break;
                }
                
                // Note: This ReadLoop might conflict with the request-response pattern 
                // if the server doesn't use unique IDs for messages.
                // However, IDspiTransfer is mostly synchronous in its usage (via DspDevice).
                // If the server pushes notifications, we'd handle them here.
                await Task.Delay(1000); 
            }
        }
        catch { }
    }

    public void ScanDevices()
    {
        // For remote, we ask the server for its connection status
        string response = SendCommand("get_deviceid");
        if (response.StartsWith("Error") || response == "Not connected")
        {
            if (_isConnected)
            {
                _isConnected = false;
                _openDeviceSerial = null;
                _selectedDeviceInfo = null;
                _availableDevices = new List<DSPiDeviceInfo>();
                _statusPollTimer.Stop();
                DeviceDisconnected?.Invoke(this, EventArgs.Empty);
                AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            
            // Try to reconnect to server if lost
            if (response.StartsWith("Error: client is null"))
            {
                 _ = ConnectAsync();
            }
            return;
        }

        string serial = response.Trim();
        if (!_isConnected || _openDeviceSerial != serial)
        {
            _isConnected = true;
            _openDeviceSerial = serial;
            _selectedDeviceInfo = new DSPiDeviceInfo(serial, $"remote://{_host}:{_port}/{serial}");
            _availableDevices = new List<DSPiDeviceInfo> { _selectedDeviceInfo };
            
            AvailableDevicesChanged?.Invoke(this, EventArgs.Empty);
            DeviceConnected?.Invoke(this, EventArgs.Empty);
            _statusPollTimer.Start();
        }
    }

    public void OpenDevice(DSPiDeviceInfo deviceInfo)
    {
        // In remote mode, the server manages the device. 
        // We just verify it's the one we want or assume it is.
        ScanDevices();
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _statusPollTimer.Stop();
            _reader?.Dispose();
            _writer?.Dispose();
            _stream?.Dispose();
            _client?.Dispose();
            _reader = null;
            _writer = null;
            _stream = null;
            _client = null;
            
            if (_isConnected)
            {
                _isConnected = false;
                DeviceDisconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Reconnect()
    {
        Disconnect();
        _ = ConnectAsync();
    }

    public bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null)
    {
        string hexData = data != null ? Convert.ToHexString(data) : "";
        string cmd = $"cto {request} {value} {hexData}".Trim();
        string response = SendCommand(cmd);
        return response == "OK";
    }

    public byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4)
    {
        // Special optimization for status polling
        if (request == VendorCommands.GetStatus && value == 9)
        {
            string resp = SendCommand("get_peaks");
            if (!resp.StartsWith("Error") && resp != "Not connected")
            {
                try
                {
                    return Convert.FromHexString(resp);
                }
                catch(Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ScanDevices error: {ex.Message}");
                }
            }
        }

        string cmd = $"cti {request} {value} {length}";
        string response = SendCommand(cmd);
        if (response.StartsWith("Error") || response == "Not connected") 
            return null;
        try
        {
            return Convert.FromHexString(response);
        }
        catch
        {
            return null;
        }
    }

    private string SendCommand(string command)
    {
        lock (_lock)
        {
            if (_client == null || !_client.Connected || _writer == null || _reader == null)
            {
                return "Error: client is null or not connected";
            }

            try
            {
                _writer.WriteLine(command);
                // IDspiTransfer expects mostly synchronous behavior
                string? response = _reader.ReadLine();
                return response ?? "Error: No response";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
