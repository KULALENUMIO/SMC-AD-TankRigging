// Shared hash / noise helpers (compute sim + raymarch shader). Procedural only: no textures.
// Gradient noise with a quintic fade, octaves rotated against each other: value noise and unrotated octaves
// print the lattice into the smoke as box-shaped blocks.
#ifndef MB_NOISE_INCLUDED
#define MB_NOISE_INCLUDED

static const float3x3 MB_ROT = float3x3(0.00, 0.80, 0.60, -0.80, 0.36, -0.48, -0.60, -0.48, 0.64);

float MBHash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return frac((p.x + p.y) * p.z);
}

float3 MBGrad(float3 i)
{
    float a = MBHash13(i) * 6.2831853;
    float z = MBHash13(i + 17.17) * 2.0 - 1.0;
    float r = sqrt(saturate(1.0 - z * z));
    return float3(r * cos(a), r * sin(a), z);
}

float MBNoise(float3 p)   // gradient noise remapped to ~0..1
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = dot(MBGrad(i), f);
    float n100 = dot(MBGrad(i + float3(1, 0, 0)), f - float3(1, 0, 0));
    float n010 = dot(MBGrad(i + float3(0, 1, 0)), f - float3(0, 1, 0));
    float n110 = dot(MBGrad(i + float3(1, 1, 0)), f - float3(1, 1, 0));
    float n001 = dot(MBGrad(i + float3(0, 0, 1)), f - float3(0, 0, 1));
    float n101 = dot(MBGrad(i + float3(1, 0, 1)), f - float3(1, 0, 1));
    float n011 = dot(MBGrad(i + float3(0, 1, 1)), f - float3(0, 1, 1));
    float n111 = dot(MBGrad(i + float3(1, 1, 1)), f - float3(1, 1, 1));
    float n = lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                   lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
    return saturate(0.5 + 0.75 * n);
}

float MBFbm(float3 p)
{
    float3 q = mul(MB_ROT, p) * 2.03 + 7.1;
    float3 r = mul(MB_ROT, q) * 2.01 + 13.7;
    return 0.5 * MBNoise(p) + 0.3 * MBNoise(q) + 0.2 * MBNoise(r);
}

float3 MBNoise3(float3 p)
{
    return float3(MBNoise(p), MBNoise(p + float3(31.4, 17.7, 5.3)), MBNoise(p + float3(11.1, 43.9, 27.2)));
}

float MBPsi(float3 q) { return MBNoise(q) + 0.5 * MBNoise(mul(MB_ROT, q) * 2.1 + 3.3); }

// divergence-free swirl: curl of a noise vector potential (2 rotated octaves)
float3 MBCurlNoise(float3 p)
{
    const float e = 0.1;
    float3 dx = float3(e, 0, 0), dy = float3(0, e, 0), dz = float3(0, 0, e);
    float3 o1 = float3(31.4, 17.7, 5.3), o2 = float3(11.1, 43.9, 27.2);
    #define PSI(q) float3(MBPsi(q), MBPsi((q) + o1), MBPsi((q) + o2))
    float3 px0 = PSI(p - dx), px1 = PSI(p + dx);
    float3 py0 = PSI(p - dy), py1 = PSI(p + dy);
    float3 pz0 = PSI(p - dz), pz1 = PSI(p + dz);
    #undef PSI
    float3 c;
    c.x = (py1.z - py0.z) - (pz1.y - pz0.y);
    c.y = (pz1.x - pz0.x) - (px1.z - px0.z);
    c.z = (px1.y - px0.y) - (py1.x - py0.x);
    return c / (2.0 * e);
}

#endif
