// What a driving tank's tracks throw off the ground, after reference footage of driving tanks (dark mud
// streaks and small puffs low at the sprocket, a thin veil on dry ground, never a big cloud). Driven by the tank's own
// motion (each track's speed over the ground, so turning throws more off the outer one).
//   mud    - streaks of soil stretched by their speed, flung back and up off the rear of the track run (a few off the
//            front), falling back within half a second (no contact: they vanish into the ground)
//   kick   - small darker puffs right behind each track, gone in under a second
//   veil   - a thin, low dust veil left behind (dry ground gives more), drifting with the wind
//   clods  - small lumps (the ground's own material, real lit meshes) thrown up and back off the rear of the run
// The ground under the tank sets the colour (its average albedo, a little paler: dry dust) and its material is
// lent to the clods; 'dustiness' scales how much a ground gives (dry dirt 1, grass much less).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankRig.FX
{
    [ExecuteAlways]
    public class TankDustFX : MonoBehaviour, IFlashParticles
    {
        [Tooltip("the tank (its motion drives the dust)")] public Transform tank;
        [Tooltip("track parts (empty = children named Track_L / Track_R, else Track*)")] public Transform[] tracks = new Transform[0];
        [Header("Veil (thin dust left behind)")]
        [Tooltip("puffs per second per track at 10 m/s")] public float rate = 22f;
        [Tooltip("m/s: below this the tracks raise almost nothing")] public float minSpeed = 0.6f;
        [Tooltip("m: puff size at birth (it swells x grow)")] public float size = 0.6f;
        public float grow = 4f;
        [Tooltip("s")] public float life = 2.6f;
        [Range(0f, 1f)] public float opacity = 0.1f;
        [Tooltip("m/s: how fast the veil rises (low: it hugs the ground)")] public float rise = 0.15f;
        [Header("Mud streaks")]
        [Tooltip("streaks per second per track at 10 m/s (off the rear; the front throws a sixth of it)")] public float mudRate = 140f;
        [Tooltip("m: streak thickness")] public float mudSize = 0.022f;
        [Tooltip("s: streak length = speed x this")] public float mudStretch = 0.035f;
        [Tooltip("mud is darker than the dry ground (wet)")] [Range(0f, 1f)] public float mudDark = 0.45f;
        [Header("Kick puffs")]
        [Tooltip("small dark puffs per second per track at 10 m/s, right behind it")] public float kickRate = 12f;
        [Range(0f, 1f)] public float kickOpacity = 0.35f;
        /// <summary>Run-time scale of the puff count (TankFx: fewer, a little denser puffs cost less screen fill). Not serialized.</summary>
        [System.NonSerialized] public float amountScale = 1f;
        [Tooltip("how much this ground gives (dry dirt 1, grass ~0.3, wet ~0.1)")] [Range(0f, 2f)] public float dustiness = 1f;
        [Tooltip("dust is paler than the ground it comes from (dry, fine)")] [Range(0f, 1f)] public float pale = 0.15f;
        [Tooltip("less dust from green (grassy) ground, judged from the ground's colour")] public bool autoDustiness = true;
        [Tooltip("override the ground's colour (alpha > 0 to use)")] public Color colorOverride = new Color(0, 0, 0, 0);
        [Header("Clods")]
        [Tooltip("clods per second per track at 10 m/s")] public float clodRate = 25f;
        public float clodSizeMin = 0.01f, clodSizeMax = 0.06f;
        public bool clodShadows = false;
        [Header("Scene")]
        public Light sun;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;
        public Vector3 wind = new Vector3(1.5f, 0f, 2.5f);
        public bool autoAdvance = true;

        readonly List<GunFlashFX.Particle> parts = new List<GunFlashFX.Particle>();
        readonly List<GunFlashFX.Particle> mud = new List<GunFlashFX.Particle>();
        Color soilColor = new Color(0.3f, 0.25f, 0.2f);
        float mudAcc, frontAcc, kickAcc;
        struct Clod { public Vector3 pos, vel, spin; public Quaternion rot; public float size, groundY; public int look; }
        readonly List<Clod> clods = new List<Clod>();
        readonly List<Transform> pool = new List<Transform>();
        Mesh[] meshes; Material[] mats;
        Color dustColor = new Color(0.4f, 0.35f, 0.28f);
        float groundFactor = 1f;
        string lookName = "";
        // the tracks in the tank's frame: contact patch ends (bottom) and the rear of the top run
        struct TrackGeo { public Vector3 rearBottom, frontBottom, rearTop; public float width; }
        TrackGeo[] geo = new TrackGeo[0];
        Vector3 fwdLocal = Vector3.forward, upLocal = Vector3.up;
        Vector3 lastPos; Quaternion lastRot; bool hasLast;
        Vector3 vel; float yawRate, emitAcc, clodAcc, lookTimer;
        System.Random rng = new System.Random(99);
        float R() => (float)rng.NextDouble();

        public Vector3 SunDir => sun ? -sun.transform.forward : Vector3.up;
        public Color SunLux => sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;
        public Color Ambient => ambient;
        public Color DustColor => dustColor;

        void OnEnable() { if (!sun) sun = FxScene.FindSun(); if (!GunFlashFX.Sources.Contains(this)) GunFlashFX.Sources.Add(this); Setup(); }
        void OnDisable() { GunFlashFX.Sources.Remove(this); foreach (var p in pool) if (p) p.gameObject.SetActive(false); }
        void OnDestroy() { foreach (var p in pool) if (p) DestroyImmediate(p.gameObject); pool.Clear(); }

        /// <summary>Find the tracks and work out their geometry and the tank's forward (call again if the tank changes).</summary>
        public void Setup()
        {
            var root = tank ? tank : transform;
            if (tracks == null || tracks.Length == 0)
            {
                var all = root.GetComponentsInChildren<Transform>(true);
                var l = System.Array.Find(all, t => t.name == "Track_L");
                var r = System.Array.Find(all, t => t.name == "Track_R");
                tracks = l && r ? new[] { l, r } : System.Array.FindAll(all, t => t.name.StartsWith("Track"));
            }
            // forward: across the two tracks, pointing away from the exhaust (the rear), like the rig's builder does
            Vector3 Centre(Transform t) { var rs = t.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return t.position; var b = rs[0].bounds; foreach (var rr in rs) b.Encapsulate(rr.bounds); return b.center; }
            Vector3 fwd = root.forward;
            if (tracks.Length >= 2)
            {
                var right = Centre(tracks[1]) - Centre(tracks[0]); right.y = 0f;
                fwd = Vector3.Cross(right.normalized, Vector3.up);
                var ex = System.Array.FindAll(root.GetComponentsInChildren<Transform>(true), t => t.name.StartsWith("Exhaust"));
                if (ex.Length > 0)
                {
                    Vector3 e = Vector3.zero; foreach (var t in ex) e += t.position; e /= ex.Length;
                    if (Vector3.Dot(e - Centre(root), fwd) > 0f) fwd = -fwd;
                }
            }
            fwdLocal = root.InverseTransformDirection(fwd).normalized;
            upLocal = root.InverseTransformDirection(Vector3.up).normalized;
            Vector3 sideLocal = Vector3.Cross(upLocal, fwdLocal);
            geo = new TrackGeo[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
            {
                // the track's extent along forward / up / side, in the tank's frame (from its renderers' boxes)
                float fMin = 1e9f, fMax = -1e9f, uMin = 1e9f, uMax = -1e9f, sMin = 1e9f, sMax = -1e9f;
                foreach (var rr in tracks[i].GetComponentsInChildren<Renderer>())
                {
                    var b = rr.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var w = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                        var lp = root.InverseTransformPoint(w);
                        float f = Vector3.Dot(lp, fwdLocal), u = Vector3.Dot(lp, upLocal), s = Vector3.Dot(lp, sideLocal);
                        fMin = Mathf.Min(fMin, f); fMax = Mathf.Max(fMax, f); uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u); sMin = Mathf.Min(sMin, s); sMax = Mathf.Max(sMax, s);
                    }
                }
                if (fMin > fMax) continue;
                float sc = (sMin + sMax) * 0.5f;
                Vector3 P(float f, float u) => fwdLocal * f + upLocal * u + sideLocal * sc;
                float h = uMax - uMin;
                geo[i] = new TrackGeo { rearBottom = P(fMin + h * 0.35f, uMin + 0.05f), frontBottom = P(fMax - h * 0.35f, uMin + 0.05f), rearTop = P(fMin + h * 0.3f, uMax - h * 0.15f), width = sMax - sMin };
            }
            hasLast = false;
            RefreshLook();
        }

        void RefreshLook()
        {
            var root = tank ? tank : transform;
            if (meshes == null) meshes = GroundLook.MakeRocks(6);
            Vector3 at = geo.Length > 0 ? root.TransformPoint(geo[0].rearBottom) : root.position;   // the ground under a track
            var baseMat = GroundLook.BaseMaterial(at, root);
            lookName = baseMat.name + " / " + (baseMat.HasProperty("_BaseColorMap") && baseMat.GetTexture("_BaseColorMap") ? baseMat.GetTexture("_BaseColorMap").name : "no texture") + " at " + at;
            if (mats != null) foreach (var mm in mats) if (mm) DestroyImmediate(mm);
            mats = GroundLook.Shades(baseMat, "Clod");
            Color g = GroundLook.AverageAlbedo(baseMat);
            DestroyImmediate(baseMat);
            // grassy ground: the dust is the soil under the turf (brown, not green), and much less of it
            float green = Mathf.Clamp01((g.g - Mathf.Max(g.r, g.b)) * 25f);
            Color soil = Color.Lerp(g, new Color(0.24f, 0.19f, 0.13f), green);
            dustColor = colorOverride.a > 0f ? colorOverride : Color.Lerp(soil, new Color(0.58f, 0.52f, 0.44f), pale);
            soilColor = colorOverride.a > 0f ? colorOverride * 0.8f : soil;
            groundFactor = autoDustiness ? Mathf.Lerp(1f, 0.25f, green) : 1f;
        }

        float GroundY(Vector3 p, Transform ignore)
        {
            var ter = Terrain.activeTerrain;
            if (ter) return ter.SampleHeight(p) + ter.transform.position.y;
            float best = float.MaxValue, y = p.y - 0.2f;
            int hits = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, GroundLook.Hits, 6f, ~0, QueryTriggerInteraction.Ignore);   // no garbage per frame
            for (int h = 0; h < hits; h++)
            {
                var hit = GroundLook.Hits[h];
                if (ignore && hit.collider.transform.IsChildOf(ignore)) continue;
                if (hit.distance < best) { best = hit.distance; y = hit.point.y; }
            }
            return y;
        }

        /// <summary>Advance the dust and clods by dt (in play mode Update does it).</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            var root = tank ? tank : transform;
            // the tank's motion
            if (hasLast)
            {
                Vector3 v = (root.position - lastPos) / dt;
                vel = Vector3.Lerp(vel, v, 1f - Mathf.Exp(-dt * 8f));
                float dyaw = Mathf.DeltaAngle(lastRot.eulerAngles.y, root.rotation.eulerAngles.y) / dt;
                yawRate = Mathf.Lerp(yawRate, dyaw, 1f - Mathf.Exp(-dt * 8f));
            }
            lastPos = root.position; lastRot = root.rotation; hasLast = true;
            Vector3 fwd = root.TransformDirection(fwdLocal), up = Vector3.up;
            float along = Vector3.Dot(vel, fwd);
            lookTimer -= dt;
            if (lookTimer <= 0f && Terrain.activeTerrain) { RefreshLook(); lookTimer = 0.5f; }

            // cloud
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var q = parts[i];
                q.age += dt;
                if (q.age >= q.life) { parts.RemoveAt(i); continue; }
                q.age01 = q.age / q.life;
                Vector3 air = wind + up * rise;   // the churned air rolls up a little
                q.vel += (air - q.vel) * (1f - Mathf.Exp(-q.drag * dt));
                q.pos += q.vel * dt;
                parts[i] = q;
            }
            // mud streaks: thrown soil, gravity and a little drag, gone when back on the ground
            for (int i = mud.Count - 1; i >= 0; i--)
            {
                var q = mud[i];
                q.age += dt;
                q.vel += Physics.gravity * dt;
                q.vel *= Mathf.Exp(-q.drag * dt);
                q.pos += q.vel * dt;
                if (q.age >= q.life || q.pos.y < q.delay) { mud.RemoveAt(i); continue; }   // delay holds the ground's height
                q.age01 = q.age / q.life;
                q.axis = q.vel.sqrMagnitude > 1e-4f ? -q.vel.normalized : Vector3.down;
                mud[i] = q;
            }
            // clods
            for (int i = clods.Count - 1; i >= 0; i--)
            {
                var c = clods[i];
                float k = 1.73e-4f / Mathf.Max(c.size, 0.004f);
                c.vel += Physics.gravity * dt;
                c.vel /= 1f + k * c.vel.magnitude * dt;
                c.pos += c.vel * dt;
                c.rot = Quaternion.Euler(c.spin * dt) * c.rot;
                if (c.pos.y < c.groundY - c.size) { clods.RemoveAt(i); continue; }
                clods[i] = c;
            }

            // emission: by each track's own speed over the ground (turning speeds one side up and slows the other)
            float halfTrack = geo.Length >= 2 ? (root.TransformPoint(geo[1].rearBottom) - root.TransformPoint(geo[0].rearBottom)).magnitude * 0.5f : 1.5f;
            for (int i = 0; i < geo.Length; i++)
            {
                if (!tracks[i]) continue;
                // turning right (yaw up) runs the left track (index 0) faster and the right one slower
                float side = geo.Length >= 2 ? (i == 0 ? 1f : -1f) : 0f;
                float trackSpeed = Mathf.Abs(along + side * yawRate * Mathf.Deg2Rad * halfTrack);
                float drive = Mathf.Clamp01((trackSpeed - minSpeed) / 10f) * dustiness * groundFactor;
                if (drive <= 0f) continue;
                bool reversing = along < -0.2f;
                Vector3 src = root.TransformPoint(reversing ? geo[i].frontBottom : geo[i].rearBottom);
                Vector3 behind = reversing ? fwd : -fwd;
                float gy = GroundY(src, root);
                // cloud puffs
                float n = rate * amountScale * drive * dt + emitAcc;
                int count = Mathf.FloorToInt(n); emitAcc = n - count;
                for (int k = 0; k < count; k++)
                {
                    Vector3 p = src + Vector3.Cross(up, fwd) * ((R() - 0.5f) * geo[i].width) + behind * R() * 0.6f;
                    p.y = gy + 0.1f + R() * 0.2f;
                    Vector3 v0 = vel * (0.3f + 0.3f * R()) + up * (0.3f + 0.6f * R()) * (0.5f + trackSpeed * 0.08f) + behind * R() * 1.5f;   // dragged along by the wake a moment, kept low
                    parts.Add(new GunFlashFX.Particle
                    {
                        pos = p, type = 8f, axis = new Vector3(dustColor.r, dustColor.g, dustColor.b),
                        width = size * (0.6f + 0.8f * R()), life = life * (0.6f + 0.8f * R()), intensity = Mathf.Min(opacity * Mathf.Lerp(1f, 1.5f, 1f - amountScale) * (0.5f + R()), 1f),
                        vel = v0, seed = R() * 100f, grow = grow * (0.6f + 0.8f * R()), drag = 1.5f
                    });
                }
                Vector3 across = Vector3.Cross(up, fwd);
                Vector3 topW = root.TransformPoint(geo[i].rearTop);
                Color mc = Color.Lerp(soilColor, soilColor * 0.5f, mudDark);
                // mud streaks off the rear of the run (where the track wraps round the sprocket and lifts off the ground)
                float mn = mudRate * amountScale * drive * dt + mudAcc;
                int mc0 = Mathf.FloorToInt(mn); mudAcc = mn - mc0;
                for (int k = 0; k < mc0; k++) EmitMud(Vector3.Lerp(src, topW, R() * 0.8f) + across * ((R() - 0.5f) * geo[i].width), behind, up, trackSpeed, gy, mc, 1f);
                // a few off the front (the idler), thrown forward and up
                Vector3 frontW = root.TransformPoint(reversing ? geo[i].rearBottom : geo[i].frontBottom);
                float fn = mudRate * amountScale * drive * dt / 6f + frontAcc;
                int fc = Mathf.FloorToInt(fn); frontAcc = fn - fc;
                for (int k = 0; k < fc; k++) EmitMud(frontW + up * (0.2f + 0.4f * R()) + across * ((R() - 0.5f) * geo[i].width), -behind, up, trackSpeed * 0.5f, gy, mc, 0.7f);
                // kick puffs: small, darker, right behind the track
                float kn = kickRate * amountScale * drive * dt + kickAcc;
                int kc = Mathf.FloorToInt(kn); kickAcc = kn - kc;
                Color kcol = Color.Lerp(dustColor, soilColor, 0.5f);
                for (int k = 0; k < kc; k++)
                {
                    Vector3 p = src + across * ((R() - 0.5f) * geo[i].width) + behind * (0.2f + 0.5f * R());
                    p.y = gy + 0.15f + 0.25f * R();
                    parts.Add(new GunFlashFX.Particle
                    {
                        pos = p, type = 8f, axis = new Vector3(kcol.r, kcol.g, kcol.b),
                        width = 0.3f * (0.7f + 0.6f * R()), life = 0.5f + 0.4f * R(), intensity = kickOpacity * (0.6f + 0.6f * R()),
                        vel = vel * 0.5f + behind * trackSpeed * 0.25f * R() + up * (0.4f + 0.8f * R()), seed = R() * 100f, grow = 2.5f, drag = 3f
                    });
                }
                // clods off the rear of the track run
                float cn = clodRate * drive * dt + clodAcc;
                int cc = Mathf.FloorToInt(cn); clodAcc = cn - cc;
                Vector3 top = root.TransformPoint(geo[i].rearTop);
                for (int k = 0; k < cc; k++)
                {
                    Vector3 p = Vector3.Lerp(src, top, R()) + Vector3.Cross(up, fwd) * ((R() - 0.5f) * geo[i].width);
                    Vector3 v0 = vel + up * trackSpeed * (0.4f + 0.6f * R()) + behind * trackSpeed * (0.3f + 0.6f * R()) + Random.insideUnitSphere * 1.5f;
                    clods.Add(new Clod
                    {
                        pos = p, vel = v0, size = Mathf.Lerp(clodSizeMin, clodSizeMax, Mathf.Pow(R(), 2.5f)), groundY = gy,
                        rot = Random.rotation, spin = Random.onUnitSphere * 600f, look = (int)(R() * 1000f)
                    });
                }
            }
            SyncClods();
        }

        void SyncClods()
        {
            while (pool.Count < clods.Count)
            {
                var go = new GameObject("Clod") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = clodShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                pool.Add(go.transform);
            }
            for (int i = 0; i < pool.Count; i++)
            {
                var tr = pool[i];
                if (i >= clods.Count) { if (tr.gameObject.activeSelf) tr.gameObject.SetActive(false); continue; }
                var c = clods[i];
                if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                tr.SetPositionAndRotation(c.pos, c.rot);
                tr.localScale = Vector3.one * c.size;
                tr.GetComponent<MeshFilter>().sharedMesh = meshes[c.look % meshes.Length];
                tr.GetComponent<MeshRenderer>().sharedMaterial = mats[(c.look / meshes.Length) % mats.Length];
            }
        }

        void EmitMud(Vector3 p, Vector3 behind, Vector3 up, float trackSpeed, float gy, Color c, float scale)
        {
            Vector3 v0 = vel + behind * trackSpeed * (0.25f + 0.6f * R()) + up * trackSpeed * (0.15f + 0.5f * R()) + Random.insideUnitSphere * (0.8f + 0.1f * trackSpeed);
            mud.Add(new GunFlashFX.Particle
            {
                pos = p, type = (float)GunFlashFX.PType.Mud, axis = -v0.normalized, vel = v0,
                width = mudSize * scale * (0.5f + R()), life = 0.3f + 0.25f * R(), intensity = 0.85f, seed = R() * 100f, drag = 0.6f,
                delay = gy,   // the ground's height (mud is never delayed: the field is free)
                extra = new Vector4(c.r * (0.8f + 0.4f * R()), c.g * (0.8f + 0.4f * R()), c.b * (0.8f + 0.4f * R()), mudStretch),
            });
        }

        public int CopyVisible(List<GunFlashFX.Particle> dst) { dst.AddRange(parts); dst.AddRange(mud); return parts.Count + mud.Count; }

        public string Stats() => $"dust: speed {vel.magnitude:F1} m/s, yaw {yawRate:F0} deg/s, {parts.Count} puffs, {mud.Count} mud streaks, {clods.Count} clods, colour {dustColor}, ground factor {groundFactor:F2}, ground {lookName}";

        void Update() { if (Application.isPlaying && autoAdvance) Tick(Time.deltaTime); }
    }
}
