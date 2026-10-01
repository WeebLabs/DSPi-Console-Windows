using System.Globalization;
using System.Text;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Core;

/// <summary>
/// One readable line per notification packet from the device's interrupt IN
/// endpoint, for the Interrupt Monitor: the v1 master-volume packet, every v2
/// event, and PARAM_CHANGED resolved to its WireBulkParams field (wire V32,
/// firmware bulk_params.h) with the value formatted by type. Port of the macOS
/// Console's InterruptEvent decoder and ParamOffsetDecoder, plus the crossover
/// bands, DAC mute and upmixer it does not name.
/// </summary>
public static class NotifyDecoder
{
    // WireBulkParams section offsets (V32).
    private const int Delays = 64, Crosspoints = 132, Outputs = 708, Pins = 816, Eq = 824;
    private const int ChannelNames = 4088, I2s = 4632, Leveller = 4648, Preamp = 4668, MasterVolume = 4700;
    private const int InputConfig = 4716, Lg = 4732, UserVolume = 4748, DacMute = 4764, Crossover = 4780;
    private const int Adat = 5868, Psybass = 5876, Upmix = 5900, Subharm = 5944, Tube = 5980, Limiter = 6028;
    private const int UpmixSize = 44, SubharmSize = 36, TubeSize = 48, LimiterOutputSize = 12, LimiterSize = 9 * 12;

    /// <summary>The packet, decoded; without a timestamp.</summary>
    public static string Decode(byte[] b)
    {
        if (b.Length == 0) return "(empty)";
        // A 1-byte packet is the IDLE keep-alive.
        if (b.Length < 4) return b[0] == 0 ? "IDLE" : $"? {b.Length}B";
        if (b[0] == 0x02) return DecodeV2(b);
        if (b[0] == 0x01)
        {
            // v1: [0x01, 0, 0, 0, float dB]
            if (b.Length < 8) return Column("v1.MasterVolume", $"(short: {b.Length} bytes)");
            return Column("v1.MasterVolume", Volume(BitConverter.ToSingle(b, 4)));
        }
        return Column($"v1?evt=0x{b[0]:X2}", Hex(b));
    }

    public static string SourceName(byte source) => source switch
    {
        0 => "?", 1 => "HOST", 2 => "BULK", 3 => "PRESET", 4 => "FACTORY", 5 => "GPIO", 6 => "INTERNAL", 7 => "UAC1", 8 => "UART", 9 => "I2C",
        _ => $"src=0x{source:X2}",
    };

    private static string Column(string name, string value) => $"{name,-14}  {value}";

    private static string DecodeV2(byte[] b)
    {
        byte evt = b[1], seq = b[3];
        string s = $"[{seq,3}]";
        switch (evt)
        {
            case 0x02:
            {
                // [ver, evt, flags, seq, off LE, size LE, src, 0, 0, 0, value...]
                if (b.Length < 12) return $"{s} v2.ParamChanged (short: {b.Length} bytes)";
                int offset = b[4] | b[5] << 8, size = b[6] | b[7] << 8;
                var payload = b.AsSpan(12, Math.Min(size, b.Length - 12)).ToArray();
                var (name, value) = Param(offset, size, payload);
                return $"{s} [{SourceName(b[8]),-8}] {name,-32}  {value}";
            }
            case 0x03: return $"{s} v2.BulkInvalidated            source={SourceName(b.Length > 4 ? b[4] : (byte)0)}";
            case 0x04: return $"{s} v2.PresetLoaded                slot={(b.Length > 4 ? b[4] : 0)}";
            case 0x05: return $"{s} v2.InputFormat                 channels={(b.Length > 4 ? b[4] : 0)}";
            case 0x07:
            {
                if (b.Length < 8) return $"{s} v2.SiggenState (short: {b.Length} bytes)";
                string[] states = { "IDLE", "FADE_IN", "RUN", "GAP", "FADE_OUT" };
                string[] reasons = { "-", "HOST", "COMPLETED", "PRESET", "RECONFIG" };
                string state = b[4] < states.Length ? states[b[4]] : $"state={b[4]}";
                string reason = b[5] < reasons.Length ? reasons[b[5]] : $"reason={b[5]}";
                return $"{s} v2.SiggenState                 {state} reason={reason} type={b[6]} ch={(b[7] == 0xFF ? "-" : b[7].ToString())}";
            }
            case 0x0A:
            {
                // 12 bytes: [ver, evt, flags, seq, state, protocol, 0, 0, code LE(4)]
                if (b.Length < 6) return $"{s} v2.IrLearn (short: {b.Length} bytes)";
                if (b[4] != 2) return $"{s} v2.IrLearn                    {(b[4] == 3 ? "TIMEOUT" : $"state={b[4]}")}";
                string[] protocols = { "NONE", "NEC", "RC5", "RC6", "HASH" };
                string protocol = b[5] < protocols.Length ? protocols[b[5]] : $"protocol={b[5]}";
                string code = b.Length >= 12 ? $" code=0x{BitConverter.ToUInt32(b, 8):X8}" : "";
                return $"{s} v2.IrLearn                    DONE {protocol}{code}";
            }
            case 0x08:
                if (b.Length < 8) return $"{s} v2.AdatState (short: {b.Length} bytes)";
                return $"{s} v2.AdatState                   enabled={b[4]} active={b[5]} pin={b[6]}";
            case 0x09:
            {
                // 9 bytes: [ver, evt, flags, seq, state, rate LE(4)]
                if (b.Length < 9) return $"{s} v2.I2sSlaveState (short: {b.Length} bytes)";
                string[] states = { "INACTIVE", "ACQUIRING", "RELOCKING", "LOCKED" };
                string state = b[4] < states.Length ? states[b[4]] : $"state={b[4]}";
                return $"{s} v2.I2sSlaveState              {state} rate={BitConverter.ToUInt32(b, 5)}";
            }
            case 0x0B:
            {
                // 10 bytes: [ver, evt, flags, seq, state, rate LE(4), clock_mode]
                if (b.Length < 10) return $"{s} v2.AdatInputState (short: {b.Length} bytes)";
                string[] states = { "INACTIVE", "ACQUIRING", "SYNCING", "LOCKED", "RELOCKING" };
                string state = b[4] < states.Length ? states[b[4]] : $"state={b[4]}";
                return $"{s} v2.AdatInputState             {state} rate={BitConverter.ToUInt32(b, 5)} mode={(b[9] == 1 ? "slave" : "master")}";
            }
            case 0x0C:
            {
                // 9 bytes: [ver, evt, flags, seq, slot, state, level_q8 LE(2), src]
                if (b.Length < 9) return $"{s} v2.CsAux (short: {b.Length} bytes)";
                double pct = (b[6] | b[7] << 8) / 256.0;
                return $"{s} v2.CsAux                      slot={b[4]} state={b[5]} level={pct.ToString("0.0", CultureInfo.InvariantCulture)}% src={SourceName(b[8])}";
            }
            default: return $"{s} v2?evt=0x{evt:X2}  {Hex(b.AsSpan(4))}";
        }
    }

    /// <summary>A WireBulkParams offset as its field name and formatted value.
    /// Unknown offsets fall back to the offset and a hex dump.</summary>
    public static (string Name, string Value) Param(int off, int sz, byte[] p)
    {
        if (off < 16) return ($"header+0x{off:X2}", Hex(p));
        switch (off)
        {
            case 16: return ("global.preamp_gain_db", F(p, " dB"));
            case 20: return ("global.bypass", B(p));
            case 21: return ("global.loudness_enabled", B(p));
            case 22: return ("global.loudness_output_mask", Hex(p));
            case 24: return ("global.loudness_ref_spl", F(p, " dB SPL"));
            case 28: return ("global.loudness_intensity_pct", F(p, "%"));
            case 32: return ("crossfeed.enabled", B(p));
            case 33: return ("crossfeed.preset", U(p));
            case 34: return ("crossfeed.itd_enabled", B(p));
            case 35: return ("crossfeed.output_pair_mask", Hex(p));
            case 36: return ("crossfeed.custom_fc", F(p, " Hz"));
            case 40: return ("crossfeed.custom_feed_db", F(p, " dB"));
        }
        if (off >= 48 && off <= 59 && (off - 48) % 4 == 0 && sz == 4) return ($"legacy.gain_db[{(off - 48) / 4}]", F(p, " dB"));
        if (off >= 60 && off <= 62 && sz == 1) return ($"legacy.mute[{off - 60}]", B(p));

        if (off >= Delays && off < Delays + 17 * 4 && (off - Delays) % 4 == 0 && sz == 4)
            return ($"delays.delay_ms[{(off - Delays) / 4}]", F(p, " ms"));

        if (off >= Crosspoints && off < Crosspoints + 8 * 9 * 8)
        {
            int rel = off - Crosspoints, idx = rel / 8, sub = rel % 8, input = idx / 9, output = idx % 9;
            if (sub == 0 && sz == 8 && p.Length >= 8)
                return ($"crosspoints[{input}][{output}]",
                    $"en={(p[0] != 0 ? 1 : 0)} inv={(p[1] != 0 ? 1 : 0)} {BitConverter.ToSingle(p, 4).ToString("+0.00;-0.00", CultureInfo.InvariantCulture),6} dB");
            return ($"crosspoints[{input}][{output}]+0x{sub:X}", Hex(p));
        }

        if (off >= Outputs && off < Outputs + 9 * 12)
        {
            int idx = (off - Outputs) / 12;
            return ((off - Outputs) % 12) switch
            {
                0 => ($"outputs[{idx}].enabled", B(p)),
                1 => ($"outputs[{idx}].mute", B(p)),
                4 => ($"outputs[{idx}].gain_db", F(p, " dB")),
                8 => ($"outputs[{idx}].delay_ms", F(p, " ms")),
                var sub => ($"outputs[{idx}]+0x{sub:X}", Hex(p)),
            };
        }

        if (off == Pins && sz == 1) return ("pins.num_pin_outputs", U(p));
        if (off >= Pins + 1 && off <= Pins + 5 && sz == 1) return ($"pins.pins[{off - Pins - 1}]", U(p));

        if (off >= Eq && off < Eq + 17 * 12 * 16) return Band("eq", off - Eq, 12, sz, p);
        // Crossover bands (V20): 17 channels x 4 bands x 16 bytes.
        if (off >= Crossover && off < Crossover + 17 * 4 * 16) return Band("crossover", off - Crossover, 4, sz, p);

        if (off >= ChannelNames && off < ChannelNames + 17 * 32 && (off - ChannelNames) % 32 == 0 && sz == 32)
            return ($"channel_names[{(off - ChannelNames) / 32}]", Str(p));

        if (off >= I2s && off <= I2s + 3 && sz == 1)
            return ($"i2s_config.output_types[{off - I2s}]", $"{Byte(p)} ({(Byte(p) == 1 ? "I2S" : "SPDIF")})");
        switch (off - I2s)
        {
            case 4: return ("i2s_config.bck_pin", U(p));
            case 5: return ("i2s_config.mck_pin", U(p));
            case 6: return ("i2s_config.mck_enabled", B(p));
            case 7: return ("i2s_config.mck_multiplier", $"{Byte(p)} ({(Byte(p) == 1 ? "256x" : "128x")})");
            case 8: return ("i2s_config.clock_pin_mode_p1", Byte(p) switch { 0 => "0 (absent)", 1 => "1 (unified)", 2 => "2 (split)", var m => m.ToString() });
            case 9: return ("i2s_config.bck_pin_slave", U(p));
        }

        switch (off - Leveller)
        {
            case 0: return ("leveller.enabled", B(p));
            case 1: return ("leveller.speed", U(p));
            case 2: return ("leveller.lookahead", B(p));
            case 4: return ("leveller.amount", F(p, "%"));
            case 8: return ("leveller.max_gain_db", F(p, " dB"));
            case 12: return ("leveller.gate_threshold_db", F(p, " dB"));
            case 16: return ("leveller.detector_mask", Hex(p));
            case 17: return ("leveller.apply_mask", Hex(p));
        }

        if (off >= Preamp && off < Preamp + 8 * 4 && (off - Preamp) % 4 == 0 && sz == 4)
            return ($"preamp.preamp_db[{(off - Preamp) / 4}]", F(p, " dB"));
        if (off == MasterVolume && sz == 4) return ("master_volume.master_volume_db", Volume(Float(p)));

        if (off == InputConfig && sz == 1)
        {
            string name = Byte(p) switch { 0 => "USB", 1 => "SPDIF", 2 => "I2S", 3 => "ADAT", 4 => "SPDIF2", 5 => "SPDIF3", 6 => "SPDIF4", _ => "?" };
            return ("input_config.input_source", $"{Byte(p)} ({name})");
        }
        switch (off - InputConfig)
        {
            case 1 when sz == 1: return ("input_config.spdif_rx_pin", U(p));
            case 2 when sz == 1: return ("input_config.i2s_rx_pin", U(p));
            case 3 when sz == 1:
                return ("input_config.i2s_input_rate", $"{Byte(p)} ({(Byte(p) < 3 ? new[] { "44100", "48000", "96000" }[Byte(p)] : "?")} Hz)");
            case 12 when sz == 1: return ("input_config.i2s_clock_mode", U(p));
            case 13 when sz == 1: return ("input_config.adat_input_pin", Byte(p) == 0 ? "unset" : $"GPIO {Byte(p)}");
            case 14 when sz == 1:
                return ("input_config.adat_input_enabled_p1", $"{Byte(p)} ({(Byte(p) == 0 ? "absent" : Byte(p) == 2 ? "enabled" : "disabled")})");
            case 15 when sz == 1:
                return ("input_config.adat_clock_mode_p1", $"{Byte(p)} ({(Byte(p) == 0 ? "absent" : Byte(p) == 2 ? "slave" : "master")})");
        }

        if (sz == 1)
            switch (off - Lg)
            {
                case 0: return ("lg_sound_sync.enabled", B(p));
                case 1: return ("lg_sound_sync.present", B(p));
                case 2: return ("lg_sound_sync.volume", Byte(p) == 0xFF ? "-" : $"{Byte(p)} / 100");
                case 3: return ("lg_sound_sync.muted", B(p));
            }

        if (off == UserVolume && sz == 4) return ("user_volume.user_volume_db", Volume(Float(p)));
        if (off == UserVolume + 4 && sz == 1) return ("user_volume.user_mute", B(p));
        // DAC hardware mute (V10+): the firmware writes the whole 16-byte struct.
        if (off == DacMute && sz == 16) return ("dac_hw_mute", Hex(p));

        if (off == Adat && sz == 1) return ("adat_config.enabled", B(p));
        if (off == Adat + 1 && sz == 1) return ("adat_config.pin", Byte(p) == 0 ? "default" : $"GPIO {Byte(p)}");

        if (off >= Psybass && off < Psybass + 24)
            switch (off - Psybass)
            {
                case 0: return ("psybass.enabled", B(p));
                case 2: return ("psybass.output_mask", Hex(p));
                case 4: return ("psybass.cutoff_hz", F(p, " Hz"));
                case 8: return ("psybass.harmonics_db", F(p, " dB"));
                case 12: return ("psybass.drive_db", F(p, " dB"));
                case 16: return ("psybass.character_pct", F(p, "%"));
                case 20: return ("psybass.original_db", F(p, " dB"));
            }

        if (off >= Upmix && off < Upmix + UpmixSize)
            switch (off - Upmix)
            {
                case 0: return ("upmix.enabled", B(p));
                case 1: return ("upmix.center_mode", Byte(p) switch { 0 => "0 (Passive)", 1 => "1 (Adaptive)", 2 => "2 (Off)", var m => m.ToString() });
                case 2: return ("upmix.surround_mode", Byte(p) switch { 0 => "0 (Off)", 1 => "1 (Passive)", 2 => "2 (Adaptive)", var m => m.ToString() });
                case 3: return ("upmix.presence", p.Length > 0 ? ((sbyte)p[0] / 2f).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " dB" : "(empty)");
                case 4: return ("upmix.strength_pct", F(p, "%"));
                case 8: return ("upmix.center_width_pct", F(p, "%"));
                case 12: return ("upmix.corr_threshold_pct", F(p, "%"));
                case 16: return ("upmix.attack_ms", F(p, " ms"));
                case 20: return ("upmix.release_ms", F(p, " ms"));
                case 24: return ("upmix.detector_hpf_hz", F(p, " Hz"));
                case 28: return ("upmix.surround_delay_ms", F(p, " ms"));
                case 32: return ("upmix.surround_hpf_hz", F(p, " Hz"));
                case 36: return ("upmix.surround_lpf_hz", F(p, " Hz"));
                case 40: return ("upmix.decorr_pct", F(p, "%"));
            }

        // Subharm has no wire offset for solo, so it never appears here.
        if (off >= Subharm && off < Subharm + SubharmSize)
            switch (off - Subharm)
            {
                case 0: return ("subharm.enabled", B(p));
                case 2: return ("subharm.output_mask", Hex(p));
                case 4: return ("subharm.low_db", F(p, " dB"));
                case 8: return ("subharm.high_db", F(p, " dB"));
                case 12: return ("subharm.boost_db", F(p, " dB"));
                case 16: return ("subharm.top_db", F(p, " dB"));
                case 20: return ("subharm.select_depth", F(p, "%"));
                case 24: return ("subharm.select_hold_ms", F(p, " ms"));
                case 28: return ("subharm.ceiling_db", F(p, " dBFS"));
                case 32: return ("subharm.select_mode", Hex(p));
                case 33: return ("subharm.link_pairs", B(p));
            }

        if (off >= Tube && off < Tube + TubeSize)
            switch (off - Tube)
            {
                case 0: return ("tube.enabled", B(p));
                case 1: return ("tube.tube_type", TubeTables.TypeName(Byte(p)));
                case 2: return ("tube.rectifier", TubeTables.RectifierName(Byte(p)));
                case 3: return ("tube.xfmr_enabled", B(p));
                case 4: return ("tube.output_mask", Hex(p));
                case 8: return ("tube.drive_db", F(p, " dB"));
                case 12: return ("tube.bias_pct", F(p, "%"));
                case 16: return ("tube.asym_db", F(p, " dB"));
                case 20: return ("tube.hardness_pct", F(p, "%"));
                case 24: return ("tube.sag_pct", F(p, "%"));
                case 28: return ("tube.xfmr_damping", F(p, ""));
                case 32: return ("tube.xfmr_res_hz", F(p, " Hz"));
                case 36: return ("tube.mix_pct", F(p, "%"));
                case 40: return ("tube.trim_db", F(p, " dB"));
            }

        // One 12-byte record per output; an all-outputs SET notifies each.
        if (off >= Limiter && off < Limiter + LimiterSize)
        {
            int rel = off - Limiter, output = rel / LimiterOutputSize;
            switch (rel % LimiterOutputSize)
            {
                case 0: return ($"limiter[{output}].enabled", B(p));
                case 1: return ($"limiter[{output}].link_group", Byte(p) == 0 ? "Unlinked" : $"Group {Byte(p)}");
                case 4: return ($"limiter[{output}].threshold_db", F(p, " dBFS"));
                case 8: return ($"limiter[{output}].release_ms", F(p, " ms"));
            }
        }

        return ($"offset=0x{off:X4} size={sz}", Hex(p));
    }

    private static (string, string) Band(string region, int rel, int bands, int sz, byte[] p)
    {
        int idx = rel / 16, sub = rel % 16, ch = idx / bands, band = idx % bands;
        if (sub == 0 && sz == 16 && p.Length >= 16)
            return ($"{region}[{ch}][{band}]", string.Format(CultureInfo.InvariantCulture,
                "type={0}  byp={1}  f={2:0.0} Hz  Q={3:0.00}  g={4:+0.00;-0.00} dB",
                p[0], p[1], BitConverter.ToSingle(p, 4), BitConverter.ToSingle(p, 8), BitConverter.ToSingle(p, 12)));
        return ($"{region}[{ch}][{band}]+0x{sub:X}", Hex(p));
    }

    // ── Value formatters ──

    private static byte Byte(byte[] p) => p.Length > 0 ? p[0] : (byte)0;
    private static float Float(byte[] p) => p.Length >= 4 ? BitConverter.ToSingle(p, 0) : float.NaN;
    private static string F(byte[] p, string suffix) =>
        p.Length < 4 ? "(short)" : Float(p).ToString("+0.000;-0.000", CultureInfo.InvariantCulture) + suffix;
    private static string B(byte[] p) => p.Length == 0 ? "(empty)" : p[0] == 0 ? "0 (false)" : "1 (true)";
    private static string U(byte[] p) => p.Length == 0 ? "(empty)" : p[0].ToString(CultureInfo.InvariantCulture);
    private static string Volume(float db) => db <= -128 ? "MUTE" : db.ToString("+0.00;-0.00", CultureInfo.InvariantCulture).PadLeft(7) + " dB";

    public static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string Str(byte[] p)
    {
        int end = Array.IndexOf(p, (byte)0);
        return $"\"{Encoding.UTF8.GetString(p, 0, end < 0 ? p.Length : end)}\"";
    }
}
