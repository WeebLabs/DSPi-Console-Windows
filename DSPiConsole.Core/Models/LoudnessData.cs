namespace DSPiConsole.Core.Models;

/// <summary>
/// ISO 226:2003 equal-loudness contour data and loudness compensation math.
/// Ported from Mac LoudnessView.swift.
/// </summary>
public static class LoudnessData
{
    // ISO 226:2003 data table: (frequency, af, Lu, Tf)
    // 30 entries from 20 Hz to 12500 Hz
    private static readonly (float f, float af, float lu, float tf)[] Iso226Table =
    {
        (20f, 0.532f, -31.6f, 78.5f),
        (25f, 0.506f, -27.2f, 68.7f),
        (31.5f, 0.480f, -23.0f, 59.5f),
        (40f, 0.455f, -19.1f, 51.1f),
        (50f, 0.432f, -15.9f, 44.0f),
        (63f, 0.409f, -13.0f, 37.5f),
        (80f, 0.387f, -10.3f, 31.5f),
        (100f, 0.367f, -8.1f, 26.5f),
        (125f, 0.349f, -6.2f, 22.1f),
        (160f, 0.330f, -4.5f, 17.9f),
        (200f, 0.315f, -3.1f, 14.4f),
        (250f, 0.301f, -2.0f, 11.4f),
        (315f, 0.288f, -1.1f, 8.6f),
        (400f, 0.276f, -0.4f, 6.2f),
        (500f, 0.267f, 0.0f, 4.4f),
        (630f, 0.259f, 0.3f, 3.0f),
        (800f, 0.253f, 0.5f, 2.2f),
        (1000f, 0.250f, 0.0f, 2.4f),
        (1250f, 0.246f, -2.7f, 3.5f),
        (1600f, 0.244f, -4.1f, 1.7f),
        (2000f, 0.243f, -1.0f, -1.3f),
        (2500f, 0.243f, 1.7f, -4.2f),
        (3150f, 0.243f, 2.5f, -6.0f),
        (4000f, 0.242f, 1.2f, -5.4f),
        (5000f, 0.242f, -2.1f, -1.5f),
        (6300f, 0.245f, -7.1f, 6.0f),
        (8000f, 0.254f, -11.2f, 12.6f),
        (10000f, 0.271f, -10.7f, 13.9f),
        (12500f, 0.301f, -3.1f, 12.3f),
        (12500f, 0.301f, -3.1f, 12.3f) // Duplicate last for interpolation
    };

    public static IReadOnlyList<float> Frequencies => Iso226Table.Select(e => e.f).ToArray();

    /// <summary>
    /// Compute the SPL of an equal-loudness contour at a given ISO 226 table entry for a given phon level.
    /// </summary>
    public static float Iso226SPL(float tf, float af, float lu, float phon)
    {
        float Af = 4.47e-3f * (MathF.Pow(10, 0.025f * phon) - 1.15f)
                   + MathF.Pow(0.4f * MathF.Pow(10, (tf + lu) / 10.0f - 9.0f), af);
        float Lp = (10.0f / af) * MathF.Log10(Af) - lu + 94.0f;
        return Lp;
    }

    /// <summary>
    /// Calculate the loudness compensation in dB for a given frequency's ISO 226 table entry.
    /// </summary>
    public static float LoudnessCompensationDB(float tf, float af, float lu, float refSPL, float effectivePhon, float intensity)
    {
        float refLevel = Iso226SPL(tf, af, lu, refSPL);
        float currentLevel = Iso226SPL(tf, af, lu, effectivePhon);
        float compensation = currentLevel - refLevel;
        return compensation * (intensity / 100.0f);
    }

    /// <summary>
    /// Get the loudness compensation curve as an array of (frequency, dB) pairs.
    /// </summary>
    public static (float freq, float db)[] GetCompensationCurve(float refSPL, float effectivePhon, float intensity)
    {
        var result = new (float freq, float db)[Iso226Table.Length - 1]; // Exclude duplicate last
        for (int i = 0; i < Iso226Table.Length - 1; i++)
        {
            var (f, af, lu, tf) = Iso226Table[i];
            float db = LoudnessCompensationDB(tf, af, lu, refSPL, effectivePhon, intensity);
            result[i] = (f, db);
        }
        return result;
    }
}
