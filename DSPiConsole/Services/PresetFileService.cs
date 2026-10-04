using System.Text.Json;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.Settings;
using DSPiConsole.Usb;
using DSPiConsole.ViewModels;

namespace DSPiConsole.Services;

/// <summary>Which parts of a document an import should apply.</summary>
public sealed class PresetApplyOptions
{
    /// <summary>EQ, crossover, delays, gains, matrix, the DSP feature blocks and
    /// channel names. Always applied — it is what a preset file is for.</summary>
    public bool AudioProcessing { get; set; } = true;

    /// <summary>Master and user volume. Off by default: a document from another
    /// system would otherwise change how loud the room gets on import.</summary>
    public bool VolumeLevels { get; set; }

    /// <summary>GPIO pin assignments, clocking, ADAT and the S/PDIF & I2S input
    /// wiring. Off by default — these describe a board, not a listening setup.</summary>
    public bool HardwareIo { get; set; }
}

/// <summary>What an import actually did, so the user is told rather than
/// left to infer it from the UI.</summary>
public sealed class PresetApplyReport
{
    public int ChannelsApplied { get; set; }
    public int BandsApplied { get; set; }
    public int CrossoverBandsApplied { get; set; }
    public int CrosspointsApplied { get; set; }

    /// <summary>Channels in the document that this device does not have.</summary>
    public List<string> MissingChannels { get; } = new();

    /// <summary>Blocks skipped because this firmware/platform lacks the feature,
    /// or because applying them would have conflicted.</summary>
    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Saves and restores a complete DSP configuration as a file. The document
/// covers what a firmware preset slot covers (see <see cref="PresetDocument"/>);
/// it is applied through the ordinary ViewModel setters rather than a bulk
/// write, so every value goes through the same clamping, platform gating and
/// dirty-tracking as a user edit.
/// </summary>
public static class PresetFileService
{
    public const string FileExtension = ".dspipreset";

    // ── Capture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot the current configuration. Reads the ViewModel, which is the
    /// live mirror of the device (kept current by the bulk fetch and the notify
    /// endpoint), so this does not need the bus to be idle.
    /// </summary>
    public static PresetDocument Capture(MainViewModel vm, string? name = null)
    {
        var doc = new PresetDocument
        {
            Meta = new PresetDocumentMeta
            {
                Name = name,
                SavedUtc = DateTimeOffset.UtcNow,
                AppVersion = AppInfo.Version,
                Platform = vm.Platform,
                WireFormatVersion = vm.Device.WireFormatVersion,
                InputChannelCount = vm.NumInputChannels,
                OutputChannelCount = vm.NumOutputChannels,
                MasterVolumeMode = vm.MasterVolumeMode,
                OutputConfigMode = vm.OutputConfigMode,
            },
        };

        // ── Global ──
        var g = doc.Global;
        for (int wireIn = 0; wireIn < g.InputPreampsDb.Length; wireIn++)
            g.InputPreampsDb[wireIn] = vm.InputPreampAt(wireIn);
        g.Bypass = vm.Bypass;
        g.MasterVolumeDb = vm.MasterVolumeDb;
        g.UserVolumeDb = vm.UserVolumeDb;
        g.InputSource = (byte)vm.ActiveInputSource;
        g.LgSoundSyncEnabled = vm.LgSoundSyncEnabled;
        for (int pair = 0; pair < g.InputPairLinked.Length; pair++)
            g.InputPairLinked[pair] = vm.GetInputPairLinked(pair);

        // ── Feature blocks ──
        doc.Loudness = new PresetLoudnessBlock
        {
            Enabled = vm.LoudnessEnabled,
            RefSpl = vm.LoudnessRefSPL,
            IntensityPct = vm.LoudnessIntensity,
            OutputMask = vm.LoudnessOutputMask,
        };

        doc.Crossfeed = new PresetCrossfeedBlock
        {
            Enabled = vm.CrossfeedEnabled,
            Preset = vm.CrossfeedPreset,
            FreqHz = vm.CrossfeedFreq,
            FeedDb = vm.CrossfeedFeed,
            Itd = vm.CrossfeedItd,
            OutputPairMask = vm.CrossfeedOutputPairMask,
        };

        doc.Leveller = new PresetLevellerBlock
        {
            Enabled = vm.LevellerEnabled,
            Speed = vm.LevellerSpeed,
            Lookahead = vm.LevellerLookahead,
            AmountPct = vm.LevellerAmount,
            MaxGainDb = vm.LevellerMaxGainDb,
            GateDb = vm.LevellerGateDb,
            DetectorMask = vm.LevellerDetectorMask,
            ApplyMask = vm.LevellerApplyMask,
        };

        if (vm.PsybassSupported)
        {
            doc.Psybass = new PresetPsybassBlock
            {
                Enabled = vm.PsybassEnabled,
                CutoffHz = vm.PsybassCutoffHz,
                HarmonicsDb = vm.PsybassHarmonicsDb,
                DriveDb = vm.PsybassDriveDb,
                CharacterPct = vm.PsybassCharacterPct,
                OriginalDb = vm.PsybassOriginalDb,
                OutputMask = vm.PsybassOutputMask,
            };
        }

        if (vm.UpmixSupported)
        {
            doc.Upmix = new PresetUpmixBlock
            {
                Enabled = vm.UpmixEnabled,
                CenterMode = vm.UpmixCenterMode,
                SurroundMode = vm.UpmixSurroundMode,
                StrengthPct = vm.UpmixStrengthPct,
                CenterWidthPct = vm.UpmixCenterWidthPct,
                ThresholdPct = vm.UpmixThresholdPct,
                AttackMs = vm.UpmixAttackMs,
                ReleaseMs = vm.UpmixReleaseMs,
                DetectorHpfHz = vm.UpmixDetectorHpfHz,
                SurroundDelayMs = vm.UpmixSurroundDelayMs,
                SurroundHpfHz = vm.UpmixSurroundHpfHz,
                SurroundLpfHz = vm.UpmixSurroundLpfHz,
                DecorrPct = vm.UpmixDecorrPct,
                PresenceDb = vm.UpmixPresenceDb,
            };
        }

        if (vm.SubharmSupported)
        {
            doc.Subharm = new PresetSubharmBlock
            {
                Enabled = vm.SubharmEnabled,
                LowDb = vm.SubharmLowDb,
                HighDb = vm.SubharmHighDb,
                TopDb = vm.SubharmTopDb,
                BoostDb = vm.SubharmBoostDb,
                OutputMask = vm.SubharmOutputMask,
                SelectMode = vm.SubharmSelectMode,
                SelectDepthPct = vm.SubharmSelectDepthPct,
                SelectHoldMs = vm.SubharmSelectHoldMs,
                CeilingDb = vm.SubharmCeilingDb,
                LinkPairs = vm.SubharmLinkPairs,
            };
        }

        if (vm.TubeSupported)
        {
            doc.Tube = new PresetTubeBlock
            {
                Enabled = vm.TubeEnabled,
                OutputMask = vm.TubeOutputMask,
                TubeType = vm.TubeType,
                DriveDb = vm.TubeDriveDb,
                BiasPct = vm.TubeBiasPct,
                AsymDb = vm.TubeAsymDb,
                HardnessPct = vm.TubeHardnessPct,
                SagPct = vm.TubeSagPct,
                Rectifier = vm.TubeRectifier,
                XfmrEnabled = vm.TubeXfmrEnabled,
                XfmrDamping = vm.TubeXfmrDamping,
                XfmrResHz = vm.TubeXfmrResHz,
                MixPct = vm.TubeMixPct,
                TrimDb = vm.TubeTrimDb,
            };
        }

        // ── Channels ──
        int wireInputs = vm.NumInputChannels;
        foreach (var channel in DeviceChannels(vm))
        {
            int id = (int)channel.Id;
            var block = new PresetChannelBlock
            {
                ChannelId = id,
                Name = vm.GetChannelName(channel),
                IsOutput = channel.IsOutput,
                // The channel delay: an input's own, or an output's output
                // delay, which is also written as OutputDelayMs (as the macOS
                // Console writes them).
                DelayMs = vm.GetPreMatrixDelay(id),
            };

            if (channel.IsOutput)
            {
                int outIndex = vm.GetOutputIndex(id);
                block.GainDb = vm.GetChannelGain(channel);
                block.Muted = outIndex >= 0 && vm.GetOutputMuted(outIndex);
                block.Enabled = outIndex >= 0 && vm.IsOutputEnabled(outIndex);
                block.OutputDelayMs = vm.GetChannelDelay(channel);
                if (outIndex >= 0)
                {
                    block.OutputIndex = outIndex;
                    block.EqChannel = wireInputs + outIndex;
                    if (vm.LimiterSupported && outIndex < vm.LimiterOutputs.Count)
                        block.Limiter = PresetLimiterBlock.From(vm.LimiterOutputs[outIndex]);
                }
            }
            else
            {
                int input = Channel.AllInputs.ToList().FindIndex(c => c.Id == channel.Id);
                if (input >= 0)
                {
                    block.InputIndex = input;
                    block.EqChannel = input;
                }
            }

            foreach (var band in vm.GetFilters(channel))
                block.Eq.Add(PresetBandBlock.From(band));

            if (channel.IsOutput && vm.CrossoverSupported)
            {
                foreach (var band in vm.GetXoverFilters(channel))
                    block.Crossover.Add(PresetBandBlock.From(band));
            }

            doc.Channels.Add(block);
        }

        // ── Matrix ──
        int outputCount = Math.Min(vm.ActiveOutputs.Count, MainViewModel.MatrixMaxOutputs);
        for (int inp = 0; inp < MainViewModel.MatrixMaxInputs; inp++)
        {
            for (int o = 0; o < outputCount; o++)
            {
                doc.Matrix.Add(new PresetCrosspointBlock
                {
                    Input = inp,
                    Output = o,
                    Enabled = vm.GetMatrixRouting(inp, o),
                    Invert = vm.GetMatrixInvert(inp, o),
                    GainDb = vm.GetMatrixGain(inp, o),
                });
            }
        }

        // ── Physical IO ──
        var io = doc.Io;
        // Indexed by pin-output id. Only the ids this board has are filled; the
        // rest stay 0, and the import skips them the same way. Note that PDM's
        // id is platform-dependent (2 on RP2040, 4 on RP2350), which is one
        // more reason the IO block is opt-in on import.
        foreach (var pinOutput in HardwarePins.AllPinOutputs(vm.Platform))
            if (pinOutput.Id >= 0 && pinOutput.Id < io.OutputPins.Length)
                io.OutputPins[pinOutput.Id] = vm.GetOutputPinValue(pinOutput.Id);
        for (int s = 0; s < io.OutputSlotTypes.Length; s++)
            io.OutputSlotTypes[s] = (byte)vm.GetOutputSlotType(s);

        io.I2sBckPin = vm.I2SBckPin;
        io.MckEnabled = vm.MckEnabled;
        io.MckPin = vm.MckPin;
        io.MckMultiplier = vm.MckMultiplier;
        io.I2sClockMode = vm.I2sClockMode;
        io.I2sClockPinMode = vm.I2sClockPinMode;
        io.I2sBckPinSlave = vm.I2sBckPinSlave;

        for (int i = 0; i < io.SpdifRxPins.Length; i++)
            io.SpdifRxPins[i] = vm.SpdifRxPinAt(i);
        io.SpdifRxPin4 = vm.SpdifRxPinAt(3);
        byte spdifMask = 0;
        for (int i = 1; i < 4; i++)
            if (vm.SpdifInputEnabled(i)) spdifMask |= (byte)(1 << (i - 1));
        io.SpdifEnabledExt = spdifMask;

        for (int pair = 0; pair < io.I2sRxPins.Length; pair++)
            io.I2sRxPins[pair] = vm.I2sRxPinAt(pair);
        io.I2sInputChannels = vm.I2sInputChannels;
        io.I2sInputRateHz = vm.I2sInputRateHz;

        io.AdatEnabled = vm.AdatEnabled;
        io.AdatPin = vm.AdatPin;
        io.AdatInputEnabled = vm.AdatInputEnabled;
        io.AdatInputPin = vm.AdatInputPin;
        io.AdatInputClockMode = vm.AdatInputClockMode;

        if (vm.DacHwMuteSupported)
        {
            var d = vm.DacHwMute;
            io.DacHwMute = new PresetDacHwMuteBlock
            {
                Enabled = d.Enabled,
                ActiveLow = d.ActiveLow,
                Pin = d.Pin,
                HoldMs = d.HoldMs,
                ReleaseMs = d.ReleaseMs,
            };
        }

        return doc;
    }

    /// <summary>The channels this device actually has: the wire input count (not
    /// the currently-streaming count, which follows the input source) plus the
    /// platform's outputs.</summary>
    private static IEnumerable<Channel> DeviceChannels(MainViewModel vm)
    {
        int inputs = Math.Clamp(vm.NumInputChannels, 1, Channel.AllInputs.Count);
        foreach (var ch in Channel.AllInputs.Take(inputs)) yield return ch;
        foreach (var ch in vm.ActiveOutputs) yield return ch;
    }

    // ── Serialization ────────────────────────────────────────────────────────

    public static string Serialize(PresetDocument doc) =>
        JsonSerializer.Serialize(doc, PresetDocumentJson.WriteOptions);

    /// <summary>
    /// Parse a document. Throws <see cref="InvalidDataException"/> with a
    /// user-facing message when the file isn't one of ours or is newer than
    /// this build understands.
    /// </summary>
    public static PresetDocument Deserialize(string json)
    {
        PresetDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<PresetDocument>(json, PresetDocumentJson.ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a valid preset file: {ex.Message}");
        }

        if (doc == null)
            throw new InvalidDataException("Not a valid preset file: the file is empty.");

        if (doc.SchemaVersion <= 0 || doc.Channels.Count == 0)
            throw new InvalidDataException("Not a valid preset file: no channel data found.");

        if (doc.SchemaVersion > PresetDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"This preset was written by a newer version of DSPi Console " +
                $"(format {doc.SchemaVersion}, this build reads {PresetDocument.CurrentSchemaVersion}).");
        }

        return doc;
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Push a document to the device. Call from the UI thread: the ViewModel
    /// setters raise change notifications. Values for features this firmware
    /// lacks are skipped and named in the report rather than written blindly.
    /// </summary>
    public static async Task<PresetApplyReport> ApplyAsync(
        PresetDocument doc, MainViewModel vm, PresetApplyOptions options,
        IProgress<double>? progress = null)
    {
        var report = new PresetApplyReport();

        var deviceChannels = DeviceChannels(vm).ToList();
        var deviceIds = new HashSet<int>(deviceChannels.Select(c => (int)c.Id));

        // Built by hand rather than ToDictionary: a hand-edited file with a
        // duplicated channel should apply the last one, not throw.
        var byId = new Dictionary<int, PresetChannelBlock>();
        foreach (var block in doc.Channels)
        {
            int? id = ResolveChannel(vm, doc, block);
            if (id is { } found && deviceIds.Contains(found)) byId[found] = block;
            else report.MissingChannels.Add(block.Name);
        }

        // Work out the total up front so the progress bar doesn't jump.
        int totalSteps = (options.AudioProcessing ? deviceChannels.Count + doc.Matrix.Count + 1 : 0)
                       + (options.HardwareIo ? 1 : 0);
        int step = 0;
        void Tick() => progress?.Report(totalSteps == 0 ? 1.0 : Math.Min(1.0, ++step / (double)totalSteps));

        if (options.AudioProcessing)
        {
            // PEQ link state first: a linked input pair mirrors every filter and
            // preamp write to its partner, so applying it after the channels
            // would let the device's *current* link state rewrite what we just
            // pushed.
            var linked = doc.Global.InputPairLinked;
            for (int pair = 0; pair < linked.Length; pair++)
                if (vm.GetInputPairLinked(pair) != linked[pair])
                    vm.SetInputPairLinked(pair, linked[pair]);

            await ApplyChannelsAsync(vm, byId, deviceChannels, SplitDelays(doc), report, Tick);
            ApplyMatrix(vm, doc, report, Tick);
            await ApplyFeatureBlocksAsync(vm, doc, report);
            Tick();
        }

        if (options.VolumeLevels)
            ApplyVolumes(vm, doc, report);

        if (options.HardwareIo)
        {
            await ApplyIoAsync(vm, doc, report);
            ApplyLimiters(vm, byId, report);
            Tick();
        }

        progress?.Report(1.0);
        return report;
    }

    /// <summary>
    /// The app channel a block refers to on this device: by its input or
    /// output index when the file has one (the macOS Console's numbering, and
    /// what newer Windows files write too), else by its Windows channel id.
    /// </summary>
    private static int? ResolveChannel(MainViewModel vm, PresetDocument doc, PresetChannelBlock block)
    {
        if (block.InputIndex is { } input)
            return input >= 0 && input < Channel.AllInputs.Count ? (int)Channel.AllInputs[input].Id : null;
        if (block.OutputIndex is { } output)
            return MapOutputIndex(vm, doc, output) is { } o ? (int)vm.ActiveOutputs[o].Id : null;
        // Older Windows files: a Windows channel id. Output ids are per board
        // (6 is PDM on RP2040 but S/PDIF 3 L on RP2350), so an output from the
        // other board goes through its output index.
        var fileOutputs = MainViewModel.OutputsForPlatform(doc.Meta.Platform);
        if (block.IsOutput && fileOutputs.Count > 0 && doc.Meta.Platform != vm.Platform)
        {
            int fileIndex = fileOutputs.ToList().FindIndex(c => (int)c.Id == block.ChannelId);
            return fileIndex >= 0 && MapOutputIndex(vm, doc, fileIndex) is { } o2
                ? (int)vm.ActiveOutputs[o2].Id : null;
        }
        return block.ChannelId;
    }

    /// <summary>
    /// A file's output index as this device's, or null if the device has no
    /// such output. The S/PDIF outputs share their indices on both boards, but
    /// PDM comes after them: output 4 on RP2040 (two S/PDIF pairs) and output 8
    /// on RP2350 (four). Matching by index alone would put an RP2040 file's
    /// PDM settings on RP2350's S/PDIF 3 L, and the reverse would put S/PDIF 3 L
    /// on RP2040's PDM. A file with no platform, or this device's, maps as is.
    /// </summary>
    private static int? MapOutputIndex(MainViewModel vm, PresetDocument doc, int fileOutput)
    {
        var deviceOutputs = vm.ActiveOutputs;
        var fileOutputs = MainViewModel.OutputsForPlatform(doc.Meta.Platform);
        if (fileOutputs.Count == 0 || doc.Meta.Platform == vm.Platform)
            return fileOutput >= 0 && fileOutput < deviceOutputs.Count ? fileOutput : null;
        if (fileOutput < 0 || fileOutput >= fileOutputs.Count || deviceOutputs.Count == 0) return null;
        // PDM is the last output on both boards.
        if (fileOutput == fileOutputs.Count - 1) return deviceOutputs.Count - 1;
        return fileOutput < deviceOutputs.Count - 1 ? fileOutput : null;
    }

    /// <summary>A per-output bit mask (bit k = output k) from the file, remapped
    /// to this device's outputs (see <see cref="MapOutputIndex"/>).</summary>
    private static int MapOutputMask(MainViewModel vm, PresetDocument doc, int mask)
    {
        if (MainViewModel.OutputsForPlatform(doc.Meta.Platform).Count == 0 || doc.Meta.Platform == vm.Platform)
            return mask;
        int mapped = 0;
        for (int k = 0; k < 16; k++)
            if ((mask & (1 << k)) != 0 && MapOutputIndex(vm, doc, k) is { } o)
                mapped |= 1 << o;
        return mapped;
    }

    /// <summary>The crossfeed output-pair mask (bit p = S/PDIF pair p) from the
    /// file. The pairs share their indices on both boards, so only pairs this
    /// device lacks are dropped.</summary>
    private static int MapOutputPairMask(MainViewModel vm, PresetDocument doc, int mask)
    {
        if (MainViewModel.OutputsForPlatform(doc.Meta.Platform).Count == 0 || doc.Meta.Platform == vm.Platform)
            return mask;
        int pairs = (vm.ActiveOutputs.Count - 1) / 2;
        return mask & ((1 << pairs) - 1);
    }

    /// <summary>True when the file carries input delays: it keeps an output's
    /// delay in OutputDelayMs and uses DelayMs as the channel delay, as the
    /// macOS Console and newer Windows files do. Older Windows files held only
    /// the output delay, in DelayMs, and no input delays.</summary>
    private static bool SplitDelays(PresetDocument doc) =>
        doc.Channels.Any(c => c.OutputDelayMs != null || c.EqChannel != null);

    /// <summary>Output limiters, from each output's block. They follow the
    /// firmware's output_config_mode like the pins, so they come with the
    /// hardware option. Applied gang-safely (see ApplyLimiterSettings).</summary>
    private static void ApplyLimiters(MainViewModel vm, Dictionary<int, PresetChannelBlock> byId, PresetApplyReport report)
    {
        var targets = new Dictionary<int, LimiterOutputSettings>();
        foreach (var (id, block) in byId)
        {
            if (block.Limiter is not { } limiter) continue;
            int output = vm.GetOutputIndex(id);
            if (output >= 0) targets[output] = limiter.ToSettings();
        }
        if (targets.Count == 0) return;
        if (!vm.LimiterSupported)
        {
            report.Skipped.Add("Output limiters (not supported by this firmware)");
            return;
        }
        vm.ApplyLimiterSettings(targets);
    }

    private static async Task ApplyChannelsAsync(
        MainViewModel vm, Dictionary<int, PresetChannelBlock> byId,
        List<Channel> deviceChannels, bool splitDelays, PresetApplyReport report, Action tick)
    {
        // Disable outputs first, then enable — an enable can conflict with a
        // channel the document is about to turn off (PDM vs S/PDIF 3 on RP2040).
        foreach (bool enabling in new[] { false, true })
        {
            foreach (var channel in deviceChannels)
            {
                if (!channel.IsOutput) continue;
                if (!byId.TryGetValue((int)channel.Id, out var block)) continue;
                if (block.Enabled != enabling) continue;

                int outIndex = vm.GetOutputIndex((int)channel.Id);
                if (outIndex < 0) continue;
                if (vm.IsOutputEnabled(outIndex) == block.Enabled) continue;

                if (block.Enabled && vm.WouldConflict(outIndex))
                {
                    report.Skipped.Add($"{block.Name} could not be enabled (conflicts with another output)");
                    continue;
                }

                vm.SetOutputEnabled(outIndex, block.Enabled);
                vm.SetOutputEnableUsb(outIndex, block.Enabled);
            }
        }

        foreach (var channel in deviceChannels)
        {
            int id = (int)channel.Id;
            if (!byId.TryGetValue(id, out var block))
            {
                tick();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(block.Name) && block.Name != vm.GetChannelName(channel))
                vm.SetChannelName(channel, block.Name);

            // An output's delay is one value in the firmware (the output-delay
            // SET also sets its channel delay), carried as OutputDelayMs, or as
            // DelayMs in older Windows files. An input's own delay is DelayMs,
            // which only files that keep the two apart carry.
            if (channel.IsOutput)
                vm.SetDelay(id, block.OutputDelayMs ?? block.DelayMs);
            else if (splitDelays)
                vm.SetPreMatrixDelay(id, block.DelayMs);

            if (channel.IsOutput)
            {
                vm.SetChannelGain(id, block.GainDb);
                int outIndex = vm.GetOutputIndex(id);
                if (outIndex >= 0 && vm.GetOutputMuted(outIndex) != block.Muted)
                {
                    // Two independent caches hold output mute: _outputMuted
                    // (matrix window) and _channelMutes (main window meters and
                    // mute buttons). Both setters send the same SET_OUTPUT_MUTE,
                    // so writing only one leaves half the UI showing stale state
                    // until the next bulk refresh. Write both; the duplicate
                    // transfer is idempotent.
                    vm.SetOutputMuted(outIndex, block.Muted);
                    vm.SetChannelMute(id, block.Muted);
                }
            }

            // EQ. A document that carries no bands for this channel leaves its
            // EQ alone (same rule as a filter file with no PEQ section); one
            // that carries some flattens the rest, so an imported channel is
            // never a blend of two presets. Bands past this channel's count are
            // dropped (a 12-band source read by a 10-band build).
            if (block.Eq.Count > 0)
            {
                for (int band = 0; band < channel.BandCount; band++)
                {
                    var fp = band < block.Eq.Count
                        ? Sanitize(vm, block.Eq[band].ToFilterParams(), report)
                        : new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f);
                    await vm.SetFilter(id, band, fp);
                    report.BandsApplied++;
                }
            }

            if (channel.IsOutput && vm.CrossoverSupported && block.Crossover.Count > 0)
            {
                for (int b = 0; b < CrossoverFilter.MaxXoverBands; b++)
                {
                    var fp = b < block.Crossover.Count
                        ? block.Crossover[b].ToFilterParams()
                        : new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f);
                    await vm.SetXoverFilter(id, b, fp);
                    report.CrossoverBandsApplied++;
                }
            }

            report.ChannelsApplied++;
            tick();
        }

        if (!vm.CrossoverSupported && byId.Values.Any(c => c.Crossover.Count > 0))
            report.Skipped.Add("Crossover bands (not supported by this firmware)");
    }

    /// <summary>
    /// Neutralise a band the connected firmware can't represent. A Linkwitz
    /// Transform is sent as an 18-byte SET that pre-V22 firmware doesn't parse,
    /// and a bypass flag on firmware without band bypass would leave the band
    /// audible while the app believed it was muted. Both are reported once.
    /// </summary>
    private static FilterParams Sanitize(MainViewModel vm, FilterParams fp, PresetApplyReport report)
    {
        if (fp.Type == FilterType.LinkwitzTransform && !vm.LinkwitzTransformSupported)
        {
            const string note = "Linkwitz Transform bands (not supported by this firmware) were set to Off";
            if (!report.Skipped.Contains(note)) report.Skipped.Add(note);
            return new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f);
        }

        if (!vm.FilterTypeSupported(fp.Type))
        {
            const string note = "Filter types this firmware can't represent were set to Off";
            if (!report.Skipped.Contains(note)) report.Skipped.Add(note);
            return new FilterParams(FilterType.Flat, 1000f, 0.707f, 0f);
        }

        if (fp.Bypass && !vm.BandBypassSupported)
        {
            const string note = "Per-band bypass (not supported by this firmware) was cleared";
            if (!report.Skipped.Contains(note)) report.Skipped.Add(note);
            fp.Bypass = false;
        }

        return fp;
    }

    private static void ApplyMatrix(MainViewModel vm, PresetDocument doc, PresetApplyReport report, Action tick)
    {
        int outputCount = Math.Min(vm.ActiveOutputs.Count, MainViewModel.MatrixMaxOutputs);
        foreach (var cp in doc.Matrix)
        {
            int? output = MapOutputIndex(vm, doc, cp.Output);
            if (cp.Input < 0 || cp.Input >= MainViewModel.MatrixMaxInputs ||
                output is not { } o || o >= outputCount)
            {
                tick();
                continue;
            }

            vm.SetMatrixRoute(cp.Input, o, cp.Enabled, cp.GainDb, cp.Invert);
            report.CrosspointsApplied++;
            tick();
        }
    }

    private static async Task ApplyFeatureBlocksAsync(MainViewModel vm, PresetDocument doc, PresetApplyReport report)
    {
        // Preamps, bypass, input source. (PEQ link is applied earlier — see
        // ApplyAsync — because it changes what a per-channel write does.)
        var g = doc.Global;
        int inputs = Math.Clamp(vm.NumInputChannels, 1, g.InputPreampsDb.Length);
        for (int wireIn = 0; wireIn < inputs; wireIn++)
            vm.SetInputPreampAt(wireIn, g.InputPreampsDb[wireIn]);

        vm.Bypass = g.Bypass;

        if (vm.InputSourceSupported)
        {
            var source = (InputSource)g.InputSource;
            if (vm.ActiveInputSource != source)
                await vm.SetInputSourceAsync(source);
        }
        else if (g.InputSource != (byte)InputSource.Usb)
        {
            report.Skipped.Add("Input source (not supported by this firmware)");
        }

        if (vm.LgSoundSyncSupported)
            vm.LgSoundSyncEnabled = g.LgSoundSyncEnabled;
        else if (g.LgSoundSyncEnabled)
            report.Skipped.Add("LG Sound Sync (not supported by this firmware)");

        // Loudness
        vm.LoudnessRefSPL = doc.Loudness.RefSpl;
        vm.LoudnessIntensity = doc.Loudness.IntensityPct;
        if (vm.LoudnessMaskSupported)
            vm.LoudnessOutputMask = MapOutputMask(vm, doc, doc.Loudness.OutputMask);
        vm.LoudnessEnabled = doc.Loudness.Enabled;

        // Crossfeed
        vm.CrossfeedPreset = doc.Crossfeed.Preset;
        vm.CrossfeedFreq = doc.Crossfeed.FreqHz;
        vm.CrossfeedFeed = doc.Crossfeed.FeedDb;
        vm.CrossfeedItd = doc.Crossfeed.Itd;
        if (vm.CrossfeedMaskSupported)
            vm.CrossfeedOutputPairMask = MapOutputPairMask(vm, doc, doc.Crossfeed.OutputPairMask);
        vm.CrossfeedEnabled = doc.Crossfeed.Enabled;

        // Volume leveller
        vm.LevellerSpeed = doc.Leveller.Speed;
        vm.LevellerLookahead = doc.Leveller.Lookahead;
        vm.LevellerAmount = doc.Leveller.AmountPct;
        vm.LevellerMaxGainDb = doc.Leveller.MaxGainDb;
        vm.LevellerGateDb = doc.Leveller.GateDb;
        if (vm.LevellerMasksSupported)
        {
            vm.LevellerDetectorMask = doc.Leveller.DetectorMask;
            vm.LevellerApplyMask = doc.Leveller.ApplyMask;
        }
        vm.LevellerEnabled = doc.Leveller.Enabled;

        // Psychoacoustic bass
        if (doc.Psybass is { } pb)
        {
            if (vm.PsybassSupported)
            {
                vm.PsybassCutoffHz = pb.CutoffHz;
                vm.PsybassHarmonicsDb = pb.HarmonicsDb;
                vm.PsybassDriveDb = pb.DriveDb;
                vm.PsybassCharacterPct = pb.CharacterPct;
                vm.PsybassOriginalDb = pb.OriginalDb;
                vm.PsybassOutputMask = MapOutputMask(vm, doc, pb.OutputMask);
                vm.PsybassEnabled = pb.Enabled;
            }
            else
            {
                report.Skipped.Add("Psychoacoustic bass (not supported by this firmware)");
            }
        }

        // Subharmonic synthesizer
        if (doc.Subharm is { } sb)
        {
            if (vm.SubharmSupported)
            {
                vm.SubharmLowDb = Math.Clamp(sb.LowDb, SubharmLimits.LevelMinDb, SubharmLimits.LevelMaxDb);
                vm.SubharmHighDb = Math.Clamp(sb.HighDb, SubharmLimits.LevelMinDb, SubharmLimits.LevelMaxDb);
                vm.SubharmBoostDb = Math.Clamp(sb.BoostDb, SubharmLimits.BoostMinDb, SubharmLimits.BoostMaxDb);
                vm.SubharmOutputMask = MapOutputMask(vm, doc, sb.OutputMask & 0xFFFF);
                if (vm.SubharmExtendedSupported)
                {
                    vm.SubharmTopDb = Math.Clamp(sb.TopDb, SubharmLimits.LevelMinDb, SubharmLimits.LevelMaxDb);
                    vm.SubharmSelectMode = Math.Clamp(sb.SelectMode, 0, SubharmSelectMode.Max);
                    vm.SubharmSelectDepthPct = Math.Clamp(sb.SelectDepthPct, SubharmLimits.DepthMinPct, SubharmLimits.DepthMaxPct);
                    vm.SubharmSelectHoldMs = Math.Clamp(sb.SelectHoldMs, SubharmLimits.HoldMinMs, SubharmLimits.HoldMaxMs);
                    vm.SubharmCeilingDb = Math.Clamp(sb.CeilingDb, SubharmLimits.CeilingMinDb, SubharmLimits.CeilingMaxDb);
                    vm.SubharmLinkPairs = sb.LinkPairs;
                }
                vm.SubharmEnabled = sb.Enabled;
            }
            else
            {
                report.Skipped.Add("Subharmonic synth (not supported by this firmware)");
            }
        }

        // Tube modeller. The character values go first and the type last: a
        // type loads its own row, and a character write drops the type to
        // Custom, so this order leaves both as the file has them.
        if (doc.Tube is { } tb)
        {
            if (vm.TubeSupported)
            {
                vm.TubeOutputMask = MapOutputMask(vm, doc, tb.OutputMask & 0xFFFF);
                vm.TubeDriveDb = Math.Clamp(tb.DriveDb, TubeLimits.DriveMinDb, TubeLimits.DriveMaxDb);
                vm.TubeBiasPct = Math.Clamp(tb.BiasPct, TubeLimits.BiasMinPct, TubeLimits.BiasMaxPct);
                vm.TubeAsymDb = Math.Clamp(tb.AsymDb, TubeLimits.AsymMinDb, TubeLimits.AsymMaxDb);
                vm.TubeHardnessPct = Math.Clamp(tb.HardnessPct, TubeLimits.HardnessMinPct, TubeLimits.HardnessMaxPct);
                vm.TubeSagPct = Math.Clamp(tb.SagPct, TubeLimits.SagMinPct, TubeLimits.SagMaxPct);
                vm.TubeRectifier = Math.Clamp(tb.Rectifier, 0, TubeLimits.RectifierMax);
                vm.TubeXfmrEnabled = tb.XfmrEnabled;
                vm.TubeXfmrDamping = Math.Clamp(tb.XfmrDamping, TubeLimits.XfmrDampingMin, TubeLimits.XfmrDampingMax);
                vm.TubeXfmrResHz = Math.Clamp(tb.XfmrResHz, TubeLimits.XfmrResMinHz, TubeLimits.XfmrResMaxHz);
                vm.TubeMixPct = Math.Clamp(tb.MixPct, TubeLimits.MixMinPct, TubeLimits.MixMaxPct);
                vm.TubeTrimDb = Math.Clamp(tb.TrimDb, TubeLimits.TrimMinDb, TubeLimits.TrimMaxDb);
                vm.TubeType = Math.Clamp(tb.TubeType, 0, TubeLimits.TypeMax);
                vm.TubeEnabled = tb.Enabled;
            }
            else
            {
                report.Skipped.Add("Tube modeller (not supported by this firmware)");
            }
        }

        // Stereo upmixer
        if (doc.Upmix is { } um)
        {
            if (vm.UpmixSupported)
            {
                vm.UpmixCenterMode = um.CenterMode;
                vm.UpmixSurroundMode = um.SurroundMode;
                vm.UpmixStrengthPct = um.StrengthPct;
                vm.UpmixCenterWidthPct = um.CenterWidthPct;
                vm.UpmixThresholdPct = um.ThresholdPct;
                vm.UpmixAttackMs = um.AttackMs;
                vm.UpmixReleaseMs = um.ReleaseMs;
                vm.UpmixDetectorHpfHz = um.DetectorHpfHz;
                vm.UpmixSurroundDelayMs = um.SurroundDelayMs;
                vm.UpmixSurroundHpfHz = um.SurroundHpfHz;
                vm.UpmixSurroundLpfHz = um.SurroundLpfHz;
                vm.UpmixDecorrPct = um.DecorrPct;
                vm.UpmixPresenceDb = um.PresenceDb;
                vm.UpmixEnabled = um.Enabled;
            }
            else
            {
                report.Skipped.Add("Stereo upmixer (not supported by this device)");
            }
        }
    }

    private static void ApplyVolumes(MainViewModel vm, PresetDocument doc, PresetApplyReport report)
    {
        // Master volume is only per-preset when the device says so; in
        // independent mode it is device-global and saved separately, so
        // overwriting it from a file would fight the user's own setting.
        if (vm.MasterVolumeMode == 1)
            vm.MasterVolumeDb = doc.Global.MasterVolumeDb;
        else
            report.Skipped.Add("Master volume (device is in independent master-volume mode)");

        vm.UserVolumeDb = doc.Global.UserVolumeDb;
    }

    private static async Task ApplyIoAsync(MainViewModel vm, PresetDocument doc, PresetApplyReport report)
    {
        var io = doc.Io;
        var rejections = new List<string>();

        // Every pin/clock setter issues a blocking control transfer. They
        // marshal their own change notifications, and the Hardware settings
        // page calls them from a background task for exactly this reason:
        // running the block on the UI thread would freeze the window.
        await Task.Run(() => ApplyIoPins(vm, io, rejections));
        report.Skipped.AddRange(rejections);

        if (io.DacHwMute is { } dm)
        {
            if (vm.DacHwMuteSupported)
            {
                await vm.ApplyDacHwMuteAsync(new DacHwMuteConfig
                {
                    Enabled = dm.Enabled,
                    ActiveLow = dm.ActiveLow,
                    Pin = dm.Pin,
                    HoldMs = dm.HoldMs,
                    ReleaseMs = dm.ReleaseMs,
                });
            }
            else
            {
                report.Skipped.Add("External DAC hardware mute (not supported by this firmware)");
            }
        }
    }

    /// <summary>
    /// The GPIO, clocking and input-wiring writes. Synchronous and blocking —
    /// call it from a background task. Refusals are collected rather than
    /// thrown: the setters answer with a <see cref="PinConfigResult"/>.
    /// </summary>
    private static void ApplyIoPins(MainViewModel vm, PresetIoBlock io, List<string> rejections)
    {
        // The pin setters answer with a PinConfigResult rather than throwing —
        // a GPIO that is already in use, or invalid on this board, is refused
        // silently. Name the refusals so a half-applied wiring change is
        // visible instead of being discovered later as no audio.
        void Try(string what, Func<byte> set)
        {
            byte status = set();
            if (status == PinConfigResult.Success) return;
            rejections.Add($"{what} rejected by the device ({DescribePinResult(status)})");
        }

        // Slot types before pins: a slot switching to I2S changes what its pin
        // assignment means, and the VM regenerates auto channel names from it.
        for (int slot = 0; slot < Math.Min(io.OutputSlotTypes.Length, vm.NumOutputSlots); slot++)
        {
            int s = slot;
            Try($"Output {s + 1} type", () => vm.SetOutputSlotType(s, (OutputSlotType)io.OutputSlotTypes[s]));
        }

        // Only the pin outputs this board actually has: the ids are contiguous
        // but PDM sits at a different one per platform (2 on RP2040, 4 on
        // RP2350), and writing an id the board lacks just earns InvalidOutput.
        foreach (var pinOutput in HardwarePins.AllPinOutputs(vm.Platform))
        {
            if (pinOutput.Id < 0 || pinOutput.Id >= io.OutputPins.Length) continue;
            byte want = io.OutputPins[pinOutput.Id];
            if (vm.GetOutputPinValue(pinOutput.Id) == want) continue;

            byte status = vm.SetOutputPinValue(pinOutput.Id, want);

            // The firmware refuses to move the PDM pin while PDM is enabled.
            // Cycle it the way the Hardware settings page does, then restore
            // whatever enable state the output is supposed to be in.
            if (status == PinConfigResult.OutputActive && pinOutput.SlotIndex < 0)
            {
                int pdmIndex = vm.ActiveOutputs.Count - 1;
                vm.Device.SetOutputEnable(pdmIndex, false);
                status = vm.SetOutputPinValue(pinOutput.Id, want);
                vm.Device.SetOutputEnable(pdmIndex, vm.IsOutputEnabled(pdmIndex));
            }

            if (status != PinConfigResult.Success)
                rejections.Add($"{pinOutput.Name} GPIO rejected by the device ({DescribePinResult(status)})");
        }

        Try("I2S BCK pin", () => vm.SetI2SBckPin(io.I2sBckPin));
        Try("MCK enable", () => vm.SetMckEnable(io.MckEnabled));
        Try("MCK pin", () => vm.SetMckPin(io.MckPin));
        Try("MCK multiplier", () => vm.SetMckMultiplier(io.MckMultiplier));

        if (vm.I2sClockModeSupported)
            vm.SetI2sClockMode(io.I2sClockMode);
        if (vm.I2sClockPinModeSupported)
        {
            Try("I2S clock pin mode", () => vm.SetI2sClockPinMode(io.I2sClockPinMode));
            Try("I2S slave BCK pin", () => vm.SetI2sBckPinSlave(io.I2sBckPinSlave));
        }

        // S/PDIF inputs: each input's pin lands before its enable, so an input
        // being switched on is already pointed at the right GPIO.
        if (io.SpdifRxPinCount > 0)
            Try("S/PDIF RX pin", () => vm.SetSpdifRxPin(io.SpdifRxPinAt(0)));
        if (vm.MultiSpdifSupported)
        {
            // A file may carry fewer inputs than the device has (written before
            // the fourth input) or more than it has (written on newer firmware);
            // apply only the overlap and report the rest as rejected.
            int applied = Math.Min(io.SpdifRxPinCount, vm.SpdifInputCount);
            for (int i = 1; i < applied; i++)
            {
                int idx = i;
                Try($"S/PDIF {idx + 1} RX pin", () => vm.SetSpdifRxPin(io.SpdifRxPinAt(idx), idx));
                Try($"S/PDIF input {idx + 1}",
                    () => vm.SetSpdifInputEnable(idx, (io.SpdifEnabledExt & (1 << (idx - 1))) != 0));
            }
            int extraBits = io.SpdifEnabledExt >> Math.Max(applied - 1, 0);
            if (extraBits != 0)
                rejections.Add("S/PDIF inputs beyond this firmware's count");
        }
        else if (io.SpdifEnabledExt != 0)
        {
            rejections.Add("Additional S/PDIF inputs (not supported by this firmware)");
        }

        // I2S input: pins, then the channel count that decides how many pairs
        // are live, then the master rate.
        for (int pair = 0; pair < Math.Min(io.I2sRxPins.Length, vm.I2sMaxPairs); pair++)
        {
            int p = pair;
            Try($"I2S serial data {p + 1} pin", () => vm.SetI2sRxPin(io.I2sRxPins[p], p));
        }
        if (io.I2sInputChannels is 2 or 4 or 6 or 8)
            Try("I2S input channels",
                () => vm.SetI2sInputChannels(Math.Min(io.I2sInputChannels, vm.I2sMaxInputChannels)));
        vm.SetI2sInputRate(io.I2sInputRateHz);

        if (vm.AdatSupported)
        {
            Try("ADAT output pin", () => vm.SetAdatPin(io.AdatPin));
            Try("ADAT output enable", () => vm.SetAdatEnable(io.AdatEnabled));
        }
        else if (io.AdatEnabled)
        {
            rejections.Add("ADAT output (not supported by this device)");
        }

        if (vm.AdatInputSupported)
        {
            if (io.AdatInputPin != MainViewModel.AdatInputPinUnset)
                Try("ADAT input pin", () => vm.SetAdatInputPin(io.AdatInputPin));
            Try("ADAT input clock mode", () => vm.SetAdatInputClockMode(io.AdatInputClockMode));
            Try("ADAT input enable", () => vm.SetAdatInputEnable(io.AdatInputEnabled));
        }
        else if (io.AdatInputEnabled)
        {
            rejections.Add("ADAT input (not supported by this device)");
        }
    }

    private static string DescribePinResult(byte status) => status switch
    {
        PinConfigResult.InvalidPin => "invalid pin",
        PinConfigResult.PinInUse => "pin already in use",
        PinConfigResult.InvalidOutput => "invalid output",
        PinConfigResult.OutputActive => "output is active",
        PinConfigResult.InvalidParam => "invalid value",
        _ => $"status 0x{status:X2}",
    };
}
