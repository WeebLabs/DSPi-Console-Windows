using DSPiConsole.Core.Models;

namespace DSPiConsole.Core;

/// <summary>
/// DSP mathematics for biquad filter coefficient calculation and frequency response.
/// Direct port of the macOS DSPMath.swift and firmware coefficient calculations.
/// </summary>
public static class DspMath
{
    public const float SampleRate = 48000.0f;

    /// <summary>
    /// Biquad filter coefficients (normalized, a0 = 1)
    /// </summary>
    public readonly struct Coefficients
    {
        public readonly float B0, B1, B2, A1, A2;

        public Coefficients(float b0, float b1, float b2, float a1, float a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        public static Coefficients Unity => new(1, 0, 0, 0, 0);
    }

    /// <summary>
    /// Calculate biquad coefficients for a filter.
    /// Matches the firmware compute_coefficients() function.
    /// </summary>
    public static Coefficients CalculateCoefficients(FilterParams p, float sampleRate = SampleRate)
    {
        if (p.Type == FilterType.Flat)
            return Coefficients.Unity;

        float omega = 2.0f * MathF.PI * p.Frequency / sampleRate;
        float sn = MathF.Sin(omega);
        float cs = MathF.Cos(omega);
        float alpha = sn / (2.0f * p.Q);
        float A = MathF.Pow(10.0f, p.Gain / 40.0f);

        float b0 = 1, b1 = 0, b2 = 0;
        float a0 = 1, a1 = 0, a2 = 0;

        switch (p.Type)
        {
            case FilterType.LowPass:
                b0 = (1 - cs) / 2;
                b1 = 1 - cs;
                b2 = (1 - cs) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.HighPass:
                b0 = (1 + cs) / 2;
                b1 = -(1 + cs);
                b2 = (1 + cs) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.Peaking:
                b0 = 1 + alpha * A;
                b1 = -2 * cs;
                b2 = 1 - alpha * A;
                a0 = 1 + alpha / A;
                a1 = -2 * cs;
                a2 = 1 - alpha / A;
                break;

            case FilterType.LowShelf:
                {
                    float sqrtA = MathF.Sqrt(A);
                    b0 = A * ((A + 1) - (A - 1) * cs + 2 * sqrtA * alpha);
                    b1 = 2 * A * ((A - 1) - (A + 1) * cs);
                    b2 = A * ((A + 1) - (A - 1) * cs - 2 * sqrtA * alpha);
                    a0 = (A + 1) + (A - 1) * cs + 2 * sqrtA * alpha;
                    a1 = -2 * ((A - 1) + (A + 1) * cs);
                    a2 = (A + 1) + (A - 1) * cs - 2 * sqrtA * alpha;
                }
                break;

            case FilterType.HighShelf:
                {
                    float sqrtA = MathF.Sqrt(A);
                    b0 = A * ((A + 1) + (A - 1) * cs + 2 * sqrtA * alpha);
                    b1 = -2 * A * ((A - 1) + (A + 1) * cs);
                    b2 = A * ((A + 1) + (A - 1) * cs - 2 * sqrtA * alpha);
                    a0 = (A + 1) - (A - 1) * cs + 2 * sqrtA * alpha;
                    a1 = 2 * ((A - 1) - (A + 1) * cs);
                    a2 = (A + 1) - (A - 1) * cs - 2 * sqrtA * alpha;
                }
                break;
        }

        // Normalize by a0
        return new Coefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// Calculate the frequency response magnitude in dB for a set of filters at a given frequency.
    /// Evaluates H(e^jω) for each filter and multiplies the magnitudes.
    /// </summary>
    public static float ResponseAt(float freq, IEnumerable<FilterParams> filters, float sampleRate = SampleRate)
    {
        float magSquaredTotal = 1.0f;

        foreach (var f in filters)
        {
            if (f.Type == FilterType.Flat || !f.IsActive)
                continue;

            var coeffs = CalculateCoefficients(f, sampleRate);
            float w = 2.0f * MathF.PI * freq / sampleRate;

            // Evaluate Transfer Function |H(e^jw)|²
            // H(z) = (b0 + b1*z^-1 + b2*z^-2) / (1 + a1*z^-1 + a2*z^-2)
            // z = e^jw = cos(w) + j*sin(w)

            float cos_w = MathF.Cos(w);
            float cos_2w = MathF.Cos(2.0f * w);
            float sin_w = MathF.Sin(w);
            float sin_2w = MathF.Sin(2.0f * w);

            // Numerator (Real and Imaginary parts)
            float num_r = coeffs.B0 + coeffs.B1 * cos_w + coeffs.B2 * cos_2w;
            float num_i = -(coeffs.B1 * sin_w + coeffs.B2 * sin_2w);

            // Denominator (a0 is normalized to 1)
            float den_r = 1.0f + coeffs.A1 * cos_w + coeffs.A2 * cos_2w;
            float den_i = -(coeffs.A1 * sin_w + coeffs.A2 * sin_2w);

            float num_mag_sq = num_r * num_r + num_i * num_i;
            float den_mag_sq = den_r * den_r + den_i * den_i;

            if (den_mag_sq > 1e-9f)
            {
                magSquaredTotal *= (num_mag_sq / den_mag_sq);
            }
        }

        return 10.0f * MathF.Log10(magSquaredTotal);
    }

    /// <summary>
    /// Generate frequency response curve points for plotting
    /// </summary>
    public static (float[] frequencies, float[] magnitudes) GenerateResponseCurve(
        IEnumerable<FilterParams> filters,
        int numPoints = 201,
        float minFreq = 20.0f,
        float maxFreq = 20000.0f,
        float sampleRate = SampleRate)
    {
        var frequencies = new float[numPoints];
        var magnitudes = new float[numPoints];

        float logMin = MathF.Log10(minFreq);
        float logMax = MathF.Log10(maxFreq);

        for (int i = 0; i < numPoints; i++)
        {
            float pct = i / (float)(numPoints - 1);
            float freq = MathF.Pow(10, logMin + pct * (logMax - logMin));
            frequencies[i] = freq;
            magnitudes[i] = ResponseAt(freq, filters, sampleRate);
        }

        return (frequencies, magnitudes);
    }
}
