// Muzzle blast prototype: a GPU gas solver attached to the muzzle (this transform: +X down the barrel, +Y up).
// Fire() starts a shot; Advance(dt) steps it (sub-stepped by the fastest emitter); the raymarch Custom Pass reads the
// globals pushed by PushGlobals(). Time base: seconds after shot exit (the 7 stages of the footage study).
using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace TankRig.FX
{
    [Serializable]
    public class MuzzleBlastParams
    {
        [Header("Grid")] public float cell = 0.075f;
        public float cfl = 3f, maxDt = 1f / 120f;
        public int jacobi = 140;
        [Header("Jet (bore blow-down)")] public float jetSpeed = 200f, jetRadius = 0.22f, jetDensity = 0.25f, jetLen = 0.35f, jetPulseHz = 30f, jetWobble = 0.12f;
        [Header("Fireball")] public Vector3 fireCenter = new Vector3(4.5f, 0.5f, 0f);
        public Vector3 fireRadii = new Vector3(5.5f, 3.0f, 3.0f);
        public float lumpFreq = 0.9f;
        public float fireSpeed = 60f, fireRadial = 15f, fireLumpVel = 25f, fuel = 1f, fireTemp = 3.2f, fireDensity = 0.05f;
        public float umbrellaSpeed = 70f;
        [Header("Combustion")] public float ignite = 0.6f, burnRate = 35f, mixK = 0.02f, heat = 1.2f, smokeYield = 0.28f, sootYield = 0.15f;
        public float expand = 0.0f, cool = 12f, warmCool = 0.6f, fuelDecay = 3f;
        [Header("Smoke")] public float tauMin = 2f, tauMax = 5f, beta = 2.5f, alpha = 0f, restRelax = 0.8f;
        public float vorticity = 0.6f, turb = 30f, turbSize = 2.5f, drag = 0.02f, windCouple = 0.25f;
        public Vector3 wind = new Vector3(1.5f, 0f, 2.5f);   // m/s, world (default: across, away from the side camera, a little downrange)
        public float dust = 0f;   // old scripted dust ring (replaced by the wind-driven pick-up below)
        [Header("Firing dust (own grid, never mixes with the smoke)")]
        public float dustCell = 0.15f, groundDust = 0.12f, groundRadius = 10f, groundSpeed = 55f, groundLift = 14f, groundWindow = 0.08f, groundCoarse = 0.3f;
        public float hullDustAmount = 3f, hullSpeed = 45f, hullLift = 8f, hullWindow = 0.15f;
        public float backBlow = 32f, backReach = 18f, hullDustDrag = 0.8f, dustKickUp = 0.4f;   // the blast's back-blow along the tank
        [Tooltip("Empty cells take their neighbours' velocity, so the dust can rise out of the layer it was thrown from (off: the old sheet)")]
        public bool dustSpread = true;
        public float dustDragFine = 5f, dustDragCoarse = 3f, dustFallFine = 0.25f, dustFallCoarse = 2.5f, dustTurb = 22f, dustTurbSize = 2.5f, dustTau = 2.4f, dustSigma = 18f;
        [Header("Old dust in the smoke grid (off)")] public float dustPick = 0f, dustThreshold = 10f, dustFall = 1.2f, dustLayer = 0.35f;
        [Header("Dust on the tank")] public float hullDust = 0f, hullShed = 0.06f, hullDustSpeed = 10f;
        public int hullDustPoints = 12000;
        [Header("Evacuator")] public float evacSpeed = 40f, evacRadius = 0.12f, evacDensity = 10f, evacPulseHz = 6f, evacWobble = 0.04f, evacTemp = 0.2f;
        [Tooltip("bore gas (jet, evacuator) density when there is no muzzle grid: the coarse grid spreads the thin streams over whole cells")]
        public float coarseBoreDens = 1f;
        public float tagFade = 12f, tagSlow = 18f, tagBase = 2.5f, tagTurb = 160f, tagPastFade = 5f, evacCoreLen = 1.8f, evacFlutter = 0.6f;   // bore gas (jet / evacuator) disperses once slower than tagSlow m/s
        [Header("Look")] public float sigma = 14f, gamma = 1.0f, flameSigma = 18f, flameRadiance = 1.5e6f, erosion = 0.6f, heatRange = 3.2f;
        public float flashLumen = 1.2e8f;
        [Header("Raymarch")] [Tooltip("optical depth per step through thin gas (adaptive steps)")] public float marchTau = 0.05f;
        [Tooltip("longest step through thin gas, in base steps (1 = fixed steps)")] public float marchMaxStep = 4f;
        public float marchNoNoise = 0f;   // cost test: 1 drops the detail noise
        [Tooltip("1 = detail noise from a precomputed tileable 3D texture (cheaper, real time); 0 = computed per step (captures)")]
        public float marchNoiseTex = 0f;
        [Header("Fireball look")] public float fireScatter = 0.8f, fireLightBlur = 1.5f, detailAmp = 0.18f, emitPow = 2.5f, fireCoreExp = 0.7f;
        [Header("Blast wave")] public float shockKick = 14f, shockRefract = 0.02f, shockShell = 0.22f, shockGlow = 0.12f, machX = 1.6f;
        [Header("Fine grid (muzzle)")] public float fineCell = 0.025f, fineJetRadius = 0.12f, fineEvacRadius = 0.0375f, fineTurb = 0.12f, fineVort = 0.25f, fineWindCouple = 1.5f, fineTurbSize = 0.8f, handoff = 0.6f, slowSpeed = 4f, handoffRate = 6f;
        public int fineJacobi = 80;
        [Header("Outer grid (the open air)")] public float outerCell = 0.3f, outerWindCouple = 1.0f, outerHandoffWidth = 2.5f, outerHandoffRate = 20f;
        public int outerJacobi = 60;
        public float gust = 1.2f, gustSize = 6f;
        public int debugView = 0;   // 1 = draw only the muzzle grid, 2 = only the coarse grid
        [Header("Fade")] public float fadeStart = 4.0f, fadeEnd = 5.2f, lateFadeStart = 3f, lateFadeRate = 1.0f;   // the last of the haze fades out: gone after ~5 s

        // "key=value,key=value" overrides (tests)
        public void Apply(string kv)
        {
            if (string.IsNullOrEmpty(kv)) return;
            foreach (var part in kv.Split(','))
            {
                var p = part.Split('=');
                if (p.Length != 2) continue;
                var f = GetType().GetField(p[0].Trim(), BindingFlags.Public | BindingFlags.Instance);
                if (f == null) { Debug.LogWarning("MuzzleBlast: unknown param " + p[0]); continue; }
                if (f.FieldType == typeof(float)) f.SetValue(this, float.Parse(p[1], CultureInfo.InvariantCulture));
                else if (f.FieldType == typeof(int)) f.SetValue(this, int.Parse(p[1], CultureInfo.InvariantCulture));
            }
        }
    }


    [ExecuteAlways]
    public class MuzzleBlastSim : MonoBehaviour
    {
        // one simulation grid (its own resolution, cell size and box, in muzzle space)
        class Grid
        {
            public string name;
            public Vector3Int res;
            public float cell;
            public Vector3 boxMin;
            public bool groundSolid;
            public Matrix4x4 toWorld = Matrix4x4.identity;   // grid space -> world (fixed when the gun fires)
            public RenderTexture v, s, r, vHat, sHat, rHat, vTmp, sTmp, rTmp, curl, pA, pB, div, expn, light, flA, flB, solid;
            public RenderTexture occRaw, occ;   // 8^3-cell bricks holding visible gas (raymarch empty-space skipping)
            public Vector3Int flRes;
            public Vector3Int BRes => new Vector3Int(res.x / 8, res.y / 8, res.z / 8);
            public Vector3 Size => (Vector3)res * cell;
            public Vector3Int Groups => new Vector3Int(res.x / 8, res.y / 8, res.z / 8);
            public bool Ready => v != null && v.IsCreated();

            public void Alloc()
            {
                if (Ready) return;
                var h4 = GraphicsFormat.R16G16B16A16_SFloat;
                v = Make(res, h4, name + "_V"); s = Make(res, h4, name + "_S"); r = Make(res, h4, name + "_R");
                vHat = Make(res, h4, name + "_VHat"); sHat = Make(res, h4, name + "_SHat"); rHat = Make(res, h4, name + "_RHat");
                vTmp = Make(res, h4, name + "_VTmp"); sTmp = Make(res, h4, name + "_STmp"); rTmp = Make(res, h4, name + "_RTmp");
                curl = Make(res, h4, name + "_Curl");
                pA = Make(res, GraphicsFormat.R32_SFloat, name + "_PA"); pB = Make(res, GraphicsFormat.R32_SFloat, name + "_PB");
                div = Make(res, GraphicsFormat.R32_SFloat, name + "_Div"); expn = Make(res, GraphicsFormat.R16_SFloat, name + "_Exp");
                light = Make(res, GraphicsFormat.R16G16_SFloat, name + "_Light");
                flRes = new Vector3Int((res.x + 3) / 4, (res.y + 3) / 4, (res.z + 3) / 4);
                flA = Make(flRes, h4, name + "_FireLightA"); flB = Make(flRes, h4, name + "_FireLightB");
                solid = Make(res, GraphicsFormat.R8_UNorm, name + "_Solid");
                occRaw = Make(BRes, GraphicsFormat.R16_SFloat, name + "_OccRaw"); occ = Make(BRes, GraphicsFormat.R16_SFloat, name + "_Occ");
            }

            public void Release()
            {
                foreach (var t in new[] { v, s, r, vHat, sHat, rHat, vTmp, sTmp, rTmp, curl, pA, pB, div, expn, light, flA, flB, solid, occRaw, occ })
                    if (t) { t.Release(); DestroyImmediate(t); }
                v = null;
            }
        }

        public ComputeShader solver;
        [Tooltip("occupancy bricks for the raymarch (MuzzleOcc.compute); none = the raymarch steps through empty space")]
        public ComputeShader occSolver;
        [Tooltip("the firing-dust solver (MuzzleDust.compute); none = no dust")]
        public ComputeShader dustSolver;
        public Vector3Int dustResolution = new Vector3Int(392, 64, 352);
        public Vector3 dustMin = new Vector3(-28f, 0f, -26f);   // y relative to the ground
        [Tooltip("the gun's muzzle (forward = +Z, the way the tank rig sets it up). Sources follow it every step (recoil, elevation, traverse); the gas stays in the world")]
        public Transform muzzle;
        [Tooltip("colliders under this root are ignored when looking for the ground under the muzzle (the tank itself)")]
        public Transform ignoreRoot;
        public float groundFallbackY = 0f;
        [Tooltip("the tank (its renderers become moving obstacles, its upward faces carry dust the blast blows off)")]
        public Transform obstacleRoot;
        public Vector3Int resolution = new Vector3Int(488, 176, 272);
        public float muzzleHeight = 2.3f;           // ground = bottom of the coarse grid
        public float behind = 10f;                   // coarse grid reaches this far behind the muzzle
        [Tooltip("fine grid around the muzzle: the blow-down jet and the bore evacuator stream")]
        public bool fineGrid = true;
        public Vector3Int fineResolution = new Vector3Int(256, 128, 128);   // 6.4 x 3.2 x 3.2 m at 2.5 cm (the evacuator stream is ~7.5 cm across)
        public Vector3 fineMin = new Vector3(-0.6f, -1.2f, -1.6f);
        [Tooltip("outer grid: the open air the smoke drifts into (coarse, large)")]
        public bool outerGrid = true;
        public Vector3Int outerResolution = new Vector3Int(272, 104, 200);
        public Vector3 outerMin = new Vector3(-16f, 0f, -30f);   // y is relative to the ground
        public MuzzleBlastParams p = new MuzzleBlastParams();
        [Tooltip("which parts of the effect appear, how strong and when; and how strongly they are drawn")]
        public MuzzleFxControls fx = new MuzzleFxControls();
        public Light sun;
        public Light flashLight;
        public Color ambient = new Color(0.55f, 0.65f, 0.85f) * 6000f;   // sky radiance seen by the smoke (nits)
        public bool autoAdvance = true;
        [Tooltip("real time: most solver steps per grid per frame (0 = as many as the CFL asks). A slow frame then runs the smoke a little slower instead of making the next frame slower still")]
        public int maxStepsPerFrame = 0;
        [Tooltip("real time: longest frame time the shot advances by (s, 0 = no limit)")]
        public float maxFrameDt = 0f;

        public float Time { get; private set; } = -1f;
        public bool Active => Time >= -0.01f && Time < FadeEnd + LastDelay + 0.2f;
        // the smoke lifetime control stretches dissipation and the final fade
        float Life => fx.look.smokeLifetime;
        float FadeEnd => p.fadeEnd * Life;
        float LateFade(float t, float life) => p.lateFadeRate / life * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(p.lateFadeStart * life, p.fadeEnd * life, t));
        float LastDelay => Mathf.Max(0f, Mathf.Max(fx.evacuator.delay, fx.groundDust.delay, fx.hullDust.delay, fx.fireball.delay));
        float SmokeSigma => p.sigma * fx.look.smokeDensity;
        float DustSigma => p.dustSigma * fx.look.dustDensity;
        // stage envelopes through the controls (timing only; amounts go to the solver separately)
        float XPre(float t) => fx.precursor.Env(EPre, t);
        float XUmb(float t) => fx.umbrella.Env(EUmb, t);
        float XMach(float t) => fx.machDisk.Env(EMach, t);
        float XFire(float t) => fx.fireball.Env(EFire, t);
        float XJet(float t) => fx.jet.Env(EJet, t);
        float XJetDens(float t) => fx.jet.Env(EJetDens, t);
        float XEvac(float t) => fx.evacuator.Env(EEvac, t);
        float XEvacSpd(float t) => EEvacSpd(fx.evacuator.Local(t));
        float XShockR(float t) => ShockR(fx.blastWave.Local(t));
        float XShockX(float t) => ShockX(fx.blastWave.Local(t));
        float XShockS(float t) => fx.blastWave.Amount * ShockS(fx.blastWave.Local(t));
        public int StepsLastAdvance { get; private set; }

        Grid A, B, C, D;
        bool UseD => dustSolver != null && D != null && D.Ready;
        const float PRE = 0.004f;

        static RenderTexture Make(Vector3Int res, GraphicsFormat f, string name)
        {
            var d = new RenderTextureDescriptor(res.x, res.y, f, 0) { dimension = TextureDimension.Tex3D, volumeDepth = res.z, enableRandomWrite = true, msaaSamples = 1 };
            var t = new RenderTexture(d) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            t.Create();
            return t;
        }

        void Alloc()
        {
            if (A == null) A = new Grid { name = "MB_A" };
            A.res = resolution; A.cell = p.cell; A.groundSolid = true;
            A.boxMin = new Vector3(-behind, -muzzleHeight, -resolution.z * p.cell * 0.5f);
            A.Alloc();
            if (fineGrid)
            {
                if (B == null) B = new Grid { name = "MB_B" };
                B.res = fineResolution; B.cell = p.fineCell; B.groundSolid = false; B.boxMin = fineMin;
                B.Alloc();
            }
            if (dustSolver)
            {
                if (D == null) D = new Grid { name = "MB_D" };
                D.res = dustResolution; D.cell = p.dustCell; D.groundSolid = true;
                D.Alloc();
            }
            if (outerGrid)
            {
                if (C == null) C = new Grid { name = "MB_C" };
                C.res = outerResolution; C.cell = p.outerCell; C.groundSolid = true;
                C.boxMin = new Vector3(outerMin.x, -muzzleHeight + outerMin.y, outerMin.z);
                C.Alloc();
            }
        }

        /// <summary>Real-time quality (game play): one coarse 0.25 m smoke grid over the same space (no muzzle grid, no
        /// open-air grid), 0.3 m dust, fewer pressure iterations, one solver step per 60 fps frame. Use with the custom
        /// pass at half resolution. Releases the grids; they are rebuilt at the next shot.</summary>
        [ContextMenu("Apply real-time preset")]
        /// <summary>Real-time settings. quality 0 = high (25 cm cells), 1 = medium (35 cm), 2 = low (50 cm): the same
        /// space covered with coarser grids and fewer pressure iterations (GPU time roughly with the cell count).</summary>
        public void ApplyRealtimePreset(int quality = 0)
        {
            Release();
            p.maxDt = 1f / 60f; p.cfl = 6f;   // longer steps in the first 0.15 s (semi-Lagrangian: stable)
            p.coarseBoreDens = 0.35f;   // the noise texture measured no faster: off
            fineGrid = false; outerGrid = false;
            maxFrameDt = 1f / 30f;   // below 30 fps the smoke slows down instead of the game
            if (quality <= 0)
            {
                resolution = new Vector3Int(144, 56, 80); p.cell = 0.25f; p.jacobi = 30;          // 36 x 14 x 20 m like the cinematic grid
                dustResolution = new Vector3Int(192, 32, 176); p.dustCell = 0.3f;                // 57.6 x 9.6 x 52.8 m
                maxStepsPerFrame = 3;
            }
            else if (quality == 1)
            {
                resolution = new Vector3Int(104, 40, 56); p.cell = 0.35f; p.jacobi = 20;          // 36.4 x 14 x 19.6 m
                dustResolution = new Vector3Int(128, 24, 120); p.dustCell = 0.45f;               // 57.6 x 10.8 x 54 m
                maxStepsPerFrame = 2;
            }
            else
            {
                resolution = new Vector3Int(72, 32, 40); p.cell = 0.5f; p.jacobi = 14;            // 36 x 16 x 20 m
                dustResolution = new Vector3Int(96, 16, 88); p.dustCell = 0.6f;                  // 57.6 x 9.6 x 52.8 m
                maxStepsPerFrame = 2;
                p.marchTau = 0.1f; p.marchMaxStep = 6f;                                         // fewer ray-march steps through thin gas
            }
        }

        void Release() { A?.Release(); B?.Release(); C?.Release(); D?.Release(); boxBuf?.Release(); surfPos?.Release(); surfNrm?.Release(); boxBuf = surfPos = surfNrm = null; }
        void OnDisable() { Live.Remove(this); Release(); }
        bool UseB => fineGrid && B != null && B.Ready;
        bool UseC => outerGrid && C != null && C.Ready;

        void Dispatch(Grid g, int k) { var n = g.Groups; solver.Dispatch(k, n.x, n.y, n.z); }

        Matrix4x4 fireFrame = Matrix4x4.identity;

        /// <summary>The muzzle as a frame with x down the bore (the tank rig's muzzle points +Z).</summary>
        Matrix4x4 MuzzleFrame()
        {
            if (muzzle) return Matrix4x4.TRS(muzzle.position, muzzle.rotation * Quaternion.Euler(0f, -90f, 0f), Vector3.one);
            return Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);   // legacy: this object, x = bore
        }

        float FindGround(Vector3 at)
        {
            var t = Terrain.activeTerrain;
            if (t != null) return t.SampleHeight(at) + t.transform.position.y;
            float best = float.MaxValue, y = groundFallbackY;
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 0.05f, Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (ignoreRoot && h.collider.transform.IsChildOf(ignoreRoot)) continue;
                if (h.distance < best) { best = h.distance; y = h.point.y; }
            }
            return y;
        }

        void Anchor()
        {
            fireFrame = MuzzleFrame();
            Vector3 mpos = fireFrame.GetColumn(3);
            Vector3 fwd = fireFrame.MultiplyVector(Vector3.right);
            Vector3 flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.right;
            var yaw = Quaternion.LookRotation(flat.normalized, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);
            float h = Mathf.Max(0.5f, mpos.y - FindGround(mpos));
            A.toWorld = Matrix4x4.TRS(mpos, yaw, Vector3.one);
            A.boxMin = new Vector3(-behind, -h, -A.res.z * A.cell * 0.5f);
            if (UseB) { B.toWorld = fireFrame; B.boxMin = fineMin; }
            if (UseC) { C.toWorld = A.toWorld; C.boxMin = new Vector3(outerMin.x, -h + outerMin.y, outerMin.z); }
            if (UseD) { D.toWorld = A.toWorld; D.boxMin = new Vector3(dustMin.x, -h + dustMin.y, dustMin.z); }
            muzzleHeightAtFire = h;
        }

        public float muzzleHeightAtFire { get; private set; }

        ComputeBuffer boxBuf, surfPos, surfNrm;
        int boxCount, surfCount;
        readonly System.Collections.Generic.List<(Transform t, Bounds b)> parts = new System.Collections.Generic.List<(Transform, Bounds)>();

        void CollectParts()
        {
            parts.Clear();
            if (!obstacleRoot) return;
            foreach (var r in obstacleRoot.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || (r.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds;
                var sz = Vector3.Scale(b.size, r.transform.lossyScale);
                if (Mathf.Abs(sz.x * sz.y * sz.z) < 0.002f) continue;   // bolts, lamps: too small for the grid
                // the gun barrel: a thin tube whose box is a big block ending at the muzzle (it walls off the muzzle and
                // throws the bore gas straight up); leave out every part whose box reaches the muzzle
                if (muzzle && Vector3.Distance(r.transform.TransformPoint(b.ClosestPoint(r.transform.InverseTransformPoint(muzzle.position))), muzzle.position) < 0.1f) continue;
                parts.Add((r.transform, b));
            }
            if (parts.Count > 128) parts.RemoveRange(128, parts.Count - 128);
        }

        // candidate surface points of the tank, sampled once (area-weighted) in each part's own space
        struct SurfCand { public Transform tr; public Vector3 p, n; public float patch, seed; }
        SurfCand[] surfCands;
        Transform surfCandRoot;
        Vector4[] surfPosArr, surfNrmArr;

        void SampleSurfaceCandidates(int want)
        {
            var tris = new System.Collections.Generic.List<(Transform tr, Vector3 a, Vector3 b, Vector3 c, float area)>();
            float total = 0;
            foreach (var r in obstacleRoot.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || (r.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                var sc = r.transform.lossyScale;
                void Add(Vector3 a, Vector3 b, Vector3 c)
                {
                    float ar = Vector3.Cross(Vector3.Scale(b - a, sc), Vector3.Scale(c - a, sc)).magnitude * 0.5f;
                    if (ar < 1e-5f) return;
                    tris.Add((r.transform, a, b, c, ar)); total += ar;
                }
                var mesh = mf.sharedMesh;
                if (!mesh.isReadable)
                {
                    // no Read/Write on the model: the top of the part's box stands in for its upper surfaces
                    var bb = mesh.bounds;
                    Vector3 C(float x, float z) => bb.center + Vector3.Scale(bb.extents, new Vector3(x, 1f, z));
                    Add(C(-1, -1), C(-1, 1), C(1, 1)); Add(C(-1, -1), C(1, 1), C(1, -1));
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
                        for (int i = 0; i + 2 < idx.Length; i += 3) Add(vs[idx[i]], vs[idx[i + 1]], vs[idx[i + 2]]);
                        idx.Dispose();
                    }
                    vs.Dispose();
                }
            }
            surfCandRoot = obstacleRoot;
            if (tris.Count == 0) { surfCands = new SurfCand[0]; return; }
            var cdf = new float[tris.Count]; float acc = 0;
            for (int i = 0; i < tris.Count; i++) { acc += tris[i].area; cdf[i] = acc; }
            var rnd = new System.Random(11);
            surfCands = new SurfCand[want];
            for (int i = 0; i < want; i++)
            {
                int ti = System.Array.BinarySearch(cdf, (float)rnd.NextDouble() * acc); if (ti < 0) ti = ~ti; ti = Mathf.Min(ti, tris.Count - 1);
                var t = tris[ti];
                float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble(); if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                var n = Vector3.Cross(t.b - t.a, t.c - t.a).normalized;
                surfCands[i] = new SurfCand { tr = t.tr, p = t.a + (t.b - t.a) * u + (t.c - t.a) * v, n = n, patch = 0f, seed = (float)rnd.NextDouble() };
            }
        }

        /// <summary>Upward-facing surface points of the tank (area-weighted), in A's grid space at the moment of firing.
        /// The tank's surface is sampled once; a shot only places the points where the parts are now and keeps those facing up.</summary>
        void CollectSurface()
        {
            surfCount = 0;
            if (!obstacleRoot || hullDustPointsOverride == 0 || !UseD) return;
            int count = hullDustPointsOverride;
            if (surfCands == null || surfCandRoot != obstacleRoot) SampleSurfaceCandidates(count * 3);   // ~a third of a tank faces up
            if (surfCands.Length == 0) return;
            if (surfPosArr == null || surfPosArr.Length != count) { surfPosArr = new Vector4[count]; surfNrmArr = new Vector4[count]; }
            var toGrid = A.toWorld.inverse;
            int k = 0;
            foreach (var c in surfCands)
            {
                if (k >= count) break;
                if (!c.tr) continue;
                var nw = c.tr.TransformDirection(c.n);
                if (nw.y < 0.35f) continue;   // dust lies on surfaces facing up (now)
                var w = c.tr.TransformPoint(c.p);
                var g = toGrid.MultiplyPoint(w);
                var gn = toGrid.MultiplyVector(nw).normalized;
                float patch = Mathf.PerlinNoise(w.x * 0.7f + 3.1f, w.z * 0.7f + 7.7f);   // dusty and clean patches
                surfPosArr[k] = new Vector4(g.x, g.y, g.z, 0.3f + 1.4f * patch);
                surfNrmArr[k] = new Vector4(gn.x, gn.y, gn.z, c.seed);
                k++;
            }
            if (k == 0) return;
            if (surfPos == null || surfPos.count != count) { surfPos?.Release(); surfNrm?.Release(); surfPos = new ComputeBuffer(count, 16); surfNrm = new ComputeBuffer(count, 16); }
            surfPos.SetData(surfPosArr, 0, 0, k); surfNrm.SetData(surfNrmArr, 0, 0, k);
            surfCount = k;
        }
        int hullDustPointsOverride => p.hullDustPoints;
        public int SurfacePointCount => surfCount;
        public int ObstacleParts => parts.Count;

        void UpdateSolids(Grid g)
        {
            if (!obstacleRoot || parts.Count == 0) return;
            var mats = new Matrix4x4[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var (t, b) = parts[i];
                // world -> unit box of the part's local bounds (slightly grown so thin plates still block a cell)
                var size = b.size + Vector3.one * (g.cell * 0.5f) * 0f;
                var unit = Matrix4x4.TRS(b.center, Quaternion.identity, new Vector3(Mathf.Max(size.x, 1e-3f), Mathf.Max(size.y, 1e-3f), Mathf.Max(size.z, 1e-3f))).inverse;
                mats[i] = unit * t.worldToLocalMatrix * g.toWorld;
            }
            if (boxBuf == null || boxBuf.count < mats.Length) { boxBuf?.Release(); boxBuf = new ComputeBuffer(Mathf.Max(mats.Length, 1), 64); }
            boxBuf.SetData(mats);
            boxCount = mats.Length;
            SetCommon(g, 0f);
            int k = solver.FindKernel("SolidMask");
            solver.SetBuffer(k, "_Boxes", boxBuf); solver.SetInt("_BoxCount", boxCount);
            solver.SetTexture(k, "_SolidOut", g.solid);
            Dispatch(g, k);
        }

        /// <summary>GPU memory held by the grids (MB).</summary>
        public float GridMemoryMB()
        {
            long bytes = 0;
            foreach (var g in new[] { A, B, C, D })
            {
                if (g == null || !g.Ready) continue;
                foreach (var t in new[] { g.v, g.s, g.r, g.vHat, g.sHat, g.rHat, g.vTmp, g.sTmp, g.rTmp, g.curl, g.pA, g.pB, g.div, g.expn, g.light, g.flA, g.flB, g.solid, g.occRaw, g.occ })
                    if (t) bytes += (long)t.width * t.height * Mathf.Max(1, t.volumeDepth) * GraphicsFormatUtility.GetBlockSize(t.graphicsFormat);
            }
            return bytes / (1024f * 1024f);
        }

        /// <summary>Debug: the obstacle parts whose box comes within r of a world point (name, box size, distance).</summary>
        public string PartsNear(Vector3 world, float r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (t, b) in parts)
            {
                var lp = t.InverseTransformPoint(world);
                var c = b.ClosestPoint(lp);
                float d = Vector3.Distance(t.TransformPoint(c), world);
                if (d <= r) sb.AppendLine($"  {t.name}: local size {b.size} scale {t.lossyScale} centre {t.TransformPoint(b.center)} dist {d:F3}");
            }
            return sb.ToString();
        }

        void DDispatch(int k) { var n = D.Groups; dustSolver.Dispatch(k, n.x, n.y, n.z); }

        void DustCommon(float t, float dt)
        {
            var ds = dustSolver;
            var toGrid = D.toWorld.inverse;
            ds.SetInts("_Res", D.res.x, D.res.y, D.res.z);
            ds.SetFloat("_Cell", D.cell); ds.SetFloat("_Dt", dt); ds.SetFloat("_T", t);
            ds.SetVector("_BoxMin", D.boxMin);
            ds.SetVector("_UpGrid", toGrid.MultiplyVector(Vector3.up).normalized);
            ds.SetVector("_Wind", toGrid.MultiplyVector(p.wind));
            ds.SetFloat("_GustAmp", p.gust * p.wind.magnitude); ds.SetFloat("_GustSize", p.gustSize);
            ds.SetVector("_ShockC", toGrid.MultiplyPoint(fireFrame.MultiplyPoint(new Vector3(XShockX(t), 0f, 0f))));
            ds.SetVector("_Under", new Vector3(0f, D.boxMin.y, 0f));
            ds.SetVector("_Back", new Vector3(-1f, 0f, 0f));
            ds.SetFloat("_UseSolid", 0f);   // the parts' bounding boxes swallow the hull top: dust ignores the tank (hidden inside it by depth)
            Vector3 toSun = sun ? -sun.transform.forward : Vector3.up;
            ds.SetVector("_SunDirGrid", toGrid.MultiplyVector(toSun).normalized);
            ds.SetFloat("_SigmaK", DustSigma);
        }

        /// <summary>The firing dust: its own grid, its own transport (no pressure, no smoke).</summary>
        void DustAdvance(float t0, float dt)
        {
            var ds = dustSolver;
            float t = t0, end = t0 + dt;
            int guard = 0;
            while (t < end - 1e-7f && guard++ < 40)
            {
                float maxV = (t < 0.3f ? p.groundSpeed * 1.3f : 8f) + p.wind.magnitude;
                float h = Mathf.Min(Mathf.Clamp(3f * D.cell / maxV, 0.0005f, p.maxDt), end - t);
                DustCommon(t, h);
                var gc = fx.groundDust; var hc = fx.hullDust;
                ds.SetFloat("_GTime", gc.Local(t)); ds.SetFloat("_HTime", hc.Local(t));
                ds.SetFloat("_GAmount", p.groundDust * gc.Amount / gc.duration); ds.SetFloat("_GRadius", p.groundRadius); ds.SetFloat("_GSpeed", p.groundSpeed);
                ds.SetFloat("_GLift", p.groundLift); ds.SetFloat("_GWindow", p.groundWindow); ds.SetFloat("_GCoarse", p.groundCoarse);
                // back-blow: rises with the blast, strongest 0.05-0.25 s, gone by ~0.8 s
                float tb = fx.backBlow.Local(t);
                float bb = fx.backBlow.Amount * (tb < 0f ? 0f : tb < 0.005f ? tb / 0.005f : tb < 0.25f ? 1f : Mathf.Clamp01(1f - (tb - 0.25f) / 0.55f));   // at full strength with the shot
                ds.SetFloat("_BackWind", p.backBlow * bb); ds.SetFloat("_BackReach", p.backReach); ds.SetFloat("_BackMax", p.backBlow); ds.SetFloat("_HullDrag", p.hullDustDrag);
                ds.SetFloat("_HAmount", p.hullDustAmount * hc.Amount / hc.duration * Mathf.Pow(0.15f / D.cell, 2f));   // bigger cells spread a point's dust over more air: thinner (not fully by volume: the sheet stays visible)
                ds.SetFloat("_HSpeed", p.hullSpeed); ds.SetFloat("_HLift", p.hullLift); ds.SetFloat("_HWindow", p.hullWindow);
                ds.SetFloat("_DragFine", p.dustDragFine); ds.SetFloat("_DragCoarse", p.dustDragCoarse);
                ds.SetFloat("_FallFine", p.dustFallFine); ds.SetFloat("_FallCoarse", p.dustFallCoarse);
                ds.SetFloat("_KickUp", p.dustKickUp); ds.SetFloat("_Spread", p.dustSpread ? 1f : 0f);
                ds.SetFloat("_TurbAmp", p.dustTurb); ds.SetFloat("_TurbSize", p.dustTurbSize); ds.SetFloat("_TauFine", p.dustTau * fx.look.dustLifetime);
                ds.SetFloat("_LateFade", LateFade(t, fx.look.dustLifetime));
                int k = ds.FindKernel("DGround");
                ds.SetTexture(k, "_DRW", D.s); ds.SetTexture(k, "_URW", D.v); ds.SetTexture(k, "_Solid", D.solid); DDispatch(k);
                if (surfCount > 0)
                {
                    k = ds.FindKernel("DSurface");
                    ds.SetBuffer(k, "_SurfPos", surfPos); ds.SetBuffer(k, "_SurfNrm", surfNrm); ds.SetInt("_SurfCount", surfCount);
                    ds.SetTexture(k, "_DRW", D.s); ds.SetTexture(k, "_URW", D.v);
                    ds.Dispatch(k, (surfCount + 63) / 64, 1, 1);
                }
                k = ds.FindKernel("DForces");
                ds.SetTexture(k, "_DRW", D.s); ds.SetTexture(k, "_URW", D.v); ds.SetTexture(k, "_Solid", D.solid); DDispatch(k);
                k = ds.FindKernel("DAdvect");
                ds.SetTexture(k, "_D", D.s); ds.SetTexture(k, "_U", D.v); ds.SetTexture(k, "_DOut", D.sTmp); ds.SetTexture(k, "_UOut", D.vTmp); DDispatch(k);
                (D.s, D.sTmp) = (D.sTmp, D.s); (D.v, D.vTmp) = (D.vTmp, D.v);
                t += h;
            }
        }

        void DustLight()
        {
            DustCommon(Time, 0f);
            int k = dustSolver.FindKernel("DLight");
            dustSolver.SetTexture(k, "_D", D.s); dustSolver.SetTexture(k, "_LightOut", D.light); DDispatch(k);
        }

        void SurfaceDustPass(float t, float dt)
        {
            if (surfCount == 0 || p.hullDust <= 0f) return;
            SetCommon(A, dt);
            solver.SetFloat("_T", t);
            solver.SetVector("_ShockC", A.toWorld.inverse.MultiplyPoint(fireFrame.MultiplyPoint(new Vector3(ShockX(t), 0f, 0f))));
            int k = solver.FindKernel("SurfaceDust");
            solver.SetBuffer(k, "_SurfPos", surfPos); solver.SetBuffer(k, "_SurfNrm", surfNrm); solver.SetInt("_SurfCount", surfCount);
            solver.SetFloat("_SurfDust", p.hullDust); solver.SetFloat("_SurfShed", p.hullShed); solver.SetFloat("_SurfSpeed", p.hullDustSpeed);
            solver.SetTexture(k, "_SRW", A.s); solver.SetTexture(k, "_VRW", A.v); solver.SetTexture(k, "_RRW", A.r);
            solver.Dispatch(k, (surfCount + 63) / 64, 1, 1);
        }

        public void Fire()
        {
            Alloc();
            Anchor();
            CollectParts();
            CollectSurface();
            var grids = new System.Collections.Generic.List<Grid> { A };
            if (UseB) grids.Add(B);
            if (UseC) grids.Add(C);
            foreach (var g in grids)
            {
                SetCommon(g, 0f);
                int k = solver.FindKernel("Clear");
                foreach (var t in new[] { (g.v, g.s, g.r, g.pA), (g.vHat, g.sHat, g.rHat, g.pB), (g.vTmp, g.sTmp, g.rTmp, g.pA) })
                {
                    solver.SetTexture(k, "_VOut", t.Item1); solver.SetTexture(k, "_SOut", t.Item2); solver.SetTexture(k, "_ROut", t.Item3);
                    solver.SetTexture(k, "_POut", t.Item4); solver.SetTexture(k, "_DivOut", g.div);
                    Dispatch(g, k);
                }
            }
            if (UseD)
            {
                int kc = dustSolver.FindKernel("DClear");
                foreach (var t in new[] { (D.s, D.v), (D.sTmp, D.vTmp) }) { dustSolver.SetTexture(kc, "_DOut", t.Item1); dustSolver.SetTexture(kc, "_UOut", t.Item2); DDispatch(kc); }
            }
            Time = -PRE;
            if (!prewarming) Fired?.Invoke(this);   // the warm-up shots throw no gravel
        }

        /// <summary>Raised at every shot (gravel and other add-ons start with it).</summary>
        public event Action<MuzzleBlastSim> Fired;
        bool prewarming;

        // ---------------------------------------------------------------- envelopes (seconds after shot exit)
        static float Env(float t, params float[] kv)   // pairs (time, value); 0 outside
        {
            if (t < kv[0] || t > kv[kv.Length - 2]) return 0f;
            for (int i = 0; i + 3 < kv.Length; i += 2)
                if (t <= kv[i + 2]) return Mathf.Lerp(kv[i + 1], kv[i + 3], Mathf.InverseLerp(kv[i], kv[i + 2], t));
            return kv[kv.Length - 1];
        }
        public static float EPre(float t) => Env(t, -0.0021f, 0f, -0.002f, 1f, 0.001f, 1f, 0.0015f, 0f);
        public static float EUmb(float t) => Env(t, 0.0004f, 0f, 0.0005f, 1f, 0.004f, 1f, 0.005f, 0f);
        public static float EFire(float t) => Env(t, 0.0019f, 0f, 0.002f, 0.3f, 0.004f, 1f, 0.010f, 1f, 0.012f, 0f);
        public static float EJet(float t) => Env(t, 0f, 1f, 0.03f, 1f, 0.08f, 0.55f, 0.15f, 0.3f, 0.30f, 0.12f, 0.40f, 0f);
        public static float EJetDens(float t) => Env(t, 0f, 1f, 0.03f, 1f, 0.15f, 0.9f, 0.30f, 0.6f, 0.40f, 0f);
        public static float EDust(float t) => Env(t, 0.0009f, 0f, 0.001f, 0.9f, 0.02f, 0.6f, 0.06f, 0.15f, 0.065f, 0f);
        public static float DustR(float t) => t < 0.001f ? Mathf.Lerp(0.3f, 0.6f, t / 0.001f) : t < 0.01f ? Mathf.Lerp(0.6f, 3.5f, (t - 0.001f) / 0.009f)
                                             : t < 0.03f ? Mathf.Lerp(3.5f, 9f, (t - 0.01f) / 0.02f) : Mathf.Lerp(9f, 15f, Mathf.Clamp01((t - 0.03f) / 0.03f));
        public static float EEvac(float t) => Env(t, 0.025f, 0f, 0.04f, 0.35f, 0.1f, 0.75f, 0.3f, 1f, 0.8f, 0.8f, 1.2f, 0.45f, 1.8f, 0.15f, 2.4f, 0f);
        public static float ETurb(float t) => t < 0.01f ? 0f : t < 0.03f ? (t - 0.01f) / 0.02f : t < 0.3f ? 1f : t < 1.2f ? Mathf.Lerp(1f, 0.15f, (t - 0.3f) / 0.9f) : Mathf.Lerp(0.15f, 0.04f, Mathf.Clamp01((t - 1.2f) / 1.5f));
        // evacuator pressure: full until ~0.3 s, then falls off as the bore empties
        public static float EEvacSpd(float t) => t < 0.3f ? 1f : t < 0.8f ? Mathf.Lerp(1f, 0.75f, (t - 0.3f) / 0.5f) : t < 1.5f ? Mathf.Lerp(0.75f, 0.4f, (t - 0.8f) / 0.7f) : Mathf.Lerp(0.4f, 0.15f, Mathf.Clamp01((t - 1.5f) / 0.9f));
        // blast wave: ~1 km/s out of the muzzle, slowing to sound speed; the centre drifts a little downrange
        public static float ShockR(float t) => t <= -0.0015f ? 0f : 340f * (t + 0.0015f) + 3.0f * (1f - Mathf.Exp(-(t + 0.0015f) / 0.004f));
        public static float ShockX(float t) => 0.6f + 1.5f * (1f - Mathf.Exp(-Mathf.Max(t, 0f) / 0.006f));
        public static float ShockS(float t) { float r = ShockR(t); return r <= 0f ? 0f : Mathf.Exp(-Mathf.Max(t, 0f) / 0.08f) / (1f + r / 1.5f); }
        public static float EMach(float t) => Env(t, -0.0016f, 0f, -0.001f, 1f, 0.0015f, 1f, 0.003f, 0f);
        public static float EFlash(float t) => Env(t, -0.0011f, 0f, -0.001f, 0.15f, 0.003f, 1f, 0.012f, 0.8f, 0.04f, 0.25f, 0.09f, 0f);

        float MaxSpeedA(float t) => Mathf.Max(XJet(t) * p.jetSpeed, XFire(t) > 0 ? p.fireSpeed + p.fireRadial + p.fireLumpVel : 0f,
                                              XUmb(t) > 0 ? 90f : 0f, XPre(t) > 0 || XMach(t) > 0 ? 65f : 0f, XShockS(t) > 0.05f ? p.shockKick * 3f : 0f, XEvac(t) * p.evacSpeed, t < 0.15f ? 80f : 30f);
        float MaxSpeedC(float t) => t < 0.1f ? 40f : 20f;
        float MaxSpeedB(float t) => Mathf.Max(XJet(t) * p.jetSpeed * 1.15f, XEvac(t) * p.evacSpeed * 1.3f, 12f);

        void SetCommon(Grid g, float dt)
        {
            solver.SetInts("_Res", g.res.x, g.res.y, g.res.z);
            solver.SetFloat("_Cell", g.cell);
            solver.SetFloat("_Dt", dt);
            solver.SetFloat("_T", Time);
            solver.SetVector("_BoxMin", g.boxMin);
            solver.SetFloat("_GroundSolid", g.groundSolid ? 1f : 0f);
            var toGrid = g.toWorld.inverse;
            var mf = MuzzleFrame();
            solver.SetMatrix("_GridToMuzzle", mf.inverse * g.toWorld);
            solver.SetMatrix("_MuzzleToGrid", toGrid * mf);
            solver.SetVector("_UpGrid", toGrid.MultiplyVector(Vector3.up).normalized);
            solver.SetVector("_Wind", toGrid.MultiplyVector(p.wind));
            Vector3 toSun = sun ? -sun.transform.forward : Vector3.up;
            solver.SetVector("_SunDirGrid", toGrid.MultiplyVector(toSun).normalized);
        }

        // fine = the muzzle grid: only the jet (visible) and the evacuator; the coarse grid then carries the jet's
        // momentum only (it drives the fireball) and takes the gas over where it leaves the fine grid
        // the muzzle grid only takes the bore gas while the (traversing / recoiling) muzzle is inside it
        bool MuzzleInB()
        {
            if (!UseB) return false;
            Vector3 m = B.toWorld.inverse.MultiplyPoint(MuzzleFrame().GetColumn(3));
            Vector3 lo = B.boxMin + Vector3.one * 0.3f, hi = B.boxMin + B.Size - new Vector3(3f, 0.3f, 0.3f);
            return m.x > lo.x && m.y > lo.y && m.z > lo.z && m.x < hi.x && m.y < hi.y && m.z < hi.z;
        }

        void SetStepParams(float t, bool fine, bool outer = false)
        {
            bool split = MuzzleInB();
            solver.SetFloat("_EMach", fine ? 0f : XMach(t)); solver.SetFloat("_MachX", p.machX);
            solver.SetFloat("_EPre", fine ? 0f : XPre(t)); solver.SetFloat("_EUmb", fine ? 0f : XUmb(t)); solver.SetFloat("_EFire", fine ? 0f : XFire(t));
            solver.SetFloat("_EJet", XJet(t)); solver.SetFloat("_EJetDens", fine || !split ? XJetDens(t) : 0f);
            solver.SetVector("_Amt1", new Vector4(fx.precursor.Amount, fx.umbrella.Amount, fx.machDisk.Amount, fx.fireball.Amount));
            solver.SetVector("_Amt2", new Vector4(fx.jet.Amount, fx.evacuator.Amount, 0f, 0f));
            solver.SetFloat("_EDust", fine ? 0f : EDust(t) * p.dust); solver.SetFloat("_DustR", DustR(t));
            solver.SetFloat("_EEvac", fine || !split ? XEvac(t) : 0f); solver.SetFloat("_ETurb", ETurb(t) * (fine ? p.fineTurb : 1f));
            solver.SetFloat("_JetSpeed", p.jetSpeed); solver.SetFloat("_JetRadius", fine ? p.fineJetRadius : p.jetRadius); solver.SetFloat("_JetDens", p.jetDensity * (UseB ? 1f : p.coarseBoreDens));
            solver.SetFloat("_LumpFreq", p.lumpFreq); solver.SetFloat("_RestRelax", p.restRelax);
            solver.SetFloat("_FireSpeed", p.fireSpeed); solver.SetFloat("_FireRadial", p.fireRadial); solver.SetFloat("_FireLumpVel", p.fireLumpVel);
            solver.SetFloat("_FuelAmt", p.fuel); solver.SetFloat("_FireTemp", p.fireTemp); solver.SetFloat("_FireDens", p.fireDensity);
            solver.SetFloat("_FireCoreExp", p.fireCoreExp); solver.SetVector("_FireCenter", p.fireCenter); solver.SetVector("_FireRadii", p.fireRadii);
            solver.SetFloat("_UmbSpeed", p.umbrellaSpeed);
            solver.SetFloat("_EvacPulseHz", p.evacPulseHz); solver.SetFloat("_EvacWobble", p.evacWobble);
            solver.SetFloat("_JetLen", p.jetLen); solver.SetFloat("_JetPulseHz", p.jetPulseHz); solver.SetFloat("_JetWobble", p.jetWobble);
            solver.SetFloat("_EvacSpeed", p.evacSpeed); solver.SetFloat("_EvacRadius", fine ? p.fineEvacRadius : p.evacRadius); solver.SetFloat("_EvacDens", p.evacDensity * (UseB ? 1f : p.coarseBoreDens));
            solver.SetFloat("_Ignite", p.ignite); solver.SetFloat("_BurnRate", p.burnRate); solver.SetFloat("_MixK", p.mixK);
            solver.SetFloat("_Heat", p.heat); solver.SetFloat("_SmokeYield", p.smokeYield); solver.SetFloat("_SootYield", p.sootYield);
            solver.SetFloat("_Expand", p.expand); solver.SetFloat("_Cool", p.cool); solver.SetFloat("_WarmCool", p.warmCool); solver.SetFloat("_EvacTemp", p.evacTemp);
            solver.SetFloat("_EvacSpdK", XEvacSpd(t)); solver.SetFloat("_TagFade", p.tagFade); solver.SetFloat("_TagSlow", p.tagSlow); solver.SetFloat("_TagBase", p.tagBase); solver.SetFloat("_TagTurb", fine ? p.tagTurb : 0f); solver.SetFloat("_TagPastFade", p.tagPastFade);
            solver.SetFloat("_EvacCoreLen", p.evacCoreLen); solver.SetFloat("_EvacFlutter", p.evacFlutter); solver.SetFloat("_FuelDecay", p.fuelDecay);
            solver.SetFloat("_TauMin", p.tauMin * Life); solver.SetFloat("_TauMax", p.tauMax * Life);
            solver.SetFloat("_Beta", p.beta); solver.SetFloat("_Alpha", p.alpha); solver.SetFloat("_Vort", p.vorticity * (fine ? p.fineVort : 1f));
            solver.SetFloat("_TurbAmp", p.turb); solver.SetFloat("_TurbSize", fine ? p.fineTurbSize : p.turbSize); solver.SetFloat("_Drag", p.drag);
            solver.SetFloat("_WindCouple", outer ? p.outerWindCouple : fine ? p.fineWindCouple : p.windCouple);
            solver.SetFloat("_GustAmp", p.gust * p.wind.magnitude); solver.SetFloat("_GustSize", p.gustSize);
            // the blast lifts the ground dust in its first moments; afterwards the breeze alone does not
            solver.SetFloat("_DustEnv", outer ? 0f : Mathf.Clamp01(1f - (t - 0.4f) / 0.6f));
            solver.SetFloat("_DustPick", p.dustPick); solver.SetFloat("_DustUT", p.dustThreshold); solver.SetFloat("_DustFall", p.dustFall); solver.SetFloat("_DustLayer", p.dustLayer);
            solver.SetFloat("_SigmaK", SmokeSigma); solver.SetFloat("_Gamma", p.gamma);
            // sharp (MacCormack) advection while the blast is young, softer later so the haze diffuses instead of graining;
            // vorticity confinement only while the blast is energetic (late on it just turns thin haze into grain)
            solver.SetFloat("_MCW", t < 0.6f ? 1f : Mathf.Lerp(1f, 0.25f, Mathf.Clamp01((t - 0.6f) / 1.2f)));
            solver.SetFloat("_VortScale", t < 0.4f ? 1f : Mathf.Lerp(1f, 0.2f, Mathf.Clamp01((t - 0.4f) / 1.1f)));
            solver.SetFloat("_LateFade", LateFade(t, Life));
            if (outer)   // the open air: no sources, only transport, wind, buoyancy and decay
            {
                foreach (var n in new[] { "_EMach", "_EPre", "_EUmb", "_EFire", "_EJet", "_EJetDens", "_EDust", "_EEvac", "_TagTurb" }) solver.SetFloat(n, 0f);
                solver.SetFloat("_ETurb", ETurb(t) * 0.3f); solver.SetFloat("_TurbSize", p.turbSize * 2f);
            }
        }

        void Step(Grid g, float t, float dt, bool fine)
        {
            bool outer = g == C;
            SetCommon(g, dt);
            solver.SetFloat("_T", t);
            SetStepParams(t + dt * 0.5f, fine, outer);
            solver.SetFloat("_ShockR0", XShockR(t)); solver.SetFloat("_ShockR1", XShockR(t + dt));
            solver.SetVector("_ShockC", g.toWorld.inverse.MultiplyPoint(fireFrame.MultiplyPoint(new Vector3(XShockX(t + dt), 0f, 0f))));
            solver.SetFloat("_ShockStr", p.shockKick * XShockS(t + dt));
            bool useSolid = obstacleRoot && parts.Count > 0 && g != C;
            solver.SetFloat("_UseSolid", useSolid ? 1f : 0f);
            foreach (var kn in new[] { "Sources", "Divergence", "Jacobi", "Project" }) solver.SetTexture(solver.FindKernel(kn), "_Solid", g.solid);
            int k;
            k = solver.FindKernel("CurlK"); solver.SetTexture(k, "_V", g.v); solver.SetTexture(k, "_CurlOut", g.curl); Dispatch(g, k);
            k = solver.FindKernel("Sources");
            solver.SetTexture(k, "_VRW", g.v); solver.SetTexture(k, "_SRW", g.s); solver.SetTexture(k, "_RRW", g.r);
            solver.SetTexture(k, "_Curl", g.curl); solver.SetTexture(k, "_ExpRW", g.expn); Dispatch(g, k);
            k = solver.FindKernel("Divergence"); solver.SetTexture(k, "_V", g.v); solver.SetTexture(k, "_ExpRW", g.expn); solver.SetTexture(k, "_DivOut", g.div); Dispatch(g, k);
            k = solver.FindKernel("Jacobi");
            int iters = outer ? p.outerJacobi : fine ? p.fineJacobi : p.jacobi;
            if (prof != null) { Sync(); profSw.Restart(); }
            for (int i = 0; i < iters; i++)
            {
                solver.SetTexture(k, "_P", g.pA); solver.SetTexture(k, "_Div", g.div); solver.SetTexture(k, "_POut", g.pB); Dispatch(g, k);
                (g.pA, g.pB) = (g.pB, g.pA);
            }
            if (prof != null) { Sync(); ProfAdd("jacobi_" + GName(g), profSw.Elapsed.TotalMilliseconds); ProfAdd("steps_" + GName(g), 1); }
            k = solver.FindKernel("Project"); solver.SetTexture(k, "_P", g.pA); solver.SetTexture(k, "_VRW", g.v); Dispatch(g, k);
            k = solver.FindKernel("AdvectSL");
            solver.SetTexture(k, "_V", g.v); solver.SetTexture(k, "_S", g.s); solver.SetTexture(k, "_R", g.r);
            solver.SetTexture(k, "_VOut", g.vHat); solver.SetTexture(k, "_SOut", g.sHat); solver.SetTexture(k, "_ROut", g.rHat); Dispatch(g, k);
            k = solver.FindKernel("AdvectMC");
            solver.SetTexture(k, "_V", g.v); solver.SetTexture(k, "_S", g.s); solver.SetTexture(k, "_R", g.r);
            solver.SetTexture(k, "_VHat", g.vHat); solver.SetTexture(k, "_SHat", g.sHat); solver.SetTexture(k, "_RHat", g.rHat);
            solver.SetTexture(k, "_VOut", g.vTmp); solver.SetTexture(k, "_SOut", g.sTmp); solver.SetTexture(k, "_ROut", g.rTmp); Dispatch(g, k);
            (g.v, g.vTmp) = (g.vTmp, g.v); (g.s, g.sTmp) = (g.sTmp, g.s); (g.r, g.rTmp) = (g.rTmp, g.r);
        }

        // gas moves from a finer grid (src) to the coarser one around it (dst): slow gas (muzzle grid only) and gas near
        // src's faces; mass is moved, not copied, so the renderer simply adds the grids
        void Handoff(Grid src, Grid dst, float dt, float width, float rate, float slow, bool back, bool bottom)
        {
            solver.SetVector("_B2Min", src.boxMin); solver.SetVector("_B2Size", src.Size); solver.SetFloat("_HandoffWidth", width);
            solver.SetFloat("_SlowSpeed", slow); solver.SetFloat("_HandoffRate", rate);
            solver.SetFloat("_HOBack", back ? 1f : 0f); solver.SetFloat("_HOBottom", bottom ? 1f : 0f);
            var dstToSrc = src.toWorld.inverse * dst.toWorld;
            solver.SetMatrix("_DstToSrc", dstToSrc); solver.SetMatrix("_SrcToDst", dstToSrc.inverse);
            SetCommon(dst, dt);
            int k = solver.FindKernel("Handoff");
            solver.SetTexture(k, "_VRW", dst.v); solver.SetTexture(k, "_SRW", dst.s); solver.SetTexture(k, "_RRW", dst.r);
            solver.SetTexture(k, "_V2", src.v); solver.SetTexture(k, "_S2", src.s); solver.SetTexture(k, "_R2", src.r);
            Dispatch(dst, k);
            SetCommon(src, dt);
            k = solver.FindKernel("HandoffB");
            solver.SetTexture(k, "_VRW", src.v); solver.SetTexture(k, "_SRW", src.s);
            Dispatch(src, k);
        }

        int Run(Grid g, float t0, float dt, bool fine, Func<float, float> maxSpeed)
        {
            float t = t0, end = t0 + dt;
            int n = 0;
            while (t < end - 1e-7f && n < 400)
            {
                float h = Mathf.Clamp(p.cfl * g.cell / maxSpeed(t), 0.0001f, p.maxDt);
                h = Mathf.Min(h, end - t);
                if (maxStepsPerFrame > 0 && n >= maxStepsPerFrame - 1) h = end - t;   // the last allowed step takes the rest (semi-Lagrangian: stays stable)
                Step(g, t, h, fine);
                t += h; n++;
            }
            return n;
        }

        /// <summary>Advance the shot by dt seconds (each grid sub-stepped by its own CFL). Returns the solver steps.</summary>
        public int Advance(float dt)
        {
            if (!Active || solver == null) return 0;
            if (maxFrameDt > 0f) dt = Mathf.Min(dt, maxFrameDt);
            Alloc();
            int n = 0;
            float t0 = Time;
            Prof("pre", () => { if (obstacleRoot && parts.Count > 0) { UpdateSolids(A); if (UseB) UpdateSolids(B); } SurfaceDustPass(t0 + dt, dt); });
            Prof("A", () => n += Run(A, t0, dt, false, MaxSpeedA));
            if (UseC) Prof("C", () => n += Run(C, t0, dt, false, MaxSpeedC));
            if (UseB) { Prof("B", () => n += Run(B, t0, dt, true, MaxSpeedB)); Prof("handoff", () => Handoff(B, A, dt, p.handoff, p.handoffRate, p.slowSpeed, false, true)); }
            if (UseC) Prof("handoff", () => Handoff(A, C, dt, p.outerHandoffWidth, p.outerHandoffRate, 1e-4f, true, false));
            if (UseD) Prof("D", () => DustAdvance(t0, dt));
            Time += dt;
            StepsLastAdvance = n;
            Prof("light", UpdateLight);
            return n;
        }

        void LightPass(Grid g)
        {
            SetCommon(g, 0f);
            solver.SetFloat("_SigmaK", SmokeSigma); solver.SetFloat("_Gamma", p.gamma);
            int k = solver.FindKernel("Light"); solver.SetTexture(k, "_S", g.s); solver.SetTexture(k, "_LightOut", g.light); Dispatch(g, k);
        }

        void FireLightPass(Grid g)
        {
            SetCommon(g, 0f);
            solver.SetInts("_FLRes", g.flRes.x, g.flRes.y, g.flRes.z);
            solver.SetInt("_FLScale", 4);
            solver.SetFloat("_HeatRange", p.heatRange); solver.SetFloat("_FlameRad", p.flameRadiance * fx.look.flameBrightness); solver.SetFloat("_EmitPow", p.emitPow);
            var n = new Vector3Int((g.flRes.x + 3) / 4, (g.flRes.y + 3) / 4, (g.flRes.z + 3) / 4);
            int k = solver.FindKernel("FireDown");
            solver.SetTexture(k, "_S", g.s); solver.SetTexture(k, "_FLOut", g.flA);
            solver.Dispatch(k, n.x, n.y, n.z);
            float sig = p.fireLightBlur / (g.cell * 4f);
            solver.SetFloat("_BlurSigma", sig); solver.SetInt("_BlurR", Mathf.CeilToInt(sig * 2.5f));
            k = solver.FindKernel("FireBlur");
            foreach (var axis in new[] { new[] { 1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, 0, 1 } })
            {
                solver.SetInts("_BlurAxis", axis);
                solver.SetTexture(k, "_FLIn", g.flA); solver.SetTexture(k, "_FLOut", g.flB);
                solver.Dispatch(k, n.x, n.y, n.z);
                (g.flA, g.flB) = (g.flB, g.flA);
            }
        }

        static RenderTexture noiseTex;
        /// <summary>The raymarch's tileable detail noise (128^3, made once on the GPU).</summary>
        RenderTexture NoiseTexture()
        {
            if (noiseTex != null && noiseTex.IsCreated()) return noiseTex;
            if (occSolver == null) return null;
            const int n = 128;
            noiseTex = Make(new Vector3Int(n, n, n), GraphicsFormat.R8G8B8A8_UNorm, "MB_DetailNoise");
            noiseTex.wrapMode = TextureWrapMode.Repeat;
            int k = occSolver.FindKernel("NoiseTex");
            occSolver.SetInt("_NoiseRes", n);
            occSolver.SetTexture(k, "_NoiseOut", noiseTex);
            occSolver.Dispatch(k, n / 4, n / 4, n / 4);
            return noiseTex;
        }

        // which bricks hold gas the raymarch would draw (dilated by one brick)
        void OccPass(Grid g, RenderTexture src, Vector4 thr)
        {
            var o = occSolver;
            var b = g.BRes;
            o.SetInts("_BRes", b.x, b.y, b.z);
            o.SetVector("_Thr", thr);
            var n = new Vector3Int((b.x + 3) / 4, (b.y + 3) / 4, (b.z + 3) / 4);
            int k = o.FindKernel("OccMax");
            o.SetTexture(k, "_Src", src); o.SetTexture(k, "_Raw", g.occRaw);
            o.Dispatch(k, n.x, n.y, n.z);
            k = o.FindKernel("OccDilate");
            o.SetTexture(k, "_RawIn", g.occRaw); o.SetTexture(k, "_Occ", g.occ);
            o.Dispatch(k, n.x, n.y, n.z);
        }

        public void UpdateLight()
        {
            if (A == null || !A.Ready) return;
            LightPass(A);
            if (UseB) LightPass(B);
            if (UseC) LightPass(C);
            if (UseD) DustLight();
            FireLightPass(A);
            if (occSolver)
            {
                OccPass(A, A.s, new Vector4(1e-4f, 0.03f, 0f, 0f));
                if (UseB) OccPass(B, B.s, new Vector4(1e-5f, 0f, 0f, 0f));
                if (UseC) OccPass(C, C.s, new Vector4(1e-5f, 0f, 0f, 0f));
                if (UseD) OccPass(D, D.s, new Vector4(1e-4f, 0f, 1f, 0f));
            }
            if (flashLight)
            {
                flashLight.transform.position = fireFrame.MultiplyPoint(new Vector3(3f, 0f, 0f));
                flashLight.intensity = p.flashLumen * fx.flash.Amount * EFlash(fx.flash.Local(Time));
                flashLight.enabled = flashLight.intensity > 1f;
            }
            PushGlobals();
        }

        /// <summary>Profiling (tests): when set, every section of Advance is GPU-synced and its wall time (ms) added here.
        /// Keys: pre (solids, old dust), A, B, C, handoff, D, light, plus jacobi_A/B/C (inside A/B/C) and steps_A/B/C.</summary>
        public System.Collections.Generic.Dictionary<string, double> prof;
        readonly System.Diagnostics.Stopwatch profSw = new System.Diagnostics.Stopwatch();
        void Prof(string key, Action a)
        {
            if (prof == null) { a(); return; }
            Sync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a();
            Sync();
            ProfAdd(key, sw.Elapsed.TotalMilliseconds);
        }
        void ProfAdd(string key, double v) { if (prof != null) prof[key] = (prof.TryGetValue(key, out var o) ? o : 0) + v; }
        string GName(Grid g) => g == A ? "A" : g == B ? "B" : g == C ? "C" : "D";

        /// <summary>Wait for the GPU (timing in tests).</summary>
        public void Sync()
        {
            if (A != null && A.Ready) AsyncGPUReadback.Request(A.s, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
        }

        /// <summary>Debug: velocity and S of the cell holding a grid-space point ("A", "B" or "D" grid).</summary>
        public string Probe(string grid, Vector3 local)
        {
            var g = grid == "B" ? B : grid == "D" ? D : A;
            if (g == null || !g.Ready) return grid + ": none";
            var c = Vector3Int.FloorToInt((local - g.boxMin) / g.cell);
            if (c.x < 0 || c.y < 0 || c.z < 0 || c.x >= g.res.x || c.y >= g.res.y || c.z >= g.res.z) return grid + ": outside";
            string Read(RenderTexture t)
            {
                var req = AsyncGPUReadback.Request(t, 0, c.x, 1, c.y, 1, c.z, 1, TextureFormat.RGBAFloat);
                req.WaitForCompletion();
                var px = req.GetData<Vector4>();
                return px.Length > 0 ? px[0].ToString("F2") : "?";
            }
            return $"{grid}{local}: V={Read(g.v)} S={Read(g.s)}";
        }

        // ---- read access for the flipbook baker (TankRig.Editor.FlashAtlasBaker)
        /// <summary>One of the grids as the baker needs it: its state texture (x smoke, y temperature, w soot / -dirt;
        /// the dust grid: x fine, y coarse) and world -> 0..1 texture coordinates.</summary>
        public struct GridView { public RenderTexture s, v; public Matrix4x4 worldToUvw, toWorld; public bool valid; }
        GridView View(Grid g, bool use) => use && g != null && g.Ready ? new GridView { s = g.s, v = g.v, worldToUvw = ToUvw(g), toWorld = g.toWorld, valid = true } : default;
        public GridView ViewA => View(A, A != null);
        public GridView ViewB => View(B, UseB);
        public GridView ViewC => View(C, UseC);
        public GridView ViewD => View(D, UseD);
        /// <summary>The smoke / flame extinction the ray march uses: smoke sigma, smoke gamma, flame sigma, heat range.</summary>
        public Vector4 BakeLook => new Vector4(SmokeSigma, p.gamma, p.flameSigma, p.heatRange);
        public float BakeDustSigma => DustSigma;
        /// <summary>The frame the shot was fired in (x along the bore, y up), world.</summary>
        public Matrix4x4 FireFrame => fireFrame;

        Matrix4x4 ToUvw(Grid g)
        {
            var size = g.Size;
            return Matrix4x4.Scale(new Vector3(1f / size.x, 1f / size.y, 1f / size.z)) * Matrix4x4.Translate(-g.boxMin) * g.toWorld.inverse;
        }

        /// <summary>Everything the raymarch / shock passes need to draw this shot (each shot draws with its own block).</summary>
        public MaterialPropertyBlock Props => propsBlock ??= new MaterialPropertyBlock();   // created on first use (not in a field initializer)
        MaterialPropertyBlock propsBlock;
        /// <summary>Where the shot was fired (the grids are anchored here): the passes draw far shots first.</summary>
        public Vector3 AnchorPos => fireFrame.GetColumn(3);
        /// <summary>Whether this shot has anything to draw right now.</summary>
        public bool Drawable => Active && A != null && A.Ready;
        public bool ShockVisible => Drawable && XShockS(Time) > 0.002f && XShockR(Time) < 60f;

        /// <summary>All enabled shots (the passes draw every one that is Drawable).</summary>
        public static readonly System.Collections.Generic.List<MuzzleBlastSim> Live = new System.Collections.Generic.List<MuzzleBlastSim>();
        void OnEnable() { if (!Live.Contains(this)) Live.Add(this); }

        /// <summary>Fill this shot's property block (name kept from when these were globals).</summary>
        public void PushGlobals()
        {
            if (A == null || !A.Ready) return;
            Props.SetMatrix("_MB_WorldToUVW", ToUvw(A));
            Props.SetTexture("_MB_S", A.s);
            Props.SetTexture("_MB_R", A.r);
            Props.SetTexture("_MB_L", A.light);
            Props.SetTexture("_MB_FL", A.flA);
            Props.SetVector("_MB_Look3", new Vector4(p.fireScatter, p.detailAmp, p.emitPow, 0f));
            Props.SetVector("_MB_Res", new Vector4(A.res.x, A.res.y, A.res.z, A.cell));
            bool occ = occSolver != null;
            Props.SetFloat("_MB_UseOcc", occ ? 1f : 0f);
            bool ntex = p.marchNoiseTex > 0.5f && NoiseTexture() != null;
            Props.SetVector("_MB_March", new Vector4(p.marchTau, Mathf.Max(1f, p.marchMaxStep), p.marchNoNoise, ntex ? 1f : 0f));
            if (ntex) Props.SetTexture("_MB_NoiseTex", noiseTex);
            if (occ) Props.SetTexture("_MB_OA", A.occ);
            bool b = UseB;
            if (b)
            {
                Props.SetMatrix("_MB_WorldToUVW2", ToUvw(B));
                Props.SetTexture("_MB_S2", B.s);
                Props.SetTexture("_MB_R2", B.r);
                Props.SetTexture("_MB_L2", B.light);
                if (occ) Props.SetTexture("_MB_OB", B.occ);
            }
            bool c = UseC;
            if (c)
            {
                Props.SetMatrix("_MB_WorldToUVW3", ToUvw(C));
                Props.SetTexture("_MB_S3", C.s);
                Props.SetTexture("_MB_R3", C.r);
                Props.SetTexture("_MB_L3", C.light);
                if (occ) Props.SetTexture("_MB_OC", C.occ);
                Props.SetVector("_MB_ResC", new Vector4(C.res.x, C.res.y, C.res.z, C.cell));
            }
            Props.SetVector("_MB_Res2", new Vector4(b ? 1f : 0f, p.debugView, c ? 1f : 0f, b ? B.cell : 1f));
            bool dd = UseD;
            if (dd)
            {
                Props.SetMatrix("_MB_WorldToUVW4", ToUvw(D));
                Props.SetTexture("_MB_D", D.s);
                Props.SetTexture("_MB_LD", D.light);
                if (occ) Props.SetTexture("_MB_OD", D.occ);
                Props.SetVector("_MB_ResD", new Vector4(D.res.x, D.res.y, D.res.z, D.cell));
            }
            Props.SetVector("_MB_DustLook", new Vector4(dd ? 1f : 0f, DustSigma, 0f, 0f));
            Props.SetVector("_MB_ResB", b ? new Vector4(B.res.x, B.res.y, B.res.z, B.cell) : Vector4.one);
            Vector3 toSun = sun ? -sun.transform.forward : Vector3.up;
            Props.SetVector("_MB_SunDir", toSun);
            Color sc = sun ? sun.color.linear * sun.intensity * Mathf.CorrelatedColorTemperatureToRGB(sun.useColorTemperature ? sun.colorTemperature : 6500f).linear : Color.white * 100000f;
            Props.SetVector("_MB_SunLux", sc);
            Props.SetVector("_MB_Ambient", ambient);
            float sr = XShockR(Time), ss = XShockS(Time);
            Vector3 sc0 = fireFrame.MultiplyPoint(new Vector3(XShockX(Time), 0f, 0f));
            Props.SetVector("_MB_Shock", new Vector4(sc0.x, sc0.y, sc0.z, Active && ss > 0.002f && sr < 60f ? sr : 0f));
            Props.SetVector("_MB_Shock2", new Vector4(p.shockRefract * Mathf.Sqrt(ss), p.shockShell * (1f + 0.04f * sr), p.shockGlow * ss, 0f));
            Props.SetVector("_MB_Look", new Vector4(SmokeSigma, p.gamma, p.flameSigma, p.flameRadiance * fx.look.flameBrightness));
            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(p.fadeStart * Life, FadeEnd, Time));
            Props.SetVector("_MB_Look2", new Vector4(p.erosion, Active ? 1f : 0f, p.heatRange, fade));
        }

        [Tooltip("in play mode, allocate the grids and run every kernel once at start so the first shot does not hitch")]
        public bool prewarmOnStart = true;

        /// <summary>Allocate the grids and run every kernel once on empty grids (first-use costs out of the way), then stand by.</summary>
        public void Prewarm()
        {
            if (solver == null) return;
            prewarming = true;
            Fire();
            Advance(1f / 60f);
            Fire();
            prewarming = false;
            Time = -1f;
            if (flashLight) flashLight.enabled = false;
            PushGlobals();
        }

        void Start() { if (Application.isPlaying && prewarmOnStart) Prewarm(); }

        void Update()
        {
            if (Application.isPlaying && autoAdvance && Active) Advance(UnityEngine.Time.deltaTime);
        }
    }
}
