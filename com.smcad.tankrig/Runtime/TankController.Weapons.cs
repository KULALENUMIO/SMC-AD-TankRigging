using System.Collections.Generic;
using UnityEngine;

namespace TankRig
{
    /// <summary>Machine guns (any number, each with its own spec and trigger) and secondary turrets.</summary>
    public partial class TankController
    {
        /// <summary>A main gun or secondary cannon shot, for custom effects.</summary>
        public struct GunShot
        {
            public Transform muzzle;     // position and direction of the shot
            public float power;          // gun power (1 = standard main gun)
            public TankSecondaryTurret secondary; // null for the main gun
            public bool hit;
            public RaycastHit hitInfo;   // where the straight shot line hits (when hit)
        }

        /// <summary>One machine gun round, for custom effects.</summary>
        public struct RoundShot
        {
            public Transform point;
            public Vector3 direction;    // with the spread
            public MachineGunSpec spec;
            public bool tracer;          // this round shows a tracer
            public bool hit;
            public RaycastHit hitInfo;
        }

        /// <summary>Raised for every main gun and secondary cannon shot (after the recoil was applied).</summary>
        public event System.Action<TankController, GunShot> GunFired;
        /// <summary>Raised for every machine gun round (also machine-gun secondary turrets).</summary>
        public event System.Action<TankController, RoundShot> RoundFired;

        void InitWeapons()
        {
            // tanks rigged before v0.7 have one machine gun in the old fields
            if ((machineGuns == null || machineGuns.Length == 0) && machineGun)
            {
                machineGuns = new[]
                {
                    new TankMachineGun
                    {
                        name = "機銃",
                        point = machineGun,
                        tracer = machineGunTracer,
                        flash = machineGunFlash,
                        spec = new MachineGunSpec { roundsPerMinute = 60f / Mathf.Max(0.01f, machineGunInterval), range = range },
                    },
                };
            }
            machineGuns ??= new TankMachineGun[0];
            secondaryTurrets ??= new TankSecondaryTurret[0];
            foreach (var mg in machineGuns) mg?.Init();

            // a part turned by two weapons would fight: the main gun keeps its parts, then the first secondary turret
            var used = new HashSet<Transform>();
            foreach (var t in new[] { turret, gun, barrel }) if (t) used.Add(t);
            foreach (var st in secondaryTurrets)
            {
                if (st == null) continue;
                st.turret = Claim(st.turret, used, st.name, "砲塔");
                st.gun = Claim(st.gun, used, st.name, "砲架");
                st.barrel = Claim(st.barrel, used, st.name, "砲身");
                st.Init(transform);
            }
        }

        Transform Claim(Transform part, HashSet<Transform> used, string owner, string what)
        {
            if (!part) return null;
            if (used.Add(part)) return part;
            Debug.LogWarning($"[TankRig] {name}: 副砲塔「{owner}」の{what}（{part.name}）は主砲かほかの副砲塔でも使われているので、外して動かします。", this);
            return null;
        }

        static bool Held(WeaponTrigger t, TankInputState input) => t switch
        {
            WeaponTrigger.Main => input.holdMain,
            WeaponTrigger.MachineGun => input.fireMachineGun,
            _ => input.fireSecondary,
        };

        void AimSecondaryWeapons(float dt)
        {
            foreach (var st in secondaryTurrets)
                st?.Aim(AimPoint, dt, recoilReturn);

            // ball-mounted machine guns: turn towards the aim point within their cone
            foreach (var mg in machineGuns)
            {
                if (mg == null || !mg.point || mg.aimCone <= 0f || !mg.point.parent) continue;
                Transform p = mg.point, parent = p.parent;
                Vector3 restF = parent.TransformDirection(mg.restLocal * Vector3.forward);
                Vector3 restU = parent.TransformDirection(mg.restLocal * Vector3.up);
                Vector3 want = AimPoint - p.position;
                if (want.sqrMagnitude < 1e-4f) continue;
                Vector3 dir = Vector3.RotateTowards(restF, want.normalized, mg.aimCone * Mathf.Deg2Rad, 0f);
                p.rotation = Quaternion.RotateTowards(p.rotation, Quaternion.LookRotation(dir, restU), 120f * dt);
            }
        }

        void TickSecondaryWeapons(TankInputState input, float dt)
        {
            foreach (var mg in machineGuns)
            {
                if (mg == null || !mg.point) continue;
                int n = mg.Feed.Tick(mg.spec, Held(mg.trigger, input), dt);
                for (int i = 0; i < n; i++) FireRound(mg.point, mg.spec, mg.tracer, mg.flash, mg.Feed);
            }
            foreach (var st in secondaryTurrets)
            {
                if (st == null || !st.muzzle) continue;
                if (st.weapon == SecondaryWeapon.Cannon)
                {
                    st.TickReload(dt);
                    // on the main gun's button it fires with each click; on a held button whenever it is loaded
                    bool pull = st.trigger == WeaponTrigger.Main ? input.fireMain : Held(st.trigger, input);
                    if (pull && st.Ready) FireSecondary(st);
                }
                else
                {
                    int n = st.Feed.Tick(st.machineGun, Held(st.trigger, input), dt);
                    for (int i = 0; i < n; i++) FireRound(st.muzzle, st.machineGun, st.tracer, st.mgFlash, st.Feed);
                }
            }
        }

        /// <summary>Fires a secondary turret's gun (cannon) now, ignoring its reload.</summary>
        public void FireSecondary(TankSecondaryTurret st)
        {
            if (st == null || !st.muzzle) return;
            st.OnFired(recoilDistance, maxRecoilDistance);
            float p = Mathf.Max(0f, st.power);
            if (builtInEffects && !externalGunFlashes && st.muzzleFlash) st.muzzleFlash.Play(true);
            if (builtInEffects && !externalGunFlashes && st.muzzleSmoke) st.muzzleSmoke.Play(true);
            if (aimCamera) aimCamera.Shake(0.4f * p);
            Kick(st.muzzle, p);
            float fx = Mathf.Clamp(p, 0.2f, 1f);
            bool didHit = TankPhysics.Raycast(st.muzzle.position, st.muzzle.forward, st.range, groundMask, transform, out RaycastHit hit);
            if (didHit && builtInEffects)
            {
                Emit(impactDust, hit.point, Mathf.CeilToInt(30 * fx));
                Emit(impactSparks, hit.point, Mathf.CeilToInt(20 * fx));
            }
            GunFired?.Invoke(this, new GunShot { muzzle = st.muzzle, power = p, secondary = st, hit = didHit, hitInfo = hit });
        }

        /// <summary>One machine-gun round: spread, tracer every N-th round, muzzle flash, hit effects.</summary>
        void FireRound(Transform p, MachineGunSpec spec, ParticleSystem tracer, ParticleSystem flashFx, GunFeed feed)
        {
            Vector3 dir = Spread(p.forward, spec.spread);
            bool isTracer = feed.NextIsTracer(spec);
            if (builtInEffects && tracer && isTracer)
            {
                float speed = Mathf.Max(10f, spec.tracerSpeed);
                var ep = new ParticleSystem.EmitParams
                {
                    position = p.position,
                    velocity = dir * speed,
                    startLifetime = Mathf.Clamp(spec.range / speed, 0.1f, 6f),
                    applyShapeToPosition = false,
                };
                tracer.Emit(ep, 1);
            }
            if (builtInEffects && !externalGunFlashes && flashFx) flashFx.Emit(1);
            bool didHit = TankPhysics.Raycast(p.position, dir, spec.range, groundMask, transform, out RaycastHit hit);
            if (didHit && builtInEffects)
            {
                float s = Mathf.Max(0f, spec.impactScale);
                Emit(impactDust, hit.point, Mathf.RoundToInt(2 * s));
                Emit(impactSparks, hit.point, Mathf.RoundToInt(3 * s));
            }
            RoundFired?.Invoke(this, new RoundShot { point = p, direction = dir, spec = spec, tracer = isTracer, hit = didHit, hitInfo = hit });
        }

        static Vector3 Spread(Vector3 forward, float degrees)
        {
            if (degrees <= 0f) return forward;
            Vector2 r = Random.insideUnitCircle * Mathf.Tan(Mathf.Min(degrees, 30f) * Mathf.Deg2Rad);
            return (Quaternion.LookRotation(forward) * new Vector3(r.x, r.y, 1f)).normalized;
        }
    }
}
