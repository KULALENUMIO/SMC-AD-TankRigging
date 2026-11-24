// Muzzle effects for automatic guns (autocannons, machine guns): cheap particles, no fluid solver.
// A shot is a handful of layered particles, after the physics of small-arms muzzle flash:
//   primary flash   - the glowing gas right at the muzzle (core glow) and the forward cone of hot gas
//   device jets     - flame jets out of a muzzle brake's side ports or a flash hider's slots (petals)
//   intermediate    - the reddish disc where the gas jet shocks down, a few calibres out
//   secondary flash - fuel-rich gas burning in the air: a short lumpy fireball (big for autocannons, rare for MGs)
//   smoke, sparks   - a few grey puffs that thin out quickly, burning propellant grains
//   light           - a point light that flickers on for a frame or two
// Nothing collides: the particles pass through the tank and the ground (they are tiny and gone in a moment).
// Every layer is a channel (on/off, amount, delay, duration) like the main gun's controls; a profile holds the
// sizes and timings, and presets derive one from the calibre.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    public enum GunKind { Autocannon, MachineGun }
    public enum MuzzleDevice { None, FlashHider, MuzzleBrake }

    [Serializable]
    public class GunFlashProfile
    {
        public GunKind kind = GunKind.Autocannon;
        [Tooltip("bore (mm); presets scale every size from it")] public float caliberMm = 30f;
        public MuzzleDevice device = MuzzleDevice.MuzzleBrake;
        [Tooltip("brake: port pairs; hider: slots")] public int devicePorts = 2;
        [Header("Primary flash")]
        public float coreSize = 0.35f;            // m, radius of the glow at the muzzle
        public float coneLength = 1.2f;           // m, forward flame cone
        public float coneWidth = 0.25f;           // m, its half width at the root
        public float flashTime = 0.03f;           // s, how long the flash glows
        public float flashRadiance = 3e6f;        // nits at the core
        [Header("Device jets")]
        public float jetLength = 0.7f;            // m
        public float jetWidth = 0.15f;            // m
        public float jetAngle = 95f;              // deg from the bore (brake: sideways, a little back; hider: ~35)
        [Header("Intermediate flash")]
        public float discDistance = 0.15f;        // m ahead of the muzzle
        public float discSize = 0.12f;            // m
        [Header("Secondary flash (fireball)")]
        [Range(0f, 1f)] public float fireChance = 0.85f;   // not every round lights the gas
        public float fireDistance = 0.9f;         // m ahead (centre of the burning region)
        public float fireSize = 0.6f;             // m radius
        public float fireTime = 0.07f;            // s
        public int fireLumps = 3;
        [Header("Smoke")]
        public int smokePuffs = 4;
        public float smokeSize = 0.35f;           // m start radius (grows x3)
        public float smokeSpeed = 12f;            // m/s down the bore, quickly braked
        public float smokeLife = 0.8f;            // s
        [Range(0f, 1f)] public float smokeOpacity = 0.35f;
        [Header("Sparks")]
        public int sparks = 6;
        public float sparkSpeed = 60f;
        public float sparkLife = 0.08f;
        [Header("Light")]
        public float lightLumen = 3e6f;
        public float lightRange = 25f;
        public float lightTime = 0.035f;          // s
        [Header("Variation")]
        [Range(0f, 1f)] public float jitter = 0.35f;        // shot-to-shot size scatter

        /// <summary>Defaults for a gun kind and bore. Sizes follow the calibre (energy of the gas ~ calibre^2..3).</summary>
        public static GunFlashProfile Preset(GunKind kind, float caliberMm)
        {
            var p = new GunFlashProfile { kind = kind, caliberMm = caliberMm };
            if (kind == GunKind.Autocannon)
            {
                float s = caliberMm / 30f;               // 30 mm = reference
                p.device = MuzzleDevice.MuzzleBrake; p.devicePorts = 2;
                p.coreSize = 0.35f * s; p.coneLength = 1.3f * s; p.coneWidth = 0.22f * s; p.flashTime = 0.03f; p.flashRadiance = 3e6f;
                p.jetLength = 0.8f * s; p.jetWidth = 0.16f * s; p.jetAngle = 95f;
                p.discDistance = 0.12f * s; p.discSize = 0.12f * s;
                p.fireChance = 0.85f; p.fireDistance = 0.9f * s; p.fireSize = 0.55f * s; p.fireTime = 0.07f + 0.02f * s; p.fireLumps = 3;
                p.smokePuffs = 3; p.smokeSize = 0.28f * s; p.smokeSpeed = 12f; p.smokeLife = 0.45f + 0.25f * s; p.smokeOpacity = Mathf.Clamp01(0.22f * s);
                p.sparks = Mathf.RoundToInt(4 * s); p.sparkSpeed = 70f; p.sparkLife = 0.08f;
                p.lightLumen = 4e5f * s * s; p.lightRange = 25f * s; p.lightTime = 0.035f;
            }
            else
            {
                float s = caliberMm / 7.62f;             // 7.62 mm = reference
                p.device = MuzzleDevice.FlashHider; p.devicePorts = 4;
                p.coreSize = 0.07f * s; p.coneLength = 0.28f * s; p.coneWidth = 0.05f * s; p.flashTime = 0.02f; p.flashRadiance = 2e6f;
                p.jetLength = 0.16f * s; p.jetWidth = 0.035f * s; p.jetAngle = 32f;
                p.discDistance = 0.04f * s; p.discSize = 0.025f * s;
                p.fireChance = 0.15f * s; p.fireDistance = 0.18f * s; p.fireSize = 0.08f * s; p.fireTime = 0.03f; p.fireLumps = 1;
                p.smokePuffs = 1; p.smokeSize = 0.05f * s; p.smokeSpeed = 6f; p.smokeLife = 0.35f; p.smokeOpacity = 0.12f;
                p.sparks = Mathf.RoundToInt(2 * s); p.sparkSpeed = 50f; p.sparkLife = 0.05f;
                p.lightLumen = 4e4f * s * s; p.lightRange = 8f * s; p.lightTime = 0.025f;
            }
            return p;
        }
    }

    [Serializable]
    public class GunFlashControls
    {
        public FxChannel flash = new FxChannel();        // core glow + forward cone
        public FxChannel jets = new FxChannel();         // brake / hider jets
        public FxChannel disc = new FxChannel();         // intermediate flash
        public FxChannel fireball = new FxChannel();     // secondary flash
        public FxChannel smoke = new FxChannel();
        public FxChannel sparks = new FxChannel();
        public FxChannel light = new FxChannel();

        public void Apply(string kv)
        {
            if (string.IsNullOrEmpty(kv)) return;
            foreach (var part in kv.Split(','))
            {
                var eq = part.Split('='); var path = eq[0].Trim().Split('.');
                if (eq.Length != 2 || path.Length != 2) continue;
                var ch = GetType().GetField(path[0])?.GetValue(this);
                var f = ch?.GetType().GetField(path[1]);
                if (f == null) { Debug.LogWarning("GunFlash: unknown control " + eq[0]); continue; }
                if (f.FieldType == typeof(bool)) f.SetValue(ch, eq[1].Trim() == "1" || eq[1].Trim() == "true");
                else f.SetValue(ch, float.Parse(eq[1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>The scene's sun for the particle lighting (RenderSettings.sun, else the brightest directional light).</summary>
    public static class FxScene
    {
        public static Light FindSun()
        {
            if (RenderSettings.sun) return RenderSettings.sun;
            Light best = null;
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.isActiveAndEnabled && (best == null || l.intensity > best.intensity)) best = l;
            return best;
        }
    }

    /// <summary>Anything that feeds particles to GunFlashPass (gun flashes, engine exhaust...).</summary>
    public interface IFlashParticles
    {
        int CopyVisible(List<GunFlashFX.Particle> dst);
        Vector3 SunDir { get; }
        Color SunLux { get; }
        Color Ambient { get; }
    }

    [ExecuteAlways]
    public class GunFlashFX : MonoBehaviour, IFlashParticles
    {
        /// <summary>Every live particle source the pass draws.</summary>
        public static readonly List<IFlashParticles> Sources = new List<IFlashParticles>();
        public Color Ambient => ambient;
        [Tooltip("the muzzle; its forward (+Z) is down the bore. Empty = this transform")]
        public Transform muzzle;
        public GunFlashProfile profile = GunFlashProfile.Preset(GunKind.Autocannon, 30f);
        public GunFlashControls fx = new GunFlashControls();
        [Header("Scene")]
        public Light sun;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;
        public Vector3 wind = new Vector3(1.5f, 0f, 2.5f);
        [Header("Fire (play mode / tests)")]
        [Tooltip("rounds per minute while firing")] public float rpm = 300f;
        public bool firing;
        public bool autoAdvance = true;

        // ---- particles (shared layout with GunFlash.shader)
        public enum PType { Core = 0, Cone = 1, Jet = 2, Disc = 3, Fire = 4, Smoke = 5, Spark = 6, Exhaust = 7, Dust = 8, Grain = 9, Sprite = 10, Mud = 11, BoreJet = 12 }
        public struct Particle
        {
            public Vector3 pos; public float type;
            public Vector3 axis; public float length;     // streaks: unit axis + length; billboards: axis unused
            public float width, age01, seed, intensity;
            public Vector3 vel; public float life;        // CPU only (kept in the buffer for simplicity)
            public float age, delay, grow, drag;
            /// <summary>Mud (type 11): xyz albedo, w stretch (s: streak length = speed x w; axis = back along the motion).
            /// Sprites (type 10): x lighting mode (0 uniform, 1 sphere, 2 normal map, 3 scatter), y flipbook slice,
            /// z emission (nits at full temperature), w erosion (1 = the opacity eats the thin parts first).</summary>
            public Vector4 extra;
            /// <summary>Sprites: x rotation (rad), y soft-depth distance (m), z near-camera fade distance (m).</summary>
            public Vector4 extra2;
        }
        public const int Stride = 4 * 28;
        readonly List<Particle> parts = new List<Particle>();
        public IReadOnlyList<Particle> Particles => parts;
        public static readonly List<GunFlashFX> Live = new List<GunFlashFX>();

        Light flashLight;
        float lightAge = 99f, lightPeak, lightDelay, lightDur;
        float fireClock;
        System.Random rng = new System.Random(1234);
        float R() => (float)rng.NextDouble();
        float J(float a) => 1f + (R() * 2f - 1f) * a;   // 1 +- a

        Transform M => muzzle ? muzzle : transform;

        void OnEnable() { if (!sun) sun = FxScene.FindSun(); if (!Live.Contains(this)) Live.Add(this); if (!Sources.Contains(this)) Sources.Add(this); }
        void OnDisable() { Live.Remove(this); Sources.Remove(this); if (flashLight) flashLight.enabled = false; }

        /// <summary>Replace the profile with the preset for a gun kind and calibre (keeps the controls).</summary>
        public void ApplyPreset(GunKind kind, float caliberMm) => profile = GunFlashProfile.Preset(kind, caliberMm);

        void Add(PType t, Vector3 pos, Vector3 axis, float len, float width, float life, float intensity, Vector3 vel, float delay, float grow = 0f, float drag = 0f)
        {
            parts.Add(new Particle { pos = pos, type = (float)t, axis = axis, length = len, width = width, life = Mathf.Max(life, 1e-3f), intensity = intensity, vel = vel, seed = R() * 100f, delay = delay, grow = grow, drag = drag });
        }

        /// <summary>One round.</summary>
        public void Shot()
        {
            var p = profile;
            var m = M;
            Vector3 o = m.position, f = m.forward, up = m.up, right = m.right;
            float j = p.jitter;
            float roll = R() * 360f;
            var c = fx;
            // primary flash: core glow and the forward cone (one or two tongues)
            if (c.flash.On)
            {
                float a = c.flash.amount, d = c.flash.delay, dur = c.flash.duration;
                Add(PType.Core, o + f * p.coreSize * 0.3f, f, 0f, p.coreSize * J(j) * Mathf.Sqrt(a), p.flashTime * dur * J(0.3f), p.flashRadiance * 0.6f * a, Vector3.zero, d);
                int tongues = R() < 0.5f ? 1 : 2;
                for (int i = 0; i < tongues; i++)
                {
                    Vector3 ax = (f + (right * (R() - 0.5f) + up * (R() - 0.5f)) * 0.12f).normalized;
                    Add(PType.Cone, o, ax, p.coneLength * J(j) * a, p.coneWidth * J(j * 0.5f), p.flashTime * dur * J(0.3f), p.flashRadiance * 0.7f * a, Vector3.zero, d);
                }
            }
            // device jets: brake ports throw flame sideways (pairs), hider slots a star of short tongues
            if (c.jets.On && p.device != MuzzleDevice.None && p.devicePorts > 0)
            {
                float a = c.jets.amount, d = c.jets.delay, dur = c.jets.duration;
                int n = p.device == MuzzleDevice.MuzzleBrake ? 2 : p.devicePorts;
                for (int i = 0; i < n; i++)
                {
                    float ang = (p.device == MuzzleDevice.MuzzleBrake ? i * 180f : roll + i * 360f / n) * Mathf.Deg2Rad;
                    Vector3 radial = right * Mathf.Cos(ang) + up * Mathf.Sin(ang);
                    float tilt = p.jetAngle * Mathf.Deg2Rad;
                    Vector3 ax = (f * Mathf.Cos(tilt) + radial * Mathf.Sin(tilt)).normalized;
                    int stack = p.device == MuzzleDevice.MuzzleBrake ? p.devicePorts : 1;   // each port pair along the brake
                    for (int k = 0; k < stack; k++)
                    {
                        Vector3 root = o - f * (k * p.jetWidth * 0.9f) + radial * p.jetWidth * 0.3f;
                        Add(PType.Jet, root, ax, p.jetLength * J(j) * a * (1f - 0.2f * k), p.jetWidth * J(j * 0.5f), p.flashTime * dur * J(0.3f), p.flashRadiance * 0.6f * a, Vector3.zero, d);
                    }
                }
            }
            if (c.disc.On)
                Add(PType.Disc, o + f * p.discDistance, f, 0f, p.discSize * J(j) * c.disc.amount, p.flashTime * c.disc.duration, p.flashRadiance * 0.25f * c.disc.amount, Vector3.zero, c.disc.delay);
            // secondary flash: lumpy burning gas a little way out, hot then cooling
            if (c.fireball.On && R() < p.fireChance)
            {
                float a = c.fireball.amount;
                for (int i = 0; i < p.fireLumps; i++)
                {
                    Vector3 pos = o + f * p.fireDistance * (0.6f + 0.8f * R()) + (right * (R() - 0.5f) + up * (R() - 0.5f)) * p.fireSize * 0.8f;
                    Add(PType.Fire, pos, f, 0f, p.fireSize * J(j) * Mathf.Sqrt(a) * (i == 0 ? 1f : 0.7f), p.fireTime * c.fireball.duration * J(0.3f), p.flashRadiance * 0.8f * a,
                        f * 8f + (pos - o - f * Vector3.Dot(pos - o, f)) * 6f, c.fireball.delay + R() * 0.008f, 1.6f, 10f);
                }
            }
            // smoke: puffs shot down the bore that brake hard, swell, rise a little and thin out
            if (c.smoke.On)
            {
                float a = c.smoke.amount;
                for (int i = 0; i < p.smokePuffs; i++)
                {
                    float frac = p.smokePuffs > 1 ? i / (p.smokePuffs - 1f) : 0.5f;
                    Vector3 v = f * p.smokeSpeed * (0.3f + 0.9f * frac) * J(0.3f) + (right * (R() - 0.5f) + up * (R() - 0.3f)) * p.smokeSpeed * 0.15f;
                    Add(PType.Smoke, o + f * p.coreSize * 0.5f, f, 0f, p.smokeSize * J(j), p.smokeLife * c.smoke.duration * J(0.25f), p.smokeOpacity * a, v, c.smoke.delay + 0.01f + 0.02f * R(), 2.2f, 7f);
                }
            }
            if (c.sparks.On)
                for (int i = 0; i < Mathf.RoundToInt(p.sparks * c.sparks.amount); i++)
                {
                    Vector3 v = (f + (right * (R() - 0.5f) + up * (R() - 0.5f)) * 0.5f).normalized * p.sparkSpeed * (0.5f + R());
                    Add(PType.Spark, o, v.normalized, 0f, 0.008f * Mathf.Max(1f, p.caliberMm / 15f), p.sparkLife * c.sparks.duration * J(0.5f), p.flashRadiance * 0.08f, v, c.sparks.delay, 0f, 1.5f);
                }
            if (c.light.On)
            {
                lightAge = 0f; lightPeak = p.lightLumen * c.light.amount * J(0.25f); lightDelay = c.light.delay; lightDur = p.lightTime * c.light.duration;
                EnsureLight();
                flashLight.transform.position = o + f * p.coneLength * 0.4f;
                flashLight.range = p.lightRange;
            }
        }

        /// <summary>Make the flash light now rather than at the first round.</summary>
        public void Prewarm() => EnsureLight();

        /// <summary>Drop every particle and put the light out.</summary>
        public void Clear()
        {
            parts.Clear();
            lightAge = 99f; lightDelay = 0f;
            if (flashLight) { flashLight.intensity = 0f; flashLight.enabled = false; }
        }

        void EnsureLight()
        {
            if (flashLight) return;
            var go = new GameObject("GunFlash Light") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            flashLight = go.AddComponent<Light>();
            go.AddComponent<HDAdditionalLightData>().volumetricDimmer = 0.25f;   // a little glow in the haze, not a lit fog bank
            flashLight.type = LightType.Point;
            flashLight.lightUnit = UnityEngine.Rendering.LightUnit.Lumen;
            flashLight.color = new Color(1f, 0.78f, 0.5f);
            flashLight.shadows = LightShadows.None;   // a frame or two: not worth a shadow map per round
            flashLight.enabled = false;
        }

        /// <summary>Advance the particles (and fire rounds while firing).</summary>
        public void Tick(float dt)
        {
            Vector3 lift = Vector3.up * 0.4f;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var q = parts[i];
                if (q.delay > 0f) { q.delay -= dt; parts[i] = q; continue; }
                q.age += dt;
                if (q.age >= q.life) { parts.RemoveAt(i); continue; }
                q.age01 = q.age / q.life;
                if (q.type == (float)PType.Smoke) q.vel += ((wind + lift) - q.vel) * (1f - Mathf.Exp(-q.drag * dt));
                else if (q.drag > 0f) q.vel *= Mathf.Exp(-q.drag * dt);
                q.pos += q.vel * dt;
                if (q.type == (float)PType.Spark) q.axis = q.vel.normalized;
                parts[i] = q;
            }
            // rounds fired this tick come last, so they are drawn at age 0 (a flash that lasts one frame is seen at full strength)
            if (firing && rpm > 0f)
            {
                fireClock -= dt;
                while (fireClock <= 0f) { Shot(); fireClock += 60f / rpm; }
            }
            else fireClock = 0f;
            if (flashLight)
            {
                if (lightDelay > 0f) lightDelay -= dt; else lightAge += dt;
                float k = lightDelay > 0f ? 0f : Mathf.Exp(-lightAge / Mathf.Max(lightDur, 1e-3f) * 2f) * (lightAge < lightDur * 2.5f ? 1f : 0f);
                flashLight.intensity = lightPeak * k;
                flashLight.enabled = k > 0.01f;
            }
        }

        /// <summary>Particles alive and visible now (delayed ones are not drawn yet).</summary>
        public int CopyVisible(List<Particle> dst)
        {
            int n = 0;
            foreach (var q in parts) if (q.delay <= 0f) { dst.Add(q); n++; }
            return n;
        }

        public Vector3 SunDir => sun ? -sun.transform.forward : Vector3.up;
        public Color SunLux => sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;

        void Update()
        {
            if (Application.isPlaying && autoAdvance) Tick(Time.deltaTime);
        }
    }
}
