using DSPiConsole.Core.Models;

namespace DSPiConsole.Core;

/// <summary>
/// DSP mathematics for biquad filter coefficient calculation and frequency response.
/// Direct port of the macOS DSPMath.swift and firmware coefficient calculations.
/// All internal math uses double (64-bit) precision for accuracy at low frequencies.
/// </summary>
public static class DspMath
{
    public const float SampleRate = 48000.0f;

    /// <summary>
    /// Biquad filter coefficients (normalized, a0 = 1)
    /// </summary>
    public readonly struct Coefficients
    {
        public readonly double B0, B1, B2, A1, A2;

        public Coefficients(double b0, double b1, double b2, double a1, double a2)
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

        // Linkwitz Transform: zeros at the driver corner (f0, Q0), poles at the
        // target (fp, Qp). fp is carried in Gain (Hz); fp <= 0 => flat. Both corners
        // are tan-prewarped so the digital response is exact at f0 & fp, matching the
        // firmware coefficient path (peq_filters.md §6).
        if (p.Type == FilterType.LinkwitzTransform)
        {
            double f0 = p.Frequency;
            double fp = p.Gain;          // fp (Hz) carried in the gain field
            if (fp <= 0.0 || f0 <= 0.0) return Coefficients.Unity;
            double q0lt = Math.Max(p.Q, 0.1);
            double qplt = Math.Max(p.Qp, 0.1);
            double g0 = Math.Tan(Math.PI * f0 / sampleRate);   // prewarped zero corner
            double gp = Math.Tan(Math.PI * fp / sampleRate);   // prewarped pole corner
            double nb0 = 1 + g0 / q0lt + g0 * g0;
            double nb1 = 2 * (g0 * g0 - 1);
            double nb2 = 1 - g0 / q0lt + g0 * g0;
            double na0 = 1 + gp / qplt + gp * gp;
            double na1 = 2 * (gp * gp - 1);
            double na2 = 1 - gp / qplt + gp * gp;
            return new Coefficients(nb0 / na0, nb1 / na0, nb2 / na0, na1 / na0, na2 / na0);
        }

        double omega = 2.0 * Math.PI * p.Frequency / sampleRate;
        double sn = Math.Sin(omega);
        double cs = Math.Cos(omega);
        double alpha = sn / (2.0 * p.Q);
        double A = Math.Pow(10.0, p.Gain / 40.0);

        double b0 = 1, b1 = 0, b2 = 0;
        double a0 = 1, a1 = 0, a2 = 0;

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
                    double sqrtA = Math.Sqrt(A);
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
                    double sqrtA = Math.Sqrt(A);
                    b0 = A * ((A + 1) + (A - 1) * cs + 2 * sqrtA * alpha);
                    b1 = -2 * A * ((A - 1) + (A + 1) * cs);
                    b2 = A * ((A + 1) + (A - 1) * cs - 2 * sqrtA * alpha);
                    a0 = (A + 1) - (A - 1) * cs + 2 * sqrtA * alpha;
                    a1 = 2 * ((A - 1) - (A + 1) * cs);
                    a2 = (A + 1) - (A - 1) * cs - 2 * sqrtA * alpha;
                }
                break;

            case FilterType.Notch:
                b0 = 1;
                b1 = -2 * cs;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            case FilterType.AllPass:
                b0 = 1 - alpha;
                b1 = -2 * cs;
                b2 = 1 + alpha;
                a0 = 1 + alpha;
                a1 = -2 * cs;
                a2 = 1 - alpha;
                break;

            // First-order types (degenerate biquads, b2 = a2 = 0). Coefficients
            // mirror the firmware compute_coefficients() fallback biquad forms.
            case FilterType.AllPass1:
                {
                    // H(z) = (a + z⁻¹)/(1 + a·z⁻¹), a = (tan(πfc/Fs)−1)/(tan(πfc/Fs)+1)
                    double ta = Math.Tan(omega / 2.0);
                    double ap = (ta - 1.0) / (ta + 1.0);
                    b0 = ap; b1 = 1; b2 = 0;
                    a0 = 1;  a1 = ap; a2 = 0;
                }
                break;

            case FilterType.LowShelf1:
                // DC gain A², unity at Nyquist.
                b0 = A * sn + 1 + cs; b1 = A * sn - 1 - cs; b2 = 0;
                a0 = sn / A + 1 + cs; a1 = sn / A - 1 - cs; a2 = 0;
                break;

            case FilterType.HighShelf1:
                // Unity at DC, gain A² at Nyquist.
                b0 = sn + A + A * cs;     b1 = sn - A - A * cs;     b2 = 0;
                a0 = sn + 1 / A + cs / A; a1 = sn - 1 / A - cs / A; a2 = 0;
                break;

            case FilterType.LowPass1:
                // Unity at DC, −3 dB at the corner, 6 dB/oct rolloff. No resonance.
                b0 = sn;          b1 = sn;          b2 = 0;
                a0 = sn + 1 + cs; a1 = sn - 1 - cs; a2 = 0;
                break;

            case FilterType.HighPass1:
                b0 = 1 + cs;      b1 = -1 - cs;     b2 = 0;
                a0 = sn + 1 + cs; a1 = sn - 1 - cs; a2 = 0;
                break;
        }

        // Normalize by a0
        return new Coefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>
    /// Calculate the frequency response magnitude in dB for a set of filters at a given frequency.
    /// Evaluates H(e^jω) for each filter and multiplies the magnitudes. PEQ bands
    /// (types 0-13) are single biquads; crossover bands (types 32-63) are multi-section
    /// cascades designed to match the firmware (see <see cref="CrossoverSections"/>).
    /// </summary>
    public static float ResponseAt(float freq, IEnumerable<FilterParams> filters, float sampleRate = SampleRate)
    {
        double magSquaredTotal = 1.0;
        double w = 2.0 * Math.PI * freq / sampleRate;

        foreach (var f in filters)
        {
            if (f.Type == FilterType.Flat || !f.IsActive || f.Bypass)
                continue;

            if (f.Type.IsCrossover())
            {
                // High-order crossover band → product of its biquad sections.
                foreach (var c in CrossoverSections(f.Type, f.Frequency, sampleRate))
                    magSquaredTotal *= BiquadMagSquared(c, w);
                continue;
            }

            magSquaredTotal *= BiquadMagSquared(CalculateCoefficients(f, sampleRate), w);
        }

        return (float)(10.0 * Math.Log10(magSquaredTotal));
    }

    /// <summary>
    /// The biquad sections a band contributes, ignoring its bypass flag: one for
    /// a PEQ type, a cascade for a crossover, none when it is Off. Lets a caller
    /// that evaluates the same bands at many frequencies (the graph editor's
    /// per-column curves) design each band once.
    /// </summary>
    public static IReadOnlyList<Coefficients> SectionsFor(FilterParams p, float sampleRate = SampleRate)
    {
        if (p.Type == FilterType.Flat) return Array.Empty<Coefficients>();
        if (p.Type.IsCrossover()) return CrossoverSections(p.Type, p.Frequency, sampleRate);
        return new[] { CalculateCoefficients(p, sampleRate) };
    }

    /// <summary>Response of a section cascade at <paramref name="freq"/>, in dB.</summary>
    public static double CascadeDb(IReadOnlyList<Coefficients> sections, double freq, float sampleRate = SampleRate)
    {
        double w = 2.0 * Math.PI * freq / sampleRate;
        double mag = 1.0;
        for (int i = 0; i < sections.Count; i++) mag *= BiquadMagSquared(sections[i], w);
        return 10.0 * Math.Log10(Math.Max(mag, 1e-30));
    }

    /// <summary>φ = sin²(w/2) at <paramref name="freq"/>: what <see cref="CascadeDbInto"/>
    /// takes per point, so a caller drawing many curves on the same axis computes
    /// the trigonometry once.</summary>
    public static double PhiAt(double freq, float sampleRate = SampleRate)
    {
        double s = Math.Sin(Math.PI * freq / sampleRate);
        return s * s;
    }

    /// <summary>
    /// A section cascade's response in dB at every φ in <paramref name="phis"/>
    /// (see <see cref="PhiAt"/>), written to <paramref name="db"/>. The same
    /// values as <see cref="CascadeDb"/>, with each section's sums hoisted out of
    /// the loop.
    /// </summary>
    public static void CascadeDbInto(IReadOnlyList<Coefficients> sections, ReadOnlySpan<double> phis, Span<double> db)
    {
        int n = Math.Min(phis.Length, db.Length);
        if (sections.Count == 0)
        {
            db[..n].Clear();
            return;
        }
        Span<double> terms = stackalloc double[sections.Count * 6];
        for (int s = 0; s < sections.Count; s++)
        {
            var c = sections[s];
            double sb = c.B0 + c.B1 + c.B2, sa = 1.0 + c.A1 + c.A2;
            terms[s * 6 + 0] = sb * sb;
            terms[s * 6 + 1] = -4.0 * (c.B0 * c.B1 + c.B1 * c.B2 + 4.0 * c.B0 * c.B2);
            terms[s * 6 + 2] = 16.0 * c.B0 * c.B2;
            terms[s * 6 + 3] = sa * sa;
            terms[s * 6 + 4] = -4.0 * (c.A1 + c.A1 * c.A2 + 4.0 * c.A2);
            terms[s * 6 + 5] = 16.0 * c.A2;
        }
        for (int i = 0; i < n; i++)
        {
            double phi = phis[i], phi2 = phi * phi, mag = 1.0;
            for (int s = 0; s < terms.Length; s += 6)
            {
                double num = terms[s] + terms[s + 1] * phi + terms[s + 2] * phi2;
                double den = terms[s + 3] + terms[s + 4] * phi + terms[s + 5] * phi2;
                if (den > 0.0) mag *= Math.Max(num, 0.0) / den;
            }
            db[i] = 10.0 * Math.Log10(Math.Max(mag, 1e-30));
        }
    }

    /// <summary>
    /// |H(e^jw)|² for one normalized biquad (a0 = 1), written in terms of
    /// φ = sin²(w/2), the form RBJ recommends for plotting. It equals evaluating
    /// the numerator and denominator at z = e^jw, but without their cancellation:
    /// the direct form lost the resonance of low, narrow sections (a 10 Hz, Q 20
    /// bell read 0 dB at its peak once the denominator fell under the cut-off).
    /// Same formulation as the macOS Console's DSPMath.magnitudeSquared.
    /// </summary>
    private static double BiquadMagSquared(Coefficients c, double w)
    {
        double s = Math.Sin(w / 2.0);
        double phi = s * s;
        double sb = c.B0 + c.B1 + c.B2, sa = 1.0 + c.A1 + c.A2;
        double num = sb * sb - 4.0 * (c.B0 * c.B1 + c.B1 * c.B2 + 4.0 * c.B0 * c.B2) * phi
                     + 16.0 * c.B0 * c.B2 * phi * phi;
        double den = sa * sa - 4.0 * (c.A1 + c.A1 * c.A2 + 4.0 * c.A2) * phi
                     + 16.0 * c.A2 * phi * phi;
        if (den <= 0.0) return 1.0;
        return Math.Max(num, 0.0) / den;
    }

    /// <summary>
    /// Phase contribution (radians) of one normalized biquad at angular frequency
    /// <paramref name="w"/>: arg(numerator) − arg(denominator). Cascade phases sum.
    /// </summary>
    private static double BiquadPhase(Coefficients c, double w)
    {
        double cos_w = Math.Cos(w);
        double cos_2w = Math.Cos(2.0 * w);
        double sin_w = Math.Sin(w);
        double sin_2w = Math.Sin(2.0 * w);

        double num_r = c.B0 + c.B1 * cos_w + c.B2 * cos_2w;
        double num_i = -(c.B1 * sin_w + c.B2 * sin_2w);
        double den_r = 1.0 + c.A1 * cos_w + c.A2 * cos_2w;
        double den_i = -(c.A1 * sin_w + c.A2 * sin_2w);

        return Math.Atan2(num_i, num_r) - Math.Atan2(den_i, den_r);
    }

    /// <summary>
    /// Total phase response in degrees, wrapped to [-180, 180], for a filter set at
    /// a given frequency. Sums the phase of every active, non-bypassed biquad (PEQ
    /// bands and crossover cascade sections), mirroring <see cref="ResponseAt"/>.
    /// </summary>
    public static float PhaseAt(float freq, IEnumerable<FilterParams> filters, float sampleRate = SampleRate)
    {
        double phase = 0.0;
        double w = 2.0 * Math.PI * freq / sampleRate;

        foreach (var f in filters)
        {
            if (f.Type == FilterType.Flat || !f.IsActive || f.Bypass)
                continue;

            if (f.Type.IsCrossover())
            {
                foreach (var c in CrossoverSections(f.Type, f.Frequency, sampleRate))
                    phase += BiquadPhase(c, w);
                continue;
            }

            phase += BiquadPhase(CalculateCoefficients(f, sampleRate), w);
        }

        double deg = phase * 180.0 / Math.PI;
        deg %= 360.0;
        if (deg > 180.0) deg -= 360.0;
        if (deg < -180.0) deg += 360.0;
        return (float)deg;
    }

    /// <summary>
    /// Remove ±360° discontinuities between adjacent wrapped phase samples,
    /// producing a continuous curve. Input/output are in degrees.
    /// </summary>
    public static float[] UnwrapPhase(float[] wrapped)
    {
        if (wrapped == null || wrapped.Length == 0) return wrapped ?? Array.Empty<float>();
        var outp = new float[wrapped.Length];
        outp[0] = wrapped[0];
        for (int i = 1; i < wrapped.Length; i++)
        {
            double d = wrapped[i] - wrapped[i - 1];
            while (d > 180.0) d -= 360.0;
            while (d <= -180.0) d += 360.0;
            outp[i] = (float)(outp[i - 1] + d);
        }
        return outp;
    }

    // ── Crossover filter design (ported from firmware crossover.c) ──────────
    // Each crossover "band" is a cascade of biquad sections built from an analog
    // prototype via the bilinear transform with frequency prewarping. We only
    // need the cascade's biquad coefficients to evaluate its magnitude; the SVF
    // path the firmware uses on low fc is the same transfer function, so the
    // bilinear (TDF2) coefficients give an identical |H|. Matches the device.

    /// <summary>
    /// Design the biquad-section cascade for a crossover filter type at cutoff
    /// <paramref name="fc"/> Hz. Returns an empty list for non-crossover types.
    /// </summary>
    public static IReadOnlyList<Coefficients> CrossoverSections(FilterType type, double fc, double sampleRate)
    {
        var sections = new List<Coefficients>(4);
        if (sampleRate <= 0.0 || !CrossoverFilter.TryGetMeta(type, out var meta))
            return sections;

        // Clamp fc to a safe range — same as the firmware / PEQ path.
        if (fc < 10.0) fc = 10.0;
        if (fc > sampleRate * 0.45) fc = sampleRate * 0.45;

        double omega_a = 2.0 * sampleRate * Math.Tan(Math.PI * fc / sampleRate); // prewarp
        bool hp = meta.IsHighPass;
        int order = meta.Order;

        switch (meta.Family)
        {
            case XoverFamily.Butterworth:
                DesignButterworth(sections, order, hp, omega_a, sampleRate);
                break;
            case XoverFamily.LinkwitzRiley:
                DesignLinkwitzRiley(sections, order, hp, omega_a, sampleRate);
                break;
            case XoverFamily.Bessel:
                DesignBessel(sections, order, hp, omega_a, sampleRate);
                break;
        }
        return sections;
    }

    private static void DesignButterworth(List<Coefficients> s, int order, bool hp, double omega_a, double Fs)
    {
        if ((order & 1) != 0)                                // odd: real pole at σ_n = 1
            s.Add(SectionEmit1st(1.0, omega_a, hp, Fs));
        for (int p = 0; p < order / 2; p++)
        {
            BwPolePair(order, p, out var sigma, out var omega);
            s.Add(SectionEmit2nd(sigma, omega, omega_a, hp, Fs));
        }
    }

    // LR_{2N} = (BW_N)²: design BW_N, then duplicate every section (squaring the
    // magnitude). LR2 is the canonical single biquad with a double real pole.
    private static void DesignLinkwitzRiley(List<Coefficients> s, int orderLr, bool hp, double omega_a, double Fs)
    {
        if (orderLr == 2)
        {
            s.Add(SectionEmit2nd(1.0, 0.0, omega_a, hp, Fs));
            return;
        }
        int start = s.Count;
        DesignButterworth(s, orderLr / 2, hp, omega_a, Fs);
        int count = s.Count - start;
        for (int i = 0; i < count; i++)
            s.Add(s[start + i]);                            // duplicate the BW cascade
    }

    private static void DesignBessel(List<Coefficients> s, int order, bool hp, double omega_a, double Fs)
    {
        foreach (var (sigma, omega) in BesselTable(order))
            s.Add(SectionEmit2nd(sigma, omega, omega_a, hp, Fs));
    }

    // Butterworth conjugate-pair pole angles measured from the negative-real
    // axis; σ_n = cos θ, ω_n = sin θ. Matches crossover.c bw_pole_pair().
    private static void BwPolePair(int order, int pairIdx, out double sigma, out double omega)
    {
        double theta = (order & 1) != 0
            ? Math.PI * (pairIdx + 1) / order
            : Math.PI * (2 * pairIdx + 1) / (2.0 * order);
        sigma = Math.Cos(theta);
        omega = Math.Sin(theta);
    }

    // Bessel (-3 dB normalized) analog pole pairs (σ_n, ω_n). Exact values from
    // firmware crossover.c (verified there against scipy). Even orders only.
    private static (double sigma, double omega)[] BesselTable(int order) => order switch
    {
        2 => new[] { (1.10160, 0.63601) },
        4 => new[] { (1.37007, 0.41025), (0.99521, 1.25711) },
        6 => new[] { (1.57149, 0.32090), (1.38186, 0.97147), (0.93066, 1.66186) },
        8 => new[] { (1.75741, 0.27287), (1.63694, 0.82280), (1.37384, 1.38836), (0.89287, 1.99833) },
        _ => Array.Empty<(double, double)>()
    };

    // 2nd-order section: analog pole pair (σ_n, ω_n) → biquad via bilinear
    // transform (K = 2·Fs). The LP→HP reciprocal (σ,ω)/(σ²+ω²) is applied for HP
    // — a no-op for BW/LR (poles on the unit circle) and required for Bessel.
    private static Coefficients SectionEmit2nd(double sigmaN, double omegaN, double omega_a, bool hp, double Fs)
    {
        if (hp)
        {
            double r2 = sigmaN * sigmaN + omegaN * omegaN;
            if (r2 > 0.0) { sigmaN /= r2; omegaN /= r2; }
        }
        double sigma = sigmaN * omega_a;
        double omega = omegaN * omega_a;
        double K = 2.0 * Fs;
        double A = 2.0 * sigma;
        double B = sigma * sigma + omega * omega;
        double A0 = K * K + A * K + B;
        double A1 = 2.0 * (B - K * K);
        double A2 = K * K - A * K + B;
        double inv = 1.0 / A0;

        double b0, b1, b2;
        if (hp) { double kk = K * K * inv; b0 = kk; b1 = -2.0 * kk; b2 = kk; }
        else { double bb = B * inv; b0 = bb; b1 = 2.0 * bb; b2 = bb; }
        return new Coefficients(b0, b1, b2, A1 * inv, A2 * inv);
    }

    // 1st-order real-pole section (Butterworth odd orders). HP reciprocal
    // 1/σ_n is a no-op for σ_n = 1 but kept for symmetry with the 2nd-order path.
    private static Coefficients SectionEmit1st(double sigmaN, double omega_a, bool hp, double Fs)
    {
        if (hp && sigmaN > 0.0) sigmaN = 1.0 / sigmaN;
        double sigma = sigmaN * omega_a;
        double K = 2.0 * Fs;
        double A0 = K + sigma;
        double A1 = sigma - K;
        double inv = 1.0 / A0;

        double b0, b1;
        if (hp) { b0 = K * inv; b1 = -K * inv; }
        else { b0 = sigma * inv; b1 = sigma * inv; }
        return new Coefficients(b0, b1, 0.0, A1 * inv, 0.0);
    }

    /// <summary>
    /// Generate frequency response curve points for plotting
    /// </summary>
    public static (float[] frequencies, float[] magnitudes) GenerateResponseCurve(
        IEnumerable<FilterParams> filters,
        int numPoints = 201,
        float minFreq = 10.0f,
        float maxFreq = 20000.0f,
        float sampleRate = SampleRate)
    {
        var frequencies = new float[numPoints];
        var magnitudes = new float[numPoints];

        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        for (int i = 0; i < numPoints; i++)
        {
            double pct = i / (double)(numPoints - 1);
            float freq = (float)Math.Pow(10, logMin + pct * (logMax - logMin));
            frequencies[i] = freq;
            magnitudes[i] = ResponseAt(freq, filters, sampleRate);
        }

        return (frequencies, magnitudes);
    }

    /// <summary>
    /// Generate phase-response curve points (degrees) for plotting, over the same
    /// log-spaced grid as <see cref="GenerateResponseCurve"/>. Set
    /// <paramref name="unwrap"/> to produce a continuous curve instead of one
    /// wrapped at ±180°.
    /// </summary>
    public static (float[] frequencies, float[] phases) GeneratePhaseCurve(
        IEnumerable<FilterParams> filters,
        bool unwrap = false,
        int numPoints = 201,
        float minFreq = 10.0f,
        float maxFreq = 20000.0f,
        float sampleRate = SampleRate)
    {
        var frequencies = new float[numPoints];
        var phases = new float[numPoints];

        // Materialize once so PhaseAt doesn't re-enumerate a lazy source per point.
        var list = filters as IReadOnlyList<FilterParams> ?? new List<FilterParams>(filters);

        double logMin = Math.Log10(minFreq);
        double logMax = Math.Log10(maxFreq);

        for (int i = 0; i < numPoints; i++)
        {
            double pct = i / (double)(numPoints - 1);
            float freq = (float)Math.Pow(10, logMin + pct * (logMax - logMin));
            frequencies[i] = freq;
            phases[i] = PhaseAt(freq, list, sampleRate);
        }

        if (unwrap) phases = UnwrapPhase(phases);
        return (frequencies, phases);
    }
}
