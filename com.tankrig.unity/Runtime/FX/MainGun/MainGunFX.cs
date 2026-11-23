// The main gun's shot as light particles (the play-mode default; the GPU simulation MuzzleBlastSim stays for recorded
// takes). Its stages follow reference footage of a 120 mm shot (game and real firing clips); the big shapes are
// flipbooks baked from our own simulation (FlashAtlas.Default, side views along the bore):
//   0-80 ms      flame cone, core, brake jets and sparks (a GunFlashFX at the main gun's calibre)
//   0-0.15 s     the flash light (lights the hull)
//   0-0.25 s     the fireball (baked slice 0) and two smaller lumps so no two shots look alike
//   0.06-0.5 s   shock dust racing out low over the ground, to the sides and ahead of the muzzle, and off the ground all
//                along the tank (out from its sides, back along it), each patch when the blast wave gets there
//   0.01-2 s     dust shaken off the tank: from its real top surface - at the shot the tank's meshes (not the gun) are drawn
//                from straight above into a small height map on the GPU (works without Read/Write) and read back; dust
//                rises from the texels facing up (hull deck, turret roof, fenders) when the blast wave gets there,
//                thrown up and back along the hull away from the muzzle; a shot uses the dust up (it builds up again
//                over time and while driving)
//   0.06-2.9 s   the ground dust (baked slice 2): rises and spreads, the ground's colour, thin enough to see the tank
//   0.15-3 s     the muzzle cloud (baked slice 1), drifting with the wind
//   0.25-4 s     a thin jet of smoke shooting out of the bore after the shot, weakening, following the muzzle as it moves
//   gravel       stones thrown off the ground all round (MuzzleDebris, without a simulation)
// Fired into the air the fireball and the cloud follow the bore up (placed along it, the cards tilted on screen the way
// it runs) and the ground under the muzzle gets less and less of the blast (none from about 45 deg); the blast still
// shakes the hull and the ground round the tank, weaker.
// The blast dust holds a while before it thins out, rises slowly and drifts with the wind.
// Everything is drawn by GunFlashPass (half resolution for the soft parts). Nothing collides.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankRig.FX
{
    /// <summary>The stages of the particle main gun: on / off, strength (opacity or amount), delay, time stretch.</summary>
    [System.Serializable]
    public class MainGunControls
    {
        [Tooltip("flame cone, core, brake jets, sparks")] public FxChannel flash = new FxChannel();
        public FxChannel light = new FxChannel();
        public FxChannel fireball = new FxChannel();
        [Tooltip("the smoke cloud in front of the gun")] public FxChannel cloud = new FxChannel();
        [Tooltip("the dust cloud raised off the ground")] public FxChannel dust = new FxChannel();
        [Tooltip("dust racing out low over the ground with the blast wave")] public FxChannel shockDust = new FxChannel();
        [Tooltip("smoke pouring out of the bore after the shot (amount: opacity, duration: how long)")] public FxChannel wisp = new FxChannel();
        [Tooltip("stones thrown off the ground")] public FxChannel gravel = new FxChannel();
        [Tooltip("dust shaken off the tank's upper faces by the blast")] public FxChannel hullDust = new FxChannel();
    }

    [AddComponentMenu("Tank Rig/Main Gun FX（主砲の発砲・軽量）")]
    public class MainGunFX : MonoBehaviour, IFlashParticles
    {
        public Transform muzzle;
        [Tooltip("the tank: its colliders are not the ground, no gravel under it")] public Transform tank;
        [Tooltip("the gun's moving part (barrel, mantlet): no dust lies on it")] public Transform gunRoot;
        [Tooltip("find the tank's top surface on the GPU at each shot (exact for any mesh); off: the parts' boxes")] public bool gpuSurface = true;
        [Tooltip("bore (mm): every size follows it (the flipbooks are baked for 120 mm)")] public float caliberMm = 120f;
        [Header("Sizes (x the calibre's)")]
        public float fireballSize = 0.8f;
        public float cloudSize = 1.0f;
        public float dustSize = 0.65f;
        [Header("Look")]
        [Range(0f, 1f)] public float cloudOpacity = 0.75f;
        [Tooltip("the dust cloud: kept thin (the footage never hides the tank)")] [Range(0f, 1f)] public float dustOpacity = 0.3f;
        [Tooltip("nits of the fireball at full temperature")] public float fireballRadiance = 6e5f;
        [Header("Dust on the tank")]
        [Tooltip("puffs off the hull per shot (with a full load of dust)")] public int hullDustPuffs = 45;
        [Range(0f, 1f)] public float hullDustOpacity = 0.3f;
        [Tooltip("s: how long the hull takes to gather its dust again standing still (driving: faster)")] public float hullDustRecover = 30f;
        [Header("Bore smoke (after the shot)")]
        [Tooltip("s: how long smoke keeps coming out of the bore")] public float boreSmokeTime = 4f;
        [Tooltip("streaks per second right after the fire (falls off to none): stretched by their speed they join into one jet")] public float boreSmokeRate = 200f;
        [Tooltip("m: jet thickness at the muzzle (x the calibre's; it swells about 4x)")] public float boreSmokeSize = 0.12f;
        [Tooltip("m/s: jet speed out of the bore right after the fire (falls off with the gas pressure)")] public float boreSmokeSpeed = 30f;
        [Tooltip("s: streak length = speed x this (longer: a more continuous jet)")] public float boreSmokeStretch = 0.12f;
        [Range(0f, 1f)] public float boreSmokeOpacity = 0.6f;
        [Tooltip("ground colour of the dust (alpha 0 = the tank's dust colour from TankDustFX, else dry soil)")] public Color dustColor = new Color(0f, 0f, 0f, 0f);
        public MainGunControls fx = new MainGunControls();
        [Header("Scene")]
        public Light sun;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;
        public Vector3 wind = new Vector3(1.5f, 0f, 2.5f);
        public bool autoAdvance = true;

        struct Puff
        {
            public GunFlashFX.Particle p;          // what is drawn
            public float delay, age, life;
            public int slice; public float bake0, rate;   // baked sprites: the shot time shown at age 0, and how fast it runs
            public float opacity, fadeIn, fadeOut;
            public Vector3 vel; public float drag, windK, rise;
        }
        readonly List<Puff> puffs = new List<Puff>();
        GunFlashFX flash;
        MuzzleDebris debris;
        TankDustFX tankDust;
        int shots;
        float boreAge = -1f, boreAcc, boreScale = 1f;   // the bore smoke of the last shot (age < 0: none)
        // the tank's surface: points sampled once (area-weighted) in each part's own space; the dust lying on it (0..1)
        struct HullPoint { public Transform tr; public Vector3 p, n; public float seed; }
        HullPoint[] hull;
        /// <summary>Debug: record where every part of the next shots starts (kind, position, opacity, size).</summary>
        [System.NonSerialized] public bool recordSources;
        public readonly List<(string kind, Vector3 pos, float opacity, float size)> DebugSources = new List<(string, Vector3, float, float)>();
        string curKind = "";
        /// <summary>Debug: the hull points that counted as out in the open at the last shot.</summary>
        public IEnumerable<Vector3> ExposedPoints { get { for (int i = 0; i < exposedCount; i++) yield return exposedP[i]; } }
        /// <summary>Debug: what the tank's surface was sampled from.</summary>
        public string HullInfo { get; private set; } = "";
        float hullLoad = 1f;
        Vector3 lastTankPos; bool hasTankPos;
        System.Random rng = new System.Random(4321);
        float R() => (float)rng.NextDouble();
        float J(float a) => 1f + (R() * 2f - 1f) * a;
        Transform M => muzzle ? muzzle : transform;

        public Vector3 SunDir => sun ? -sun.transform.forward : Vector3.up;
        public Color SunLux => sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;
        public Color Ambient => ambient;
        /// <summary>The flash (flame cone, jets, light) of the main gun.</summary>
        public GunFlashFX Flash => flash;
        public MuzzleDebris Gravel => debris;

        void OnEnable()
        {
            if (!sun) sun = FxScene.FindSun();
            if (!GunFlashFX.Sources.Contains(this)) GunFlashFX.Sources.Add(this);
            if (flash) flash.enabled = true;
        }
        void OnDisable()
        {
            GunFlashFX.Sources.Remove(this);
            puffs.Clear();
            if (flash) flash.enabled = false;
        }
        void OnDestroy()
        {
            if (heightRT) { heightRT.Release(); Destroy(heightRT); }
            heightCmd?.Release();
            if (flash) Destroy(flash.gameObject);
            if (debris) Destroy(debris.gameObject);
        }

        /// <summary>Make the flash, its light, the gravel's look and load the flipbooks now (not at the first shot).</summary>
        public void Prewarm()
        {
            EnsureParts();
            flash.Prewarm();
            if (debris) debris.Prewarm();
            var _ = FlashAtlas.Default;
            // a shot nobody sees: the first real one then costs no more than the others (code, look and hull footprint ready)
            Shot();
            shots--;
            puffs.Clear(); flash.Clear(); if (debris) debris.Clear(); boreAge = -1f; hullLoad = 1f;
            ignoreSurfaceUpTo = surfaceSerial;   // its height map, when it comes back, makes no dust
        }

        void EnsureParts()
        {
            if (!flash)
            {
                var go = new GameObject("Main Gun Flash");
                go.transform.SetParent(transform, false);
                flash = go.AddComponent<GunFlashFX>();
            }
            flash.muzzle = M;
            flash.sun = sun;
            if (!debris)
            {
                var go = new GameObject($"{name} Main Gun Gravel");   // in the world: stones fly on where they were thrown
                debris = go.AddComponent<MuzzleDebris>();
            }
            debris.muzzle = M; debris.ignoreRoot = tank;
            if (!tankDust) tankDust = GetComponent<TankDustFX>();
            if (hull == null && tank) SampleHull(2400);
        }

        /// <summary>Points on the tank's surface, area-weighted, in each part's own space (readable meshes: their triangles;
        /// others: the top of their box). Sampled once; a shot keeps the ones facing up at that moment.</summary>
        void SampleHull(int want)
        {
            var tris = new List<(Transform tr, Vector3 a, Vector3 b, Vector3 c, float area)>();
            float total = 0f;
            int nonReadable = 0;
            foreach (var r in tank.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || (r.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;   // our own clods, flashes...
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var sc = r.transform.lossyScale;
                void AddTri(Vector3 a, Vector3 b, Vector3 c)
                {
                    float ar = Vector3.Cross(Vector3.Scale(b - a, sc), Vector3.Scale(c - a, sc)).magnitude * 0.5f;
                    if (ar < 1e-5f) return;
                    tris.Add((r.transform, a, b, c, ar)); total += ar;
                }
                var mesh = mf.sharedMesh;
                if (!mesh.isReadable)
                {
                    nonReadable++;
                    // no Read/Write: the part's box stands in, and of its six faces the one that looks up in the world
                    // (blocks are often placed turned over or on their side: their own +y is not "up")
                    var bb = mesh.bounds;
                    int bestAxis = 1; float bestSign = 1f, bestY = -2f;
                    for (int ax = 0; ax < 3; ax++)
                        for (int sg = -1; sg <= 1; sg += 2)
                        {
                            var ln = Vector3.zero; ln[ax] = sg;
                            float y = r.transform.TransformDirection(ln).normalized.y;
                            if (y > bestY) { bestY = y; bestAxis = ax; bestSign = sg; }
                        }
                    int a1 = (bestAxis + 1) % 3, a2 = (bestAxis + 2) % 3;
                    Vector3 C(float x, float z)
                    {
                        var o = Vector3.zero; o[bestAxis] = bestSign; o[a1] = x; o[a2] = z;
                        return bb.center + Vector3.Scale(bb.extents, o);
                    }
                    // wound so the normal points out of the box along that axis (this order faces -axis, the other +axis)
                    if (bestSign < 0f) { AddTri(C(-1, -1), C(-1, 1), C(1, 1)); AddTri(C(-1, -1), C(1, 1), C(1, -1)); }
                    else { AddTri(C(-1, -1), C(1, 1), C(-1, 1)); AddTri(C(-1, -1), C(1, -1), C(1, 1)); }
                    continue;
                }
                using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
                {
                    var md = data[0];
                    var vs = new Unity.Collections.NativeArray<Vector3>(md.vertexCount, Unity.Collections.Allocator.Temp);
                    md.GetVertices(vs);
                    for (int sm = 0; sm < md.subMeshCount; sm++)
                    {
                        var idx = new Unity.Collections.NativeArray<int>(md.GetSubMesh(sm).indexCount, Unity.Collections.Allocator.Temp);
                        md.GetIndices(idx, sm);
                        for (int i = 0; i + 2 < idx.Length; i += 3) AddTri(vs[idx[i]], vs[idx[i + 1]], vs[idx[i + 2]]);
                        idx.Dispose();
                    }
                    vs.Dispose();
                }
            }
            int cols = tank.GetComponentsInChildren<Collider>().Length;
            HullInfo = $"{tank.GetComponentsInChildren<MeshRenderer>().Length} renderers, {tris.Count} triangles, {total:F1} m2, {cols} colliders, {nonReadable} meshes without Read/Write";
            if (tris.Count == 0) { hull = new HullPoint[0]; return; }
            var cdf = new float[tris.Count]; float acc = 0f;
            for (int i = 0; i < tris.Count; i++) { acc += tris[i].area; cdf[i] = acc; }
            var rnd = new System.Random(17);
            hull = new HullPoint[want];
            for (int i = 0; i < want; i++)
            {
                int ti = System.Array.BinarySearch(cdf, (float)rnd.NextDouble() * acc); if (ti < 0) ti = ~ti; ti = Mathf.Min(ti, tris.Count - 1);
                var t = tris[ti];
                float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble(); if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                hull[i] = new HullPoint { tr = t.tr, p = t.a + (t.b - t.a) * u + (t.c - t.a) * v, n = Vector3.Cross(t.b - t.a, t.c - t.a).normalized, seed = (float)rnd.NextDouble() };
            }
        }

        // ---- the tank's top surface from the GPU: a height map seen straight from above
        static Material heightMat;
        RenderTexture heightRT;
        CommandBuffer heightCmd;
        MaterialPropertyBlock heightProps;
        readonly List<Renderer> surfaceParts = new List<Renderer>();
        int surfaceSerial, ignoreSurfaceUpTo = -1;
        float surfaceGroundK = 1f;   // how much of the blast reaches the ground (raised gun: less)
        const float HeightCell = 0.1f;
        /// <summary>Debug: the last height map's size and how many texels counted as top surface.</summary>
        public string SurfaceInfo { get; private set; } = "";

        void CollectSurfaceParts()
        {
            surfaceParts.Clear();
            if (!tank) return;
            foreach (var r in tank.GetComponentsInChildren<MeshRenderer>())
            {
                if ((r.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;
                if (gunRoot && r.transform.IsChildOf(gunRoot)) continue;   // the gun: no dust on it
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                surfaceParts.Add(r);
            }
        }

        /// <summary>Draws the tank (not the gun) from above into the height map and asks for it back; the hull dust and
        /// the dust along the tank are made when it arrives. False when the GPU way is not available.</summary>
        bool RequestSurface(Vector3 muzzlePos, Vector3 bore, Vector3 under, float s, Color col, bool ground)
        {
            if (!SystemInfo.supportsAsyncGPUReadback) return false;
            if (!heightMat) { var sh = Shader.Find("Hidden/TankRig/HullHeight"); if (!sh) return false; heightMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave }; }
            if (surfaceParts.Count == 0) CollectSurfaceParts();
            Vector3 right = Vector3.Cross(Vector3.up, bore);   // the map's x; its y runs along the bore
            float r0 = float.MaxValue, r1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            Vector3 tp = tank.position;
            int used = 0;
            foreach (var r in surfaceParts)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                if (b.size.magnitude > 20f || Vector3.Distance(new Vector3(b.center.x, tp.y, b.center.z), tp) > 14f) continue;   // stray helpers
                for (int c = 0; c < 8; c++)
                {
                    var w = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                    float x = Vector3.Dot(w, right), z = Vector3.Dot(w, bore);
                    r0 = Mathf.Min(r0, x); r1 = Mathf.Max(r1, x); b0 = Mathf.Min(b0, z); b1 = Mathf.Max(b1, z); y0 = Mathf.Min(y0, w.y); y1 = Mathf.Max(y1, w.y);
                }
                used++;
            }
            if (used == 0) return false;
            r0 -= 0.2f; r1 += 0.2f; b0 -= 0.2f; b1 += 0.2f;
            float cell = Mathf.Max(HeightCell, Mathf.Max(r1 - r0, b1 - b0) / 256f);
            int nx = Mathf.Clamp(Mathf.CeilToInt((r1 - r0) / cell), 8, 256), ny = Mathf.Clamp(Mathf.CeilToInt((b1 - b0) / cell), 8, 256);
            if (!heightRT || heightRT.width != nx || heightRT.height != ny)
            {
                if (heightRT) { heightRT.Release(); Destroy(heightRT); }
                heightRT = new RenderTexture(nx, ny, 24, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) { name = "TankRig HullHeight", hideFlags = HideFlags.HideAndDontSave };
                heightRT.Create();
            }
            // the camera: above the middle, looking down, the bore up the image
            float cx = (r0 + r1) * 0.5f, cz = (b0 + b1) * 0.5f;
            Vector3 camPos = right * cx + bore * cz + Vector3.up * (y1 + 1f);
            camPos += Vector3.up * 0f;
            var rot = Quaternion.LookRotation(Vector3.down, bore);
            var view = Matrix4x4.TRS(camPos, rot, new Vector3(1f, 1f, -1f)).inverse;
            float hw = nx * cell * 0.5f, hh = ny * cell * 0.5f;
            var proj = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-hw, hw, -hh, hh, 0.05f, (y1 - y0) + 3f), true);
            heightCmd ??= new CommandBuffer { name = "TankRig hull height" };
            heightProps ??= new MaterialPropertyBlock();
            heightCmd.Clear();
            heightCmd.SetRenderTarget(heightRT);
            heightCmd.ClearRenderTarget(true, true, new Color(-1e4f, 0f, 0f, 0f), 1f);
            heightCmd.SetGlobalMatrix("_HH_VP", proj * view);
            foreach (var r in surfaceParts)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                heightProps.SetMatrix("_HH_M", r.localToWorldMatrix);
                for (int sm = 0; sm < mesh.subMeshCount; sm++) heightCmd.DrawMesh(mesh, Matrix4x4.identity, heightMat, sm, 0, heightProps);
            }
            Graphics.ExecuteCommandBuffer(heightCmd);
            int serial = ++surfaceSerial;
            float t0 = Time.time;
            Vector3 origin = camPos - right * hw - bore * hh;   // texel (0, 0)'s corner, at the camera's height
            var cfx = fx;
            AsyncGPUReadback.Request(heightRT, 0, req =>
            {
                if (!this || serial <= ignoreSurfaceUpTo) return;
                float shift = Time.time - t0;
                if (req.hasError) { ExposeHull(muzzlePos, bore); }
                else FromHeight(req.GetData<Vector2>(), nx, ny, cell, origin, right, bore, muzzlePos);
                curKind = "hull";
                if (cfx.hullDust.On) HullDust(muzzlePos, bore, s, col, shift);
                curKind = "alongtank";
                if (cfx.shockDust.On && ground) GroundAlongTank(muzzlePos, under, bore, s, col, shift);
                curKind = "";
            });
            return true;
        }

        /// <summary>The exposed top surface and the footprint from the height map.</summary>
        void FromHeight(Unity.Collections.NativeArray<Vector2> data, int nx, int ny, float cell, Vector3 origin, Vector3 right, Vector3 bore, Vector3 muzzlePos)
        {
            exposedCount = 0;
            int n = nx * ny;
            if (exposedP.Length < n) { exposedP = new Vector3[n]; exposedN = new Vector3[n]; }
            Vector3 side = Vector3.Cross(Vector3.up, bore);
            float uMin = float.MaxValue, uMax = float.MinValue, wMax = 0f;
            int valid = 0;
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    var t = data[y * nx + x];
                    if (t.x < -1e3f) continue;   // nothing of the tank here
                    valid++;
                    Vector3 w = origin + right * ((x + 0.5f) * cell) + bore * ((y + 0.5f) * cell);
                    w.y = t.x;
                    var d = w - muzzlePos;
                    float u = Vector3.Dot(d, bore), sw = Mathf.Abs(Vector3.Dot(d, side));
                    uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u); wMax = Mathf.Max(wMax, sw);
                    if (t.y < 0.6f) continue;    // a steep face seen from above: no dust lies there
                    exposedP[exposedCount] = w; exposedN[exposedCount] = Vector3.up; exposedCount++;
                }
            if (valid > 0) { footBack = uMin; footFront = Mathf.Min(uMax, 0f); footHalf = Mathf.Max(wMax, 1f); }
            SurfaceInfo = $"height map {nx} x {ny} at {cell:F2} m: {valid} texels of tank, {exposedCount} facing up; footprint {footBack:F1}..{footFront:F1} x +-{footHalf:F1} m";
        }

        // the tank's exposed upper surface at this shot (world), and its footprint along / across the bore
        Vector3[] exposedP = new Vector3[0], exposedN = new Vector3[0];
        int exposedCount;
        float footBack, footFront, footHalf;   // along the bore from the muzzle (back < 0), half width
        readonly Dictionary<long, float> topY = new Dictionary<long, float>();

        /// <summary>Of the hull points facing up now, the ones on top: only the highest surface in each 0.3 m column of
        /// the tank is out in the open (parts inside the hull, or under the turret, are not). Also the tank's footprint.</summary>
        void ExposeHull(Vector3 muzzlePos, Vector3 bore)
        {
            Vector3 gunAxis = M.forward;   // the barrel lies along the bore line, back from the muzzle
            exposedCount = 0;
            footBack = -6f; footFront = 0f; footHalf = 1.8f;
            if (hull == null || hull.Length == 0) return;
            if (exposedP.Length != hull.Length) { exposedP = new Vector3[hull.Length]; exposedN = new Vector3[hull.Length]; }
            Vector3 side = Vector3.Cross(Vector3.up, bore);
            const float cell = 0.3f;
            long Key(Vector3 w) => ((long)Mathf.FloorToInt(w.x / cell) << 32) ^ (uint)Mathf.FloorToInt(w.z / cell);
            topY.Clear();
            float uMin = float.MaxValue, uMax = float.MinValue, wMax = 0f;
            var wp = new Vector3[hull.Length]; var wn = new Vector3[hull.Length];
            for (int i = 0; i < hull.Length; i++)
            {
                var h = hull[i];
                if (!h.tr) { wn[i] = Vector3.zero; continue; }
                wp[i] = h.tr.TransformPoint(h.p);
                var n = h.tr.TransformDirection(h.n);
                wn[i] = n.sqrMagnitude > 1e-6f ? n.normalized : Vector3.zero;
                var d = wp[i] - muzzlePos;
                // the gun itself: within 0.35 m of its axis, up to 9 m back - no dust lies on it, and it is not the hull
                float along = Vector3.Dot(d, gunAxis);
                if (along < 0.3f && along > -9f && (d - gunAxis * along).magnitude < 0.35f) { wn[i] = Vector3.zero; continue; }
                float u = Vector3.Dot(d, bore), sw = Mathf.Abs(Vector3.Dot(d, side));
                if (u > -15f && u < 1f && sw < 3f)   // the hull, not the gun's far end (or stray helpers)
                {
                    uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u); wMax = Mathf.Max(wMax, sw);
                }
                long key = Key(wp[i]);
                if (!topY.TryGetValue(key, out float y) || wp[i].y > y) topY[key] = wp[i].y;
            }
            // the hull's front edge: the frontmost upward point well off the middle line (the gun sticks out past it)
            float hullFront = float.MinValue;
            for (int i = 0; i < hull.Length; i++)
            {
                if (wn[i].y < 0.35f) continue;
                var d = wp[i] - muzzlePos;
                float u = Vector3.Dot(d, bore), sw = Mathf.Abs(Vector3.Dot(d, side));
                if (sw > 0.6f && sw < 3f && u < 0.5f && u > -15f) hullFront = Mathf.Max(hullFront, u);
            }
            if (hullFront == float.MinValue) hullFront = 0f;
            if (uMin < uMax) { footBack = uMin; footFront = Mathf.Min(hullFront, 0f); footHalf = Mathf.Max(wMax, 1f); }
            for (int i = 0; i < hull.Length; i++)
            {
                if (wn[i].y < 0.35f) continue;                               // dust lies on faces looking up
                if (wp[i].y < topY[Key(wp[i])] - 0.12f) continue;           // something above it: inside / under
                {   // the gun out past the hull's front (barrel, muzzle brake): no dust there
                    var d = wp[i] - muzzlePos;
                    if (Vector3.Dot(d, bore) > hullFront + 0.1f && Mathf.Abs(Vector3.Dot(d, side)) < 0.5f) continue;
                }
                exposedP[exposedCount] = wp[i]; exposedN[exposedCount] = wn[i]; exposedCount++;
            }
        }

        /// <summary>The blast wave raises the dust off the ground all along the tank: out from its sides, back along it
        /// behind the muzzle, each patch when the wave gets there (the dust round the muzzle itself is the shock dust).</summary>
        void GroundAlongTank(Vector3 muzzlePos, Vector3 under, Vector3 bore, float s, Color col, float shift = 0f)
        {
            Vector3 side = Vector3.Cross(Vector3.up, bore);
            int n = Mathf.RoundToInt(28 * fx.shockDust.amount * (0.5f + 0.5f * surfaceGroundK));
            for (int i = 0; i < n; i++)
            {
                // a strip just outside each side of the hull, from its rear to the muzzle, and a patch behind it
                float u = Mathf.Lerp(footBack - 2.5f, Mathf.Max(footFront, footBack + 1f), R());
                bool behind = u < footBack;
                float sd = R() < 0.5f ? -1f : 1f;
                float wAcross = behind ? (R() * 2f - 1f) * footHalf : sd * (footHalf + 0.05f + 2.0f * R() * R());   // most right by the tracks
                Vector3 p = under + bore * u + side * wAcross;
                if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out var hit, 8f, ~0, QueryTriggerInteraction.Ignore) && !(tank && hit.collider.transform.IsChildOf(tank)))
                    p.y = hit.point.y;
                float d = Vector3.Distance(p, muzzlePos);
                float strength = 1f / (1f + (d / (5f * s)) * (d / (5f * s)));
                Vector3 outDir = behind ? -bore : (side * Mathf.Sign(wAcross) * 0.8f - bore * 0.6f).normalized;   // out from the hull, back along it
                Vector3 v = outDir * (12f + 22f * R()) * (0.4f + 0.6f * strength) * Mathf.Sqrt(s) + Vector3.up * (0.5f + 1.3f * R()) * (0.5f + 0.5f * strength);
                Add(new GunFlashFX.Particle
                {
                    pos = p + Vector3.up * 0.3f, type = (float)GunFlashFX.PType.Dust, axis = new Vector3(col.r, col.g, col.b),
                    width = (0.9f + 0.6f * R()) * Mathf.Sqrt(s), grow = 2.2f + 1.0f * R(),
                    intensity = Mathf.Min(1f, (0.42f + 0.22f * R()) * (0.5f + 0.5f * strength) * dustOpacity / 0.6f), seed = R() * 100f, life = 1f,
                    extra = new Vector4(0.3f, 1f, 0f, 0f)
                }, Mathf.Max(0f, fx.shockDust.delay + d / 340f + 0.02f * R() - shift), (3.5f + 2.5f * R()) * fx.shockDust.duration, v, 5f, 1f, behind ? 0.6f : 0.25f);   // lingers; behind the hull it climbs
            }
        }

        /// <summary>The blast shakes the dust off the tank's upper faces: strongest near the muzzle, each point when the
        /// wave gets there, thrown up and back along the hull (away from the muzzle).</summary>
        void HullDust(Vector3 muzzlePos, Vector3 bore, float s, Color col, float shift = 0f)
        {
            if (exposedCount == 0 || hullLoad <= 0.02f) return;
            int want = Mathf.RoundToInt(hullDustPuffs * fx.hullDust.amount * hullLoad * (0.6f + 0.4f * surfaceGroundK));
            if (want <= 0) return;
            // exposed points facing up, the stronger the nearer the muzzle; dusty and clean patches
            int made = 0;
            for (int k = 0; k < want * 8 && made < want; k++)
            {
                int pick = rng.Next(exposedCount);   // anywhere on the top (the points are stored row by row: no runs)
                var w = exposedP[pick];
                var n = exposedN[pick];
                float d = Vector3.Distance(w, muzzlePos);
                float strength = 0.55f + 0.45f / (1f + (d / (5f * s)) * (d / (5f * s)));   // the whole top is shaken, more near the gun
                float patch = Mathf.PerlinNoise(w.x * 0.7f + 3.1f, w.z * 0.7f + 7.7f);
                if (R() > (0.25f + 0.75f * strength) * (0.3f + 0.9f * patch)) continue;
                made++;
                Vector3 away = Vector3.ProjectOnPlane(w - muzzlePos, Vector3.up);
                away = away.sqrMagnitude > 1e-4f ? away.normalized : -bore;
                Vector3 v = n * (2f + 3f * R()) * (0.5f + 0.5f * strength) + away * (3f + 7f * R()) * strength + Random.insideUnitSphere * 0.6f;
                Add(new GunFlashFX.Particle
                {
                    pos = w + n * 0.05f + new Vector3(R() - 0.5f, 0f, R() - 0.5f) * 0.3f, type = (float)GunFlashFX.PType.Dust, axis = new Vector3(col.r, col.g, col.b),
                    width = (0.3f + 0.25f * R()) * Mathf.Sqrt(s), grow = 2.5f + 1.2f * R(),
                    intensity = Mathf.Min(1f, hullDustOpacity * (0.7f + 0.5f * R()) * (0.5f + 0.5f * strength)), seed = R() * 100f, life = 1f,
                    extra = new Vector4(0.15f, 1f, 0f, 0f)
                }, Mathf.Max(0f, fx.hullDust.delay + d / 340f + 0.01f * R() - shift), (1.6f + 1.4f * R()) * fx.hullDust.duration, v, 3.5f, 1f, 0.35f);
            }
            hullLoad = Mathf.Max(0f, hullLoad - 0.55f);   // most of it is gone now
        }

        /// <summary>One shot of the main gun.</summary>
        public void Shot()
        {
            EnsureParts();
            shots++;
            var m = M;
            Vector3 mp = m.position, f = m.forward, up = Vector3.up;
            Vector3 bore = Vector3.ProjectOnPlane(f, up);
            if (bore.sqrMagnitude < 1e-4f) bore = Vector3.ProjectOnPlane(m.up, up);
            bore.Normalize();
            Vector3 side = Vector3.Cross(up, bore);
            float yaw = Mathf.Atan2(bore.z, bore.x);
            // fired into the air: the elevation, how much of it is "up" (0 level .. 1 steep), how much reaches the ground under the muzzle
            float elev = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f));
            float up01 = Mathf.Clamp01(elev / 0.6f);
            float groundK = Mathf.Clamp01(1f - (elev - 0.1f) / 0.6f);
            Vector3 perpUp = Vector3.Cross(f, side).normalized;   // "up" in the plane of the bore
            float s = Mathf.Pow(Mathf.Max(caliberMm, 20f) / 120f, 0.9f);
            bool ground = GroundBelow(mp, out float gy) && mp.y - gy < 8f * s;
            Vector3 under = new Vector3(mp.x, ground ? gy : mp.y - 2.3f * s, mp.z);
            var at = FlashAtlas.Default;
            var c = fx;

            // flame: the gun-flash model at this calibre, a little longer for a tank gun; the light longer still
            var p = GunFlashProfile.Preset(GunKind.Autocannon, caliberMm);
            p.flashTime = 0.05f; p.fireChance = 0f; p.smokePuffs = 0;
            p.lightLumen = 3e7f * s * s; p.lightRange = 60f * s; p.lightTime = 0.06f;
            flash.profile = p;
            flash.sun = sun; flash.wind = wind;
            var fc = flash.fx;
            Copy(c.flash, fc.flash); Copy(c.flash, fc.jets); Copy(c.flash, fc.disc); Copy(c.flash, fc.sparks);
            Copy(c.light, fc.light);
            fc.fireball.enabled = false; fc.smoke.enabled = false;
            if (c.flash.On || c.light.On) flash.Shot();

            Color smokeCol = new Color(0.55f, 0.53f, 0.50f);
            Color dust = DustColour();

            // fireball: the baked one and two smaller lumps beside it (each shot a little different)
            curKind = "fireball";
            if (c.fireball.On)
            {
                float k = s * fireballSize, rate = J(0.1f) / c.fireball.duration;
                Vector3 fbPos = AlongBore(at, FlashAtlas.SliceFire, mp, f, perpUp, k);
                Baked(FlashAtlas.SliceFire, fbPos, at.timing[FlashAtlas.SliceFire].w * k * J(0.08f), yaw, true,
                      rate, c.fireball.delay, Mathf.Min(1f, c.fireball.amount), 0f, 0.1f, new Color(0.25f, 0.22f, 0.2f), fireballRadiance, 0f, 0f, 0f, elev);
                for (int i = 0; i < 2; i++)
                {
                    Vector3 off = side * ((i == 0 ? 1f : -1f) * (0.4f + 0.8f * R()) * k * 2f) + bore * (R() - 0.3f) * k * 3f + up * (R() - 0.4f) * k;
                    off = side * Vector3.Dot(off, side) + f * Vector3.Dot(off, bore) + perpUp * off.y;   // in the bore's frame
                    Baked(FlashAtlas.SliceFire, fbPos + off, at.timing[FlashAtlas.SliceFire].w * k * (0.45f + 0.2f * R()), yaw, true,
                          rate * J(0.15f), c.fireball.delay + 0.005f + 0.02f * R(), 0.8f * Mathf.Min(1f, c.fireball.amount), 0.01f, 0.08f, new Color(0.25f, 0.22f, 0.2f), fireballRadiance * 0.8f, 0f, 0f, 0f, elev);
                }
            }
            // the muzzle cloud and a smaller one beside it, drifting off with the wind
            curKind = "cloud";
            if (c.cloud.On)
            {
                float k = s * cloudSize * Mathf.Lerp(1f, 0.7f, up01), rate = J(0.08f) / c.cloud.duration;   // in the air it does not spread over the ground
                // on the ground in front of a level gun, out along the bore in the air for a raised one
                Vector3 cPos = Vector3.Lerp(Place(at, FlashAtlas.SliceSmoke, mp, under, bore, k, ground), AlongBore(at, FlashAtlas.SliceSmoke, mp, f, perpUp, k * 0.85f), up01);
                Baked(FlashAtlas.SliceSmoke, cPos, at.timing[FlashAtlas.SliceSmoke].w * k, yaw, false,
                      rate, c.cloud.delay, cloudOpacity * c.cloud.amount, 0.1f, 1.4f, smokeCol, 0f, 0.6f, 1f, 0.25f, elev * up01);
                Vector3 off = side * (R() < 0.5f ? 1f : -1f) * (1.5f + 1.5f * R()) * k + bore * (R() - 0.5f) * 2f * k;
                Vector3 c2 = Vector3.Lerp(Place(at, FlashAtlas.SliceSmoke, mp, under, bore, k * 0.7f, ground), AlongBore(at, FlashAtlas.SliceSmoke, mp, f, perpUp, k * 0.6f), up01);
                Baked(FlashAtlas.SliceSmoke, c2 + off, at.timing[FlashAtlas.SliceSmoke].w * k * 0.7f, yaw + (R() - 0.5f) * 1.2f, false,
                      rate * J(0.1f), c.cloud.delay + 0.05f, 0.6f * cloudOpacity * c.cloud.amount, 0.12f, 1.2f, smokeCol, 0f, 0.6f, 1f, 0.3f, elev * up01);
            }
            // dust off the ground under the gun: the baked cloud (round: it keeps its width seen along the bore) and two to the sides
            curKind = "dustcard";
            if (c.dust.On && ground && groundK > 0.05f)
            {
                float k = s * dustSize, rate = J(0.08f) / c.dust.duration;
                Baked(FlashAtlas.SliceDust, Place(at, FlashAtlas.SliceDust, mp, under, bore, k, true), at.timing[FlashAtlas.SliceDust].w * k, yaw, false,
                      rate, c.dust.delay, dustOpacity * c.dust.amount * groundK, 0.06f, 1.2f, dust, 0f, 0.5f, 0.8f, 0.1f);
                for (int i = 0; i < 2; i++)
                {
                    float sd = i == 0 ? 1f : -1f;
                    Vector3 off = side * sd * (2.5f + 2f * R()) * k * 2f + bore * (R() - 0.2f) * 3f * k;
                    float y2 = yaw + sd * (0.6f + 0.4f * R());   // turned a little out to its side
                    Baked(FlashAtlas.SliceDust, Place(at, FlashAtlas.SliceDust, mp, under, bore, k * 0.6f, true) + off, at.timing[FlashAtlas.SliceDust].w * k * 0.6f, y2, false,
                          rate * J(0.1f), c.dust.delay + 0.04f + 0.05f * R(), 0.6f * dustOpacity * c.dust.amount * groundK, 0.06f, 1.0f, dust, 0f, 0.5f, 0.8f, 0.1f);
                }
            }
            // shock dust: low puffs racing out with the blast wave, to the sides and ahead (little behind: the hull is there)
            curKind = "shock";
            if (c.shockDust.On && ground)
            {
                int n = Mathf.RoundToInt(14 * c.shockDust.amount * groundK);
                for (int i = 0; i < n; i++)
                {
                    float ang = (R() * 2f - 1f) * 150f;
                    Vector3 dir = Quaternion.AngleAxis(ang, up) * bore;
                    float r0 = (0.8f + 2f * R()) * s;
                    Vector3 pos = under + dir * r0 + up * 0.25f * s;
                    // the blast throws it out hard and low; the air stops it within a few metres; then it hangs, rolls and drifts
                    float v = (25f + 30f * R()) * s * (1f - 0.35f * Mathf.Abs(ang) / 150f);
                    Add(new GunFlashFX.Particle
                    {
                        pos = pos, type = (float)GunFlashFX.PType.Dust, axis = new Vector3(dust.r, dust.g, dust.b),
                        width = (1.0f + 0.6f * R()) * s, grow = 2.2f + 1.0f * R(), intensity = Mathf.Min(1f, (0.48f + 0.25f * R()) * dustOpacity / 0.6f), seed = R() * 100f, life = 1f,
                        extra = new Vector4(0.25f, 1f, 0f, 0f)   // billow mode; holds the first quarter of its life
                    }, c.shockDust.delay + 0.06f + r0 / 340f + 0.03f * R(), (2.5f + 1.5f * R()) * c.shockDust.duration, dir * v + up * (0.6f + 1.4f * R()), 6f, 1f, 0.25f);
                }
            }
            // dust shaken off the tank, and raised off the ground along it: from the tank's real top surface (GPU height
            // map, read back a frame or two later - the blast wave takes about as long to get there), else from the boxes
            surfaceGroundK = groundK;
            if (!(gpuSurface && RequestSurface(mp, bore, under, s, dust, ground)))
            {
                ExposeHull(mp, bore);
                curKind = "hull";
                if (c.hullDust.On) HullDust(mp, bore, s, dust);
                curKind = "alongtank";
                if (c.shockDust.On && ground) GroundAlongTank(mp, under, bore, s, dust);
            }
            curKind = "bore";
            // smoke keeps pouring out of the bore once the fire has gone (emitted in Tick, from wherever the muzzle is then)
            if (c.wisp.On) { boreAge = -c.wisp.delay - 0.25f; boreAcc = 0f; boreScale = s; }
            // gravel
            if (debris)
            {
                debris.channel.enabled = c.gravel.enabled; debris.channel.amount = c.gravel.amount; debris.channel.delay = c.gravel.delay; debris.channel.duration = c.gravel.duration;
                debris.Throw();
            }
        }

        static void Copy(FxChannel a, FxChannel b) { b.enabled = a.enabled; b.amount = a.amount; b.delay = a.delay; b.duration = a.duration; }

        /// <summary>A baked card's centre out along the real bore (raised too): its offset along and "above" it.</summary>
        static Vector3 AlongBore(FlashAtlas at, int slice, Vector3 muzzlePos, Vector3 fwd, Vector3 perpUp, float k)
        {
            var pl = at.place[slice];
            return muzzlePos + fwd * pl.x * k + perpUp * pl.y * k;
        }

        /// <summary>Where a baked card's centre goes: from the muzzle, or from the ground under it (the cloud and the dust).</summary>
        static Vector3 Place(FlashAtlas at, int slice, Vector3 muzzlePos, Vector3 under, Vector3 bore, float k, bool fromGround)
        {
            var pl = at.place[slice];
            return fromGround ? under + bore * pl.x * k + Vector3.up * (pl.y + pl.z) * k
                              : muzzlePos + bore * pl.x * k + Vector3.up * pl.y * k;
        }

        void Baked(int slice, Vector3 pos, float half, float yaw, bool narrowAlong, float rate, float delay, float opacity, float fadeIn, float fadeOut,
                   Color albedo, float emission, float drag, float windK, float rise, float elev = 0f)
        {
            var at = FlashAtlas.Default;
            var tm = at.timing[slice];
            if (recordSources) DebugSources.Add((curKind, pos, opacity, half));
            float life = Mathf.Max((tm.y - tm.x) / Mathf.Max(rate, 0.05f), 0.02f);
            puffs.Add(new Puff
            {
                p = new GunFlashFX.Particle
                {
                    pos = pos, type = (float)GunFlashFX.PType.Sprite, axis = new Vector3(albedo.r, albedo.g, albedo.b), width = half, seed = R() * 100f, life = 1f,
                    delay = elev,   // the card's direction's elevation (rad; the shader tilts it) - the puff's own delay is separate
                    extra = new Vector4(3f, slice, emission, 0f),
                    extra2 = new Vector4((R() - 0.5f) * 0.08f, slice == FlashAtlas.SliceDust ? 1.5f : 0.6f, 1.0f, (narrowAlong ? 1f : -1f) * (10f + Mathf.Repeat(yaw + Mathf.PI, 2f * Mathf.PI) - Mathf.PI)),
                },
                delay = delay + tm.x / Mathf.Max(rate, 0.05f), life = life, slice = slice, bake0 = tm.x, rate = rate,
                opacity = opacity, fadeIn = fadeIn, fadeOut = fadeOut, drag = drag, windK = windK, rise = rise,
            });
        }

        void Add(GunFlashFX.Particle p, float delay, float life, Vector3 vel, float drag, float windK, float rise = 0f)
        {
            if (recordSources) DebugSources.Add((curKind, p.pos, p.intensity, p.width));
            AddPuff(p, delay, life, vel, drag, windK);
            if (rise != 0f) { var q = puffs[puffs.Count - 1]; q.rise = rise; puffs[puffs.Count - 1] = q; }
        }
        void AddPuff(GunFlashFX.Particle p, float delay, float life, Vector3 vel, float drag, float windK)
            => puffs.Add(new Puff { p = p, delay = delay, life = Mathf.Max(life, 0.02f), slice = -1, opacity = p.intensity, vel = vel, drag = drag, windK = windK });

        Color DustColour()
        {
            if (dustColor.a > 0f) return dustColor;
            if (tankDust && tankDust.isActiveAndEnabled)
            {
                var g = tankDust.DustColor;
                return Color.Lerp(g, new Color(0.68f, 0.62f, 0.52f), 0.6f);   // fine blast dust is much paler than the soil
            }
            return new Color(0.58f, 0.52f, 0.43f);
        }

        bool GroundBelow(Vector3 p, out float y)
        {
            y = 0f;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 0.5f, Vector3.down, GroundLook.Hits, 40f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = GroundLook.Hits[i];
                if (tank && h.collider.transform.IsChildOf(tank)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return best < float.MaxValue;
        }

        /// <summary>Advance every part (the flash and the gravel advance themselves).</summary>
        public void Tick(float dt)
        {
            BoreSmoke(dt);
            // the hull gathers dust again: slowly standing, faster while driving
            if (tank && dt > 0f)
            {
                float speed = hasTankPos ? (tank.position - lastTankPos).magnitude / dt : 0f;
                lastTankPos = tank.position; hasTankPos = true;
                hullLoad = Mathf.Min(1f, hullLoad + dt / Mathf.Max(hullDustRecover, 0.1f) * (1f + Mathf.Min(speed, 15f) * 0.6f));
            }
            if (puffs.Count == 0) return;
            var at = FlashAtlas.Default;
            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                var q = puffs[i];
                if (q.delay > 0f) { q.delay -= dt; puffs[i] = q; continue; }
                q.age += dt;
                if (q.age >= q.life) { puffs.RemoveAt(i); continue; }
                if (q.windK > 0f) q.vel += (wind * q.windK + Vector3.up * q.rise - q.vel) * (1f - Mathf.Exp(-Mathf.Max(q.drag, 0.3f) * dt));
                else if (q.drag > 0f) q.vel *= Mathf.Exp(-q.drag * dt);
                q.p.pos += q.vel * dt;
                q.p.age01 = q.age / q.life;
                if (q.p.type == (float)GunFlashFX.PType.BoreJet && q.vel.sqrMagnitude > 1e-4f) q.p.axis = -q.vel.normalized;
                if (q.slice >= 0)
                {
                    q.p.length = at.FrameAt(q.slice, q.bake0 + q.age * q.rate);
                    float fin = q.fadeIn > 0f ? Mathf.Clamp01(q.age / q.fadeIn) : 1f, fout = q.fadeOut > 0f ? Mathf.Clamp01((q.life - q.age) / q.fadeOut) : 1f;
                    q.p.intensity = q.opacity * fin * fin * (3f - 2f * fin) * fout * fout * (3f - 2f * fout);
                }
                puffs[i] = q;
            }
        }

        void BoreSmoke(float dt)
        {
            if (boreAge == -1f) return;
            boreAge += dt;
            if (boreAge < 0f) return;
            float T = boreSmokeTime * Mathf.Max(fx.wisp.duration, 0.05f);
            if (boreAge > T) { boreAge = -1f; return; }
            float k = 1f - boreAge / T;
            boreAcc += boreSmokeRate * (0.15f + 0.85f * k * k) * k * dt;
            var m = M;
            Vector3 f = m.forward;
            float s = boreScale;
            while (boreAcc >= 1f)
            {
                boreAcc -= 1f;
                float back = R() * dt;
                // a jet: fast and straight down the bore while the pressure lasts, braked by the air; each bit a streak
                // stretched by its speed, so the bits overlap into one continuous jet (shorter puffs as it slows)
                Vector3 v = f * boreSmokeSpeed * Mathf.Sqrt(s) * (0.06f + 0.94f * k * k) * (0.9f + 0.2f * R()) + Random.insideUnitSphere * 0.1f;
                Add(new GunFlashFX.Particle
                {
                    pos = m.position + f * 0.1f * s + v * back, type = (float)GunFlashFX.PType.BoreJet, axis = -v.normalized,
                    width = boreSmokeSize * s * (0.8f + 0.4f * R()), grow = 3f + 1.5f * R(),
                    intensity = Mathf.Min(1f, boreSmokeOpacity * fx.wisp.amount * (0.45f + 0.55f * k) * (0.8f + 0.4f * R())), seed = R() * 100f, life = 1f,
                    extra = new Vector4(0f, 0f, 0f, boreSmokeStretch)
                }, 0f, (1.0f + 0.7f * R()), v, 1.6f, 1f, 0.4f);
            }
        }

        public int CopyVisible(List<GunFlashFX.Particle> dst)
        {
            int n = 0;
            foreach (var q in puffs) if (q.delay <= 0f && q.age > 0f) { dst.Add(q.p); n++; }
            return n;
        }

        /// <summary>Debug: parts alive.</summary>
        public string Stats()
        {
            int sprites = 0, other = 0, waiting = 0;
            foreach (var q in puffs) { if (q.delay > 0f) waiting++; else if (q.slice >= 0) sprites++; else other++; }
            return $"main gun: {shots} shots, {sprites} cards, {other} puffs, {waiting} waiting, hull points {(hull == null ? 0 : hull.Length)} ({exposedCount} exposed), footprint {footBack:F1}..{footFront:F1} x +-{footHalf:F1} m, hull dust load {hullLoad:F2}";
        }

        void Update()
        {
            if (Application.isPlaying && autoAdvance) Tick(Time.deltaTime);
        }
    }
}
