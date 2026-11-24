// Bakes the sprite flipbooks from the main-gun simulation itself: a shot is fired with no tank and no wind, run at the
// cinematic settings, and at chosen moments each part is projected side-on (orthographic, across the bore) into one
// cell of an 8x8 flipbook:  0 fireball (0-0.25 s), 1 muzzle cloud (0.15-3 s), 2 ground dust (0.06-2.9 s).
// A: r coverage (1 - e^-tau), g temperature, b brightness (1 - soot)   B: rg normal from the coverage's slope.
// B and C are half size. C: rg how the visible gas moves to the next frame (card uv, from the simulation's velocity; empty parts
// take the nearest motion) - the shader slides the two frames it blends along it, so the smoke flows instead of
// cross-fading in steps.
// The look (how each slice is lit) is stored with it. Output: Resources/TankRigFX/BlastAtlas.asset in this package
// (FlashAtlas.Default) - our own simulation, so it ships under the package's licence.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TankRig.FX;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    public static class FlashAtlasBaker
    {
        public const string AssetPath = "Packages/com.smcad.tankrig/Runtime/Resources/TankRigFX/BlastAtlas.asset";
        const string ComputePath = "Packages/com.smcad.tankrig/Editor/Bake/FlashBake.compute";

        class Part
        {
            public string name; public int mode;          // 0 smoke + flame, 1 dust
            public Vector3 centre; public float half, halfV, depth;   // in the firing frame (x down the bore, y up, z across), m; halfV: the box's half height
            public float k = 1f;                           // optical depth scale: keeps the thick middle from going flat white
            public float t0, t1, pow;                      // frame k at t0 + (t1 - t0) (k/15)^pow
            public int sideOf = -1;                        // >= 0: the same part seen from straight above (U down the bore, V across, W down
                                                           //       through the box); halfV is then the half width across, depth the half height
            public float cardY;                            // seen from above: the height the flat card lies at (firing frame, m)
            public float Time(int k, int n) => t0 + (t1 - t0) * Mathf.Pow(k / (n - 1f), pow);
        }

        [MenuItem("Tools/Tank Rig/主砲の煙をフリップブックに焼く", priority = 23)]
        static void Menu()
        {
            var r = Bake(AssetPath, 256, null);   // 8 x 256 = 2048
            Debug.Log("[TankRig] " + r);
        }

        /// <summary>Bake and save. previewDir: also write each slice as a PNG there (null = no previews).</summary>
        public static string Bake(string assetPath, int cell, string previewDir)
        {
            var parts = new[]
            {
                // boxes in the firing frame (muzzle at 0, ground at -2.3 m): just above the ground, wide enough for the last frame
                new Part { name = "fireball", mode = 0, centre = new Vector3(7f, 1.2f, 0f), half = 8f, halfV = 4f, depth = 9f, t0 = 0.004f, t1 = 0.25f, pow = 1.4f, k = 0.06f },
                new Part { name = "muzzle cloud", mode = 0, centre = new Vector3(16f, 4.5f, 0f), half = 18f, halfV = 7.5f, depth = 18f, t0 = 0.15f, t1 = 3.0f, pow = 1.3f, k = 0.12f },
                new Part { name = "ground dust", mode = 1, centre = new Vector3(4f, -2.3f + 4.7f, 0f), half = 18f, halfV = 5f, depth = 20f, t0 = 0.06f, t1 = 2.9f, pow = 1.3f, k = 0.3f },
                // the same three seen from straight above (a card baked side-on stands up like a wall when looked down on):
                // the run-time cross-fades to these as the view gets steep. Flat cards at about the gas's middle height.
                new Part { name = "fireball top", mode = 0, centre = new Vector3(7f, 1.2f, 0f), half = 8f, halfV = 6f, depth = 4f, t0 = 0.004f, t1 = 0.25f, pow = 1.4f, k = 0.06f, sideOf = 0, cardY = 0f },
                new Part { name = "muzzle cloud top", mode = 0, centre = new Vector3(16f, 4.5f, 0f), half = 18f, halfV = 12f, depth = 7.5f, t0 = 0.15f, t1 = 3.0f, pow = 1.3f, k = 0.17f, sideOf = 1, cardY = 1.0f },
                new Part { name = "ground dust top", mode = 1, centre = new Vector3(4f, -2.3f + 4.7f, 0f), half = 18f, halfV = 16f, depth = 5f, t0 = 0.06f, t1 = 2.9f, pow = 1.3f, k = 0.65f, sideOf = 2, cardY = -2.3f + 1.6f },
            };
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
            if (!cs) return "no " + ComputePath;
            int k = cs.FindKernel("Project");
            const int F = FlashAtlas.BakedFrames, frames = F * F;
            int N = F * cell, hc = cell / 2, NC = F * hc;   // N must be a power of two (compression), B and C half of it
            var A = new Color32[parts.Length][]; var B = new Color32[parts.Length][]; var C = new Color32[parts.Length][];
            for (int i = 0; i < parts.Length; i++) { A[i] = new Color32[N * N]; B[i] = new Color32[NC * NC]; C[i] = new Color32[NC * NC]; }
            var nrm = new Vector2[cell * cell];

            // a shot with no tank and no wind, cinematic grids
            var root = new GameObject("FlashBake") { hideFlags = HideFlags.DontSave };
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.hideFlags = HideFlags.DontSave; ground.transform.SetParent(root.transform); ground.transform.localScale = Vector3.one * 20f;
            var muzzle = new GameObject("Muzzle") { hideFlags = HideFlags.DontSave }.transform;
            muzzle.SetParent(root.transform);
            muzzle.SetPositionAndRotation(new Vector3(0f, 2.3f, 0f), Quaternion.LookRotation(Vector3.right));
            var sim = root.AddComponent<MuzzleBlastSim>();
            sim.muzzle = muzzle; sim.ignoreRoot = null; sim.obstacleRoot = null;
            sim.solver = Resources.Load<ComputeShader>("TankRigFX/MuzzleBlast");
            sim.dustSolver = Resources.Load<ComputeShader>("TankRigFX/MuzzleDust");
            sim.occSolver = Resources.Load<ComputeShader>("TankRigFX/MuzzleOcc");
            sim.p.wind = Vector3.zero;   // symmetric: the sprites get their own drift at run time
            sim.p.backBlow = 0f;         // the back-blow runs along the tank's hull: with no tank it only stands up a wall of dust
            sim.autoAdvance = false; sim.prewarmOnStart = false;
            var rt = new RenderTexture(cell, cell, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
            rt.Create();
            var read = new Texture2D(cell, cell, TextureFormat.RGBAFloat, false, true);
            var rtF = new RenderTexture(cell, cell, 0, RenderTextureFormat.RGFloat) { enableRandomWrite = true };
            rtF.Create();
            var readF = new Texture2D(cell, cell, TextureFormat.RGFloat, false, true);
            var flow = new Vector2[cell * cell]; var has = new bool[cell * cell]; var tmpF = new Vector2[cell * cell]; var tmpH = new bool[cell * cell];
            string report; var stats = new System.Text.StringBuilder();
            try
            {
                Physics.SyncTransforms();
                sim.Fire();
                var shots = new List<(float t, int part, int frame)>();
                for (int i = 0; i < parts.Length; i++) for (int f = 0; f < frames; f++) shots.Add((parts[i].Time(f, frames), i, f));
                shots.Sort((a, b) => a.t.CompareTo(b.t));
                foreach (var s in shots)
                {
                    while (s.t - sim.Time > 1e-5f) sim.Advance(Mathf.Min(s.t - sim.Time, 1f / 120f));
                    var p = parts[s.part];
                    var fr = sim.FireFrame;
                    bool top = p.sideOf >= 0;
                    Vector3 o = fr.MultiplyPoint(p.centre), U = fr.MultiplyVector(Vector3.right).normalized * p.half,
                            V = fr.MultiplyVector(top ? Vector3.forward : Vector3.up).normalized * p.halfV,
                            W = fr.MultiplyVector(top ? Vector3.down : Vector3.forward).normalized * p.depth;
                    Bind(cs, k, sim);
                    cs.SetInt("_Mode", p.mode);
                    cs.SetVector("_Origin", o); cs.SetVector("_AxisU", U); cs.SetVector("_AxisV", V); cs.SetVector("_AxisW", W);
                    cs.SetInt("_Steps", 192); cs.SetInt("_Res", cell);
                    cs.SetTexture(k, "_Out", rt);
                    cs.SetTexture(k, "_OutFlow", rtF);
                    cs.SetVector("_FlowU", U.normalized / (2f * p.half)); cs.SetVector("_FlowV", V.normalized / (2f * p.halfV));
                    cs.Dispatch(k, (cell + 7) / 8, (cell + 7) / 8, 1);
                    RenderTexture.active = rt; read.ReadPixels(new Rect(0, 0, cell, cell), 0, 0); read.Apply(); RenderTexture.active = null;
                    RenderTexture.active = rtF; readF.ReadPixels(new Rect(0, 0, cell, cell), 0, 0); readF.Apply(); RenderTexture.active = null;
                    var px = read.GetPixels();
                    var pf = readF.GetPixels();
                    // motion to the next frame (card uv): velocity x the time between the frames, where the gas is seen
                    float dtNext = s.frame < frames - 1 ? p.Time(s.frame + 1, frames) - s.t : s.t - p.Time(s.frame - 1, frames);
                    for (int q = 0; q < cell * cell; q++)
                    {
                        has[q] = 1f - Mathf.Exp(-p.k * px[q].a) > 0.02f;
                        flow[q] = has[q] ? new Vector2(pf[q].r, pf[q].g) * dtNext : Vector2.zero;
                    }
                    // empty parts take the nearest motion (the edge the gas moves into must slide too)
                    for (int it = 0; it < 24; it++)
                    {
                        bool any = false;
                        for (int y = 0; y < cell; y++)
                            for (int x = 0; x < cell; x++)
                            {
                                int q = y * cell + x;
                                tmpF[q] = flow[q]; tmpH[q] = has[q];
                                if (has[q]) continue;
                                Vector2 sum = Vector2.zero; int n = 0;
                                if (x > 0 && has[q - 1]) { sum += flow[q - 1]; n++; }
                                if (x < cell - 1 && has[q + 1]) { sum += flow[q + 1]; n++; }
                                if (y > 0 && has[q - cell]) { sum += flow[q - cell]; n++; }
                                if (y < cell - 1 && has[q + cell]) { sum += flow[q + cell]; n++; }
                                if (n > 0) { tmpF[q] = sum / n; tmpH[q] = true; any = true; }
                            }
                        (flow, tmpF) = (tmpF, flow); (has, tmpH) = (tmpH, has);
                        if (!any) break;
                    }
                    int fx = s.frame % F, fy = s.frame / F;
                    {   // stats: the thickest optical depth near the ground (lowest fifth of the box) and above it; the largest motion
                        float lo = 0f, hi = 0f, mv = 0f;
                        for (int y = 0; y < cell; y++) for (int x = 0; x < cell; x++) { float a = px[y * cell + x].a; if (y < cell / 5) lo = Mathf.Max(lo, a); else hi = Mathf.Max(hi, a); mv = Mathf.Max(mv, flow[y * cell + x].magnitude); }
                        stats.AppendLine($"{p.name} f{s.frame} t={s.t:F3}: tau low {lo:F2} high {hi:F2}, motion to next frame up to {mv:F3} of the card");
                    }
                    // the motion at half size, 8 bit each way (+-FlowRange of the card)
                    for (int y = 0; y < hc; y++)
                        for (int x = 0; x < hc; x++)
                        {
                            Vector2 m = (flow[(2 * y) * cell + 2 * x] + flow[(2 * y) * cell + 2 * x + 1] + flow[(2 * y + 1) * cell + 2 * x] + flow[(2 * y + 1) * cell + 2 * x + 1]) * 0.25f;
                            m = m / FlashAtlas.FlowRange * 0.5f + new Vector2(0.5f, 0.5f);
                            C[s.part][(fy * hc + y) * NC + fx * hc + x] = new Color32((byte)(Mathf.Clamp01(m.x) * 255f + 0.5f), (byte)(Mathf.Clamp01(m.y) * 255f + 0.5f), 0, 255);
                        }
                    for (int y = 0; y < cell; y++)
                        for (int x = 0; x < cell; x++)
                        {
                            var c = px[y * cell + x];
                            float K = p.k;
                            // coverage from the scaled optical depth (a): the thick middle keeps its shape instead of going flat
                            float Cv(int xx, int yy) => 1f - Mathf.Exp(-K * px[Mathf.Clamp(yy, 0, cell - 1) * cell + Mathf.Clamp(xx, 0, cell - 1)].a);
                            // keep the cell's border empty (no seams between frames)
                            float edge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 1f, Mathf.Max(Mathf.Abs((x + 0.5f) / cell * 2 - 1), Mathf.Abs((y + 0.5f) / cell * 2 - 1))));
                            float cov = Mathf.Clamp01(1f - Mathf.Exp(-K * c.a)) * edge;
                            var n = new Vector3(-(Cv(x + 1, y) - Cv(x - 1, y)) * 3f, -(Cv(x, y + 1) - Cv(x, y - 1)) * 3f, 1f).normalized;
                            int idx = (fy * cell + y) * N + fx * cell + x;
                            A[s.part][idx] = new Color32((byte)(cov * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), 255);
                            nrm[y * cell + x] = new Vector2(n.x, n.y);
                        }
                    // the normal at half size
                    for (int y = 0; y < hc; y++)
                        for (int x = 0; x < hc; x++)
                        {
                            Vector2 nn = (nrm[(2 * y) * cell + 2 * x] + nrm[(2 * y) * cell + 2 * x + 1] + nrm[(2 * y + 1) * cell + 2 * x] + nrm[(2 * y + 1) * cell + 2 * x + 1]) * 0.25f;
                            B[s.part][(fy * hc + y) * NC + fx * hc + x] = new Color32((byte)((nn.x * 0.5f + 0.5f) * 255), (byte)((nn.y * 0.5f + 0.5f) * 255), 255, 255);
                        }
                }
                // save
                // compressed: A as BC7 (coverage, temperature, brightness), B as BC5 (the normal's x, y) - about a quarter of RGBA32
                var arrA = Compressed(A, N, TextureFormat.BC7, "BlastAtlasA");
                var arrB = Compressed(B, NC, TextureFormat.BC5, "BlastAtlasB");
                var arrC = Compressed(C, NC, TextureFormat.BC5, "BlastAtlasFlow");
                var tmp = new FlashAtlas();
                tmp.SetLook(FlashAtlas.SliceFire, softness: 0.5f, translucency: 0.2f, hgG: 0.3f, selfShadow: 0.6f, rim: 0.2f, tempPower: 2.0f);
                tmp.SetLook(FlashAtlas.SliceSmoke);
                tmp.SetLook(FlashAtlas.SliceDust, hgG: 0.45f, selfShadow: 0.6f, rim: 0.4f, ambSelfShadow: 0.35f, dirAmbMin: 0.7f);   // thin light dust: little self-shadow
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].sideOf >= 0) { int so2 = parts[i].sideOf; tmp.light[i] = tmp.light[so2]; tmp.scatter[i] = tmp.scatter[so2]; tmp.look[i] = tmp.look[so2]; }
                var so = ScriptableObject.CreateInstance<FlashAtlasAsset>();
                so.A = arrA; so.B = arrB; so.C = arrC;
                so.sliceNames = parts.Select(p => p.name).ToArray();
                so.grid = parts.Select(pp => new Vector4(F, F, frames, pp.halfV / pp.half)).ToArray();   // w: the card's height / width
                so.timing = parts.Select(pp => new Vector4(pp.t0, pp.t1, pp.pow, pp.half)).ToArray();
                so.place = parts.Select(pp => new Vector4(pp.centre.x, pp.sideOf >= 0 ? pp.cardY : pp.centre.y, 2.3f, !pp.name.StartsWith("fireball") ? 1f : 0f)).ToArray();   // the bake's muzzle is 2.3 m up; the cloud and the dust sit on the ground
                so.top = parts.Select(pp => System.Array.FindIndex(parts, qq => qq.sideOf >= 0 && parts[qq.sideOf] == pp)).ToArray();   // each side-on slice's top-down one (-1: none)
                so.light = tmp.light.Take(parts.Length).ToArray(); so.scatter = tmp.scatter.Take(parts.Length).ToArray(); so.look = tmp.look.Take(parts.Length).ToArray();
                so.notes = "baked " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " from MuzzleBlastSim (cinematic grids, no tank, no wind), " + cell + " px cells; " +
                           string.Join("; ", parts.Select(p => $"{p.name}: {p.t0:F3}-{p.t1:F2} s, box {p.half * 2:F0} m"));
                if (AssetDatabase.LoadAssetAtPath<Object>(assetPath)) AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(so, assetPath);
                AssetDatabase.AddObjectToAsset(arrA, so);
                AssetDatabase.AddObjectToAsset(arrB, so);
                AssetDatabase.AddObjectToAsset(arrC, so);
                AssetDatabase.SaveAssets();
                FlashAtlas.ResetDefault();
                if (previewDir != null)
                {
                    Directory.CreateDirectory(previewDir);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
                        // preview: coverage as grey, temperature as orange on top
                        var pv = A[i].Select(c => new Color32((byte)Mathf.Min(255, c.r + c.g), (byte)Mathf.Min(255, c.r + c.g / 2), (byte)c.r, 255)).ToArray();
                        t.SetPixels32(pv); t.Apply();
                        File.WriteAllBytes(Path.Combine(previewDir, $"slice{i}_{parts[i].name.Replace(' ', '_')}.png"), t.EncodeToPNG());
                        Object.DestroyImmediate(t);
                    }
                }
                report = $"baked {parts.Length} slices of {frames} frames ({N} px) to {assetPath}\n" + stats;
            }
            finally
            {
                rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(read);
                rtF.Release(); Object.DestroyImmediate(rtF); Object.DestroyImmediate(readF);
                Object.DestroyImmediate(root);
            }
            return report;
        }

        static Texture2DArray Compressed(Color32[][] slices, int n, TextureFormat fmt, string name)
        {
            int mips = Mathf.FloorToInt(Mathf.Log(n / 12f, 2f)) + 1;   // down to 12 px: every level a whole number of 4x4 blocks
            var arr = new Texture2DArray(n, n, slices.Length, fmt, mips, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            for (int i = 0; i < slices.Length; i++)
            {
                var t = new Texture2D(n, n, TextureFormat.RGBA32, mips, true);
                t.SetPixels32(slices[i]); t.Apply(true, false);
                EditorUtility.CompressTexture(t, fmt, TextureCompressionQuality.Best);
                if (t.format != fmt) throw new System.InvalidOperationException($"{name}: {n} px did not compress to {fmt} (needs a power of two)");   // it fails silently otherwise
                for (int m = 0; m < t.mipmapCount; m++) arr.SetPixelData(t.GetPixelData<byte>(m), m, i);
                Object.DestroyImmediate(t);
            }
            arr.Apply(false, false);
            return arr;
        }

        static void Bind(ComputeShader cs, int k, MuzzleBlastSim sim)
        {
            var a = sim.ViewA; var b = sim.ViewB; var c = sim.ViewC; var d = sim.ViewD;
            var dummy = Texture3DDummy();
            cs.SetTexture(k, "_SA", a.valid ? (Texture)a.s : dummy); cs.SetMatrix("_W2UA", a.worldToUvw);
            cs.SetTexture(k, "_SB", b.valid ? (Texture)b.s : dummy); cs.SetMatrix("_W2UB", b.worldToUvw);
            cs.SetTexture(k, "_SC", c.valid ? (Texture)c.s : dummy); cs.SetMatrix("_W2UC", c.worldToUvw);
            cs.SetTexture(k, "_SD", d.valid ? (Texture)d.s : dummy); cs.SetMatrix("_W2UD", d.worldToUvw);
            cs.SetVector("_Use", new Vector4(a.valid ? 1 : 0, b.valid ? 1 : 0, c.valid ? 1 : 0, d.valid ? 1 : 0));
            cs.SetTexture(k, "_VA", a.valid ? (Texture)a.v : dummy); cs.SetMatrix("_RA", a.toWorld);
            cs.SetTexture(k, "_VB", b.valid ? (Texture)b.v : dummy); cs.SetMatrix("_RB", b.toWorld);
            cs.SetTexture(k, "_VC", c.valid ? (Texture)c.v : dummy); cs.SetMatrix("_RC", c.toWorld);
            cs.SetTexture(k, "_VD", d.valid ? (Texture)d.v : dummy); cs.SetMatrix("_RD", d.toWorld);
            cs.SetVector("_Look", sim.BakeLook);
            cs.SetFloat("_DustSigma", sim.BakeDustSigma);
        }

        static Texture3D dummy3D;
        static Texture3D Texture3DDummy()
        {
            if (dummy3D) return dummy3D;
            dummy3D = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false) { hideFlags = HideFlags.DontSave };
            dummy3D.SetPixel(0, 0, 0, Color.clear); dummy3D.Apply();
            return dummy3D;
        }
    }
}
