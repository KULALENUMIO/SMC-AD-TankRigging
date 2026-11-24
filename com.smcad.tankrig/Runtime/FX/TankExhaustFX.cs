// Diesel engine exhaust: short-lived puffs from the tank's exhaust outlets, drawn by GunFlashPass.
// The engine load (throttle) sets how much comes out and how fast. At idle it is a thin grey-blue haze that rises a
// little; under load (as in reference footage) it is a dense pale jet shot out level, straight
// away from the outlet, about 5 m long, densest while the engine is still spinning up; opening the throttle adds a
// darker puff that clears over a second. The gas lives in the world (stays behind a moving tank), drifts with the
// wind, touches nothing and thins out quickly.
using System.Collections.Generic;
using UnityEngine;

namespace TankRig.FX
{
    [ExecuteAlways]
    public class TankExhaustFX : MonoBehaviour, IFlashParticles
    {
        [Tooltip("exhaust outlets (empty = every child of the tank named Exhaust*)")]
        public Transform[] outlets = new Transform[0];
        [Tooltip("the tank (its TankController, if any, drives the engine load)")]
        public Transform tank;
        [Tooltip("direction the gas leaves in, in the tank's frame (x right, y up, z forward)")]
        public Vector3 exitDirection = new Vector3(0f, 0.55f, -0.85f);
        [Tooltip("the gas leaves along each outlet's own forward (blue arrow) instead of exitDirection")] public bool alongOutlet = true;
        [Header("Engine")]
        [Tooltip("engine load 0..1 (set by code, or read from the tank's throttle)")] [Range(0f, 1f)] public float load;
        public bool readTankThrottle = true;
        [Tooltip("how fast the engine follows the throttle (1/s)")] public float response = 3f;
        [Header("Gas")]
        [Tooltip("puffs per second per outlet at idle / at full load")] public float rateIdle = 12f, rateFull = 40f;
        [Tooltip("m/s leaving the outlet at idle / full load")] public float speedIdle = 4f, speedFull = 12f;
        [Tooltip("m: puff size at the outlet (grows about 4x)")] public float size = 0.2f;
        [Tooltip("s: how long a puff lasts")] public float life = 0.9f;
        [Tooltip("opacity at idle / full load")] [Range(0f, 1f)] public float opacityIdle = 0.1f, opacityFull = 0.3f;
        [Tooltip("soot at steady full load (0 grey-blue haze .. 1 black)")] [Range(0f, 1f)] public float sootLoad = 0.12f;
        [Tooltip("extra soot when the throttle is opened (the dark puff)")] [Range(0f, 1f)] public float sootKick = 0.35f;
        [Header("Jet (under load)")]
        [Tooltip("under load the gas leaves level (0 = always along the outlet / exitDirection)")] [Range(0f, 1f)] public float jetLevel = 0.85f;
        [Tooltip("m/s: jet speed at full load (with jetDrag it carries about speed / drag metres)")] public float jetSpeed = 16f;
        [Tooltip("1/s: how fast the air brakes the jet")] public float jetDrag = 3f;
        [Tooltip("opacity at full load while the engine spins up (the dense jet)")] [Range(0f, 1f)] public float jetOpacity = 0.45f;
        [Tooltip("m/s: rise of the gas at idle / under load (hot, but the jet stays level)")] public float riseIdle = 0.6f, riseLoad = 0.1f;
        [Tooltip("s: how long the dark puff takes to clear")] public float sootClear = 1.2f;
        [Header("Scene")]
        public Light sun;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;
        public Vector3 wind = new Vector3(1.5f, 0f, 2.5f);
        public bool autoAdvance = true;

        readonly List<GunFlashFX.Particle> parts = new List<GunFlashFX.Particle>();
        float engine, soot, emitAcc, spinUp;
        System.Random rng = new System.Random(4321);
        float R() => (float)rng.NextDouble();
        TankController controller;

        public Vector3 SunDir => sun ? -sun.transform.forward : Vector3.up;
        public Color SunLux => sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;
        public Color Ambient => ambient;

        void OnEnable() { if (!sun) sun = FxScene.FindSun(); if (!GunFlashFX.Sources.Contains(this)) GunFlashFX.Sources.Add(this); FindOutlets(); }
        void OnDisable() => GunFlashFX.Sources.Remove(this);

        void FindOutlets()
        {
            var root = tank ? tank : transform;
            if (outlets == null || outlets.Length == 0)
            {
                var list = new List<Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name.StartsWith("Exhaust")) list.Add(t);
                outlets = list.ToArray();
            }
            controller = root.GetComponentInParent<TankController>(true);
            if (!controller) controller = root.GetComponentInChildren<TankController>(true);
        }

        float ReadThrottle()
        {
            if (!readTankThrottle || !controller) return load;
            return Mathf.Abs(controller.CurrentInput.throttle);
        }

        /// <summary>Advance the engine and the gas by dt.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            float target = Mathf.Clamp01(ReadThrottle());
            float before = engine;
            engine = Mathf.MoveTowards(engine, target, response * dt);
            // opening the throttle: a dark puff that clears as the turbo catches up
            if (engine > before) soot = Mathf.Min(1f, soot + (engine - before) * 3f * sootKick);
            soot *= Mathf.Exp(-dt / Mathf.Max(sootClear, 0.05f));
            // spinning up: dense while the load climbs, eases off over a couple of seconds at a steady load
            spinUp = Mathf.Max(spinUp * Mathf.Exp(-dt / 2f), engine > before + 1e-4f ? 1f : 0f);

            // move and age the gas
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var q = parts[i];
                q.age += dt;
                if (q.age >= q.life) { parts.RemoveAt(i); continue; }
                q.age01 = q.age / q.life;
                Vector3 air = wind + Vector3.up * q.extra.x;   // hot: it rises a little (the jet hardly)
                q.vel += (air - q.vel) * (1f - Mathf.Exp(-q.drag * dt));
                q.pos += q.vel * dt;
                parts[i] = q;
            }

            // emit (spread over the frame so a moving tank leaves an even trail)
            if (outlets == null || outlets.Length == 0) return;
            var root = tank ? tank : transform;
            Vector3 dir = root.TransformDirection(exitDirection.normalized);
            float rate = Mathf.Lerp(rateIdle, rateFull, engine);
            float jet = engine * jetLevel;   // how much of a level jet it is now
            float spd = Mathf.Lerp(Mathf.Lerp(speedIdle, speedFull, engine), jetSpeed, jet);
            float drag = Mathf.Lerp(5f, jetDrag, jet);
            float rise = Mathf.Lerp(riseIdle, riseLoad, engine);
            float op = Mathf.Lerp(opacityIdle, opacityFull, engine);
            op = Mathf.Min(Mathf.Lerp(op, jetOpacity, engine * spinUp) * (1f + 1.2f * soot), 0.95f);
            float sootNow = Mathf.Clamp01(sootLoad * engine + soot);
            emitAcc += rate * dt;
            int n = Mathf.FloorToInt(emitAcc);
            emitAcc -= n;
            for (int k = 0; k < n; k++)
            {
                float back = R() * dt;   // born somewhere within this frame
                foreach (var o in outlets)
                {
                    if (!o) continue;
                    Vector3 d0 = alongOutlet ? o.forward : dir;
                    // the level jet: the outlet's direction laid flat (an outlet pointing up: away from the tank's middle)
                    Vector3 flat = Vector3.ProjectOnPlane(d0, Vector3.up);
                    if (flat.sqrMagnitude < 0.09f) flat = Vector3.ProjectOnPlane(o.position - root.position, Vector3.up);
                    Vector3 d = flat.sqrMagnitude > 1e-4f ? Vector3.Slerp(d0.normalized, (flat.normalized + Vector3.down * 0.05f).normalized, jet) : d0;
                    Vector3 v = (d + Random.insideUnitSphere * Mathf.Lerp(0.15f, 0.07f, jet)).normalized * spd * (0.8f + 0.4f * R());
                    Vector3 p = o.position + v * back;
                    parts.Add(new GunFlashFX.Particle
                    {
                        pos = p, type = (float)GunFlashFX.PType.Exhaust, axis = new Vector3(sootNow, 0f, 0f),
                        width = size * (0.8f + 0.4f * R()), life = life * (0.7f + 0.6f * R()), intensity = op * (0.7f + 0.6f * R()),
                        vel = v, seed = R() * 100f, grow = Mathf.Lerp(3f, 4.5f, jet) + 2f * R(), drag = drag, age = back,
                        extra = new Vector4(rise, 0f, 0f, 0f)
                    });
                }
            }
        }

        public string Stats()
        {
            float op = 0f, so = 0f; Vector3 c = Vector3.zero;
            foreach (var q in parts) { op += q.intensity; so += q.axis.x; c += q.pos; }
            int n = Mathf.Max(parts.Count, 1);
            return $"exhaust: engine {engine:F2} soot {soot:F2} spin-up {spinUp:F2}, {parts.Count} puffs, mean opacity {op / n:F2}, mean soot {so / n:F2}, centre {c / n}";
        }

        public int CopyVisible(List<GunFlashFX.Particle> dst) { dst.AddRange(parts); return parts.Count; }

        void Update() { if (Application.isPlaying && autoAdvance) Tick(Time.deltaTime); }
    }
}
