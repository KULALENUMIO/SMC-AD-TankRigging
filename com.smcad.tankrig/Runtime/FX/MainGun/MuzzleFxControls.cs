// Per-effect controls for a shot: which parts of the firing effect appear, how strong, and when.
// Each part is a channel with on/off, amount, delay and duration. Its envelope (the built-in timing of that stage) is
// evaluated at local time (t - delay) / duration, so delay moves the stage later and duration stretches it.
// The look group scales what the renderer draws (density, flame brightness, lifetimes) without touching the motion.
using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace TankRig.FX
{
    [Serializable]
    public class FxLook
    {
        [Tooltip("smoke opacity (also its self-shadowing)")] [Min(0f)] public float smokeDensity = 1f;
        [Tooltip("flame glow")] [Min(0f)] public float flameBrightness = 1f;
        [Tooltip("how long the smoke lingers before it is gone (scales dissipation and the final fade)")] [Min(0.1f)] public float smokeLifetime = 1f;
        [Tooltip("dust opacity")] [Min(0f)] public float dustDensity = 1f;
        [Tooltip("how long the fine dust hangs in the air")] [Min(0.1f)] public float dustLifetime = 1f;
    }

    [Serializable]
    public class MuzzleFxControls
    {
        [Header("Gas stages")]
        [Tooltip("grey-brown gas pushed out ahead of the round, with a small burning core")] public FxChannel precursor = new FxChannel();
        [Tooltip("the grey vortex ring right at the muzzle")] public FxChannel umbrella = new FxChannel();
        [Tooltip("the white-hot normal shock a little way out")] public FxChannel machDisk = new FxChannel();
        [Tooltip("the lumpy burning fireball")] public FxChannel fireball = new FxChannel();
        [Tooltip("the bore blow-down jet (drives the fireball downrange)")] public FxChannel jet = new FxChannel();
        [Tooltip("the bore evacuator's white stream")] public FxChannel evacuator = new FxChannel();
        [Header("Blast")]
        [Tooltip("the blast wave: its kick on the gas, the refraction ring and its glow")] public FxChannel blastWave = new FxChannel();
        [Tooltip("the muzzle flash light (lights the scene; drives the lens flare)")] public FxChannel flash = new FxChannel();
        [Header("Dust")]
        [Tooltip("dust thrown up from the ground around the tank")] public FxChannel groundDust = new FxChannel();
        [Tooltip("dust shaken off the tank's upper surfaces")] public FxChannel hullDust = new FxChannel();
        [Tooltip("the air blown back along the tank that carries the dust away")] public FxChannel backBlow = new FxChannel();
        [Tooltip("gravel thrown up from the ground (MuzzleDebris): on/off, amount (number of stones), delay")] public FxChannel debris = new FxChannel();
        [Header("Look")]
        public FxLook look = new FxLook();

        /// <summary>"channel.field=value,..." overrides, e.g. "fireball.amount=0.5,evacuator.delay=0.1,look.smokeDensity=0.8,flash.enabled=0".</summary>
        public void Apply(string kv)
        {
            if (string.IsNullOrEmpty(kv)) return;
            foreach (var part in kv.Split(','))
            {
                var eq = part.Split('=');
                var path = eq[0].Trim().Split('.');
                if (eq.Length != 2 || path.Length != 2) { Debug.LogWarning("MuzzleFx: bad override " + part); continue; }
                var cf = GetType().GetField(path[0], BindingFlags.Public | BindingFlags.Instance);
                object target = cf?.GetValue(this);
                var f = target?.GetType().GetField(path[1], BindingFlags.Public | BindingFlags.Instance);
                if (f == null) { Debug.LogWarning("MuzzleFx: unknown control " + eq[0]); continue; }
                string v = eq[1].Trim();
                if (f.FieldType == typeof(bool)) f.SetValue(target, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                else if (f.FieldType == typeof(float)) f.SetValue(target, float.Parse(v, CultureInfo.InvariantCulture));
            }
        }
    }
}
