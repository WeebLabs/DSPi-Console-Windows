namespace DSPiConsole.Core.Models;

/// <summary>
/// BS2B (Bauer Stereophonic-to-Binaural) crossfeed presets and the response the
/// firmware's filter actually has, as the macOS Console draws it.
/// </summary>
public static class CrossfeedData
{
    /// <summary>The device's presets by index; the last is Custom, which starts
    /// from the Default values.</summary>
    public static readonly (string Name, string Description, float Freq, float Feed)[] Presets =
    {
        ("Default", "700 Hz / 4.5 dB - Balanced, most popular", 700f, 4.5f),
        ("Chu Moy", "700 Hz / 6.0 dB - Stronger spatial effect", 700f, 6.0f),
        ("Jan Meier", "650 Hz / 9.5 dB - Natural speaker-like", 650f, 9.5f),
        ("Custom", "User-defined parameters", 700f, 4.5f),
    };

    public const int CustomPreset = 3;
    public const float FreqMin = 500, FreqMax = 2000, FeedMin = 0, FeedMax = 15;

    /// <summary>
    /// The direct and crossfed paths' magnitude at 100 log-spaced frequencies
    /// from 20 Hz to 20 kHz. The crossfeed path is the firmware's one-pole
    /// lowpass at the cutoff with complementary gain G = 1 / (1 + 10^(feed/20));
    /// the direct path is one minus it, so the two sum to unity.
    /// </summary>
    public static (float[] Freqs, float[] DirectDb, float[] CrossfeedDb) GetResponseCurves(float cutoffFreq, float feedDb)
    {
        const int numPoints = 100;
        const float sampleRate = 48000f;
        float fc = Math.Clamp(cutoffFreq, FreqMin, FreqMax);
        float feed = Math.Clamp(feedDb, FeedMin, FeedMax);

        float g = 1f / (1f + MathF.Pow(10f, feed / 20f));
        float x = MathF.Exp(-2f * MathF.PI * fc / sampleRate);
        float a0 = g * (1f - x);

        var freqs = new float[numPoints];
        var direct = new float[numPoints];
        var cross = new float[numPoints];
        for (int i = 0; i < numPoints; i++)
        {
            float f = 20f * MathF.Pow(1000f, (float)i / (numPoints - 1));
            float omega = 2f * MathF.PI * f / sampleRate;
            // H(z) = a0 / (1 - x z^-1)
            float denRe = 1f - x * MathF.Cos(omega), denIm = x * MathF.Sin(omega);
            float den = denRe * denRe + denIm * denIm;
            float lpRe = a0 * denRe / den, lpIm = -a0 * denIm / den;
            float dRe = 1f - lpRe, dIm = -lpIm;
            freqs[i] = f;
            cross[i] = 20f * MathF.Log10(MathF.Max(MathF.Sqrt(lpRe * lpRe + lpIm * lpIm), 1e-10f));
            direct[i] = 20f * MathF.Log10(MathF.Max(MathF.Sqrt(dRe * dRe + dIm * dIm), 1e-10f));
        }
        return (freqs, direct, cross);
    }
}
