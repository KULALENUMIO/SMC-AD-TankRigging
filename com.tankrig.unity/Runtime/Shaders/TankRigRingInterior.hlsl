// SPDX-License-Identifier: MIT
// Turret ring interior (fake interior): a disk on the hull roof (side 0) or a disk + basket-shaped proxy under the
// turret (side 1) shows a Western-style tank interior by ray marching a procedural scene (signed distance fields)
// from the surface point along the view ray. Everything is built for the default ring (inner diameter 2.25 m) in
// "design" metres and scaled by _InnerDiameter / 2.25; the ring band width (_RingWidth) is separate.
// The interior has its own fixed lighting (lamps, light through the opening, ambient occlusion): nothing outside
// (sun, shadows, other lights) changes it. Called from the Shader Graph "TankRig/Turret Ring Interior".
#ifndef TANKRIG_RING_INTERIOR_INCLUDED
#define TANKRIG_RING_INTERIOR_INCLUDED

// material properties (Shader Graph blackboard): _InnerDiameter, _RingWidth, _Side, _Brightness,
// _PaintColor, _EquipColor, _FloorColor

#define RI_RIN 1.125          // design inner radius (m)
#define RI_PI 3.14159265

// ------------------------------------------------------------------ distance helpers

float RI_Box(float3 p, float3 b)
{
    float3 q = abs(p) - b;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

// capped cylinder along Y, half height h
float RI_CylY(float3 p, float r, float h)
{
    float2 d = abs(float2(length(p.xz), p.y)) - float2(r, h);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0));
}
float RI_CylX(float3 p, float r, float h) { return RI_CylY(p.yxz, r, h); }
float RI_CylZ(float3 p, float r, float h) { return RI_CylY(p.xzy, r, h); }

float RI_Torus(float3 p, float R, float r)
{
    float2 q = float2(length(p.xz) - R, p.y);
    return length(q) - r;
}

float RI_Capsule(float3 p, float3 a, float3 b, float r)
{
    float3 pa = p - a, ba = b - a;
    float h = saturate(dot(pa, ba) / dot(ba, ba));
    return length(pa - ba * h) - r;
}

float RI_BoxFrame(float3 p, float3 b, float e)
{
    p = abs(p) - b;
    float3 q = abs(p + e) - e;
    return min(min(
        length(max(float3(p.x, q.y, q.z), 0.0)) + min(max(p.x, max(q.y, q.z)), 0.0),
        length(max(float3(q.x, p.y, q.z), 0.0)) + min(max(q.x, max(p.y, q.z)), 0.0)),
        length(max(float3(q.x, q.y, p.z), 0.0)) + min(max(q.x, max(q.y, p.z)), 0.0));
}

void RI_U(inout float2 a, float d, float id) { if (d < a.x) a = float2(d, id); }

float RI_Hash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float RI_Noise(float2 p)
{
    float2 i = floor(p), f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(RI_Hash(i), RI_Hash(i + float2(1, 0)), f.x), lerp(RI_Hash(i + float2(0, 1)), RI_Hash(i + float2(1, 1)), f.x), f.y);
}

// material ids
#define RI_STEEL 1      // ring race
#define RI_GEAR 2       // ring gear teeth
#define RI_PAINT 3      // interior paint (off-white)
#define RI_FLOOR 4      // floor / tread plate
#define RI_EQUIP 5      // equipment boxes (olive)
#define RI_BLACK 6      // cables, batteries, rubber
#define RI_RED 7        // fire extinguishers
#define RI_BARE 8       // bare / machined metal
#define RI_LAMP 9       // dome lights
#define RI_SEAT 10      // seats
#define RI_DARK 11      // far passage (driver's compartment)
#define RI_BREECH 12    // gun breech
#define RI_BASKET 13    // basket floor plate

// ------------------------------------------------------------------ hull side (looking down into the hull)

float RI_Teeth(float3 p)
{
    float a = atan2(p.z, p.x);
    float f = abs(frac(a * (160.0 / (2.0 * RI_PI))) - 0.5) * 2.0;   // 0 at a tooth, 1 between
    return smoothstep(0.25, 0.75, f);
}

float2 RI_SceneHull(float3 p)
{
    float2 res = float2(1e5, 0.0);
    float r = length(p.xz);
    // ring gear (teeth pointing inward) and the roof plate around the opening
    float rin = RI_RIN + 0.022 * RI_Teeth(p);
    RI_U(res, 0.8 * max(abs(p.y + 0.065) - 0.065, rin - r), RI_GEAR);
    RI_U(res, max(abs(p.y + 0.165) - 0.035, RI_RIN + 0.03 - r), RI_PAINT);

    // hull: upper part over the tracks (sponsons), lower part between them, the passage to the driver; open upwards
    float dIn = RI_Box(p - float3(0.0, 0.125, 0.15), float3(1.45, 0.875, 1.4));        // y -0.75..1, z -1.25..1.55
    dIn = min(dIn, RI_Box(p - float3(0.0, -0.175, 0.15), float3(1.0, 1.175, 1.4)));    // y -1.35..1
    dIn = min(dIn, RI_Box(p - float3(0.0, -0.95, 1.6), float3(0.38, 0.4, 0.6)));       // driver's passage
    float walls = -dIn;
    float wallId = p.y < -1.32 ? RI_FLOOR : (p.z > 1.62 ? RI_DARK : RI_PAINT);
    RI_U(res, walls, wallId);

    // floor: torsion bar housings across the hull, the slip ring (hydraulic swivel) in the middle
    float zc = clamp(round((p.z - 0.35) / 0.6), -2.0, 1.0) * 0.6 + 0.35;
    RI_U(res, length(float2(p.y + 1.35, p.z - zc)) - 0.055, RI_FLOOR);
    RI_U(res, RI_CylY(p - float3(0.0, -1.18, 0.0), 0.15, 0.17), RI_BARE);
    RI_U(res, RI_CylY(p - float3(0.0, -1.33, 0.0), 0.24, 0.025), RI_BARE);
    RI_U(res, RI_CylY(p - float3(0.0, -0.99, 0.0), 0.09, 0.03), RI_BLACK);

    // driver's seat at the end of the passage
    RI_U(res, RI_Box(p - float3(0.0, -0.85, 1.95), float3(0.24, 0.3, 0.06)) - 0.02, RI_SEAT);
    RI_U(res, RI_Box(p - float3(0.0, -0.47, 1.97), float3(0.14, 0.08, 0.05)) - 0.02, RI_SEAT);
    RI_U(res, RI_Box(p - float3(0.0, -1.12, 1.75), float3(0.24, 0.06, 0.22)) - 0.02, RI_SEAT);

    // rear bulkhead (engine firewall): ventilation / NBC unit, batteries, duct
    RI_U(res, RI_Box(p - float3(0.55, -0.97, -1.05), float3(0.32, 0.28, 0.18)) - 0.01, RI_EQUIP);
    RI_U(res, RI_Box(p - float3(-0.35, -1.2, -1.03), float3(0.17, 0.14, 0.2)) - 0.008, RI_BLACK);
    RI_U(res, RI_Box(p - float3(-0.74, -1.2, -1.03), float3(0.17, 0.14, 0.2)) - 0.008, RI_BLACK);
    RI_U(res, RI_Capsule(p, float3(0.55, -0.69, -1.08), float3(0.55, -0.26, -1.08), 0.085), RI_EQUIP);

    // sponson shelves: radios, fire extinguishers (left); hydraulic reservoir, stowage boxes (right)
    RI_U(res, RI_Box(p - float3(-1.22, -0.57, -0.45), float3(0.18, 0.17, 0.3)) - 0.01, RI_EQUIP);
    RI_U(res, RI_CylY(p - float3(-1.25, -0.52, 0.35), 0.08, 0.23), RI_RED);
    RI_U(res, RI_CylY(p - float3(-1.25, -0.52, 0.6), 0.08, 0.23), RI_RED);
    RI_U(res, RI_CylZ(p - float3(1.22, -0.58, -0.2), 0.14, 0.4), RI_BARE);
    RI_U(res, RI_Box(p - float3(1.22, -0.6, 0.85), float3(0.2, 0.15, 0.4)) - 0.01, RI_EQUIP);

    // cable runs along the walls and dome lights under the roof
    float cx = p.x > 0.0 ? 1.0 : -1.0;
    RI_U(res, RI_Capsule(p, float3(1.42 * cx, -0.3, -1.2), float3(1.42 * cx, -0.3, 1.5), 0.018), RI_BLACK);
    RI_U(res, RI_Capsule(p, float3(0.98 * cx, -0.95, -1.2), float3(0.98 * cx, -0.95, 1.5), 0.016), RI_BLACK);
    RI_U(res, RI_Box(p - float3(1.3 * cx, -0.215, 0.6), float3(0.07, 0.018, 0.12)), RI_LAMP);
    return res;
}

// ------------------------------------------------------------------ turret side (basket below, turret above)

float2 RI_SceneTurret(float3 p)
{
    float2 res = float2(1e5, 0.0);
    float r = length(p.xz);
    // turret floor around the ring opening, turret walls and roof (inside), gun breech and its guard
    RI_U(res, max(abs(p.y - 0.04) - 0.04, RI_RIN - r), RI_PAINT);
    // the turret's inside: solid outside a cylinder (walls) and above the roof, only above the turret floor
    float inside = max(r - 1.75, p.y - 1.05);
    RI_U(res, max(-inside, 0.08 - p.y), RI_PAINT);
    RI_U(res, RI_Box(p - float3(0.0, 0.48, 0.3), float3(0.27, 0.24, 0.48)) - 0.02, RI_BREECH);
    RI_U(res, RI_CylZ(p - float3(0.0, 0.5, 0.95), 0.2, 0.2), RI_BREECH);
    RI_U(res, RI_BoxFrame(p - float3(0.0, 0.42, -0.05), float3(0.45, 0.3, 0.55), 0.02), RI_EQUIP);
    // roof fittings: periscope bodies, hatch rings
    RI_U(res, RI_Box(p - float3(0.6, 0.93, 0.55), float3(0.12, 0.12, 0.08)), RI_BLACK);
    RI_U(res, RI_Box(p - float3(-0.55, 0.95, 0.4), float3(0.1, 0.1, 0.07)), RI_BLACK);
    RI_U(res, RI_Torus(p - float3(0.65, 1.02, -0.45), 0.32, 0.03), RI_BARE);
    RI_U(res, RI_Torus(p - float3(-0.65, 1.02, -0.4), 0.28, 0.03), RI_BARE);
    // equipment on the turret walls: fire control computer, radios, ventilation, ready lamps
    RI_U(res, RI_Box(p - float3(1.45, 0.55, 0.45), float3(0.18, 0.22, 0.32)) - 0.01, RI_EQUIP);
    RI_U(res, RI_Box(p - float3(1.42, 0.45, -0.55), float3(0.2, 0.18, 0.28)) - 0.01, RI_BLACK);
    RI_U(res, RI_Box(p - float3(-1.45, 0.5, -0.2), float3(0.18, 0.26, 0.42)) - 0.01, RI_EQUIP);
    RI_U(res, RI_Box(p - float3(-1.3, 0.62, 0.7), float3(0.25, 0.14, 0.18)) - 0.01, RI_BLACK);
    RI_U(res, RI_CylY(p - float3(-0.9, 0.55, -1.05), 0.09, 0.35), RI_RED);
    RI_U(res, RI_Capsule(p, float3(-1.68, 0.92, -0.9), float3(-1.2, 0.92, 1.0), 0.03), RI_BLACK);
    RI_U(res, RI_Capsule(p, float3(1.68, 0.88, -0.9), float3(1.25, 0.88, 1.0), 0.03), RI_BLACK);
    // rear bulkhead (ammunition doors behind it)
    RI_U(res, max(-(p.z + 1.25), 0.08 - p.y), RI_PAINT);

    // basket: floor plate with lightening holes, posts, two rails
    float rb = 1.05;
    float holes = length(float2(frac(p.x * 2.5) - 0.5, frac(p.z * 2.5) - 0.5)) - 0.12;
    float plate = max(RI_CylY(p - float3(0.0, -1.165, 0.0), rb, 0.015), -holes);
    RI_U(res, plate, RI_BASKET);
    float a = atan2(p.z, p.x);
    float seg = 2.0 * RI_PI / 10.0;
    float ac = (round(a / seg)) * seg;
    float3 pp = float3(cos(ac) * rb, 0.0, sin(ac) * rb);
    RI_U(res, RI_Box(float3(p.x - pp.x, p.y + 0.58, p.z - pp.z), float3(0.018, 0.58, 0.018)), RI_EQUIP);
    RI_U(res, RI_Torus(p - float3(0.0, -0.45, 0.0), rb, 0.013), RI_EQUIP);
    RI_U(res, RI_Torus(p - float3(0.0, -0.88, 0.0), rb, 0.013), RI_EQUIP);
    RI_U(res, RI_Torus(p - float3(0.0, -0.02, 0.0), rb, 0.02), RI_EQUIP);

    // seats: gunner (front right), commander (rear right, higher), loader (left, folding)
    RI_U(res, RI_Box(p - float3(0.62, -0.38, 0.55), float3(0.2, 0.04, 0.19)) - 0.02, RI_SEAT);
    RI_U(res, RI_Box(p - float3(0.66, -0.12, 0.36), float3(0.19, 0.22, 0.04)) - 0.02, RI_SEAT);
    RI_U(res, RI_Capsule(p, float3(0.62, -0.42, 0.55), float3(0.62, -1.15, 0.55), 0.025), RI_BARE);
    RI_U(res, RI_Box(p - float3(0.7, -0.08, -0.5), float3(0.2, 0.04, 0.19)) - 0.02, RI_SEAT);
    RI_U(res, RI_Box(p - float3(0.74, 0.18, -0.7), float3(0.19, 0.22, 0.04)) - 0.02, RI_SEAT);
    RI_U(res, RI_Capsule(p, float3(0.7, -0.12, -0.5), float3(0.7, -1.15, -0.5), 0.025), RI_BARE);
    RI_U(res, RI_Box(p - float3(-0.7, -0.55, 0.05), float3(0.18, 0.03, 0.16)) - 0.015, RI_SEAT);

    // on the basket floor: turret electronics, hydraulic power unit; the turret half of the slip ring below
    RI_U(res, RI_Box(p - float3(-0.45, -0.98, -0.5), float3(0.2, 0.17, 0.15)) - 0.01, RI_EQUIP);
    RI_U(res, RI_Box(p - float3(0.25, -1.0, -0.62), float3(0.18, 0.15, 0.2)) - 0.01, RI_BLACK);
    RI_U(res, RI_CylY(p - float3(0.0, -1.24, 0.0), 0.12, 0.07), RI_BARE);
    // dome lights on the turret roof
    float cx = p.x > 0.0 ? 1.0 : -1.0;
    RI_U(res, RI_Box(p - float3(0.95 * cx, 1.03, 0.1), float3(0.06, 0.018, 0.11)), RI_LAMP);
    return res;
}

float2 RI_Scene(float3 p, float side) { return side > 0.5 ? RI_SceneTurret(p) : RI_SceneHull(p); }

float3 RI_Normal(float3 p, float side)
{
    const float2 k = float2(1.0, -1.0);
    const float h = 0.0015;
    return normalize(k.xyy * RI_Scene(p + k.xyy * h, side).x + k.yyx * RI_Scene(p + k.yyx * h, side).x
                   + k.yxy * RI_Scene(p + k.yxy * h, side).x + k.xxx * RI_Scene(p + k.xxx * h, side).x);
}

float RI_AO(float3 p, float3 n, float side)
{
    float occ = 0.0, w = 1.0;
    for (int i = 1; i <= 5; i++)
    {
        float hh = 0.03 * i * i * 0.5 + 0.01;
        occ += (hh - RI_Scene(p + n * hh, side).x) * w;
        w *= 0.7;
    }
    return saturate(1.0 - 2.2 * occ);
}

// ------------------------------------------------------------------ materials and the interior's own light

float3 RI_Albedo(float id, float3 p, float3 n)
{
    float3 paint = _PaintColor.rgb, equip = _EquipColor.rgb, floorc = _FloorColor.rgb;
    float grain = RI_Noise(p.xz * 23.0 + p.y * 17.0) * 0.12 - 0.06;
    if (id == RI_GEAR)
    {
        float t = RI_Teeth(p);
        return float3(0.16, 0.155, 0.14) * (0.8 + 0.5 * (1.0 - t)) + grain * 0.3;
    }
    if (id == RI_PAINT)
    {
        // panel seams every 0.62 m and a row of rivets along them, grime towards the floor
        float2 q = abs(n.x) > 0.5 ? p.zy : abs(n.z) > 0.5 ? p.xy : p.xz;
        float seam = smoothstep(0.006, 0.0, abs(frac(q.x / 0.62 + 0.5) - 0.5) * 0.62);
        float rivet = smoothstep(0.012, 0.006, length(float2((frac(q.x / 0.62 + 0.5) - 0.5) * 0.62 - 0.025, (frac(q.y / 0.08) - 0.5) * 0.08)));
        float grime = smoothstep(-0.9, -1.35, p.y) * 0.35;
        return paint * (1.0 - 0.35 * seam - grime + grain) * (1.0 - 0.25 * rivet);
    }
    if (id == RI_FLOOR)
    {
        // tread plate (raised diamonds) and the escape hatch outline
        float d = sin((p.x + p.z) * 70.0) * sin((p.x - p.z) * 70.0);
        float tread = smoothstep(0.6, 0.9, d);
        float hatch = smoothstep(0.008, 0.0, abs(length(p.xz - float2(0.5, 0.95)) - 0.27));
        return floorc * (1.0 + 0.35 * tread - 0.5 * hatch + grain);
    }
    if (id == RI_EQUIP)
    {
        // olive boxes with a pale label plate on the face
        float lab = step(abs(frac(p.y * 3.1) - 0.5), 0.12) * step(abs(frac((p.x + p.z) * 2.3) - 0.5), 0.18);
        return lerp(equip, float3(0.55, 0.53, 0.45), lab * 0.6) * (1.0 + grain);
    }
    if (id == RI_BLACK) return float3(0.025, 0.025, 0.024) * (1.0 + grain);
    if (id == RI_RED) return (p.y > -0.32 && p.y < 0.0) || p.y > 0.85 ? float3(0.4, 0.4, 0.38) : float3(0.42, 0.025, 0.02) * (1.0 + grain);
    if (id == RI_BARE || id == RI_STEEL) return float3(0.34, 0.34, 0.33) * (1.0 + grain * 1.5);
    if (id == RI_SEAT) return float3(0.045, 0.05, 0.035) * (1.0 + grain);
    if (id == RI_DARK) return paint * 0.25;
    if (id == RI_BREECH) return float3(0.07, 0.075, 0.08) * (1.0 + grain);
    if (id == RI_BASKET) return floorc * 1.2 * (1.0 + grain);
    return float3(1.0, 0.95, 0.85);
}

float3 RI_Light(float3 p, float3 n, float ao, float side)
{
    float3 lit = 0.11 * ao;                                       // ambient, occluded
    if (side < 0.5)
    {
        // daylight coming in through the opening: the line to the opening's centre must pass inside the ring
        float t = (-0.2 - p.y) / max(1e-3, -p.y);
        float rr = length(p.xz * (1.0 - saturate(t)));
        float vis = p.y > -0.2 ? 1.0 : smoothstep(1.35, 0.85, rr);
        lit += float3(0.95, 0.97, 1.0) * 0.55 * vis * saturate(dot(n, normalize(-p)) * 0.8 + 0.2) * (0.5 + 0.5 * ao);
        float3 l1 = float3(1.3, -0.3, 0.6) - p, l2 = float3(-1.3, -0.3, 0.6) - p;
        lit += float3(1.0, 0.92, 0.78) * 0.35 * (saturate(dot(n, normalize(l1))) / (1.0 + dot(l1, l1)) + saturate(dot(n, normalize(l2))) / (1.0 + dot(l2, l2)));
    }
    else
    {
        float3 l1 = float3(0.95, 0.95, 0.1) - p, l2 = float3(-0.95, 0.95, 0.1) - p;
        lit += float3(1.0, 0.92, 0.78) * 0.6 * (saturate(dot(n, normalize(l1))) / (1.0 + 0.6 * dot(l1, l1)) + saturate(dot(n, normalize(l2))) / (1.0 + 0.6 * dot(l2, l2)));
        lit += float3(0.75, 0.8, 0.9) * 0.18 * saturate(-n.y) * ao;  // light from the hull below
    }
    return lit;
}

// the flat ring race (top of the hull ring / underside of the turret ring): rings, groove, bolt circle
float3 RI_Race(float r, float3 p, float w, float side)
{
    float u = saturate((r - RI_RIN) / max(1e-3, w));
    float3 c = float3(0.3, 0.3, 0.29);
    c *= 1.0 + 0.5 * smoothstep(0.06, 0.0, u) + 0.12 * smoothstep(0.92, 1.0, u);   // machined edges catch the light
    c *= 1.0 - 0.75 * smoothstep(0.035, 0.0, abs(u - 0.48));                           // seal groove
    float a = atan2(p.z, p.x);
    float n = side > 0.5 ? 48.0 : 56.0;
    float sa = (frac(a * n / (2.0 * RI_PI)) - 0.5) * 2.0 * RI_PI / n * r;
    float br = length(float2(sa, r - (RI_RIN + w * 0.78)));
    float bolt = smoothstep(0.02, 0.016, br);
    c = lerp(c, float3(0.2, 0.2, 0.19) * (1.2 - 0.8 * smoothstep(0.0, 0.016, br)), bolt);
    c *= 0.9 + 0.2 * RI_Noise(float2(a * 40.0, r * 30.0));
    return c * 0.95;
}

void TankRigRingInterior_float(float3 PositionOS, float3 PositionWS, out float3 Color, out float Alpha)
{
    float s = max(0.05, _InnerDiameter / 2.25);
    float w = max(0.0, _RingWidth) / s;
    float side = _Side;
    float3 viewWS = IsPerspectiveProjection() ? PositionWS - GetCurrentViewPosition() : -GetViewForwardDir();
    float3 rd = normalize(TransformWorldToObjectDir(viewWS));
    float3 ro = PositionOS / s;
    float r0 = length(ro.xz);
    Color = 0.0;
    Alpha = 0.0;

    // on the ring race itself (flat band at the ring plane): no ray needed
    if (abs(ro.y) < 0.02 && r0 >= RI_RIN)
    {
        if (r0 > RI_RIN + w) return;
        Color = RI_Race(r0, ro, w, side) * _Brightness;
        Alpha = 1.0;
        return;
    }

    float t = 0.0;
    float2 h = float2(1.0, 0.0);
    bool hit = false;
    int steps = side > 0.5 ? 128 : 96;
    [loop] for (int i = 0; i < steps; i++)
    {
        float3 p = ro + rd * t;
        if (side > 0.5 && (p.y < -1.4 || p.y > 1.12 || length(p.xz) > 1.9)) break;
        if (side < 0.5 && t > 8.0) break;
        h = RI_Scene(p, side);
        if (h.x < 0.0008 * (1.0 + t)) { hit = true; break; }
        t += h.x * 0.9;
    }
    if (!hit) { if (side < 0.5) { Color = 0.0; Alpha = 1.0; } return; }
    float3 p = ro + rd * t;
    float3 n = RI_Normal(p, side);
    float ao = RI_AO(p, n, side);
    float3 albedo = RI_Albedo(h.y, p, n);
    float3 col = h.y == RI_LAMP ? float3(2.2, 2.05, 1.8) : albedo * RI_Light(p, n, ao, side);
    Color = col * _Brightness;
    Alpha = 1.0;
}

#endif
