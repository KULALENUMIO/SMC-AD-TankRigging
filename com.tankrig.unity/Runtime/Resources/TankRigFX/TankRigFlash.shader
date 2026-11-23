// Particles of the tank effects (gun flashes, exhaust, dust, track spill, flipbook sprites), drawn by GunFlashPass:
// one procedural quad per particle, premultiplied alpha (emissive layers output alpha 0 = additive, smoke outputs its
// coverage), soft where it meets surfaces. Radiances are physical (nits) and go through the camera exposure.
//
// Resolution (GunFlashPass.halfResolution): the soft, big particles are drawn into a half-size target (colour +
// remaining transmittance in alpha) against a half-size depth that keeps the nearest and the farthest depth of each
// 2x2 block in a checkerboard; a depth-aware 4-tap upsample then puts them over the scene, so they stay off the tank's
// silhouette instead of being blurred over it. Small sharp ones (flash core, flame tongues, sparks, soil grains) are
// drawn afterwards at full resolution. _GF_Mode: 0 = everything, 1 = low-res kinds only, 2 = sharp kinds only.
//
// Sprite particles (type 10) read a flipbook (texture arrays A: r coverage / thickness, g temperature, b detail;
// B: rg normal) and are lit one of four ways (extra.x): 0 uniform, 1 sphere normal, 2 normal map, 3 "scatter":
// coverage as optical depth, a Henyey-Greenstein lobe toward the sun, two-octave self-shadowing, a backlit rim and
// self-occluded sky light - the usual way to make a lit card read as a volume of smoke.
//
// Particle shadows (Fourier opacity map): the soft particles are drawn from the sun into a 256 x 256 orthographic map
// that keeps the optical depth along each sun ray as a Fourier series in depth (a0, a1, b1, a2, b2; two RGBA16F
// targets, added up). From it any point gets the transmittance toward the sun: the particles shade each other's
// sunlit side, and a full-screen pass darkens the scene (ground, tank) under them.
// Heat haze: hot particles (exhaust near the outlet, flame, fireball) draw a screen offset into a quarter-size
// target; a full-screen pass then reads a copy of the scene through it.
Shader "Hidden/TankRig/Flash"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "MBNoise.hlsl"

    struct GP
    {
        float3 pos; float type;
        float3 axis; float length;
        float width; float age01; float seed; float intensity;
        float3 vel; float life;
        float age; float delay; float grow; float drag;
        float4 extra;    // sprites: x lighting mode, y flipbook slice, z emission (nits at full temperature), w erosion (1 = alpha threshold over life)
        float4 extra2;   // sprites: x rotation (rad), y soft-depth distance (m), z near-camera fade distance (m), w -
    };
    StructuredBuffer<GP> _GF_Particles;
    float4 _GF_SunDir, _GF_SunLux, _GF_Ambient;
    float _GF_Mode;
    TEXTURE2D_X(_GF_HalfDepth);    // linear eye depth, half size, checkerboard nearest / farthest
    TEXTURE2D_X(_GF_Half);         // the low-res particles: premultiplied colour, transmittance in alpha
    TEXTURE2D_ARRAY(_GF_AtlasA); SAMPLER(sampler_GF_AtlasA);
    TEXTURE2D_ARRAY(_GF_AtlasB); SAMPLER(sampler_GF_AtlasB);
    TEXTURE2D_ARRAY(_GF_AtlasC); SAMPLER(sampler_GF_AtlasC);   // motion to the next frame (rg, 0.5 = none)
    float _GF_Flow;                                            // its range (card uv); 0 = no motion texture: cross-fade
    float4 _GF_Grid[16];      // per slice: frames x, frames y, frame count, -
    float4 _GF_Light[16];     // per slice: normal softness (wrap), translucency, sphere power, max opacity
    float4 _GF_Scatter[16];   // per slice: HG g, sun self-shadow, rim strength, ambient self-shadow
    float4 _GF_Look[16];      // per slice: direction-ambient min, thin fade, rim opacity, temperature power

    // ---- Fourier opacity map (particle shadows)
    TEXTURE2D(_GF_Fom0); TEXTURE2D(_GF_Fom1);
    float4 _GF_FomO;   // xyz origin (absolute world, the map's centre at its near plane), w 1 = on
    float4 _GF_FomR;   // xyz right axis, w width (m)
    float4 _GF_FomU;   // xyz up axis, w height (m)
    float4 _GF_FomF;   // xyz direction the sunlight travels, w depth (m)
    float4 _GF_FomK;   // x scene darkening (the sun's share of the light), y particle self-shadow, z bias toward the sun (m)

    float3 FomUVZ(float3 P)
    {
        float3 d = P - _GF_FomO.xyz;
        return float3(dot(d, _GF_FomR.xyz) / _GF_FomR.w + 0.5, dot(d, _GF_FomU.xyz) / _GF_FomU.w + 0.5, dot(d, _GF_FomF.xyz) / _GF_FomF.w);
    }
    // transmittance toward the sun at an absolute world position
    float FomT(float3 P)
    {
        if (_GF_FomO.w < 0.5) return 1.0;
        float3 q = FomUVZ(P - _GF_FomF.xyz * _GF_FomK.z);
        if (any(q.xy < 0.0) || any(q.xy > 1.0) || q.z <= 0.0) return 1.0;
        float2 uv = q.xy;
    #if UNITY_UV_STARTS_AT_TOP
        uv.y = 1.0 - uv.y;
    #endif
        float4 c0 = SAMPLE_TEXTURE2D_LOD(_GF_Fom0, s_linear_clamp_sampler, uv, 0);
        float c1 = SAMPLE_TEXTURE2D_LOD(_GF_Fom1, s_linear_clamp_sampler, uv, 0).x;
        float z = saturate(q.z);
        float tau = c0.x * 0.5 * z
                  + (c0.y * sin(TWO_PI * z) + c0.z * (1.0 - cos(TWO_PI * z))) / TWO_PI
                  + (c0.w * sin(2.0 * TWO_PI * z) + c1 * (1.0 - cos(2.0 * TWO_PI * z))) / (2.0 * TWO_PI);
        tau = clamp(tau, 0.0, c0.x * 0.5);   // never more than everything along the ray (the series rings)
        return exp(-tau);
    }
    // the sun term of a particle: lit through the ones between it and the sun
    float FomSelf(float3 P) { return lerp(1.0, FomT(P), _GF_FomK.y); }

    // ---- heat haze
    TEXTURE2D_X(_GF_Haze);       // quarter size: rg screen offset (uv), b weight
    TEXTURE2D_X(_GF_SceneCopy);  // the scene before the haze
    float _GF_HazeK;             // strength

    struct V2F
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        nointerpolation float4 info : TEXCOORD1;   // type, age01, seed, intensity
        nointerpolation float4 info2 : TEXCOORD3;  // axis (exhaust: x = soot; dust, grains, sprites: albedo)
        float viewDepth : TEXCOORD2;
        nointerpolation float4 extra : TEXCOORD4;
        nointerpolation float4 extra2 : TEXCOORD5;
        nointerpolation float4 frame : TEXCOORD6;  // frame A, frame B, blend, flip bits
        float3 wpos : TEXCOORD7;                   // absolute world position of the fragment
        nointerpolation float2 rot : TEXCOORD8;    // the card's rotation (cos, sin): turns its normals with it
        nointerpolation float elev : TEXCOORD9;    // oriented sprites: their direction's elevation (rad)
    };

    float3 GFBlackbody(float h)
    {
        const float3 c0 = float3(1.0, 0.10, 0.0), c1 = float3(1.0, 0.30, 0.02), c2 = float3(1.0, 0.52, 0.08);
        const float3 c3 = float3(1.0, 0.74, 0.25), c4 = float3(1.0, 0.92, 0.70);
        h = saturate(h) * 4.0;
        if (h < 1.0) return lerp(c0, c1, h);
        if (h < 2.0) return lerp(c1, c2, h - 1.0);
        if (h < 3.0) return lerp(c2, c3, h - 2.0);
        return lerp(c3, c4, h - 3.0);
    }

    // sharp kinds stay at full resolution: flash core, flame tongues, disc, sparks, soil grains, mud streaks
    bool IsSharp(int t) { return t <= 3 || t == 6 || t == 9 || t == 11; }

    V2F VertP(uint vid : SV_VertexID)
    {
        static const float2 cs[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };
        GP p = _GF_Particles[vid / 6];
        float2 c = cs[vid % 6];
        int t = (int)(p.type + 0.5);
        V2F o = (V2F)0;
        int mode = (int)(_GF_Mode + 0.5);
        if ((mode == 1 && IsSharp(t)) || (mode == 2 && !IsSharp(t))) { o.positionCS = float4(0, 0, -2, 1); return o; }   // not in this pass: off screen
        float size = p.width * (1.0 + p.grow * ((t == 8 && p.extra.y > 0.5) ? sqrt(p.age01) : p.age01));   // billowing dust: fast, then slow
        float3 camAbs = GetAbsolutePositionWS(float3(0, 0, 0));
        float3 P;
        if (t == 1 || t == 2 || t == 6 || t == 11 || t == 12)
        {
            // streak from its root along the axis, turned to face the camera around that axis
            float3 toCam = normalize(camAbs - p.pos);
            float3 side = cross(p.axis, toCam);
            side = length(side) > 1e-4 ? normalize(side) : UNITY_MATRIX_V[0].xyz;
            float len = t == 6 ? max(length(p.vel) * 0.004, 0.03)                 // sparks: a short streak (longer reads as a tracer)
                      : t == 11 ? max(length(p.vel) * p.extra.w, size * 2.0)          // mud: stretched by its speed (axis back along it)
                      : t == 12 ? max(length(p.vel) * p.extra.w, size * 5.0)          // jet smoke: the same, never shorter than a short streak
                      : p.length;
            P = p.pos + p.axis * ((c.y * 0.5 + 0.5) * len) + side * (c.x * size);
            o.uv = float2(c.x, c.y * 0.5 + 0.5);   // x across (-1..1), y along (0 root .. 1 tip)
        }
        else
        {
            // billboard, turned by the particle's seed (sprites: plus their own rotation) so repeats do not repeat
            float a = t == 10 ? p.extra2.x : p.seed * 2.39996;   // sprites: the emitter's angle only (baked cards stand upright, ground down)
            float asp = t == 10 ? _GF_Grid[(int)(p.extra.y + 0.5)].w : 0.0;   // sprites: the flipbook's height / width (0 = square)
            float2 cc = float2(c.x, c.y * (asp > 0.0 ? asp : 1.0));
            // oriented sprites (a card baked side-on along a direction, extra2.w = +-(10 + yaw), elevation in 'delay'):
            // seen down that direction the card narrows towards square (+ only; - keeps its width: round things like the
            // ground dust); a raised direction tilts the card the way it runs on screen (not when seen along it)
            if (t == 10 && abs(p.extra2.w) > 1.0)
            {
                float yaw = abs(p.extra2.w) - 10.0, el = p.delay;
                float3 dir = float3(cos(yaw) * cos(el), sin(el), sin(yaw) * cos(el));
                float sinA = length(cross(dir, normalize(camAbs - p.pos)));
                // (- cards narrow too once their direction is raised: seen along a gun pointing up, a cloud baked lying
                //  along the ground would otherwise read as a flat band in the sky)
                float nw = p.extra2.w > 0.0 ? 1.0 : saturate(el / 0.6);
                if (asp > 0.0) cc.x *= lerp(1.0, lerp(asp, 1.0, sinA), nw);
                // the tilt the raise adds on screen: the raised direction's screen angle less the level one's (a level
                // direction seen from above runs up the screen too - that is no reason to tilt)
                float3 flat = float3(cos(yaw), 0, sin(yaw));
                float px = dot(dir, UNITY_MATRIX_V[0].xyz), py = dot(dir, UNITY_MATRIX_V[1].xyz);
                float fx = dot(flat, UNITY_MATRIX_V[0].xyz), fy = dot(flat, UNITY_MATRIX_V[1].xyz);
                float lp = max(length(float2(px, py)), 1e-4);
                float tilt = (atan2(py, abs(px)) - atan2(fy, max(abs(fx), 1e-4))) * smoothstep(0.15, 0.45, abs(px) / lp);
                a += px < 0.0 ? -tilt : tilt;   // mirrored cards turn the other way
            }
            float2 r = float2(cc.x * cos(a) - cc.y * sin(a), cc.x * sin(a) + cc.y * cos(a));
            P = p.pos + (UNITY_MATRIX_V[0].xyz * r.x + UNITY_MATRIX_V[1].xyz * r.y) * size;
            o.uv = c;
            o.rot = float2(cos(a), sin(a));
        }
        float3 rws = GetCameraRelativePositionWS(P);
        o.positionCS = TransformWorldToHClip(rws);
        o.viewDepth = -TransformWorldToView(rws).z;
        o.wpos = P;
        o.info = float4(t, p.age01, p.seed, p.intensity);
        o.info2 = float4(p.axis, 0);
        o.extra = p.extra;
        o.extra2 = p.extra2;
        o.elev = (t == 10 && abs(p.extra2.w) > 1.0) ? p.delay : 0.0;
        if (t == 10)
        {
            int slice = (int)(p.extra.y + 0.5);
            float4 g = _GF_Grid[slice];
            float frames = max(g.z, 1.0);
            float f = clamp(p.length, 0.0, frames - 1.0);   // the source sets the frame (life curve, random start)
            float fa = floor(f);
            float flip = (float)(uint)(frac(p.seed * 7.31) * 4.0);   // random mirroring in x / y (bits)
            if (abs(p.extra2.w) > 1.0)
            {
                // oriented: never upside down; mirrored when its direction points left on screen - not at once: around
                // looking along it the two ways cross-fade (w = how much of the unmirrored one), stored as -(1 + w)
                float yaw = abs(p.extra2.w) - 10.0, el = p.delay;
                float3 dir3 = float3(cos(yaw) * cos(el), sin(el), sin(yaw) * cos(el));
                float px = dot(dir3, UNITY_MATRIX_V[0].xyz), py = dot(dir3, UNITY_MATRIX_V[1].xyz);
                float d = px / max(length(float2(px, py)), 1e-4);
                flip = -(1.0 + smoothstep(-0.35, 0.35, d));
            }
            o.frame = float4(fa, min(fa + 1.0, frames - 1.0), f - fa, flip);
        }
        return o;
    }

    float2 FrameUV(float2 uv01, float frame, int slice)
    {
        float4 g = _GF_Grid[slice];
        float fx = max(g.x, 1.0), fy = max(g.y, 1.0);
        float2 cell = float2(fmod(frame, fx), floor(frame / fx));
        return (cell + uv01) / float2(fx, fy);
    }

    // the flipbook at a card position, flow-blended: frame A slid forward along its motion, frame B slid back,
    // so the gas moves between them (uvA / uvB: where the two frames were read)
    float4 SpriteA(float2 uv01, float4 frame, int slice, out float2 uvA, out float2 uvB)
    {
        float2 mo = 0;
        if (_GF_Flow > 0.0) mo = (SAMPLE_TEXTURE2D_ARRAY(_GF_AtlasC, sampler_GF_AtlasC, FrameUV(uv01, frame.x, slice), slice).rg * 2.0 - 1.0) * _GF_Flow;
        uvA = clamp(uv01 - mo * frame.z, 0.001, 0.999); uvB = clamp(uv01 + mo * (1.0 - frame.z), 0.001, 0.999);
        float4 A0 = SAMPLE_TEXTURE2D_ARRAY(_GF_AtlasA, sampler_GF_AtlasA, FrameUV(uvA, frame.x, slice), slice);
        float4 A1 = SAMPLE_TEXTURE2D_ARRAY(_GF_AtlasA, sampler_GF_AtlasA, FrameUV(uvB, frame.y, slice), slice);
        return lerp(A0, A1, frame.z);
    }

    // ---- flipbook sprite, lit
    float4 ShadeSprite(V2F i, float sceneZ)
    {
        int slice = (int)(i.extra.y + 0.5);
        int lmode = (int)(i.extra.x + 0.5);
        float2 q = i.uv;                                   // -1..1 across the card (already rotated with it)
        // oriented cards (frame.w < 0) cross-fade between their two mirror images; others use their flip bits
        bool oriented = i.frame.w < -0.5;
        float wKeep = oriented ? saturate(-i.frame.w - 1.0) : 1.0;
        uint flip = oriented ? (wKeep < 0.5 ? 1u : 0u) : (uint)(i.frame.w + 0.5);
        float2 uv01 = q * 0.5 + 0.5;
        if (!oriented && (flip & 1u)) uv01.x = 1.0 - uv01.x;
        if (!oriented && (flip & 2u)) uv01.y = 1.0 - uv01.y;
        float2 uvA, uvB;
        float4 A = SpriteA(uv01, i.frame, slice, uvA, uvB);
        if (oriented)
        {
            float2 uvAm, uvBm;
            float4 Am = SpriteA(float2(1.0 - uv01.x, uv01.y), i.frame, slice, uvAm, uvBm);
            A = lerp(Am, A, wKeep);
            if (wKeep < 0.5) { uvA = uvAm; uvB = uvBm; }   // the normal map follows the one that dominates
        }
        float4 L = _GF_Light[slice], S = _GF_Scatter[slice], K = _GF_Look[slice];
        float opacity = i.info.w;
        // coverage: plain, or an alpha threshold that eats the thin parts first as the opacity drops (erosion)
        float a = i.extra.w > 0.5 ? saturate(A.r - (1.0 - opacity)) : A.r * opacity;
        // a card baked against the ground (flat-bottomed where the gas met it), raised into the air: no hard bottom edge
        if (i.extra2.w < 0.0) a *= lerp(1.0, smoothstep(-1.0, -0.3, q.y), saturate(i.elev / 0.35));   // (- cards: the cloud)
        // fades: where it meets surfaces, and near the camera
        float soft = saturate((sceneZ - i.viewDepth) / max(i.extra2.y, 0.05));
        float nearF = saturate((i.viewDepth - 0.3) / max(i.extra2.z, 0.05));
        a *= soft * nearF;
        if (a < 1e-3 && i.extra.z <= 0.0) discard;

        // normals: a sphere bulging toward the camera (also the ambient-occlusion proxy), or the flipbook's normal map
        // the card's own axes on screen (it is turned by i.rot): right, up, and toward the camera
        float3 Vr0 = UNITY_MATRIX_V[0].xyz, Vu0 = UNITY_MATRIX_V[1].xyz, Vc = UNITY_MATRIX_V[2].xyz;
        float3 Vr = Vr0 * i.rot.x + Vu0 * i.rot.y, Vu = -Vr0 * i.rot.y + Vu0 * i.rot.x;
        float r2 = dot(q, q);
        float3 nS = float3(q.x, q.y, sqrt(saturate(1.0 - r2)));
        nS = normalize(lerp(float3(0, 0, 1), nS, L.z));
        float3 sphereN = normalize(Vr * nS.x + Vu * nS.y + Vc * nS.z);
        float3 n = sphereN;
        float nda = 1.0;
        if (lmode == 0) n = float3(0, 1, 0);
        if (lmode == 2)
        {
            float2 B0 = SAMPLE_TEXTURE2D_ARRAY(_GF_AtlasB, sampler_GF_AtlasB, FrameUV(uvA, i.frame.x, slice), slice).rg;
            float2 B1 = SAMPLE_TEXTURE2D_ARRAY(_GF_AtlasB, sampler_GF_AtlasB, FrameUV(uvB, i.frame.y, slice), slice).rg;
            float2 nb = lerp(B0, B1, i.frame.z) * 2.0 - 1.0;
            if (flip & 1u) nb.x = -nb.x;
            if (flip & 2u) nb.y = -nb.y;
            float3 nt = float3(nb, sqrt(saturate(1.0 - dot(nb, nb))));
            n = normalize(Vr * nt.x + Vu * nt.y + Vc * nt.z);
            nda = saturate(dot(sphereN, n));
        }
        float3 Ls = _GF_SunDir.xyz;                          // toward the sun
        float3 toPart = normalize(i.wpos - GetAbsolutePositionWS(float3(0, 0, 0)));   // camera -> particle
        float ndl;
        if (lmode == 3)
        {
            // scatter: coverage as optical depth; forward scattering toward the sun; self-shadow; rim; self-occluded sky
            float tau = -log2(max(1.0 - a * L.w, 1e-4));
            float T = exp2(-tau);
            float g = S.x, cosT = dot(toPart, -Ls);           // light travels sun -> particle -> camera
            float hg = (1.0 + g) * rsqrt(max(1.0 + g * g - 2.0 * g * cosT, 1e-4));
            ndl = lerp(hg * hg * hg, 1.0, L.x) * (1.0 - g);   // normalised so g = 0 gives 1
            float tauSun = tau * S.y * saturate(dot(sphereN, Ls) * 0.5 + 0.5);
            ndl *= exp2(-tauSun) * 0.7 + exp2(-0.3 * tauSun) * 0.3;
            ndl *= FomSelf(i.wpos);   // the other particles between it and the sun
            ndl *= lerp(1.0 - T, 1.0, K.y);                   // thin parts do not glow at their outline
            float rim = smoothstep(K.z * 0.5, K.z, a) * smoothstep(K.z * 1.6, K.z, a) * saturate(cosT) * saturate(cosT);
            ndl += rim * S.z;
            float ambE = (0.25 * (1.0 - T * T) + 0.3466 * tau * T) / max(1.0 - T, 1e-3);
            nda *= lerp(1.0, saturate(ambE), S.w);
            a = (1.0 - T) * step(1e-3, opacity);
        }
        else
        {
            ndl = saturate(dot(n, Ls));
            ndl = ndl * (1.0 - L.x) + L.x;                               // wrap
            ndl = lerp(ndl, 1.0, saturate(dot(toPart, Ls)) * L.y);       // backlit (sun behind it): the card glows through
            ndl *= FomSelf(i.wpos);
        }
        nda *= lerp(K.x, 1.0, saturate(sphereN.y * 0.5 + 0.5));           // the sky lights the top more than the bottom
        float3 albedo = saturate(i.info2.xyz) * (0.85 + 0.3 * A.b);
        float3 lit = albedo * (_GF_SunLux.rgb * ndl / PI + _GF_Ambient.rgb * nda);
        float temp = saturate(A.g);
        float3 E = GFBlackbody(temp) * (i.extra.z * pow(temp, max(K.w, 0.1)));
        float ex = GetCurrentExposureMultiplier();
        return float4((lit * a + E * saturate(A.r * 1.5) * soft * nearF) * ex, a);
    }

    float4 Shade(V2F i, float sceneZ)
    {
        int t = (int)(i.info.x + 0.5);
        float age = i.info.y, seed = i.info.z, I = i.info.w;
        float2 uv = i.uv;
        float ex = GetCurrentExposureMultiplier();
        float soft = saturate((sceneZ - i.viewDepth) / 0.3);
        float flick = pow(saturate(1.0 - age), 1.5);
        if (t == 10) return ShadeSprite(i, sceneZ);
        if (t == 0)   // primary flash: white-hot glow at the muzzle
        {
            float r = length(uv);
            float win = smoothstep(1.0, 0.55, r);   // nothing at the quad's edge (else the square shows at these radiances)
            float g = (exp(-r * r * 12.0) * (0.75 + 0.5 * MBNoise(float3(uv * 3.0, seed))) + 0.08 * exp(-r * r * 4.0)) * win;
            float3 col = lerp(float3(1.0, 0.72, 0.38), float3(1.0, 0.96, 0.88), saturate(g));
            return float4(col * (I * g * flick * ex), 0);
        }
        if (t == 1 || t == 2)   // flame tongue: narrow root, bulge, ragged taper; hot at the root, orange at the tip
        {
            float x = uv.y, y = uv.x;
            float w = saturate(pow(x, 0.35) * pow(1.0 - x, 0.8) * 2.0);
            float n = 0.65 * MBFbm(float3(x * 4.5 - seed, y * 1.6, seed * 0.37)) + 0.35 * MBNoise(float3(x * 11.0, y * 3.0, seed + 5.0));
            float edge = w * (0.45 + 0.8 * n);
            float m = smoothstep(edge, edge * 0.25, abs(y));
            m *= smoothstep(0.0, 0.06, x);
            float heat = saturate(1.05 - x * (t == 1 ? 0.9 : 1.1) - (1.0 - n) * 0.25);
            float3 col = GFBlackbody(heat);
            return float4(col * (I * m * pow(heat, 1.5) * flick * ex), 0);
        }
        if (t == 3)   // intermediate flash: small reddish disc
        {
            float r = length(uv);
            float g = exp(-pow(r - 0.35, 2.0) * 25.0) + 0.6 * exp(-r * r * 9.0);
            return float4(float3(1.0, 0.38, 0.12) * (I * g * flick * ex), 0);
        }
        if (t == 4)   // secondary flash: lumpy burning gas, hot then cooling, a little soot as it dies
        {
            float r = length(uv);
            float n = MBFbm(float3(uv * 2.2 + seed, seed * 0.5 + age * 1.5));
            float d = smoothstep(1.0, 0.45, r + (n - 0.5) * 0.8) * soft;
            float temp = pow(saturate(1.0 - age), 1.2) * (0.55 + 0.6 * n) * (1.0 - 0.45 * r);
            float3 E = GFBlackbody(temp) * (I * pow(saturate(temp), 3.5));   // glow falls steeply as the gas cools (no red blob at the end)
            float soot = d * 0.3 * smoothstep(0.35, 1.0, age);
            return float4(E * d * ex, soot);
        }
        if (t == 7)   // engine exhaust: thin, soft, thins out almost at once; grey-blue haze .. black soot
        {
            float r = length(uv);
            float n = MBFbm(float3(uv * 1.4 + seed, seed * 0.3 + age * 0.9));
            float d = smoothstep(1.0, 0.1, r + (n - 0.5) * 1.0);
            float a = saturate(I * d * pow(saturate(1.0 - age), 1.8) * smoothstep(0.0, 0.08, age) * soft);
            float3 nrm = normalize(UNITY_MATRIX_V[0].xyz * uv.x + UNITY_MATRIX_V[1].xyz * uv.y + UNITY_MATRIX_V[2].xyz * sqrt(saturate(1.0 - r * r)) * 0.8);
            float ndl = saturate(dot(nrm, _GF_SunDir.xyz) * 0.5 + 0.5);
            float soot = saturate(i.info2.x);
            float3 albedo = lerp(float3(0.60, 0.62, 0.68), float3(0.05, 0.045, 0.04), soot);
            // soot soaks up light inside the puff as well: its sunlit side stays dark
            float3 col = albedo * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI * (1.0 - 0.7 * soot) + _GF_Ambient.rgb * 0.9);
            return float4(col * a * ex, a);
        }
        if (t == 11)   // a streak of mud: an opaque lump at the head, thinning back along its motion (extra.xyz = albedo)
        {
            float x = uv.x, y = uv.y;   // across -1..1, along 0 head .. 1 tail
            float w = 1.0 - 0.6 * y;
            float d = smoothstep(1.0, 0.5, abs(x) / w) * smoothstep(1.0, 0.35, y) * smoothstep(0.0, 0.05, y + 0.05);
            float a = saturate(d * I * (1.0 - smoothstep(0.7, 1.0, age)));
            float ndl = saturate(0.35 + 0.65 * saturate(_GF_SunDir.y)) * (0.75 + 0.25 * sqrt(saturate(1.0 - x * x)));
            float3 col = saturate(i.extra.xyz) * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI + _GF_Ambient.rgb * 0.6);
            return float4(col * a * ex, a);
        }
        if (t == 9)   // a grain of soil: a small opaque speck the ground's colour (axis = albedo), lit
        {
            float r = length(uv);
            float d = smoothstep(1.0, 0.55, r + (MBNoise(float3(uv * 3.0, seed)) - 0.5) * 0.5);
            float3 nrm = normalize(UNITY_MATRIX_V[0].xyz * uv.x + UNITY_MATRIX_V[1].xyz * uv.y + UNITY_MATRIX_V[2].xyz * sqrt(saturate(1.0 - r * r)));
            float ndl = saturate(dot(nrm, _GF_SunDir.xyz));
            float3 col = saturate(i.info2.xyz) * (_GF_SunLux.rgb * ndl / PI + _GF_Ambient.rgb * 0.7);
            return float4(col * d * ex, d);
        }
        if (t == 8 && i.extra.y > 0.5)   // blast dust (billow mode): a lumpy, rolling cloud that breaks up into wisps as it goes
        {
            float r = length(uv);
            float tt = age;
            // the lumps grow out with the puff and roll up a little; domain warp for cauliflower folds
            float3 np = float3(uv * (1.7 - 0.6 * tt), seed * 0.37) + float3(0.0, -tt * 0.7, tt * 0.4);
            float2 warp = float2(MBNoise(np * 1.3 + 3.1), MBNoise(np * 1.3 + 7.7)) - 0.5;
            float n1 = MBFbm(np + float3(warp * 0.9, 0.0));
            float n2 = MBNoise(np * 4.3 + 11.0);
            float dens = (1.0 - r) * 1.5 + (n1 - 0.5) * 1.7 + (n2 - 0.5) * 0.4;
            float thr = tt * tt * 0.6;                                        // eaten from the thin parts as it dissipates
            float d = saturate((dens - thr) * 1.7) * smoothstep(1.0, 0.72, r);   // translucent inside, not a solid lump
            float softD = saturate((sceneZ - i.viewDepth) / 1.2);
            float hold = i.extra.x;
            float fade = hold > 0.0 ? 1.0 - smoothstep(hold, 1.0, tt) : 1.0;
            float a = saturate(I * d * fade * smoothstep(0.0, 0.03, tt) * softD);
            float3 V0 = UNITY_MATRIX_V[0].xyz, V1 = UNITY_MATRIX_V[1].xyz, V2 = UNITY_MATRIX_V[2].xyz;
            float3 nrm = normalize(V0 * (uv.x + warp.x * 0.9) + V1 * (uv.y + warp.y * 0.9) + V2 * sqrt(saturate(1.0 - r * r)) * 0.6);
            float ndl = saturate(dot(nrm, _GF_SunDir.xyz) * 0.65 + 0.35);
            float ao = lerp(0.5, 1.0, saturate(uv.y * 0.5 + 0.5)) * lerp(0.65, 1.0, n1);   // the underside and the folds are darker
            float3 albedo = saturate(i.info2.xyz) * (0.82 + 0.36 * n2);
            float3 col = albedo * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI + _GF_Ambient.rgb * 0.85 * ao);
            return float4(col * a * ex, a);
        }
        if (t == 12)   // jet smoke out of the bore: a soft streak stretched by its speed, bluish white, greying as it cools
        {
            float x = uv.x, y = uv.y;   // across -1..1, along 0 head .. 1 tail
            float n = MBFbm(float3(x * 1.4 + seed, y * 2.5 - age * 2.0, seed * 0.3));
            float d = smoothstep(1.0, 0.1, abs(x) + (n - 0.5) * 0.7) * smoothstep(0.0, 0.12, y) * smoothstep(1.0, 0.7, y);   // even along it: the streaks join
            float a = saturate(I * d * pow(saturate(1.0 - age), 1.3) * smoothstep(0.0, 0.05, age) * soft);
            float ndl = saturate(_GF_SunDir.y * 0.4 + 0.6);
            float3 albedo = lerp(float3(0.56, 0.58, 0.64), float3(0.48, 0.48, 0.50), saturate(age * 1.5));
            float3 col = albedo * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI + _GF_Ambient.rgb * 0.9);
            return float4(col * a * ex, a);
        }
        if (t == 8)   // track dust: a big soft puff the colour of the ground (axis = albedo), swells and thins out
        {
            float r = length(uv);
            float n = MBFbm(float3(uv * 1.3 + seed, seed * 0.3 + age * 0.5));
            float d = smoothstep(1.0, 0.15, r + (n - 0.5) * 0.9);
            float softD = saturate((sceneZ - i.viewDepth) / 1.2);   // big puffs: a wide soft fade where they meet the ground
            // fade: from birth (track dust), or held a while first (blast dust: extra.x = share of the life it holds)
            float fade = i.extra.x > 0.0 ? 1.0 - smoothstep(i.extra.x, 1.0, age) : pow(saturate(1.0 - age), 1.3);
            float a = saturate(I * d * fade * smoothstep(0.0, 0.05, age) * softD);
            float3 nrm = normalize(UNITY_MATRIX_V[0].xyz * uv.x + UNITY_MATRIX_V[1].xyz * uv.y + UNITY_MATRIX_V[2].xyz * sqrt(saturate(1.0 - r * r)) * 0.8);
            float ndl = saturate(dot(nrm, _GF_SunDir.xyz) * 0.5 + 0.5);
            float3 albedo = saturate(i.info2.xyz) * (0.9 + 0.2 * n);
            float3 col = albedo * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI + _GF_Ambient.rgb * 0.9);
            return float4(col * a * ex, a);
        }
        if (t == 5)   // smoke puff: grey, lit by the sun and the sky, thins out as it swells
        {
            float r = length(uv);
            float n = MBFbm(float3(uv * 1.7 + seed, seed * 0.3 + age * 0.6));
            float d = smoothstep(1.0, 0.2, r + (n - 0.5) * 0.9);
            float a = saturate(I * d * pow(saturate(1.0 - age), 1.4) * smoothstep(0.0, 0.06, age) * soft);
            float3 nrm = normalize(UNITY_MATRIX_V[0].xyz * uv.x + UNITY_MATRIX_V[1].xyz * uv.y + UNITY_MATRIX_V[2].xyz * sqrt(saturate(1.0 - r * r)) * 0.8);
            float ndl = saturate(dot(nrm, _GF_SunDir.xyz) * 0.6 + 0.4);
            float3 albedo = lerp(float3(0.50, 0.48, 0.45), float3(0.44, 0.44, 0.46), n);   // propellant smoke: grey, a little brown
            float3 col = albedo * (_GF_SunLux.rgb * ndl * FomSelf(i.wpos) / PI + _GF_Ambient.rgb * 0.9);
            return float4(col * a * ex, a);
        }
        // spark: a short hot streak
        float x = uv.y, y = uv.x;
        float m = exp(-y * y * 4.0) * smoothstep(0.0, 0.4, x) * smoothstep(1.0, 0.6, x);
        return float4(float3(1.0, 0.6, 0.22) * (I * m * flick * ex), 0);
    }

    // full resolution: against the camera depth
    float4 FragFull(V2F i) : SV_Target
    {
        float sceneZ = LinearEyeDepth(LoadCameraDepth(i.positionCS.xy), _ZBufferParams);
        return Shade(i, sceneZ);
    }

    // half resolution: against the half-size depth (no hardware depth test: the soft fade is the depth test)
    float4 FragHalf(V2F i) : SV_Target
    {
        float sceneZ = LOAD_TEXTURE2D_X(_GF_HalfDepth, int2(i.positionCS.xy)).x;
        if (i.viewDepth > sceneZ + 0.02) discard;
        return Shade(i, sceneZ);
    }

    // ---- half-size depth: each texel keeps the nearest or the farthest of its 2x2 block, alternating (checkerboard)
    struct VF { float4 positionCS : SV_POSITION; };
    VF VertFS(uint vid : SV_VertexID)
    {
        VF o;
        float2 uv = float2((vid << 1) & 2, vid & 2);
        o.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
        return o;
    }
    float FragHalfDepth(VF i) : SV_Target
    {
        int2 h = int2(i.positionCS.xy);
        int2 p = h * 2;
        float d0 = LinearEyeDepth(LoadCameraDepth(p), _ZBufferParams), d1 = LinearEyeDepth(LoadCameraDepth(p + int2(1, 0)), _ZBufferParams);
        float d2 = LinearEyeDepth(LoadCameraDepth(p + int2(0, 1)), _ZBufferParams), d3 = LinearEyeDepth(LoadCameraDepth(p + int2(1, 1)), _ZBufferParams);
        float nearD = min(min(d0, d1), min(d2, d3)), farD = max(max(d0, d1), max(d2, d3));
        return ((h.x + h.y) & 1) ? nearD : farD;
    }

    // ---- composite: depth-aware 4-tap upsample of the half-size particles over the scene (scene * T + colour)
    float4 FragComposite(VF i) : SV_Target
    {
        int2 px = int2(i.positionCS.xy);
        float z = LinearEyeDepth(LoadCameraDepth(px), _ZBufferParams);
        float2 hp = (float2(px) + 0.5) * 0.5 - 0.5;
        float2 b = floor(hp), f = hp - b;
        int2 hmax = int2(_ScreenSize.xy * 0.5) - 1;
        float4 acc = 0; float wsum = 0;
        float4 best = float4(0, 0, 0, 1); float bestD = 1e9;
        [unroll] for (int j = 0; j < 4; j++)
        {
            int2 o = int2(j & 1, j >> 1);
            int2 h = clamp(int2(b) + o, 0, hmax);
            float zh = LOAD_TEXTURE2D_X(_GF_HalfDepth, h).x;
            float4 c = LOAD_TEXTURE2D_X(_GF_Half, h);
            float dz = abs(zh - z) / max(z, 1e-3);
            float wb = (o.x ? f.x : 1 - f.x) * (o.y ? f.y : 1 - f.y);
            float w = wb * (dz < 0.02 ? 1.0 : 0.0);           // only texels at this pixel's depth (2 %)
            acc += c * w; wsum += w;
            if (dz < bestD) { bestD = dz; best = c; }
        }
        return wsum > 1e-4 ? acc / wsum : best;                // none matches: the nearest depth
    }

    // ---- Fourier opacity map: the soft particles seen from the sun (cards facing it), optical depth as a series in depth
    bool CastsShadow(int t) { return t == 5 || t == 7 || t == 8 || t == 10; }
    struct V2S
    {
        float4 positionCS : SV_POSITION;
        float2 uv : TEXCOORD0;
        nointerpolation float4 info : TEXCOORD1;    // type, age01, seed, intensity
        nointerpolation float4 frame : TEXCOORD2;   // sprites: frame A, frame B, blend, slice
        float z : TEXCOORD3;                        // 0..1 through the map, from the sun
    };
    V2S VertShadow(uint vid : SV_VertexID)
    {
        static const float2 cs[6] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(-1, 1), float2(1, -1), float2(1, 1) };
        GP p = _GF_Particles[vid / 6];
        float2 c = cs[vid % 6];
        int t = (int)(p.type + 0.5);
        V2S o = (V2S)0;
        if (!CastsShadow(t) || p.intensity <= 0.0) { o.positionCS = float4(0, 0, -2, 1); return o; }
        float size = p.width * (1.0 + p.grow * ((t == 8 && p.extra.y > 0.5) ? sqrt(p.age01) : p.age01));   // billowing dust: fast, then slow
        float2 cc = c;
        if (t == 10)
        {
            int slice = (int)(p.extra.y + 0.5);
            float asp = _GF_Grid[slice].w;
            cc.y *= asp > 0.0 ? asp : 1.0;
            float frames = max(_GF_Grid[slice].z, 1.0);
            float f = clamp(p.length, 0.0, frames - 1.0), fa = floor(f);
            o.frame = float4(fa, min(fa + 1.0, frames - 1.0), f - fa, slice);
        }
        float3 P = p.pos + (_GF_FomR.xyz * cc.x + _GF_FomU.xyz * cc.y) * size;
        float3 q = FomUVZ(P);
        o.positionCS = float4(q.xy * 2.0 - 1.0, 0.5, 1.0);
        o.uv = c;
        o.info = float4(t, p.age01, p.seed, p.intensity * (t == 10 && p.extra.z > 0.0 ? 0.3 : 1.0));   // glowing gas (the fireball) hardly shades
        o.z = saturate(FomUVZ(p.pos).z);
        return o;
    }
    struct FomOut { float4 c0 : SV_Target0; float4 c1 : SV_Target1; };
    FomOut FragShadow(V2S i)
    {
        int t = (int)(i.info.x + 0.5);
        float age = i.info.y, I = i.info.w;
        float r = length(i.uv);
        float a;
        if (t == 10)
        {
            int slice = (int)(i.frame.w + 0.5);
            float2 uv01 = i.uv * 0.5 + 0.5;
            float A0 = SAMPLE_TEXTURE2D_ARRAY_LOD(_GF_AtlasA, sampler_GF_AtlasA, FrameUV(uv01, i.frame.x, slice), slice, 1).r;
            float A1 = SAMPLE_TEXTURE2D_ARRAY_LOD(_GF_AtlasA, sampler_GF_AtlasA, FrameUV(uv01, i.frame.y, slice), slice, 1).r;
            a = lerp(A0, A1, i.frame.z) * I;
        }
        else a = I * smoothstep(1.0, 0.2, r) * pow(saturate(1.0 - age), 1.4) * smoothstep(0.0, 0.06, age);
        float sigma = -log(1.0 - min(saturate(a), 0.98));
        if (sigma < 1e-4) discard;
        float z = i.z;
        FomOut o;
        o.c0 = 2.0 * sigma * float4(1.0, cos(TWO_PI * z), sin(TWO_PI * z), cos(2.0 * TWO_PI * z));
        o.c1 = float4(2.0 * sigma * sin(2.0 * TWO_PI * z), 0, 0, 0);
        return o;
    }

    // ---- the scene under the particles: its sunlit share darkened by their transmittance (multiplied in)
    float4 FragSceneShadow(VF i) : SV_Target
    {
        float d = LoadCameraDepth(int2(i.positionCS.xy));
        if (d == UNITY_RAW_FAR_CLIP_VALUE) return 1.0;
        PositionInputs pi = GetPositionInput(i.positionCS.xy, _ScreenSize.zw, d, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float m = lerp(1.0, FomT(GetAbsolutePositionWS(pi.positionWS)), _GF_FomK.x);
        return float4(m, m, m, 1.0);
    }

    // ---- heat haze: hot particles write a wobbling screen offset (quarter size), kept off what stands in front of them
    float4 FragHaze(V2F i) : SV_Target
    {
        int t = (int)(i.info.x + 0.5);
        float age = i.info.y, seed = i.info.z, I = i.info.w;
        float heat = 0.0;
        if (t == 7) heat = saturate(1.0 - age * 2.5) * 0.6;                         // exhaust, near the outlet
        else if (t == 4 || t == 0) heat = saturate(1.0 - age) * 1.0;                 // flame
        else if (t == 10 && i.extra.z > 0.0) heat = saturate(1.0 - age * 1.5) * 0.8; // the fireball
        if (heat <= 0.0) discard;
        float sceneZ = LinearEyeDepth(LoadCameraDepth(int2(i.positionCS.xy * 4.0)), _ZBufferParams);
        if (i.viewDepth > sceneZ) discard;
        float r = length(i.uv);
        float w = smoothstep(1.0, 0.3, r) * heat;
        float tt = _TimeParameters.x;
        float2 n = float2(MBNoise(float3(i.uv * 2.5 + seed, tt * 3.0)), MBNoise(float3(i.uv * 2.5 + seed + 7.3, tt * 3.0 + 3.1))) - 0.5;
        float amp = 0.006 / max(i.viewDepth * 0.15, 1.0);   // a few pixels, less far away
        return float4(n * amp * w, w, 0);
    }
    float4 FragHazeApply(VF i) : SV_Target
    {
        int2 px = int2(i.positionCS.xy);
        float3 h = LOAD_TEXTURE2D_X(_GF_Haze, px / 4).xyz;
        if (h.z < 1e-3) discard;
        float2 uv = (float2(px) + 0.5) * _ScreenSize.zw + h.xy * _GF_HazeK;
        return float4(SAMPLE_TEXTURE2D_X_LOD(_GF_SceneCopy, s_linear_clamp_sampler, uv * _RTHandleScale.xy, 0).rgb, 1.0);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass   // 0: particles at full resolution, depth-tested against the scene
        {
            Name "GunFlash"
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha, Zero One
            Cull Off
            HLSLPROGRAM
                #pragma vertex VertP
                #pragma fragment FragFull
            ENDHLSL
        }
        Pass   // 1: half-size depth
        {
            Name "HalfDepth"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
                #pragma vertex VertFS
                #pragma fragment FragHalfDepth
            ENDHLSL
        }
        Pass   // 2: particles at half resolution: colour premultiplied, alpha = remaining transmittance
        {
            Name "GunFlashHalf"
            ZWrite Off ZTest Always Cull Off
            Blend One OneMinusSrcAlpha, Zero OneMinusSrcAlpha
            HLSLPROGRAM
                #pragma vertex VertP
                #pragma fragment FragHalf
            ENDHLSL
        }
        Pass   // 3: composite over the scene
        {
            Name "Composite"
            ZWrite Off ZTest Always Cull Off
            Blend One SrcAlpha
            HLSLPROGRAM
                #pragma vertex VertFS
                #pragma fragment FragComposite
            ENDHLSL
        }
        Pass   // 4: Fourier opacity map (two targets, added up)
        {
            Name "FOM"
            ZWrite Off ZTest Always Cull Off
            Blend 0 One One
            Blend 1 One One
            HLSLPROGRAM
                #pragma vertex VertShadow
                #pragma fragment FragShadow
            ENDHLSL
        }
        Pass   // 5: the scene darkened under the particles
        {
            Name "SceneShadow"
            ZWrite Off ZTest Always Cull Off
            Blend DstColor Zero
            HLSLPROGRAM
                #pragma vertex VertFS
                #pragma fragment FragSceneShadow
            ENDHLSL
        }
        Pass   // 6: heat haze offsets (quarter size, added up)
        {
            Name "Haze"
            ZWrite Off ZTest Always Cull Off
            Blend One One
            HLSLPROGRAM
                #pragma vertex VertP
                #pragma fragment FragHaze
            ENDHLSL
        }
        Pass   // 7: the scene read through the haze
        {
            Name "HazeApply"
            ZWrite Off ZTest Always Cull Off
            Blend Off
            HLSLPROGRAM
                #pragma vertex VertFS
                #pragma fragment FragHazeApply
            ENDHLSL
        }
    }
    Fallback Off
}
