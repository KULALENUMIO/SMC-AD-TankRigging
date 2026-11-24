// Track ruts on the ground: a world-space deformation field written by TankTrackRuts (compute) and read by the
// GroundDeform shader (a copy of HDRP/LitTessellation): the domain shader moves the tessellated ground down into
// the ruts (real geometry: silhouette, self-shadowing), the pixel shader bends the normal, darkens / wets the
// compacted floor, colours the pushed-up berms and darkens the cavities.
//
// Field (RGHalf, wrapping toroidally around the tank): R = depth below the (lifted) ground in m, G = berm height in m.
// The whole ground is lifted by _RutParams.x (the depth a track sinks), so ruts go down to the real ground the tank
// stands on and the tracks look sunk in rather than floating over a hole.
#ifndef GROUND_RUTS_INCLUDED
#define GROUND_RUTS_INCLUDED

TEXTURE2D(_RutTex);
TEXTURE2D(_RutOcc);   // coarse occupancy (any rut in the cell)
float4 _RutOccP;     // x cell (m), y cells per side
float4 _RutWindow;   // xy window centre (world xz), z 1/size (1/m), w half size (m)
float4 _RutParams;   // x lift (m), y texel (m), z edge fade (m), w 1 = on
float4 _RutTint;     // rgb: rut floor albedo multiply, a: smoothness added on the floor (moist, pressed)
float4 _RutSoil;     // rgb: torn soil (linear) showing in the grouser bites, a: how much
float4 _RutBerm;     // rgb: berm albedo multiply, a: share of the ground's own detail normal kept on the floor
float4 _RutMisc;     // x groove depth (m), y cavity AO strength, z berm normal boost

float2 RutRaw(float2 xz)
{
    if (_RutParams.w < 0.5) return 0;
    float2 d = abs(xz - _RutWindow.xy);
    float m = saturate((_RutWindow.w - max(d.x, d.y)) / max(_RutParams.z, 1e-3));
    return SAMPLE_TEXTURE2D_LOD(_RutTex, s_linear_repeat_sampler, xz * _RutWindow.z, 0).rg * m;
}

// height offset of the ground surface (m): lift - rut + berm (berms do not stand inside a rut)
float RutHeightFrom(float2 v)
{
    float lift = _RutParams.x;
    return lift - v.x + v.y * saturate(1.0 - v.x / max(lift * 0.35, 1e-4));
}
float RutHeight(float2 xz) { return _RutParams.w < 0.5 ? 0 : RutHeightFrom(RutRaw(xz)); }

// tessellate only near ruts: any occupied cell around (the 3x3 cells reach past every triangle touching a rut)
float RutNear(float2 xz)
{
    if (_RutParams.w < 0.5) return 0;
    int n = (int)_RutOccP.y;
    int2 c = (int2)floor(xz / _RutOccP.x);
    float m = 0;
    [unroll] for (int j = -1; j <= 1; j++)
        [unroll] for (int i = -1; i <= 1; i++)
        {
            int2 q = c + int2(i, j);
            q -= n * (int2)floor(q / (float)n);
            m = max(m, LOAD_TEXTURE2D_LOD(_RutOcc, q, 0).r);
        }
    return m;
}

// pixel: normal, colour, smoothness, cavity occlusion
void ApplyRutSurface(float3 posAWS, float3 geomN, inout SurfaceData s)
{
    if (_RutParams.w < 0.5) return;
    float2 xz = posAWS.xz;
    float e = _RutParams.y;
    float2 v = RutRaw(xz);
    float h = RutHeightFrom(v);
    float hl = RutHeight(xz - float2(e, 0)), hr = RutHeight(xz + float2(e, 0));
    float hd = RutHeight(xz - float2(0, e)), hu = RutHeight(xz + float2(0, e));
    float2 g = float2(hr - hl, hu - hd) / (2.0 * e);

    float lift = max(_RutParams.x, 1e-4);
    float rut = saturate(v.x / lift);                                        // 0 untouched .. 1 pressed to the floor
    float bite = saturate((v.x - lift) / max(_RutMisc.x, 1e-4));             // the grouser bites below the floor
    float berm = saturate(v.y / (lift * 0.3)) * (1.0 - rut);

    // the floor is pressed smooth: the ground's own small detail fades, then the rut's shape tilts the normal
    float3 n = normalize(lerp(s.normalWS, geomN, rut * (1.0 - _RutBerm.a)));
    n = normalize(n + float3(-g.x, 0, -g.y) * (1.0 + berm * _RutMisc.z));
    s.normalWS = n;

    float3 c = s.baseColor;
    c = lerp(c, c * _RutTint.rgb, rut);
    c = lerp(c, _RutSoil.rgb, saturate(_RutSoil.a * (0.35 * rut + 0.9 * bite)));
    c = lerp(c, c * _RutBerm.rgb, berm);
    s.baseColor = c;
    s.perceptualSmoothness = saturate(s.perceptualSmoothness + _RutTint.a * (rut + 0.5 * bite));

    // cavity: deeper than its surroundings -> less sky light
    const float r = 0.14;
    float wide = 0.25 * (RutHeight(xz + float2(r, 0)) + RutHeight(xz - float2(r, 0)) + RutHeight(xz + float2(0, r)) + RutHeight(xz - float2(0, r)));
    float cav = saturate((wide - h) * 12.0);
    s.ambientOcclusion *= 1.0 - _RutMisc.y * cav;
}

#endif
