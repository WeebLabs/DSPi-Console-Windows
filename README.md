# DSPi Console for Windows

A WinUI 3 control application for the RP2040-based DSPi audio processor. This is a Windows port of the original macOS Swift application.

## Features

- **Real-time DSP Control**: Configure parametric EQ with up to 10 bands per channel
- **5 Audio Channels**:
  - Master L/R (USB inputs) - 10 EQ bands each
  - Out L/R (SPDIF outputs) - 2 EQ bands each  
  - Sub (PDM output) - 2 EQ bands
- **Filter Types**: Off, Peaking, Low Shelf, High Shelf, Low Pass, High Pass
- **Per-Channel Delay**: 0-170ms delay for output channels
- **Global Controls**: Preamp gain (-60 to +10 dB), Master EQ bypass
- **Real-time Monitoring**: Peak meters for all channels, CPU load for both RP2040 cores
- **Live Bode Plot**: Frequency response visualization with Win2D hardware acceleration
- **Modern UI**: Mica/Acrylic backdrop with automatic fallback

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Windows 11 recommended for Mica backdrop support
- Visual Studio 2022 with the following workloads:
  - .NET Desktop Development
  - Windows App SDK (C#)

## Building

1. Open `DSPiConsole.sln` in Visual Studio 2022
2. Select your target platform (x64, x86, or ARM64)
3. Build the solution (Ctrl+Shift+B)

For release builds:
```bash
dotnet build -c Release -r win-x64
```

## USB Driver Setup

The DSPi device presents as a UAC 1.0 audio device with vendor-specific control requests. The application uses LibUsbDotNet for USB communication.

### Option 1: WinUSB Driver (Recommended)

Use Zadig (https://zadig.akeo.ie/) to install the WinUSB driver:

1. Connect your DSPi device
2. Run Zadig as Administrator
3. Select "Pico DSP 2.1" from the device list (Options → List All Devices if not visible)
4. Select "WinUSB" as the driver
5. Click "Install Driver"

**Note**: This will replace the audio driver for the control interface only. The audio streaming will continue to work through the standard Windows audio driver.

### Option 2: libusb-win32

If WinUSB doesn't work, try installing libusb-win32 via Zadig instead.

## Project Structure

```
DSPiConsole/
├── DSPiConsole.sln              # Solution file
├── DSPiConsole/                 # Main WinUI 3 application
│   ├── App.xaml(.cs)            # Application entry point
│   ├── MainWindow.xaml(.cs)     # Main window with all UI
│   ├── Controls/                # Custom controls
│   │   ├── BodePlotControl.cs   # Frequency response graph
│   │   ├── HorizontalMeterBar.cs # Level meters
│   │   └── CpuMeter.cs          # CPU load display
│   ├── Converters/              # Value converters for XAML binding
│   └── ViewModels/              # MVVM ViewModels
│       └── MainViewModel.cs     # Main application state
├── DSPiConsole.Core/            # Core library (models, DSP math)
│   ├── Models/                  # Data models
│   │   ├── Channel.cs           # Channel definitions
│   │   ├── FilterParams.cs      # Filter parameters
│   │   └── SystemStatus.cs      # Real-time status
│   └── DspMath.cs               # Biquad coefficient calculation
└── DSPiConsole.Usb/             # USB communication library
    └── DspDevice.cs             # USB device handling
```

## USB Communication Protocol

The application communicates with the DSPi firmware using USB vendor control transfers:

| Request | Code | Direction | Description |
|---------|------|-----------|-------------|
| SET_EQ_PARAM | 0x42 | OUT | Set filter parameters |
| GET_EQ_PARAM | 0x43 | IN | Get filter parameters |
| SET_PREAMP | 0x44 | OUT | Set preamp gain |
| GET_PREAMP | 0x45 | IN | Get preamp gain |
| SET_BYPASS | 0x46 | OUT | Set bypass state |
| GET_BYPASS | 0x47 | IN | Get bypass state |
| SET_DELAY | 0x48 | OUT | Set channel delay |
| GET_DELAY | 0x49 | IN | Get channel delay |
| GET_STATUS | 0x50 | IN | Get peak/CPU status |

## Troubleshooting

### Device not detected

1. Check that the DSPi device is connected and powered
2. Verify the WinUSB driver is installed (Device Manager → Universal Serial Bus devices)
3. Try unplugging and reconnecting the device
4. Click the reconnect button in the application

### No audio

This application only controls the DSP parameters. Ensure:
1. The DSPi is selected as the audio output device in Windows
2. Audio is playing through an application

### High CPU usage

The application polls the device status at 60ms intervals. This is normal and required for real-time meter updates.

## License

This project is provided as-is for use with the DSPi hardware.

## Credits

- Original macOS application by Troy Dunn-Higgins
- Windows port using WinUI 3 and .NET 8
