// Every PEQ band's lobe in one pass: the area between the band's own curve and
// 0 dB, strongest along the curve and fading to nothing at 0 dB, as the macOS
// Console's lobe shader draws it. The CPU hands over each band's curve (its y
// in DIPs, one value per column) and style; the GPU does every pixel.
//
// The compiled PeqLobes.bin is embedded in the app. After editing this file,
// rebuild it from this folder with (Windows SDK, x64 developer prompt or the
// full path to fxc.exe):
//
//   fxc /nologo /T ps_4_0 /E main /O3 /D D2D_FULL_SHADER /D D2D_ENTRY=main
//       /I "%WindowsSdkDir%Include\%WindowsSDKVersion%um" /Fo PeqLobes.bin PeqLobes.hlsl

#define D2D_INPUT_COUNT 0
#define D2D_REQUIRES_SCENE_POSITION
#include "d2d1effecthelpers.hlsli"

#define MAX_BANDS 12
// Columns of curve data; each holds four bands per float4, three float4s a column.
#define MAX_COLUMNS 1024

cbuffer constants : register(b0)
{
    float count;      // bands to draw, back to front
    float columns;    // columns of curve data in use
    float colStep;    // DIPs between columns
    float zero;       // y of 0 dB, DIPs
    float pxPerDip;   // device pixels per DIP
    float3 pad;
    float4 colors[MAX_BANDS];    // rgb, fill opacity
    float4 reaches[MAX_BANDS];   // top and bottom of the band's reach, DIPs
    float4 curves[MAX_COLUMNS * 3];
};

float CurveAt(int k, float fx)
{
    float c = clamp(fx, 0, columns - 1);
    int i0 = (int)c;
    int i1 = min(i0 + 1, (int)columns - 1);
    float a = curves[i0 * 3 + k / 4][k % 4];
    float b = curves[i1 * 3 + k / 4][k % 4];
    return lerp(a, b, c - i0);
}

D2D_PS_ENTRY(main)
{
    float2 pos = D2DGetScenePosition().xy / pxPerDip;
    float fx = pos.x / colStep;
    float half = 0.5 / colStep;
    float4 result = 0;

    [unroll]
    for (int k = 0; k < MAX_BANDS; k++)
    {
        if (k < count)
        {
            float cy = CurveAt(k, fx);
            float span = abs(cy - zero);
            // Distance from 0 dB toward the curve, and from the curve edge
            // inward, measured across the curve so steep edges stay smooth.
            float into = (pos.y - zero) * (cy < zero ? -1 : 1);
            float slope = CurveAt(k, fx + half) - CurveAt(k, fx - half);
            float edge = (span - into) / sqrt(1 + slope * slope);
            float t = saturate(into / max(span, 0.001));
            float4 reach = reaches[k];
            float cover = saturate(edge * pxPerDip + 0.5)
                        * saturate((pos.y - reach.x) * pxPerDip + 0.5)
                        * saturate((reach.y - pos.y) * pxPerDip + 0.5);
            // A band that barely departs from flat fades out too, or it would
            // leave a tinted line along 0 dB.
            float alpha = colors[k].a * pow(t, 1.5) * smoothstep(2, 10, span) * cover;
            result = float4(colors[k].rgb * alpha, alpha) + result * (1 - alpha);
        }
    }
    return result;
}
