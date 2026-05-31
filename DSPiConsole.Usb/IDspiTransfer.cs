using System;
using System.Collections.Generic;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Usb;

/// <summary>
/// Interface for USB transfer operations and device management.
/// </summary>
public interface IDspiTransfer : IDisposable
{
    string DeviceType { get; }
    bool IsConnected { get; }
    string? OpenDeviceSerial { get; }
    IReadOnlyList<DSPiDeviceInfo> AvailableDevices { get; }
    DSPiDeviceInfo? SelectedDeviceInfo { get; }

    event EventHandler<byte[]>? NotifyPacketReceived;
    event EventHandler? AvailableDevicesChanged;
    event EventHandler? DeviceConnected;
    event EventHandler? DeviceDisconnected;
    event EventHandler? StatusPollRequested;

    void StartMonitoring();
    void StopMonitoring();
    void ScanDevices();
    void OpenDevice(DSPiDeviceInfo deviceInfo);
    void Disconnect();
    void Reconnect();
    bool ControlTransferOut(byte request, ushort value = 0, byte[]? data = null);
    byte[]? ControlTransferIn(byte request, ushort value = 0, int length = 4);
}
