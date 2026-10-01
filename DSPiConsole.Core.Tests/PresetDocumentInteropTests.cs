using System.Text.Json;
using DSPiConsole.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// Files must move between the Windows and macOS Consoles. The fixture is the
/// macOS test suite's own shared-schema document (PresetDocumentTests.swift,
/// testWindowsWrittenDocumentDecodes), so both apps are pinned to one shape.
/// </summary>
public class PresetDocumentInteropTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static PresetDocument Read(string json) =>
        JsonSerializer.Deserialize<PresetDocument>(json, PresetDocumentJson.ReadOptions)!;

    [Fact]
    public void MacWrittenDocumentDecodes()
    {
        var doc = Read(Fixture("mac-shared-schema.dspipreset"));

        Assert.Equal("RP2350", doc.Meta.Platform);
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10 }, doc.Io.OutputPins);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, doc.Io.OutputSlotTypes);
        Assert.Equal(new byte[] { 5, 20, 21 }, doc.Io.SpdifRxPins);
        Assert.Equal(new byte[] { 4, 16, 17, 18 }, doc.Io.I2sRxPins);
        Assert.Null(doc.Io.SpdifRxPin4);
        Assert.Equal(3, doc.Io.SpdifRxPinCount);
        Assert.Equal(3, doc.Channels.Count);
        Assert.Equal(34, doc.Channels[2].Crossover[0].Type);
    }

    [Fact]
    public void ByteArraysAreWrittenAsNumberArrays()
    {
        var doc = new PresetDocument();
        doc.Io.OutputPins = new byte[] { 6, 7, 8, 9, 10 };
        string json = JsonSerializer.Serialize(doc, PresetDocumentJson.WriteOptions);

        using var parsed = JsonDocument.Parse(json);
        var pins = parsed.RootElement.GetProperty("io").GetProperty("outputPins");
        Assert.Equal(JsonValueKind.Array, pins.ValueKind);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, pins.EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void SpdifPinArrayStaysThreeLongWithFourthPinSeparate()
    {
        var doc = new PresetDocument();
        doc.Io.SpdifRxPins = new byte[] { 5, 20, 21 };
        doc.Io.SpdifRxPin4 = 22;
        string json = JsonSerializer.Serialize(doc, PresetDocumentJson.WriteOptions);

        using var parsed = JsonDocument.Parse(json);
        var io = parsed.RootElement.GetProperty("io");
        Assert.Equal(3, io.GetProperty("spdifRxPins").GetArrayLength());
        Assert.Equal(22, io.GetProperty("spdifRxPin4").GetInt32());

        var back = Read(json);
        Assert.Equal(4, back.Io.SpdifRxPinCount);
        Assert.Equal(22, back.Io.SpdifRxPinAt(3));
    }

    /// <summary>Earlier Windows builds wrote byte arrays as base64 and four
    /// S/PDIF pins in one array. Those files must keep importing.</summary>
    [Fact]
    public void LegacyWindowsBase64DocumentStillReads()
    {
        string legacy = """
        {
          "schemaVersion": 1,
          "channels": [ { "channelId": 0, "name": "In", "eq": [], "crossover": [] } ],
          "io": {
            "outputPins": "BgcICQo=",
            "spdifRxPins": "BRQVFg==",
            "i2sRxPins": "BBAREg=="
          }
        }
        """;
        var doc = Read(legacy);
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10 }, doc.Io.OutputPins);
        Assert.Equal(4, doc.Io.SpdifRxPinCount);
        Assert.Equal(22, doc.Io.SpdifRxPinAt(3));
        Assert.Equal(new byte[] { 4, 16, 17, 18 }, doc.Io.I2sRxPins);
    }

    [Fact]
    public void RoundTripPreservesIoBlock()
    {
        var doc = Read(Fixture("mac-shared-schema.dspipreset"));
        var again = Read(JsonSerializer.Serialize(doc, PresetDocumentJson.WriteOptions));
        Assert.Equal(doc.Io.OutputPins, again.Io.OutputPins);
        Assert.Equal(doc.Io.SpdifRxPins, again.Io.SpdifRxPins);
        Assert.Equal(doc.Io.I2sRxPins, again.Io.I2sRxPins);
        Assert.Equal(doc.Io.SpdifEnabledExt, again.Io.SpdifEnabledExt);
    }
}
