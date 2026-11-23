// Gravel thrown up by the main gun's blast: small stones made of the ground they lie on.
// At the shot every stone is released when the blast wave reaches it (a few ms: with the bang), thrown outward from
// under the muzzle and up, hardest in front of the gun and near it; it flies ballistically (gravity, a little air
// drag, tumbling) and drops out of sight when it comes back down to the ground (no contact with anything: in a real
// field the grass would hide where it lands).
// The stones take the ground's look: a mesh ground lends its material (same textures, darker or lighter copies);
// a terrain lends the terrain layer that dominates under the gun. The rocks are real lit meshes (HDRP shades and
// shadows them), drawn instanced (one batch per rock shape and shade). They pass through the tank (no collisions but the ground).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    [ExecuteAlways]
    public class MuzzleDebris : MonoBehaviour
    {
        [Tooltip("the main-gun shot this gravel belongs to (fires with it)")]
        public MuzzleBlastSim sim;
        [Tooltip("without a sim (the particle main gun): the muzzle, the tank (stones are not picked up under it) and the gravel's channel; Throw() is called by the owner")]
        public Transform muzzle;
        public Transform ignoreRoot;
        public FxChannel channel = new FxChannel();
        Transform Muzzle => sim ? sim.muzzle : muzzle;
        Transform IgnoreRoot => sim ? sim.ignoreRoot : ignoreRoot;
        FxChannel Channel => sim ? sim.fx.debris : channel;
        float MuzzleHeight => sim ? sim.muzzleHeight : 2.3f;   // only where no ground is hit
        [Header("Amount and reach")]
        public int count = 600;
        [Tooltip("m: stones are picked up all round the tank: this far in front of the muzzle, behind the tank's rear, and out from its sides")]
        public float radiusFront = 9f, radiusBack = 5f, radiusSide = 7f;
        [Tooltip("m/s: launch speed (strongest at the muzzle, still well above zero all round the tank)")] public float speed = 40f;
        [Tooltip("share of the launch that goes up (the blast runs along the ground: mostly a flat spray)")] [Range(0f, 1f)] public float lift = 0.35f;
        [Tooltip("deg: scatter of the throw direction around 'straight out from the tank'")] public float spread = 25f;
        [Tooltip("air drag (1 = a rock of ~2.6 g/cm3 in air, Cd 0.5)")] public float dragScale = 1f;
        [Tooltip("how much harder it throws in front of the gun than behind")] [Range(0f, 1f)] public float forwardBias = 0.15f;
        [Tooltip("m/s: fastest upward launch (14 m/s = about 10 m high)")] public float maxUp = 14f;
        [Tooltip("m: the blast weakens over this distance from the muzzle (it never drops below 'floor')")] public float falloff = 6f;
        [Tooltip("weakest throw anywhere round the tank (share of the muzzle's)")] [Range(0f, 1f)] public float floor = 0.45f;
        [Tooltip("behind the muzzle the blast runs back along the tank: extra push backwards (share of the launch speed)")] [Range(0f, 1f)] public float backBlow = 0.35f;
        [Header("Stones")]
        [Tooltip("m: smallest / largest stone (diameter); most are small, a few big ones")] public float sizeMin = 0.006f, sizeMax = 0.2f;
        [Tooltip("bigger = more small stones")] public float sizePower = 3.5f;
        public bool castShadows = true;

        struct Stone { public Vector3 pos, vel, spin; public Quaternion rot; public float size, release, groundY; public bool live; }
        readonly List<Stone> stones = new List<Stone>();
        Mesh[] meshes;
        Material[] mats;
        string lookKey;
        // instanced drawing: one batch per (rock shape, shade)
        List<Matrix4x4>[] batches;
        float t = -1f, fallbackY;
        Vector3 start;
        System.Random rng = new System.Random(77);
        float R() => (float)rng.NextDouble();

        void OnEnable() { if (sim) sim.Fired += OnFired; }
        void OnDisable() { if (sim) sim.Fired -= OnFired; }
        void OnDestroy() { if (mats != null) foreach (var mm in mats) if (mm) DestroyImmediate(mm); }

        void OnFired(MuzzleBlastSim s) => Throw();

        /// <summary>Make the rock meshes and the ground's look now (they read the ground texture back from the GPU), not at the first shot.</summary>
        public void Prewarm()
        {
            if (!Muzzle) return;
            var m = Muzzle.position;
            fallbackY = m.y - MuzzleHeight;
            BuildLook(new Vector3(m.x, GroundY(m), m.z));
        }

        /// <summary>Throw a fresh lot of gravel from the ground under the gun (the sim calls this when it fires).</summary>
        public void Throw()
        {
            if (!Muzzle) return;
            var ch = Channel;
            if (!ch.On) { stones.Clear(); t = -1f; Sync(); return; }
            Physics.SyncTransforms();   // colliders moved or made this frame must be seen by the ground raycasts
            Vector3 m = Muzzle.position;
            fallbackY = m.y - MuzzleHeight;
            Vector3 bore = Vector3.ProjectOnPlane(Muzzle.forward, Vector3.up).normalized;
            Vector3 under = new Vector3(m.x, GroundY(m), m.z);
            start = under;
            float muzzleH = Mathf.Max(m.y - under.y, 0.3f);
            BuildLook(under);
            Footprint();
            stones.Clear();
            Vector3 side = Vector3.Cross(Vector3.up, bore);
            float L = TankLength(under, bore);   // the tank's length behind the muzzle (from its hull)
            int n = Mathf.RoundToInt(count * ch.amount);
            for (int i = 0, tries = 0; i < n && tries < n * 20; tries++)
            {
                // where: anywhere round the tank (a capsule round the line from the muzzle back to the tank's rear),
                // a little denser towards the muzzle
                float u = Mathf.Lerp(-L - radiusBack, radiusFront, R());
                float w = Mathf.Lerp(-radiusSide, radiusSide, R());
                Vector3 p = under + bore * u + side * w;
                float along = Mathf.Clamp(u, -L, 0f);
                Vector3 axis = under + bore * along;                       // nearest point of the tank's line
                Vector3 off = p - axis;
                float lim = u > 0f ? radiusFront : u < -L ? radiusBack : radiusSide;
                if (off.magnitude > lim) continue;
                if (UnderTank(p)) continue;                                // nothing lies loose under the hull
                float dm = Vector3.Distance(new Vector3(p.x, under.y, p.z), under);
                float strength = floor + (1f - floor) / (1f + (dm / falloff) * (dm / falloff));
                if (R() > 0.35f + 0.65f * strength) continue;              // more stones where the blast is strong
                i++;
                float gy = GroundY(p);
                p.y = gy;
                Vector3 flat = new Vector3(off.x, 0f, off.z);
                Vector3 dir = flat.sqrMagnitude > 1e-4f ? flat.normalized : (u >= 0f ? bore : side * Mathf.Sign(w + 1e-4f));   // out from the tank
                strength *= 1f - forwardBias * 0.5f * (1f - Vector3.Dot(dir, bore));
                float size = Mathf.Lerp(sizeMin, sizeMax, Mathf.Pow(R(), sizePower));
                // the blast's push is about the same pressure on every stone, so small ones leave faster (impulse / mass)
                float v = speed * strength * (0.5f + 0.8f * R()) * Mathf.Clamp(Mathf.Sqrt(0.03f / Mathf.Max(size, 0.004f)), 0.6f, 1.6f);
                float yaw = (R() * 2f - 1f) * spread;
                dir = Quaternion.AngleAxis(yaw, Vector3.up) * dir;
                float up = Mathf.Min(v * lift * (0.4f + 1.2f * R()), maxUp * (0.5f + 0.5f * R()));
                Vector3 vel = dir * v * Mathf.Sqrt(Mathf.Max(1f - lift * lift, 0.2f)) + Vector3.up * up;
                float behind = Mathf.Clamp01(-u / 1.5f);   // behind the muzzle: blown back along the tank as well
                vel -= bore * (speed * backBlow * behind * strength * (0.5f + R()));
                float dist = Vector3.Distance(p, m);
                float release = ch.delay + dist / 340f + R() * 0.01f;   // when the blast wave gets there
                stones.Add(new Stone
                {
                    pos = p + Vector3.up * size * 0.5f, vel = vel, size = size, groundY = gy,
                    rot = Quaternion.Euler(R() * 360f, R() * 360f, R() * 360f), spin = Random.onUnitSphere * Mathf.Min(v * 40f / Mathf.Max(size * 20f, 0.3f), 1500f),
                    release = release, live = true
                });
            }
            t = 0f;
            Sync();
        }

        /// <summary>Drop every stone.</summary>
        public void Clear() { stones.Clear(); t = -1f; Sync(); }

        /// <summary>Debug: stones thrown, in the air; highest point; pool size; look.</summary>
        public string Stats()
        {
            int air = 0, live = 0; float top = float.MinValue;
            float far = 0f;
            foreach (var st in stones) { if (!st.live) continue; live++; if (t >= st.release) air++; top = Mathf.Max(top, st.pos.y - st.groundY); far = Mathf.Max(far, new Vector2(st.pos.x - start.x, st.pos.z - start.z).magnitude); }
            return $"gravel: t {t:F3} s, {stones.Count} thrown, {live} live, {air} flying, highest {top:F2} m, farthest {far:F1} m from the muzzle, batches {(batches == null ? 0 : batches.Length)}, mats {(mats == null ? 0 : mats.Length)} ({(mats != null && mats.Length > 0 ? mats[0].shader.name : "-")})";
        }

        // the hull and tracks (renderers low on the tank) as a box in the tank's frame: stones are not picked up under it
        Bounds hull; Matrix4x4 hullToLocal; bool hasHull;
        void Footprint()
        {
            hasHull = false;
            var root = IgnoreRoot;
            if (!root) return;
            hullToLocal = root.worldToLocalMatrix;
            foreach (var rend in root.GetComponentsInChildren<Renderer>())
            {
                if (rend.bounds.center.y > root.position.y + 1.6f) continue;   // turret, gun: they do not sit on the ground
                var b = rend.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                    var lp = hullToLocal.MultiplyPoint3x4(corner);
                    if (!hasHull) { hull = new Bounds(lp, Vector3.zero); hasHull = true; } else hull.Encapsulate(lp);
                }
            }
            if (hasHull) hull.Expand(new Vector3(-0.3f, 0f, -0.3f));   // world-aligned renderer boxes are a bit generous
        }

        float TankLength(Vector3 under, Vector3 bore)
        {
            if (!hasHull) return 8f;
            var toWorld = hullToLocal.inverse;
            float minProj = 0f;
            for (int c = 0; c < 8; c++)
            {
                var corner = hull.center + Vector3.Scale(hull.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                minProj = Mathf.Min(minProj, Vector3.Dot(toWorld.MultiplyPoint3x4(corner) - under, bore));
            }
            return Mathf.Max(-minProj, 2f);
        }

        bool UnderTank(Vector3 p)
        {
            if (!hasHull) return false;
            var lp = hullToLocal.MultiplyPoint3x4(p);
            return lp.x > hull.min.x && lp.x < hull.max.x && lp.z > hull.min.z && lp.z < hull.max.z;
        }

        float GroundY(Vector3 p)
        {
            var ter = Terrain.activeTerrain;
            if (ter) return ter.SampleHeight(p) + ter.transform.position.y;
            float best = float.MaxValue, y = fallbackY;
            int hits = Physics.RaycastNonAlloc(p + Vector3.up * 4f, Vector3.down, GroundLook.Hits, 30f, ~0, QueryTriggerInteraction.Ignore);
            for (int hi = 0; hi < hits; hi++)
            {
                var h = GroundLook.Hits[hi];
                if (IgnoreRoot && h.collider.transform.IsChildOf(IgnoreRoot)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return y;
        }

        // ---- the stones' look: borrowed from the ground under the gun (GroundLook)
        void BuildLook(Vector3 at)
        {
            if (meshes == null) meshes = GroundLook.MakeRocks(6);
            var baseMat = GroundLook.BaseMaterial(at, IgnoreRoot);
            // the same ground as last time: keep its stones' look (making it reads the texture back from the GPU)
            var key = baseMat ? baseMat.name + "|" + (baseMat.HasProperty("_BaseColorMap") && baseMat.GetTexture("_BaseColorMap") ? baseMat.GetTexture("_BaseColorMap").GetInstanceID() : 0) + "|" + (baseMat.HasProperty("_BaseColor") ? baseMat.GetColor("_BaseColor").ToString() : "") : "";
            if (mats != null && key == lookKey) { if (baseMat) DestroyImmediate(baseMat); return; }
            if (mats != null) foreach (var mm in mats) if (mm) DestroyImmediate(mm);
            mats = GroundLook.Shades(baseMat, "Gravel");
            foreach (var mm in mats) mm.enableInstancing = true;
            lookKey = key;
            DestroyImmediate(baseMat);
        }

        // ---- motion
        /// <summary>Advance the gravel (the sim's Advance calls this with its own dt).</summary>
        public void Tick(float dt)
        {
            if (t < 0f) return;
            t += dt;
            bool any = false;
            for (int i = 0; i < stones.Count; i++)
            {
                var s = stones[i];
                if (!s.live) continue;
                any = true;
                if (t < s.release) continue;
                // quadratic air drag of a rock: a = k v^2, k = 3 rho_air Cd / (4 rho_rock d) ~ 1.7e-4 / d (d in m)
                float k = dragScale * 1.73e-4f / Mathf.Max(s.size, 0.004f);
                s.vel += Physics.gravity * dt;
                s.vel /= 1f + k * s.vel.magnitude * dt;
                s.pos += s.vel * dt;
                s.rot = (Quaternion.Euler(s.spin * dt) * s.rot).normalized;   // spun every step: keep it unit length (TRS asserts otherwise)
                // no contact with anything: a stone that comes back down just drops out of sight into the ground
                // (in the grass of a real field it would vanish anyway)
                if (s.pos.y < s.groundY - s.size) s.live = false;
                stones[i] = s;
            }
            Sync();
            if (!any) t = -1f;
        }

        void Sync()
        {
            if (meshes == null || mats == null) return;
            int nb = meshes.Length * mats.Length;
            if (batches == null || batches.Length != nb) { batches = new List<Matrix4x4>[nb]; for (int b = 0; b < nb; b++) batches[b] = new List<Matrix4x4>(128); }
            foreach (var b in batches) b.Clear();
            for (int i = 0; i < stones.Count; i++)
            {
                var s = stones[i];
                if (!s.live || t < s.release) continue;
                int mi = i % meshes.Length, ci = (i / meshes.Length) % mats.Length;
                batches[ci * meshes.Length + mi].Add(Matrix4x4.TRS(s.pos, s.rot, Vector3.one * s.size));
            }
        }

        void LateUpdate()
        {
            if (batches == null || t < 0f) return;
            for (int b = 0; b < batches.Length; b++)
            {
                if (batches[b].Count == 0) continue;
                var rp = new RenderParams(mats[b / meshes.Length]) { shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off, receiveShadows = true };
                Graphics.RenderMeshInstanced(rp, meshes[b % meshes.Length], 0, batches[b]);
            }
        }

        void Update()
        {
            if (Application.isPlaying) Tick(Time.deltaTime);   // tests step it themselves
        }
    }
}
