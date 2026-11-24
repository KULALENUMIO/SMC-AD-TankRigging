// One part of an effect: on/off, how strong, and when. Its envelope (the built-in timing of that part) is evaluated at
// local time (t - delay) / duration, so delay moves it later and duration stretches it.
using System;
using UnityEngine;

namespace TankRig.FX
{
    [Serializable]
    public class FxChannel
    {
        public bool enabled = true;
        [Tooltip("strength (1 = the tuned look). For gas stages it scales the gas, heat and fuel put in")]
        [Min(0f)] public float amount = 1f;
        [Tooltip("seconds later than the built-in timing (negative = earlier)")]
        public float delay = 0f;
        [Tooltip("time stretch of the stage (2 = lasts twice as long). For the blast wave it also slows the wave")]
        [Min(0.05f)] public float duration = 1f;

        public bool On => enabled && amount > 0f;
        /// <summary>The stage's own time for shot time t.</summary>
        public float Local(float t) => (t - delay) / Mathf.Max(duration, 0.05f);
        /// <summary>The stage's envelope at shot time t (0 when off); amount is applied separately.</summary>
        public float Env(Func<float, float> env, float t) => On ? env(Local(t)) : 0f;
        public float Amount => On ? amount : 0f;
    }
}
