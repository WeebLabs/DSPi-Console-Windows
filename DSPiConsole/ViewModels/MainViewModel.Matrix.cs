namespace DSPiConsole.ViewModels;

/// <summary>The matrix mixer's quick routes for a multichannel input, after
/// the macOS Console's applyDirectRouting and clearAllRoutes: an 8-channel
/// stream is silent out of the box until routes are set (spec §10.D).</summary>
public partial class MainViewModel
{
    /// <summary>Each input to the output of the same number (FL to OUT1, FR to
    /// OUT2, ...) at unity and normal phase, every other crosspoint off, and the
    /// PDM sub disabled first so the EQ-worker outputs can be enabled.</summary>
    public void ApplyDirectRouting()
    {
        int inputs = ActiveInputChannelCount, outputs = ActiveOutputs.Count;
        int n = Math.Min(inputs, outputs);
        if (IsOutputEnabled(PdmOutputIndex))
        {
            SetOutputEnabled(PdmOutputIndex, false);
            SetOutputEnableUsb(PdmOutputIndex, false);
        }
        for (int i = 0; i < inputs; i++)
            for (int o = 0; o < outputs; o++)
            {
                bool on = i == o && i < n;
                if (GetMatrixRouting(i, o) != on || GetMatrixGain(i, o) != 0 || GetMatrixInvert(i, o))
                    SetMatrixRoute(i, o, on, 0, false);
            }
        for (int o = 0; o < n; o++)
        {
            if (o == PdmOutputIndex || IsOutputEnabled(o)) continue;
            SetOutputEnabled(o, true);
            SetOutputEnableUsb(o, true);
        }
    }

    /// <summary>Disconnects every crosspoint, keeping each one's gain and phase
    /// for when it is connected again.</summary>
    public void ClearAllRoutes()
    {
        for (int i = 0; i < ActiveInputChannelCount; i++)
            for (int o = 0; o < ActiveOutputs.Count; o++)
                if (GetMatrixRouting(i, o))
                    SetMatrixRoute(i, o, false, GetMatrixGain(i, o), GetMatrixInvert(i, o));
    }
}
