// Soil carried round by the tracks ("巻き込み"): the track picks up earth where it lifts off the ground, carries it up
// round the rear wheel and forward along the top run, where some dribbles off; at the front wheel, where the track
// turns down, most of it is flung off forward and falls in front of the tank (to be run over again).
// Each track is a loop in its own vertical plane (a stadium round its renderers' box: bottom run, rear arc, top run,
// front arc) moving at that track's speed. Soil riding on it is drawn where it is on the loop; once it lets go it
// flies ballistically and vanishes into the ground (no contact). Two kinds: lumps (lit meshes of the ground's own
// material) and fine grains (small specks the ground's colour, GunFlashPass type 9). 'mud' makes it stickier,
// darker and lumpier (carried further, flung harder at the front), dry ground gives more fine grains.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankRig.FX
{
    [ExecuteAlways]
    public class TankTrackSpillFX : MonoBehaviour, IFlashParticles
    {
        [Tooltip("the tank (its motion drives the tracks)")] public Transform tank;
        [Tooltip("track parts (empty = Track_L / Track_R, else Track*)")] public Transform[] tracks = new Transform[0];
        [Header("Soil")]
        [Tooltip("pieces picked up per second per track at 10 m/s")] public float pickup = 300f;
        [Tooltip("0 dry dirt / sand .. 1 wet mud: stickier, darker, lumpier")] [Range(0f, 1f)] public float mud = 0.3f;
        [Tooltip("share of the pieces that are lumps (the rest fine grains)")] [Range(0f, 1f)] public float lumpShare = 0.3f;
        public float lumpSizeMin = 0.02f, lumpSizeMax = 0.06f, grainSize = 0.012f;
        [Tooltip("1/s: how readily soil lets go on the top run / round the rear wheel / round the front wheel")]
        public float dribble = 2f, rearShed = 1.3f, frontFling = 7f;
        [Tooltip("extra throw off the track when soil lets go (share of the track speed): centrifugal kick out from the wheels")] public float fling = 0.25f;
        [Tooltip("m/s: fastest rise of soil thrown off the rising wheel, relative to the tank (keeps it below the hull top)")] public float maxRise = 1.8f;
        [Tooltip("fine soil puffs where soil is flung off (per piece let go)")] [Range(0f, 1f)] public float puffChance = 0.12f;
        public float puffSize = 0.25f, puffLife = 1.2f;
        [Range(0f, 1f)] public float puffOpacity = 0.25f;
        [Tooltip("m: the top run sits this far below the top of the track's box (return rollers, side skirts)")] public float topDrop = 0.15f;
        [Tooltip("m: soil rides this far outside the loop (the links' outer face)")] public float surface = 0.06f;
        [Header("Where the soil rides (fit it to the track; the loop shows in the Scene view when selected)")]
        [Tooltip("m: move the loop forward / up / outward (away from the hull, mirrored for the other track)")] public Vector3 loopOffset = Vector3.zero;
        [Tooltip("stretch the loop's straight runs (1 = the track's length)")] public float lengthScale = 1f;
        [Tooltip("scale the wheel radius at each end (1 = half the track's height)")] public float radiusScale = 1f;
        [Tooltip("share of the track's width the soil spreads over")] [Range(0.1f, 1.2f)] public float widthScale = 0.9f;
        public bool showLoopGizmo = true;
        [Tooltip("most pieces on the tracks at once / in the air at once")] public int maxRiding = 450, maxFalling = 700;
        [Tooltip("most lumps drawn as meshes at once (the rest are drawn as grains)")] public int maxLumps = 160;
        [Tooltip("s: how often the tracks' belts are read again (they change with the suspension)")] public float beltRefresh = 0.1f;
        [Header("Scene")]
        public Light sun;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;
        public bool autoAdvance = true;

        struct Loop { public float f0, f1, uc, R, sc, width, length; }
        struct Piece { public int track; public float s, across, size; public bool lump, riding; public Vector3 pos, vel, spin; public Quaternion rot; public float groundY; public int look; public float seed; }
        readonly List<Piece> pieces = new List<Piece>();
        readonly List<GunFlashFX.Particle> grains = new List<GunFlashFX.Particle>();
        readonly List<GunFlashFX.Particle> puffs = new List<GunFlashFX.Particle>();
        readonly List<Transform> pool = new List<Transform>();
        Loop[] loops = new Loop[0], rawLoops = new Loop[0];
        // the generated track's real belt (TankRig TankTrack), resampled in the tank's frame along the way the links run
        // when the tank goes forward: s = 0 where the belt leaves the ground at the rear; e0 / e1 / e2 = ends of the
        // rising wheel, the top run and the falling wheel; past e2 the belt is on the ground again
        class Belt { public Vector3[] p, n; public float[] cum; public float len, e0, e1, e2; }
        Belt[] belts = new Belt[0];
        float beltTimer;
        float[] trackGround = new float[0];
        [Tooltip("follow the generated track's own belt (outer face of its links) when the track has one")] public bool useTrackBelt = true;
        Vector3 fwdLocal = Vector3.forward, upLocal = Vector3.up, sideLocal = Vector3.right;
        Mesh[] meshes; Material[] mats; Color soil = new Color(0.25f, 0.2f, 0.14f); float groundFactor = 1f;
        Vector3 lastPos; Quaternion lastRot; bool hasLast; Vector3 vel; float yawRate; float[] acc = new float[0];
        System.Random rng = new System.Random(321);
        float R() => (float)rng.NextDouble();

        public Vector3 SunDir => sun ? -sun.transform.forward : Vector3.up;
        public Color SunLux => sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;
        public Color Ambient => ambient;

        void OnEnable() { if (!sun) sun = FxScene.FindSun(); if (!GunFlashFX.Sources.Contains(this)) GunFlashFX.Sources.Add(this); Setup(); }
        void OnDisable() { GunFlashFX.Sources.Remove(this); foreach (var p in pool) if (p) p.gameObject.SetActive(false); }
        void OnDestroy() { foreach (var p in pool) if (p) DestroyImmediate(p.gameObject); pool.Clear(); poolMf.Clear(); poolMr.Clear(); }

        public void Setup()
        {
            var root = tank ? tank : transform;
            var all = root.GetComponentsInChildren<Transform>(true);
            if (tracks == null || tracks.Length == 0)
            {
                var l = System.Array.Find(all, t => t.name == "Track_L");
                var r = System.Array.Find(all, t => t.name == "Track_R");
                tracks = l && r ? new[] { l, r } : System.Array.FindAll(all, t => t.name.StartsWith("Track"));
            }
            Vector3 Centre(Transform t) { var rs = t.GetComponentsInChildren<Renderer>(); if (rs.Length == 0) return t.position; var b = rs[0].bounds; foreach (var rr in rs) b.Encapsulate(rr.bounds); return b.center; }
            Vector3 fwd = root.forward;
            if (tracks.Length >= 2)
            {
                var right = Centre(tracks[1]) - Centre(tracks[0]); right.y = 0f;
                fwd = Vector3.Cross(right.normalized, Vector3.up);
                var ex = System.Array.FindAll(all, t => t.name.StartsWith("Exhaust"));
                if (ex.Length > 0) { Vector3 e = Vector3.zero; foreach (var t in ex) e += t.position; e /= ex.Length; if (Vector3.Dot(e - Centre(root), fwd) > 0f) fwd = -fwd; }
            }
            fwdLocal = root.InverseTransformDirection(fwd).normalized;
            upLocal = root.InverseTransformDirection(Vector3.up).normalized;
            sideLocal = Vector3.Cross(upLocal, fwdLocal);
            loops = new Loop[tracks.Length];
            acc = new float[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
            {
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
                float top = uMax - topDrop;
                float Rr = Mathf.Max((top - uMin) * 0.5f, 0.15f);
                var lp2 = new Loop { f0 = fMin + Rr, f1 = fMax - Rr, uc = uMin + Rr, R = Rr, sc = (sMin + sMax) * 0.5f, width = sMax - sMin };
                lp2.length = 2f * Mathf.PI * Rr + 2f * (lp2.f1 - lp2.f0);
                loops[i] = lp2;
            }
            rawLoops = (Loop[])loops.Clone();
            ApplyLoopAdjust();
            hasLast = false;
            RefreshLook();
        }

        /// <summary>Read each generated track's belt (its pitch line pushed out to the links' outer face) into the tank's frame.</summary>
        void BuildBelts()
        {
            var root = tank ? tank : transform;
            if (belts == null || belts.Length != tracks.Length) belts = new Belt[tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
            {
                belts[i] = null;
                if (!useTrackBelt || !tracks[i]) continue;
                var tt = tracks[i].GetComponent<TankTrack>();
                if (!tt) continue;
                var path = tt.PathPoints;
                var cum = tt.PathDistances;
                if (path == null || cum == null || path.Count < 3 || cum.Count != path.Count) continue;
                float length = tt.PathLength, depth = tt.LinkDepth;
                Vector3 FL = tt.forwardLocal.normalized, UL = tt.upLocal.normalized, AL = tt.axleLocal.normalized;
                float ground = tt.linkGroundDepth;
                if (length <= 0f) continue;
                var tr = tracks[i];
                Vector2 Eval(float sv)
                {
                    sv = Mathf.Repeat(sv, length);
                    int lo = 0, hi = cum.Count - 1;
                    while (lo < hi) { int mid = (lo + hi + 1) / 2; if (cum[mid] <= sv) lo = mid; else hi = mid - 1; }
                    int nx = (lo + 1) % path.Count;
                    float end = lo + 1 < cum.Count ? cum[lo + 1] : length;
                    float t = end - cum[lo] > 1e-6f ? (sv - cum[lo]) / (end - cum[lo]) : 0f;
                    return Vector2.Lerp(path[lo], path[nx], t);
                }
                const int N = 240;
                float ds = length / N;
                var pts = new Vector3[N]; var nrm = new Vector3[N];
                for (int k = 0; k < N; k++)
                {
                    float sv = k * ds;
                    Vector2 a = Eval(sv - ds * 0.5f), b = Eval(sv + ds * 0.5f), c2 = Eval(sv);
                    Vector2 d = b - a; if (d.sqrMagnitude < 1e-12f) d = Vector2.right; d.Normalize();
                    Vector2 o = new Vector2(d.y, -d.x);   // outward (the track's own convention: +s runs forward along the bottom)
                    Vector3 pl = FL * (c2.x + o.x * ground) + UL * (c2.y + o.y * ground) + AL * depth;   // outer face of the links
                    pts[k] = root.InverseTransformPoint(tr.TransformPoint(pl));
                    nrm[k] = root.InverseTransformDirection(tr.TransformDirection(FL * o.x + UL * o.y)).normalized;
                }
                // start where the belt leaves the ground at the rear: the rear-most point whose face looks down
                int start = -1; float best = float.MaxValue;
                for (int k = 0; k < N; k++)
                    if (Vector3.Dot(nrm[k], upLocal) < -0.7f) { float fk = Vector3.Dot(pts[k], fwdLocal); if (fk < best) { best = fk; start = k; } }
                if (start < 0) continue;
                // walk the way the links run when the tank goes forward (-s on the track): rising wheel, top run, falling wheel, ground
                var bp = new Vector3[N]; var bn = new Vector3[N];
                for (int k = 0; k < N; k++) { int idx = ((start - k) % N + N) % N; bp[k] = pts[idx]; bn[k] = nrm[idx]; }
                var belt = new Belt { p = bp, n = bn, cum = new float[N] };
                float acc = 0f;
                for (int k = 0; k < N; k++) { belt.cum[k] = acc; acc += Vector3.Distance(bp[k], bp[(k + 1) % N]); }
                belt.len = acc;
                float midF = 0f; foreach (var q in bp) midF += Vector3.Dot(q, fwdLocal); midF /= N;
                int k0 = 0;
                while (k0 < N - 1 && Vector3.Dot(bn[k0], upLocal) < 0.7f) k0++;                                     // up round the rear wheel
                int k1 = k0;
                while (k1 < N - 1 && !(Vector3.Dot(bn[k1], upLocal) < 0.5f && Vector3.Dot(bp[k1], fwdLocal) > midF)) k1++;   // along the top to the front wheel
                int k2 = k1;
                while (k2 < N - 1 && Vector3.Dot(bn[k2], upLocal) > -0.7f) k2++;                                    // down round the front wheel to the ground
                belt.e0 = belt.cum[k0]; belt.e1 = belt.cum[k1]; belt.e2 = belt.cum[k2];
                belts[i] = belt;
            }
        }

        /// <summary>Loops from the measured tracks with the position controls applied.</summary>
        void ApplyLoopAdjust()
        {
            if (rawLoops.Length != loops.Length) loops = new Loop[rawLoops.Length];
            for (int i = 0; i < rawLoops.Length; i++)
            {
                var r = rawLoops[i];
                float mid = (r.f0 + r.f1) * 0.5f, half = Mathf.Max((r.f1 - r.f0) * 0.5f * lengthScale, 0f);
                float bottom = r.uc - r.R;                                     // keep the ground contact where it is
                float R2 = Mathf.Max(r.R * radiusScale, 0.05f);
                var L = r;
                L.R = R2;
                L.f0 = mid - half + loopOffset.x; L.f1 = mid + half + loopOffset.x;
                L.uc = bottom + R2 + loopOffset.y;
                L.sc = r.sc + Mathf.Sign(r.sc + 1e-4f) * loopOffset.z;
                L.width = r.width * widthScale / 0.9f;
                L.length = 2f * Mathf.PI * R2 + 2f * (L.f1 - L.f0);
                loops[i] = L;
            }
        }

        void OnValidate() { if (rawLoops != null && rawLoops.Length > 0) ApplyLoopAdjust(); }

        void OnDrawGizmosSelected()
        {
            if (!showLoopGizmo || loops == null || loops.Length == 0) return;
            if (!Application.isPlaying) BuildBelts();
            var root = tank ? tank : transform;
            for (int i = 0; i < loops.Length; i++)
            {
                var L = loops[i];
                float LL = LoopLen(i);
                if (LL <= 0f) continue;
                Gizmos.color = new Color(1f, 0.6f, 0.1f);
                Vector3 prev = root.TransformPoint(Local(i, 0f, 0f));
                int n = 96;
                for (int k = 1; k <= n; k++)
                {
                    Vector3 p = root.TransformPoint(Local(i, LL * k / n, 0f));
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
                // the soil's spread across the track, and where it is picked up (rear) / dropped (front)
                Gizmos.color = new Color(1f, 0.85f, 0.2f);
                Gizmos.DrawLine(root.TransformPoint(Local(i, 0f, -L.width * 0.5f)), root.TransformPoint(Local(i, 0f, L.width * 0.5f)));
                var gb = BeltOf(i);
                float a = Mathf.PI * L.R, top = L.f1 - L.f0;
                Gizmos.DrawSphere(root.TransformPoint(Local(i, 0f, 0f)), 0.05f);
                Gizmos.color = new Color(0.3f, 0.8f, 1f);
                Gizmos.DrawSphere(root.TransformPoint(Local(i, gb != null ? gb.e2 : 2f * a + top, 0f)), 0.05f);
                if (gb != null) { Gizmos.color = Color.green; Gizmos.DrawSphere(root.TransformPoint(Local(i, gb.e0, 0f)), 0.04f); Gizmos.DrawSphere(root.TransformPoint(Local(i, gb.e1, 0f)), 0.04f); }
            }
        }

        void RefreshLook()
        {
            var root = tank ? tank : transform;
            if (meshes == null) meshes = GroundLook.MakeRocks(6);
            Vector3 at = loops.Length > 0 ? root.TransformPoint(Local(0, 0f, 0f)) : root.position;
            var baseMat = GroundLook.BaseMaterial(at, root);
            Color g = GroundLook.AverageAlbedo(baseMat);
            float green = Mathf.Clamp01((g.g - Mathf.Max(g.r, g.b)) * 25f);
            soil = Color.Lerp(g, new Color(0.24f, 0.19f, 0.13f), green);   // under turf: the soil
            soil = Color.Lerp(soil, soil * 0.75f, mud);                     // wet earth is darker
            soil.a = 1f;
            groundFactor = Mathf.Lerp(1f, 0.4f, green);
            if (mats != null) foreach (var mm in mats) if (mm) DestroyImmediate(mm);
            mats = GroundLook.Shades(baseMat, "TrackSoil");
            foreach (var mm in mats)
            {
                if (mm.HasProperty("_BaseColor")) { var c = mm.GetColor("_BaseColor"); mm.SetColor("_BaseColor", Color.Lerp(c, c * 0.75f, mud)); }
                if (mm.HasProperty("_Smoothness")) mm.SetFloat("_Smoothness", Mathf.Lerp(0.15f, 0.55f, mud));   // mud glistens
            }
            DestroyImmediate(baseMat);
        }

        // a point of track i's loop at arc length s (0 = where the track lifts off the ground at the rear), in the tank's frame
        Belt BeltOf(int i) => useTrackBelt && belts != null && i < belts.Length ? belts[i] : null;
        float LoopLen(int i) { var b = BeltOf(i); return b != null ? b.len : loops[i].length; }

        // position on the belt polyline at s (tank frame), and its outward normal
        void BeltAt(Belt b, float s, out Vector3 p, out Vector3 n)
        {
            s = Mathf.Repeat(s, b.len);
            int lo = 0, hi = b.cum.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (b.cum[mid] <= s) lo = mid; else hi = mid - 1; }
            int nx = (lo + 1) % b.p.Length;
            float segEnd = lo + 1 < b.cum.Length ? b.cum[lo + 1] : b.len;
            float t = segEnd - b.cum[lo] > 1e-6f ? (s - b.cum[lo]) / (segEnd - b.cum[lo]) : 0f;
            p = Vector3.Lerp(b.p[lo], b.p[nx], t);
            n = Vector3.Lerp(b.n[lo], b.n[nx], t).normalized;
        }

        /// <summary>Outward normal of track i's soil path at s (tank frame).</summary>
        Vector3 NormalLocal(int i, float s)
        {
            var b = BeltOf(i);
            if (b != null) { BeltAt(b, s, out _, out var n); return n; }
            var L = loops[i];
            Vector3 lp = Local(i, s, 0f);
            float lf = Vector3.Dot(lp, fwdLocal), lu = Vector3.Dot(lp, upLocal);
            Vector2 o2 = new Vector2(lf - Mathf.Clamp(lf, L.f0, L.f1), lu - L.uc);
            return o2.sqrMagnitude > 1e-6f ? (fwdLocal * o2.x + upLocal * o2.y).normalized : upLocal;
        }

        Vector3 Local(int i, float s, float across)
        {
            var bb = BeltOf(i);
            if (bb != null)
            {
                BeltAt(bb, s, out var bp, out var bn);
                return bp + bn * surface + sideLocal * across + fwdLocal * loopOffset.x + upLocal * loopOffset.y + sideLocal * Mathf.Sign(loops[i].sc + 1e-4f) * loopOffset.z;
            }
            var L = loops[i];
            float a = Mathf.PI * L.R, top = L.f1 - L.f0;
            s = Mathf.Repeat(s, L.length);
            float f, u;
            if (s < a) { float al = s / L.R; f = L.f0 - L.R * Mathf.Sin(al); u = L.uc - L.R * Mathf.Cos(al); }          // rear arc, going up
            else if (s < a + top) { f = L.f0 + (s - a); u = L.uc + L.R; }                                                  // top run, going forward
            else if (s < 2f * a + top) { float be = (s - a - top) / L.R; f = L.f1 + L.R * Mathf.Sin(be); u = L.uc + L.R * Mathf.Cos(be); }   // front arc, going down
            else { f = L.f1 - (s - 2f * a - top); u = L.uc - L.R; }                                                       // bottom run
            // a little outside the belt (soil sits on the links' outer face)
            float cf = Mathf.Clamp(f, L.f0, L.f1);
            Vector2 outward = new Vector2(f - cf, u - L.uc);
            if (outward.sqrMagnitude > 1e-6f) { outward.Normalize(); f += outward.x * surface; u += outward.y * surface; }
            return fwdLocal * f + upLocal * u + sideLocal * (L.sc + across);
        }
        int Segment(int i, float s)
        {
            var bb = BeltOf(i);
            if (bb != null) { float sb = Mathf.Repeat(s, bb.len); return sb < bb.e0 ? 0 : sb < bb.e1 ? 1 : sb < bb.e2 ? 2 : 3; }
            var L = loops[i]; float a = Mathf.PI * L.R, top = L.f1 - L.f0;
            s = Mathf.Repeat(s, L.length);
            return s < a ? 0 : s < a + top ? 1 : s < 2f * a + top ? 2 : 3;
        }

        float GroundY(Vector3 p, Transform ignore)
        {
            var ter = Terrain.activeTerrain;
            if (ter) return ter.SampleHeight(p) + ter.transform.position.y;
            float best = float.MaxValue, y = p.y - 1f;
            int hits = Physics.RaycastNonAlloc(p + Vector3.up * 3f, Vector3.down, GroundLook.Hits, 10f, ~0, QueryTriggerInteraction.Ignore);   // no garbage per frame
            for (int h = 0; h < hits; h++)
            {
                var hit = GroundLook.Hits[h];
                if (ignore && hit.collider.transform.IsChildOf(ignore)) continue;
                if (hit.distance < best) { best = hit.distance; y = hit.point.y; }
            }
            return y;
        }

        public void Tick(float dt)
        {
            if (dt <= 0f || loops.Length == 0) return;
            ApplyLoopAdjust();   // the position controls apply live
            beltTimer -= dt;
            if (beltTimer <= 0f || belts == null || belts.Length != tracks.Length) { BuildBelts(); beltTimer = beltRefresh; }   // the belts move with the suspension
            var root = tank ? tank : transform;
            if (hasLast)
            {
                Vector3 v = (root.position - lastPos) / dt;
                vel = Vector3.Lerp(vel, v, 1f - Mathf.Exp(-dt * 8f));
                float dyaw = Mathf.DeltaAngle(lastRot.eulerAngles.y, root.rotation.eulerAngles.y) / dt;
                yawRate = Mathf.Lerp(yawRate, dyaw, 1f - Mathf.Exp(-dt * 8f));
            }
            lastPos = root.position; lastRot = root.rotation; hasLast = true;
            Vector3 fwd = root.TransformDirection(fwdLocal);
            float along = Vector3.Dot(vel, fwd);
            float halfTrack = loops.Length >= 2 ? Mathf.Abs(loops[1].sc - loops[0].sc) * 0.5f : 1.5f;
            float stick = Mathf.Lerp(1f, 0.35f, mud);   // how readily it lets go (mud clings)
            if (trackGround.Length != loops.Length) trackGround = new float[loops.Length];
            for (int i = 0; i < loops.Length; i++) trackGround[i] = GroundY(root.TransformPoint(Local(i, 0f, 0f)), root);
            float groundY = trackGround.Length > 0 ? trackGround[0] : root.position.y;
            bool terrain = Terrain.activeTerrain;

            // pick up: where the track leaves the ground (rear when going forward, front when reversing)
            for (int i = 0; i < loops.Length; i++)
            {
                float side = loops.Length >= 2 ? (i == 0 ? 1f : -1f) : 0f;
                float ts = along + side * yawRate * Mathf.Deg2Rad * halfTrack;   // this track's ground speed (signed)
                float n = pickup * Mathf.Abs(ts) / 10f * groundFactor * dt + acc[i];
                int cnt = Mathf.FloorToInt(n); acc[i] = n - cnt;
                var bl = BeltOf(i);
                float s0 = ts >= 0f ? 0f : bl != null ? bl.e2 : 2f * Mathf.PI * loops[i].R + (loops[i].f1 - loops[i].f0);   // reversing: lifts off at the front
                for (int k = 0; k < cnt && pieces.Count < maxRiding + maxFalling; k++)
                {
                    bool lump = R() < Mathf.Lerp(lumpShare, Mathf.Min(1f, lumpShare * 2.5f), mud);
                    pieces.Add(new Piece
                    {
                        track = i, s = s0 + R() * 0.2f * Mathf.Sign(ts + 1e-4f), across = (R() - 0.5f) * loops[i].width * 0.9f, riding = true, lump = lump,
                        size = lump ? Mathf.Lerp(lumpSizeMin, lumpSizeMax, Mathf.Pow(R(), 2f)) * (1f + mud * 0.5f) : grainSize * (0.6f + 0.8f * R()),
                        rot = Random.rotation, spin = Random.onUnitSphere * 400f, look = (int)(R() * 1000f), seed = R() * 100f, groundY = groundY
                    });
                }
            }

            // move: riding pieces go round with their track, and may let go; loose ones fly
            int riding = 0;
            for (int j = pieces.Count - 1; j >= 0; j--)
            {
                var p = pieces[j];
                if (p.riding)
                {
                    int i = p.track;
                    float side = loops.Length >= 2 ? (i == 0 ? 1f : -1f) : 0f;
                    float ts = along + side * yawRate * Mathf.Deg2Rad * halfTrack;
                    float sPrev = p.s;
                    p.s += ts * dt;                                           // the track runs round at its ground speed
                    int seg = Segment(i, p.s);
                    if (seg == 3) { pieces.RemoveAt(j); continue; }           // back under the track: pressed into the ground again
                    // which part of the loop: the arc where the track rises off the ground (rear going forward), the top run,
                    // or the arc where it turns down to go under (front going forward)
                    var L = loops[i];
                    var bl2 = BeltOf(i);
                    float e0 = bl2 != null ? bl2.e0 : Mathf.PI * L.R, e1 = bl2 != null ? bl2.e1 : Mathf.PI * L.R + (L.f1 - L.f0), e2 = bl2 != null ? bl2.e2 : 2f * Mathf.PI * L.R + (L.f1 - L.f0);
                    float sr = Mathf.Repeat(p.s, LoopLen(i));
                    bool fwdRun = ts >= 0f;
                    bool lifting = fwdRun ? seg == 0 : seg == 2, dropping = fwdRun ? seg == 2 : seg == 0;
                    float arc01 = seg == 0 ? sr / Mathf.Max(e0, 1e-4f) : seg == 2 ? (sr - e1) / Mathf.Max(e2 - e1, 1e-4f) : 0f;   // 0..1 along the arc in loop order
                    float low = dropping ? (fwdRun ? arc01 : 1f - arc01) : 0f;               // dropping arc: 1 where the track meets the ground
                    float rate = seg == 1 ? dribble : lifting ? rearShed : frontFling * Mathf.SmoothStep(0.15f, 1f, low);
                    rate *= stick * (Mathf.Abs(ts) / 6f + 0.2f);
                    Vector3 lpos = Local(i, p.s, p.across), lprev = Local(i, sPrev, p.across);
                    Vector3 wpos = root.TransformPoint(lpos);
                    // riding on the outer face of the track
                    p.pos = wpos;
                    if (R() < 1f - Mathf.Exp(-rate * dt) || riding >= maxRiding)
                    {
                        p.riding = false;
                        Vector3 tangent = root.TransformVector(lpos - lprev) / dt;   // the track surface's own motion (relative to the tank)
                        Vector3 fwdW = root.TransformDirection(fwdLocal) * (fwdRun ? 1f : -1f);   // the way the tank is going
                        Vector3 outSide = root.TransformDirection(sideLocal) * Mathf.Sign(L.sc + 1e-4f);   // away from the hull
                        Vector3 rel;
                        if (seg == 1)
                        {
                            // off the top run: it slides off the track's outer edge and falls down its side
                            p.across = Mathf.Sign(L.sc + 1e-4f) * L.width * 0.5f;
                            wpos = root.TransformPoint(Local(i, p.s, p.across));
                            rel = tangent * 0.5f + outSide * (0.4f + 1.0f * R()) + Vector3.up * 0.3f * R();
                        }
                        else if (lifting)
                        {
                            // round the rising wheel: thrown off backwards and a little up, never on over the hull
                            Vector3 outW = root.TransformDirection(NormalLocal(i, p.s));
                            rel = tangent * 0.6f + outW * (Mathf.Abs(ts) * fling * (0.3f + 0.7f * R())) + outSide * 0.6f * R();
                            float onward = Vector3.Dot(rel, fwdW);
                            if (onward > 0f) rel -= fwdW * onward;                                  // not forward over the hull
                            float rise = Vector3.Dot(rel, Vector3.up), riseMax = maxRise * (0.5f + 0.5f * R());
                            if (rise > riseMax) rel -= Vector3.up * (rise - riseMax);              // stays below the hull top
                        }
                        else
                        {
                            // where the track turns down to go under: it drops off in front of the wheel
                            rel = tangent * 0.35f + outSide * 0.4f * R() + Random.insideUnitSphere * 0.3f;
                        }
                        p.pos = wpos;
                        p.vel = vel + rel + Random.insideUnitSphere * (0.3f + Mathf.Abs(ts) * 0.05f);
                        p.groundY = terrain ? GroundY(wpos, root) : trackGround[i];   // terrain: cheap exact height; a mesh ground: the track's own
                        if (seg != 1 && R() < puffChance)
                            puffs.Add(new GunFlashFX.Particle
                            {
                                pos = wpos, type = 8f, axis = new Vector3(soil.r, soil.g, soil.b) * 1.15f, width = puffSize * (0.6f + 0.8f * R()),
                                life = puffLife * (0.6f + 0.8f * R()), intensity = puffOpacity * (0.5f + R()), vel = vel + rel * 0.3f, seed = R() * 100f, grow = 3f + 2f * R(), drag = 2.5f
                            });
                    }
                    else riding++;
                }
                else
                {
                    float k = 1.73e-4f / Mathf.Max(p.size, 0.002f);
                    p.vel += Physics.gravity * dt;
                    p.vel /= 1f + k * p.vel.magnitude * dt;
                    p.pos += p.vel * dt;
                    p.rot = Quaternion.Euler(p.spin * dt) * p.rot;
                    if (p.pos.y < p.groundY - p.size) { pieces.RemoveAt(j); continue; }
                }
                pieces[j] = p;
            }
            for (int q = puffs.Count - 1; q >= 0; q--)
            {
                var pf = puffs[q];
                pf.age += dt;
                if (pf.age >= pf.life) { puffs.RemoveAt(q); continue; }
                pf.age01 = pf.age / pf.life;
                pf.vel += (Vector3.up * 0.3f - pf.vel) * (1f - Mathf.Exp(-pf.drag * dt));
                pf.pos += pf.vel * dt;
                puffs[q] = pf;
            }
            Sync();
        }

        readonly List<MeshFilter> poolMf = new List<MeshFilter>();
        readonly List<MeshRenderer> poolMr = new List<MeshRenderer>();

        void Sync()
        {
            grains.Clear();
            int li = 0;
            foreach (var p in pieces)
            {
                if (p.lump && li < maxLumps)
                {
                    if (li >= pool.Count)
                    {
                        var go = new GameObject("TrackSoil") { hideFlags = HideFlags.DontSave };
                        go.transform.SetParent(transform, false);
                        poolMf.Add(go.AddComponent<MeshFilter>());
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.shadowCastingMode = ShadowCastingMode.Off;
                        poolMr.Add(mr);
                        pool.Add(go.transform);
                    }
                    var tr = pool[li];
                    if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                    tr.SetPositionAndRotation(p.pos, p.rot);
                    tr.localScale = new Vector3(1f, 0.6f, 1f) * p.size;
                    var mesh = meshes[p.look % meshes.Length];
                    if (poolMf[li].sharedMesh != mesh) poolMf[li].sharedMesh = mesh;
                    var mat = mats[(p.look / meshes.Length) % mats.Length];
                    if (poolMr[li].sharedMaterial != mat) poolMr[li].sharedMaterial = mat;
                    li++;
                }
                else
                    grains.Add(new GunFlashFX.Particle { pos = p.pos, type = 9f, axis = new Vector3(soil.r, soil.g, soil.b), width = p.lump ? p.size * 0.7f : p.size, life = 1f, intensity = 1f, seed = p.seed });
            }
            for (int k = li; k < pool.Count; k++) if (pool[k].gameObject.activeSelf) pool[k].gameObject.SetActive(false);
        }

        public int CopyVisible(List<GunFlashFX.Particle> dst) { dst.AddRange(puffs); dst.AddRange(grains); return grains.Count + puffs.Count; }

        public string Stats()
        {
            int ride = 0; Vector3 mr = Vector3.zero, mf = Vector3.zero; int nf = 0;
            var root = tank ? tank : transform;
            foreach (var p in pieces) { var lp = root.InverseTransformPoint(p.pos); var fl = new Vector3(Vector3.Dot(lp, fwdLocal), Vector3.Dot(lp, upLocal), Vector3.Dot(lp, sideLocal)); if (p.riding) { ride++; mr += fl; } else { nf++; mf += fl; } }
            string where = $" riding mean (fwd,up,side) {(ride > 0 ? mr / ride : Vector3.zero)}, flying mean {(nf > 0 ? mf / nf : Vector3.zero)}, loop0 f0 {(loops.Length > 0 ? loops[0].f0 : 0):F2} f1 {(loops.Length > 0 ? loops[0].f1 : 0):F2} uc {(loops.Length > 0 ? loops[0].uc : 0):F2} sc {(loops.Length > 0 ? loops[0].sc : 0):F2}";
            var b0 = BeltOf(0);
            where += b0 != null ? $", belt0 len {b0.len:F2} rise {b0.e0:F2} top {b0.e1 - b0.e0:F2} fall {b0.e2 - b0.e1:F2} ground {b0.len - b0.e2:F2}, start {b0.p[0]}" : ", no belt (box loop)";
            return $"spill: speed {vel.magnitude:F1} m/s, {pieces.Count} pieces ({ride} riding), loops {loops.Length} (R {(loops.Length > 0 ? loops[0].R : 0f):F2} m, length {(loops.Length > 0 ? loops[0].length : 0f):F1} m), soil {soil}" + where;
        }

        void Update() { if (Application.isPlaying && autoAdvance) Tick(Time.deltaTime); }
    }
}
