// Full-screen Custom Pass (HDRP, Before Post Process): raymarches the muzzle-blast grid of MuzzleBlastSim.
// Smoke scatters sun + sky (sun transmittance precomputed per cell), hot gas emits a blackbody colour,
// thin fringes are eroded by noise carried with the gas (rest position + per-lump seed).
Shader "Hidden/TankRig/BlastRaymarch"
{
    HLSLINCLUDE

    #pragma vertex Vert
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "MBNoise.hlsl"

    Texture3D<float4> _MB_S;
    Texture3D<float4> _MB_R;
    Texture3D<float2> _MB_L;
    Texture3D<float4> _MB_FL;
    float4 _MB_Look3;   // x fire-light scatter, y sub-cell detail amplitude (m)
    SamplerState mb_linear_clamp_sampler;
    float4x4 _MB_WorldToUVW, _MB_WorldToUVW2;
    Texture3D<float4> _MB_S2, _MB_R2;
    Texture3D<float2> _MB_L2;
    float4 _MB_Res2, _MB_ResB, _MB_ResC, _MB_ResD, _MB_DustLook;
    float4x4 _MB_WorldToUVW4;
    Texture3D<float4> _MB_D;
    Texture3D<float2> _MB_LD;
    float4x4 _MB_WorldToUVW3;
    Texture3D<float4> _MB_S3, _MB_R3;
    Texture3D<float2> _MB_L3;
    float4 _MB_Res, _MB_SunDir, _MB_SunLux, _MB_Ambient, _MB_Look, _MB_Look2;
    // empty-space skipping: per-grid bricks (8^3 cells, dilated by one) that hold visible gas
    Texture3D<float> _MB_OA, _MB_OB, _MB_OC, _MB_OD;
    float _MB_UseOcc;
    // adaptive steps: x = optical depth per step in thin gas, y = longest step (x the grid's base step), z = 1 drops all
    // the detail noise (cost test only)
    float4 _MB_March;   // w = 1: detail noise from the tileable texture
    Texture3D<float4> _MB_NoiseTex;
    SamplerState mb_linear_repeat_sampler;
    float4 TN(float3 p) { return _MB_NoiseTex.SampleLevel(mb_linear_repeat_sampler, p * (1.0 / 16.0), 0); }   // 4 noises, lattice like MBNoise
    float3 N3(float3 p, bool tex) { return tex ? TN(p).rgb : MBNoise3(p); }
    float N1(float3 p, bool tex) { return tex ? TN(p).a : MBNoise(p); }
    float NF(float3 p, bool tex) { return tex ? 0.5 * TN(p).a + 0.3 * TN(p * 2.03 + 7.1).a + 0.2 * TN(p * 4.08 + 13.7).a : MBFbm(p); }
    SamplerState mb_point_clamp_sampler;
    // half resolution: the march runs on a half-size target, then is upsampled with the depth buffer as a guide
    TEXTURE2D_X(_MB_Half);
    // temporal reuse: mean gas distance per half texel, last frame's history, its view-projection (absolute world)
    TEXTURE2D_X(_MB_HalfDist);
    TEXTURE2D_X(_MB_Hist);
    float4x4 _MB_PrevVP;
    float4 _MB_TParams;   // x history valid, y weight of the new frame
    float _MB_Frame;      // per-camera frame index (0 = fixed jitter)

    // smooth (B-spline) 3D lookup from 8 bilinear taps: hides the cell lattice ("mosaic") of trilinear sampling
    float4 MBTricubic(Texture3D<float4> tex, float3 uvw, float3 res)
    {
        float3 c = uvw * res - 0.5;
        float3 i = floor(c);
        float3 f = c - i;
        float3 f2 = f * f, f3 = f2 * f;
        float3 w0 = (-f3 + 3.0 * f2 - 3.0 * f + 1.0) / 6.0;
        float3 w1 = (3.0 * f3 - 6.0 * f2 + 4.0) / 6.0;
        float3 w2 = (-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0) / 6.0;
        float3 w3 = f3 / 6.0;
        float3 g0 = w0 + w1, g1 = w2 + w3;
        float3 h0 = (i - 0.5 + w1 / g0) / res;
        float3 h1 = (i + 1.5 + w3 / g1) / res;
        float4 a = lerp(tex.SampleLevel(mb_linear_clamp_sampler, float3(h1.x, h0.y, h0.z), 0), tex.SampleLevel(mb_linear_clamp_sampler, float3(h0.x, h0.y, h0.z), 0), g0.x);
        float4 b = lerp(tex.SampleLevel(mb_linear_clamp_sampler, float3(h1.x, h1.y, h0.z), 0), tex.SampleLevel(mb_linear_clamp_sampler, float3(h0.x, h1.y, h0.z), 0), g0.x);
        float4 lo = lerp(b, a, g0.y);
        a = lerp(tex.SampleLevel(mb_linear_clamp_sampler, float3(h1.x, h0.y, h1.z), 0), tex.SampleLevel(mb_linear_clamp_sampler, float3(h0.x, h0.y, h1.z), 0), g0.x);
        b = lerp(tex.SampleLevel(mb_linear_clamp_sampler, float3(h1.x, h1.y, h1.z), 0), tex.SampleLevel(mb_linear_clamp_sampler, float3(h0.x, h1.y, h1.z), 0), g0.x);
        float4 hi = lerp(b, a, g0.y);
        return lerp(hi, lo, g0.z);
    }

    // ray (world) vs the unit box of a grid's uvw space -> [t0, t1] in metres along the ray
    float2 MBBox(float4x4 w2u, float3 ro, float3 rd)
    {
        float3 o = mul(w2u, float4(ro, 1)).xyz;
        float3 d = mul((float3x3)w2u, rd);
        float3 inv = 1.0 / (abs(d) > 1e-8 ? d : 1e-8);
        float3 ta = (0 - o) * inv, tb = (1 - o) * inv;
        float t0 = max(max(min(ta.x, tb.x), min(ta.y, tb.y)), min(ta.z, tb.z));
        float t1 = min(min(max(ta.x, tb.x), max(ta.y, tb.y)), max(ta.z, tb.z));
        return t1 > max(t0, 0) ? float2(t0, t1) : float2(1e9, -1e9);
    }

    float3 MBBlackbody(float h)
    {
        const float3 c0 = float3(1.0, 0.08, 0.0), c1 = float3(1.0, 0.26, 0.0), c2 = float3(1.0, 0.48, 0.03);
        const float3 c3 = float3(1.0, 0.70, 0.10), c4 = float3(1.0, 0.88, 0.42);
        h = saturate(h) * 4.0;
        if (h < 1.0) return lerp(c0, c1, h);
        if (h < 2.0) return lerp(c1, c2, h - 1.0);
        if (h < 3.0) return lerp(c2, c3, h - 2.0);
        return lerp(c3, c4, h - 3.0);
    }

    float MBPhase(float c)   // forward-scattering HG lobe + a little back-scatter
    {
        const float g = 0.45;
        float hg = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * c, 1.5);
        float hb = (1.0 - 0.04) / pow(1.0 + 0.04 + 0.4 * c, 1.5);
        return (0.8 * hg + 0.2 * hb) / (4.0 * PI);
    }

    float4 March(float2 pixel, float2 jitterPix, out float gasDist)
    {
        gasDist = 0;
        if (_MB_Look2.y < 0.5) return float4(0, 0, 0, 1);
        float depth = LoadCameraDepth(pixel);
        PositionInputs posInput = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float3 camAbs = GetAbsolutePositionWS(float3(0, 0, 0));
        float3 pAbs = GetAbsolutePositionWS(posInput.positionWS);
        float3 dir = pAbs - camAbs;
        float sceneDist = length(dir);
        dir /= max(sceneDist, 1e-5);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE) sceneDist = 1e6;

        // ray intervals of the coarse grid (A) and the muzzle grid (B)
        float2 iA = MBBox(_MB_WorldToUVW, camAbs, dir);
        float2 iB = _MB_Res2.x > 0.5 ? MBBox(_MB_WorldToUVW2, camAbs, dir) : float2(1e9, -1e9);
        float2 iC = _MB_Res2.z > 0.5 ? MBBox(_MB_WorldToUVW3, camAbs, dir) : float2(1e9, -1e9);   // the open-air grid
        float2 iD = _MB_DustLook.x > 0.5 ? MBBox(_MB_WorldToUVW4, camAbs, dir) : float2(1e9, -1e9);   // the firing dust
        float tmin = max(min(min(min(iA.x, iB.x), iC.x), iD.x), 0);
        float tmax = min(max(max(max(iA.y, iB.y), iC.y), iD.y), sceneDist);
        if (tmax <= tmin) return float4(0, 0, 0, 1);

        float stepA = _MB_Res.w * 0.6, stepB = _MB_Res2.w * 0.6, stepC = _MB_ResC.w * 0.6;
        float3 sizeC = _MB_ResC.xyz * _MB_ResC.w;
        float jitter = frac(52.9829189 * frac(dot(jitterPix, float2(0.06711056, 0.00583715))) + 0.61803398875 * _MB_Frame);
        float wSum = 0, dSum = 0;
        // safe jumps through empty bricks: 7 cells (the lookups reach 1 cell, the bricks are dilated by 8)
        float jA = _MB_Res.w * 7.0, jB = _MB_Res2.w * 7.0, jC = _MB_ResC.w * 7.0, jD = _MB_ResD.w * 7.0;
        float phase = MBPhase(dot(dir, _MB_SunDir.xyz));
        float3 sunL = _MB_SunLux.rgb * phase;

        float T = 1.0;
        float3 L = 0;
        float t = tmin;
        float stepMul = 1.0;
        bool noNoise = _MB_March.z > 0.5;
        bool nt = _MB_March.w > 0.5;
        [loop] for (int i = 0; i < 1200 && t < tmax; i++)
        {
            bool inB = t >= iB.x && t <= iB.y;
            bool inA = t >= iA.x && t <= iA.y;
            bool inC = t >= iC.x && t <= iC.y;
            bool inD = t >= iD.x && t <= iD.y;
            float baseLen = inB ? stepB : inA ? stepA : inD ? _MB_ResD.w * 0.6 : stepC;
            float stepLen = min(baseLen * (inB ? 1.0 : stepMul), tmax - t);
            if (!inD && t < iD.x) stepLen = min(stepLen, iD.x - t + 1e-4);
            if (!inA && t < iA.x) stepLen = min(stepLen, iA.x - t + 1e-4);
            if (!inB && t < iB.x) stepLen = min(stepLen, iB.x - t + 1e-4);   // land on the muzzle grid's face (else the entry phase bands)
            float3 P = camAbs + dir * (t + jitter * stepLen);
            if (_MB_UseOcc > 0.5)
            {
                // nothing to draw near P in every grid the ray is in: jump instead of stepping
                bool busy = false;
                float jump = 1e9;
                if (inA) { if (_MB_OA.SampleLevel(mb_point_clamp_sampler, mul(_MB_WorldToUVW, float4(P, 1)).xyz, 0) > 0.5) busy = true; else jump = min(jump, jA); }
                if (inB) { if (_MB_OB.SampleLevel(mb_point_clamp_sampler, mul(_MB_WorldToUVW2, float4(P, 1)).xyz, 0) > 0.5) busy = true; else jump = min(jump, jB); }
                if (inC) { if (_MB_OC.SampleLevel(mb_point_clamp_sampler, mul(_MB_WorldToUVW3, float4(P, 1)).xyz, 0) > 0.5) busy = true; else jump = min(jump, jC); }
                if (inD) { if (_MB_OD.SampleLevel(mb_point_clamp_sampler, mul(_MB_WorldToUVW4, float4(P, 1)).xyz, 0) > 0.5) busy = true; else jump = min(jump, jD); }
                if (!busy)
                {
                    float j = jump;
                    if (!inD && t < iD.x) j = min(j, iD.x - t + 1e-4);
                    if (!inA && t < iA.x) j = min(j, iA.x - t + 1e-4);
                    if (!inB && t < iB.x) j = min(j, iB.x - t + 1e-4);
                    if (!inC && t < iC.x) j = min(j, iC.x - t + 1e-4);
                    t += max(j, stepLen);
                    continue;
                }
            }
            t += stepLen;
            float4 S = 0, R = 0;
            float2 Lt = 1;
            float3 uA = mul(_MB_WorldToUVW, float4(P, 1)).xyz;
            if (all(uA >= 0) && all(uA <= 1) && _MB_Res2.y != 1 && _MB_Res2.y != 3)
            {
                S = _MB_S.SampleLevel(mb_linear_clamp_sampler, uA, 0);
                if ((S.x > 1e-4 || S.y > 0.03) && !noNoise)
                {
                    // sub-cell billows: displace the lookup with noise riding on the gas (rest coordinates)
                    float4 R0 = _MB_R.SampleLevel(mb_linear_clamp_sampler, uA, 0);
                    float3 off = (N3(R0.xyz * 3.1 + R0.w * 7.0, nt) - 0.5) * 2.0 + (N3(R0.xyz * 7.3 + 11.0, nt) - 0.5) * 0.9 + (N3(R0.xyz * 15.1 + 23.0, nt) - 0.5) * 0.4;
                    S = MBTricubic(_MB_S, uA + off * _MB_Look3.y / (_MB_Res.xyz * _MB_Res.w), _MB_Res.xyz);
                }
                if (_MB_Res2.z < 0.5)
                {
                    // no open-air grid (real time): the gas thins out towards the coarse grid's sides, top and back so
                    // smoke drifting out of it fades instead of being cut off at a straight edge (never at the ground)
                    float3 sizeA = _MB_Res.xyz * _MB_Res.w;
                    float3 dlo = uA * sizeA, dhi = (1 - uA) * sizeA;
                    S.x *= saturate(min(min(dlo.x, dlo.z), min(dhi.x, min(dhi.y, dhi.z))) / 3.0);
                }
                if (S.x > 2e-4 || S.y > 0.05) { R = _MB_R.SampleLevel(mb_linear_clamp_sampler, uA, 0); Lt = _MB_L.SampleLevel(mb_linear_clamp_sampler, uA, 0); }
            }
            if (inB && _MB_Res2.y != 2 && _MB_Res2.y != 3)
            {
                float3 uB = mul(_MB_WorldToUVW2, float4(P, 1)).xyz;
                float4 SB = _MB_S2.SampleLevel(mb_linear_clamp_sampler, uB, 0);
                if (SB.x > 1e-4) SB = MBTricubic(_MB_S2, uB, _MB_ResB.xyz);
                if (SB.x > 1e-5)   // the two grids hold different gas (moved, not copied): add them
                {
                    float total = S.x + SB.x;
                    if (SB.x > S.x)
                    {
                        R = _MB_R2.SampleLevel(mb_linear_clamp_sampler, uB, 0);
                        Lt = min(Lt, _MB_L2.SampleLevel(mb_linear_clamp_sampler, uB, 0));
                    }
                    S.w = (S.w * S.x + SB.w * SB.x) / total;
                    S.y = max(S.y, SB.y);
                    S.x = total;
                }
            }
            if (inC && _MB_Res2.y != 1 && _MB_Res2.y != 2)   // debugView 3 = only the open-air grid
            {
                float3 uC = mul(_MB_WorldToUVW3, float4(P, 1)).xyz;
                float4 SC = _MB_S3.SampleLevel(mb_linear_clamp_sampler, uC, 0);
                if (SC.x > 1e-4 && !noNoise)
                {
                    float4 RC = _MB_R3.SampleLevel(mb_linear_clamp_sampler, uC, 0);
                    float3 offC = (N3(RC.xyz * 1.3 + RC.w * 7.0, nt) - 0.5) * 2.0 + (N3(RC.xyz * 3.1 + 5.0, nt) - 0.5);
                    SC = MBTricubic(_MB_S3, uC + offC * 0.6 / sizeC, _MB_ResC.xyz);   // the coarse air gets billows too
                }
                // the open air has no walls: thin the gas out towards its faces (never the ground) so no box shows
                float3 dlo = uC * sizeC, dhi = (1 - uC) * sizeC;
                SC.x *= saturate(min(min(dlo.x, dlo.z), min(dhi.x, min(dhi.y, dhi.z))) / 4.0);
                if (SC.x > 1e-5)
                {
                    float total = S.x + SC.x;
                    if (SC.x > S.x)
                    {
                        R = _MB_R3.SampleLevel(mb_linear_clamp_sampler, uC, 0);
                        Lt = min(Lt, _MB_L3.SampleLevel(mb_linear_clamp_sampler, uC, 0));
                    }
                    S.w = (S.w * S.x + SC.w * SC.x) / total;
                    S.y = max(S.y, SC.y);
                    S.x = total;
                }
            }
            S.x *= _MB_Look2.w;
            // the firing dust: its own medium, drawn together with the smoke (they do not mix)
            float sigD = 0; float3 srcD = 0;
            if (inD)
            {
                float3 uD = mul(_MB_WorldToUVW4, float4(P, 1)).xyz;
                float4 DD = _MB_D.SampleLevel(mb_linear_clamp_sampler, uD, 0);
                float dTot = (DD.x + 2.0 * DD.y) * _MB_Look2.w;
                if (dTot > 1e-4)
                {
                    DD = MBTricubic(_MB_D, uD + (noNoise ? 0 : (N3(P * 1.1 + DD.w * 5.0, nt) - 0.5)) * 0.8 / (_MB_ResD.xyz * _MB_ResD.w), _MB_ResD.xyz);
                    float nzD = noNoise ? 0.5 : NF(P * 3.1 + DD.w * 9.0, nt);
                    dTot = max(0, (DD.x * saturate(1.0 - 0.7 * nzD * (1.0 - saturate(DD.x / 0.3))) + 2.0 * DD.y)) * _MB_Look2.w;
                    sigD = _MB_DustLook.y * dTot;
                    float2 LD = _MB_LD.SampleLevel(mb_linear_clamp_sampler, uD, 0);
                    float tD = noNoise ? 0.5 : N1(P * 0.5 + DD.w * 3.0, nt);
                    float3 albD = lerp(float3(0.74, 0.66, 0.54), float3(0.64, 0.57, 0.47), tD);   // dry dust: light grey-tan
                    albD = lerp(albD, albD * 0.7, saturate(DD.y / max(DD.x + DD.y, 1e-4)));   // coarse grit is darker
                    float3 fireLD = all(uA >= 0) && all(uA <= 1) ? _MB_FL.SampleLevel(mb_linear_clamp_sampler, uA, 0).rgb * _MB_Look3.x : 0;
                    srcD = albD * (sunL * (0.25 + 0.75 * LD.x) + _MB_Ambient.rgb * (0.5 + 0.5 * LD.y) + fireLD);   // multiple scattering keeps dense dust bright
                }
            }
            if (S.x < 2e-4 && S.y < 0.05 && sigD <= 0) { stepMul = min(stepMul * 1.6, _MB_March.y); continue; }
            // fringe erosion by noise riding with the gas
            float nz = noNoise ? 0.5 : NF(R.xyz * 2.3 + R.w * 13.0, nt);
            float de = S.x * saturate(1.0 - _MB_Look2.x * nz * (1.0 - saturate(S.x / 0.35)));   // proportional: thin haze thins, never cut off
            float sigS = _MB_Look.x * pow(de, _MB_Look.y);
            float heat = saturate((S.y - 0.7) / _MB_Look2.z);
            float sigF = _MB_Look.z * heat;
            float sigT = sigS + sigF + sigD;
            // next step: long through thin haze (a fixed small optical depth per step), base length in flame and dense gas
            stepMul = heat > 0.01 ? 1.0 : clamp(_MB_March.x / max(sigT * baseLen, 1e-6), 1.0, _MB_March.y);
            if (sigT < 1e-5) continue;
            // colour variety: each lump of smoke gets its own warm/cool tint; thin haze turns bluish (fine particles),
            // bore gas (seed ~0.9) is whiter, burnt-out fire smoke (soot) brown-grey
            float tint = noNoise ? 0.5 : N1(R.xyz * 0.35 + R.w * 5.0, nt);
            float3 albedo = lerp(float3(0.86, 0.82, 0.75), float3(0.79, 0.84, 0.93), tint);
            albedo = lerp(albedo, float3(0.80, 0.86, 0.97), saturate(1.0 - S.x / 0.08) * 0.5);
            albedo = lerp(albedo, float3(0.93, 0.94, 0.96), smoothstep(0.86, 0.92, R.w));
            albedo = lerp(albedo, float3(0.30, 0.24, 0.18), saturate(S.w));
            albedo = lerp(albedo, lerp(float3(0.64, 0.50, 0.34), float3(0.55, 0.45, 0.33), tint), saturate(-S.w));   // dirt
            float3 fireL = all(uA >= 0) && all(uA <= 1) ? _MB_FL.SampleLevel(mb_linear_clamp_sampler, uA, 0).rgb * _MB_Look3.x : 0;
            float3 inS = albedo * (sunL * Lt.x + _MB_Ambient.rgb * (0.25 + 0.75 * Lt.y) + fireL);
            float3 E = MBBlackbody(heat) * _MB_Look.w * pow(heat, _MB_Look3.z);
            float3 src = (sigS * (1.0 - heat) * inS + (sigF + sigS * heat) * E + sigD * srcD) / sigT;
            float a = 1.0 - exp(-sigT * stepLen);
            L += T * a * src;
            wSum += T * a; dSum += T * a * dot(P - camAbs, dir);
            T *= 1.0 - a;
            if (T < 0.003) break;
        }
        gasDist = wSum > 1e-4 ? dSum / wSum : 0;
        return float4(L * GetCurrentExposureMultiplier(), T);
    }

    float4 FullScreenPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float gd;
        return March(varyings.positionCS.xy, varyings.positionCS.xy, gd);
    }

    // half resolution: each half-size texel marches the top-left pixel of its 2x2 block
    // several shots are drawn one after another into the same half-size target, far ones first: colour composites
    // as 'this shot over what is behind', the gas distance (for reprojection) is mixed in by this shot's coverage
    struct HalfOut { float4 color : SV_Target0; float4 dist : SV_Target1; };
    HalfOut HalfPass(Varyings varyings)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float2 hp = floor(varyings.positionCS.xy);
        HalfOut o;
        float d;
        o.color = March(hp * 2.0 + 0.5, hp, d);
        o.dist = float4(d, 0, 0, d > 0 ? 1.0 - o.color.a : 0.0);
        return o;
    }

    // temporal reuse at half size: last frame's result where this texel's gas was then (camera motion), clamped to
    // the spread of this frame's 3x3 neighbourhood (so fast-changing blast does not ghost), blended in
    float4 TemporalPass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        int2 h = int2(floor(varyings.positionCS.xy));
        int2 hmax = int2(_ScreenSize.xy * 0.5) - 1;
        float4 c = LOAD_TEXTURE2D_X(_MB_Half, h);
        if (_MB_TParams.x < 0.5) return c;
        float4 m1 = 0, m2 = 0;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float4 n = LOAD_TEXTURE2D_X(_MB_Half, clamp(h + int2(x, y), 0, hmax));
            m1 += n; m2 += n * n;
        }
        m1 /= 9.0; m2 /= 9.0;
        float4 sd = sqrt(max(m2 - m1 * m1, 0));
        float4 lo = m1 - 1.25 * sd, hi = m1 + 1.25 * sd;
        // where was this texel's gas on screen last frame
        float2 pixel = h * 2.0 + 0.5;
        float depth = LoadCameraDepth(pixel);
        PositionInputs pi = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float3 camAbs = GetAbsolutePositionWS(float3(0, 0, 0));
        float3 pAbs = GetAbsolutePositionWS(pi.positionWS);
        float3 dir = normalize(pAbs - camAbs);
        float dist = LOAD_TEXTURE2D_X(_MB_HalfDist, h).x;
        if (dist <= 0) dist = depth == UNITY_RAW_FAR_CLIP_VALUE ? 1e4 : length(pAbs - camAbs);
        float4 pc = mul(_MB_PrevVP, float4(camAbs + dir * dist, 1));
        if (pc.w <= 1e-4) return c;
        float2 ndc = pc.xy / pc.w;
        float2 prevPix = float2(ndc.x * 0.5 + 0.5, 0.5 - 0.5 * ndc.y) * _ScreenSize.xy;   // full-size pixels
        float2 hpPrev = (prevPix - 0.5) * 0.5;                                               // half-size texel coords
        if (any(hpPrev < -0.5) || any(hpPrev > float2(hmax) + 0.5)) return c;
        float2 b = floor(hpPrev), f = hpPrev - b;
        int2 i0 = clamp(int2(b), 0, hmax), i1 = clamp(int2(b) + 1, 0, hmax);
        float4 hist = lerp(lerp(LOAD_TEXTURE2D_X(_MB_Hist, i0), LOAD_TEXTURE2D_X(_MB_Hist, int2(i1.x, i0.y)), f.x),
                           lerp(LOAD_TEXTURE2D_X(_MB_Hist, int2(i0.x, i1.y)), LOAD_TEXTURE2D_X(_MB_Hist, i1), f.x), f.y);
        hist = clamp(hist, lo, hi);
        return lerp(hist, c, _MB_TParams.y);
    }

    // back to full size: the four nearest half-size texels, weighted by how close the depth they marched to is to
    // this pixel's depth (smoke stays off the tank's silhouette); no match at all -> the closest in depth
    float4 UpsamplePass(Varyings varyings) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
        float2 px = floor(varyings.positionCS.xy);
        float z = LinearEyeDepth(LoadCameraDepth(px), _ZBufferParams);
        float2 hp = px * 0.5 - 0.25;
        float2 b = floor(hp), f = hp - b;
        int2 hmax = int2(_ScreenSize.xy * 0.5) - 1;
        float4 acc = 0; float wsum = 0;
        float4 best = float4(0, 0, 0, 1); float bestD = 1e9;
        [unroll] for (int j = 0; j < 4; j++)
        {
            int2 o = int2(j & 1, j >> 1);
            int2 h = clamp(int2(b) + o, 0, hmax);
            float zh = LinearEyeDepth(LoadCameraDepth(h * 2), _ZBufferParams);
            float4 c = LOAD_TEXTURE2D_X(_MB_Half, h);
            float dz = abs(zh - z) / max(z, 1e-3);
            float wb = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
            float w = wb * exp(-dz * 40.0);
            acc += c * w; wsum += w;
            if (dz < bestD) { bestD = dz; best = c; }
        }
        return wsum > 1e-3 ? acc / wsum : best;
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "MuzzleBlast"
            ZWrite Off
            ZTest Always
            Blend One SrcAlpha, Zero One
            Cull Off
            HLSLPROGRAM
                #pragma fragment FullScreenPass
            ENDHLSL
        }
        Pass
        {
            Name "MarchHalf"
            ZWrite Off
            ZTest Always
            Blend 0 One SrcAlpha, Zero SrcAlpha
            Blend 1 SrcAlpha OneMinusSrcAlpha
            Cull Off
            HLSLPROGRAM
                #pragma fragment HalfPass
            ENDHLSL
        }
        Pass
        {
            Name "Upsample"
            ZWrite Off
            ZTest Always
            Blend One SrcAlpha
            Cull Off
            HLSLPROGRAM
                #pragma fragment UpsamplePass
            ENDHLSL
        }
        Pass
        {
            Name "Temporal"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off
            HLSLPROGRAM
                #pragma fragment TemporalPass
            ENDHLSL
        }
    }
    Fallback Off
}
