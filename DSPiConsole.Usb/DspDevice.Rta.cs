using DSPiConsole.Core.Rta;

namespace DSPiConsole.Usb;

/// <summary>The spectrum analyser's vendor requests (0x08-0x0F): plain
/// control transfers, so the engine in Core drives them through
/// <see cref="IRtaTransport"/>.</summary>
public partial class DspDevice : IRtaTransport
{
    public byte[]? RtaIn(byte request, ushort value, int length) => ControlTransferIn(request, value, length);

    public bool RtaOut(byte request, ushort value, byte[] data) => ControlTransferOut(request, value, data);
}
