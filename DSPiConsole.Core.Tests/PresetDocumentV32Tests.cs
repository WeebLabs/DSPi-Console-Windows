using System.Text.Json;
using System.Text.Json.Nodes;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>
/// The blocks the macOS Console added for wire V29 to V32 (subharm, tube, the
/// per-output limiter) and the channel fields that keep a document's channels
/// and delays apart, so a Mac file survives a Windows re-save. Ported from
/// PresetDocumentTests.swift.
/// </summary>
public class PresetDocumentV32Tests
{
    private static PresetDocument Read(string json) =>
        JsonSerializer.Deserialize<PresetDocument>(json, PresetDocumentJson.ReadOptions)!;

    private static string Write(PresetDocument doc) => JsonSerializer.Serialize(doc, PresetDocumentJson.WriteOptions);

    [Fact]
    public void NewBlocksRoundTrip()
    {
        var doc = new PresetDocument
        {
            Subharm = new PresetSubharmBlock { LowDb = -6, TopDb = 3, SelectMode = 2, LinkPairs = false },
            Tube = new PresetTubeBlock { TubeType = 0, BiasPct = -40, Rectifier = 3, XfmrEnabled = false },
        };
        doc.Meta.MasterVolumeMode = 0;
        doc.Meta.OutputConfigMode = 1;
        doc.Channels.Add(new PresetChannelBlock
        {
            ChannelId = 2,
            EqChannel = 8,
            OutputIndex = 0,
            IsOutput = true,
            DelayMs = 3,
            OutputDelayMs = 7,
            Limiter = new PresetLimiterBlock { Enabled = true, ThresholdDb = -3.5f, ReleaseMs = 250, LinkGroup = 2 },
        });

        var back = Read(Write(doc));
        Assert.Equal(-6f, back.Subharm!.LowDb);
        Assert.Equal(3f, back.Subharm.TopDb);
        Assert.Equal(2, back.Subharm.SelectMode);
        Assert.False(back.Subharm.LinkPairs);
        Assert.Equal(0, back.Tube!.TubeType);
        Assert.Equal(-40f, back.Tube.BiasPct);
        Assert.Equal(3, back.Tube.Rectifier);
        Assert.False(back.Tube.XfmrEnabled);
        Assert.Equal(0, back.Meta.MasterVolumeMode);
        Assert.Equal(1, back.Meta.OutputConfigMode);
        var ch = back.Channels[0];
        Assert.Equal(8, ch.EqChannel);
        Assert.Equal(0, ch.OutputIndex);
        Assert.Null(ch.InputIndex);
        Assert.Equal(3f, ch.DelayMs);
        Assert.Equal(7f, ch.OutputDelayMs);
        Assert.Equal(new LimiterOutputSettings { Enabled = true, ThresholdDb = -3.5f, ReleaseMs = 250, LinkGroup = 2 },
                     ch.Limiter!.ToSettings());
    }

    [Fact]
    public void KeysMatchTheSharedSchema()
    {
        var doc = new PresetDocument { Subharm = new(), Tube = new() };
        doc.Meta.MasterVolumeMode = 1;
        doc.Meta.OutputConfigMode = 1;
        doc.Channels.Add(new PresetChannelBlock { EqChannel = 0, InputIndex = 0, OutputIndex = 0, OutputDelayMs = 0, Limiter = new() });
        var root = JsonNode.Parse(Write(doc))!.AsObject();

        static ISet<string> Keys(JsonNode node) => node.AsObject().Select(p => p.Key).ToHashSet();

        Assert.Superset(new HashSet<string> { "subharm", "tube" }, Keys(root));
        Assert.Superset(new HashSet<string> { "masterVolumeMode", "outputConfigMode" }, Keys(root["meta"]!));
        Assert.Superset(new HashSet<string>
        {
            "enabled", "lowDb", "highDb", "topDb", "boostDb", "outputMask",
            "selectMode", "selectDepthPct", "selectHoldMs", "ceilingDb", "linkPairs",
        }, Keys(root["subharm"]!));
        Assert.Superset(new HashSet<string>
        {
            "enabled", "outputMask", "tubeType", "driveDb", "biasPct", "asymDb",
            "hardnessPct", "sagPct", "rectifier", "xfmrEnabled", "xfmrDamping",
            "xfmrResHz", "mixPct", "trimDb",
        }, Keys(root["tube"]!));
        var channel = root["channels"]![0]!;
        Assert.Superset(new HashSet<string> { "eqChannel", "inputIndex", "outputIndex", "outputDelayMs", "limiter" }, Keys(channel));
        Assert.Superset(new HashSet<string> { "enabled", "thresholdDb", "releaseMs", "linkGroup" }, Keys(channel["limiter"]!));
    }

    [Fact]
    public void MacWrittenBlocksDecodeAndSparseOnesTakeTheFirmwareDefaults()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "meta": { "savedUtc": "2026-09-30T10:00:00Z", "platform": "RP2350", "masterVolumeMode": 0 },
          "subharm": { "enabled": true, "lowDb": -3 },
          "tube": { "tubeType": 12, "driveDb": 6 },
          "channels": [
            { "channelId": 2, "eqChannel": 8, "outputIndex": 0, "isOutput": true, "delayMs": 1.5,
              "outputDelayMs": 4, "limiter": { "enabled": true, "linkGroup": 9 }, "eq": [] }
          ]
        }
        """;
        var doc = Read(json);
        Assert.True(doc.Subharm!.Enabled);
        Assert.Equal(-3f, doc.Subharm.LowDb);
        Assert.Equal(SubharmLimits.DefaultTopDb, doc.Subharm.TopDb);
        Assert.Equal(SubharmLimits.DefaultHoldMs, doc.Subharm.SelectHoldMs);
        Assert.True(doc.Subharm.LinkPairs);
        Assert.Equal(12, doc.Tube!.TubeType);
        Assert.Equal(TubeLimits.DefaultXfmrResHz, doc.Tube.XfmrResHz);
        Assert.True(doc.Tube.XfmrEnabled);
        Assert.Null(doc.Meta.OutputConfigMode);
        var ch = doc.Channels[0];
        Assert.Equal(4f, ch.OutputDelayMs);
        Assert.Equal(1.5f, ch.DelayMs);
        // A group past the maximum clamps, thresholds and releases default.
        var limiter = ch.Limiter!.ToSettings();
        Assert.Equal(LimiterLimits.LinkGroupMax, limiter.LinkGroup);
        Assert.Equal(LimiterLimits.DefaultThresholdDb, limiter.ThresholdDb);
        Assert.Equal(LimiterLimits.DefaultReleaseMs, limiter.ReleaseMs);
    }

    [Fact]
    public void OlderWindowsFilesHaveNoSplitFields()
    {
        var doc = Read("""{ "schemaVersion": 1, "meta": { "savedUtc": "2026-01-01T00:00:00Z" }, "channels": [ { "channelId": 2, "isOutput": true, "delayMs": 5 } ] }""");
        var ch = doc.Channels[0];
        Assert.Null(ch.OutputDelayMs);
        Assert.Null(ch.EqChannel);
        Assert.Null(ch.OutputIndex);
        Assert.Null(ch.Limiter);
        Assert.Null(doc.Subharm);
        Assert.Null(doc.Tube);
    }
}
