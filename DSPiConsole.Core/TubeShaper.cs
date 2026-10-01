namespace DSPiConsole.Core;

/// <summary>
/// The firmware's static tube waveshaper (tube_preamp_spec.md §1), evaluated in
/// double for the transfer graph: drive, bias, the two knees, the hardness
/// blend, the rest-point offset, mix and trim. It leaves out everything with
/// memory (sag, the DC blocker, the output stage), which is why the graph is a
/// transfer curve and not a frequency response. Port of the macOS Console's
/// TubeShaper.
/// </summary>
public sealed record TubeShaper
{
    public double M { get; }
    public double B { get; }
    public double RatioN { get; }
    private readonly double _c1, _c3, _c5, _sP, _sN, _dryW, _wetW, _v0;

    public TubeShaper(float driveDb, float biasPct, float asymDb, float hardnessPct, float mixPct, float trimDb)
    {
        M = Math.Pow(10, driveDb / 20.0);
        B = biasPct / 200.0;
        RatioN = Math.Pow(10, -asymDb / 20.0);
        double h = hardnessPct / 100.0;
        _c1 = 1.5 + 0.375 * h;
        _c3 = -0.5 - 0.75 * h;
        _c5 = 0.375 * h;
        // 1/m makeup gain in the half scales: drive moves the knee rather than
        // the level, so clean material keeps unity small-signal gain.
        _sP = 1 / (_c1 * M);
        _sN = Math.Pow(10, asymDb / 20.0) / (_c1 * M);
        double mix = mixPct / 100.0;
        _dryW = 1 - mix;
        _wetW = mix * Math.Pow(10, trimDb / 20.0);
        _v0 = Shape(0);
    }

    private double Shape(double x)
    {
        double t = M * x + B;
        if (t < 0) t *= RatioN;
        t = Math.Clamp(t, -1, 1);
        double t2 = t * t;
        double p = t * (_c1 + t2 * (_c3 + t2 * _c5));
        return p * (t >= 0 ? _sP : _sN);
    }

    /// <summary>The input at the shaper's knee scale, before the clamp; a
    /// magnitude of 1 or more is fully clipped.</summary>
    public double Knee(double x)
    {
        double t = M * x + B;
        return t < 0 ? t * RatioN : t;
    }

    /// <summary>The wet path alone, with the rest-point offset removed.</summary>
    public double Wet(double x) => Shape(x) - _v0;

    /// <summary>What leaves the stage for input <paramref name="x"/>.</summary>
    public double Output(double x) => _dryW * x + _wetW * Wet(x);

    /// <summary>The input above which the positive half clips, and below which
    /// the negative half does (solved from the knees, not sampled).</summary>
    public (double Positive, double Negative) ClipPoints => ((1 - B) / M, (-1 / RatioN - B) / M);

    /// <summary>
    /// Second and third harmonic of a full-scale sine relative to the
    /// fundamental, in dB; -120 when absent. A 256-point DFT at three bins is
    /// exact for a memoryless curve.
    /// </summary>
    public (double Second, double Third) Harmonics()
    {
        const int n = 256;
        double[] re = new double[3], im = new double[3];
        for (int i = 0; i < n; i++)
        {
            double phase = 2 * Math.PI * i / n;
            double y = Output(Math.Sin(phase));
            for (int k = 0; k < 3; k++)
            {
                re[k] += y * Math.Cos((k + 1) * phase);
                im[k] += y * Math.Sin((k + 1) * phase);
            }
        }
        double Mag(int k) => Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        double fundamental = Mag(0);
        double Rel(double a) => fundamental > 1e-12 && a > fundamental * 1e-6 ? 20 * Math.Log10(a / fundamental) : -120;
        return (Rel(Mag(1)), Rel(Mag(2)));
    }
}

/// <summary>The visibly different kinds of tube among the sixteen the
/// firmware models; tubes that look alike on a shelf share a drawing.</summary>
public enum TubeFamily
{
    NovalTriode,      // 12AX7, 5751, 12AT7, 12AY7, 12AU7, 6DJ8
    NovalPentode,     // EF86
    NovalPower,       // EL84
    OctalGlass,       // 6SN7, 6SL7
    OctalMetal,       // 6SJ7 (steel envelope)
    OctalPowerLarge,  // EL34
    OctalPowerSmall,  // 6V6
    ShoulderedPower,  // 6L6 (ST "coke bottle")
    BeamBottle,       // KT88
    DirectlyHeated,   // 300B / 2A3
}

public static class TubeFamilies
{
    /// <summary>Custom has no tube of its own, so it shows the 12AX7.</summary>
    public static TubeFamily Of(int tubeType) => tubeType switch
    {
        6 or 7 => TubeFamily.OctalGlass,
        9 => TubeFamily.NovalPentode,
        10 => TubeFamily.OctalMetal,
        11 => TubeFamily.NovalPower,
        12 => TubeFamily.OctalPowerLarge,
        13 => TubeFamily.ShoulderedPower,
        14 => TubeFamily.OctalPowerSmall,
        15 => TubeFamily.BeamBottle,
        16 => TubeFamily.DirectlyHeated,
        _ => TubeFamily.NovalTriode,
    };
}
