// Track ruts: the ground really gives under the tracks (like War Thunder's deformable soil, Batman AO's snow):
// a deformation height field around the tank (RGHalf, 2 cm texels, ~80 m window that follows the tank and wraps)
// is stamped every frame where each track presses: the floor at the tank's real ground level, a grouser bite every
// link (fixed to the ground: the track does not slip), rounded shoulders and lumpy berms of squeezed-out soil.
// The ground's material is switched to SMC/GroundDeform (HDRP LitTessellation + ruts): the tessellated surface is
// moved by the field (real geometry: silhouette, self-shadowing), the pixel shader bends normals, darkens and wets
// the pressed floor, tears turf to soil in the grouser bites and darkens the cavities.
// The whole ground is lifted by the rut depth so the ruts go down to where the tank actually stands.
// The footprint: width, side position and height from the links on the bottom run; the length from the road wheels -
// each frame from the first to the last of them on the ground (not the links curving up round the sprocket and the
// idler, not a wheel lifted over a bump); along the track the rut ends where the contact ends (no shoulder ahead or
// behind). Ground: a mesh fine enough to tessellate (~1-2 m triangles), or a Unity Terrain (TerrainRuts shader).
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    public class TankTrackRuts : MonoBehaviour
    {
        [Tooltip("the tank (its motion stamps the ruts)")] public Transform tank;
        [Tooltip("track parts (empty = Track_L / Track_R, else Track*)")] public Transform[] tracks = new Transform[0];
        [Tooltip("ground renderers to deform (empty = big HDRP/Lit mesh renderers in the scene)")] public Renderer[] grounds = new Renderer[0];
        public ComputeShader compute;
        public Shader groundShader;
        [Tooltip("HDRP/TerrainLit + ruts (for Unity Terrain)")] public Shader terrainShader;
        [Tooltip("also deform Unity Terrains (their material is swapped for SMC/TerrainRuts while this is on)")] public bool terrains = true;

        [Header("Ground")]
        [Tooltip("0 hard ground (shallow) .. 1 soft dirt / mud (deep)")] [Range(0f, 1f)] public float softness = 0.7f;
        [Tooltip("judge grass / dirt from the ground's colour (turf tears to soil, sinks less)")] public bool autoGround = true;
        [Tooltip("m: rut depth at softness 1")] public float maxDepth = 0.10f;
        [Tooltip("m: grouser bite below the floor at softness 1")] public float maxBite = 0.03f;

        [Header("Track footprint")]
        [Tooltip("measure the footprint from the track's own links on the bottom run (width, side position, height) and its length from the road wheels (first to last, each frame only those on the ground); off = from the track's box")] public bool autoFit = true;
        [Tooltip("share of the track box's length that touches the ground (only without autoFit)")] [Range(0.4f, 1f)] public float contactScale = 0.78f;
        [Tooltip("share of the track's width pressed")] [Range(0.5f, 1.2f)] public float widthScale = 1f;
        [Tooltip("m: link pitch (one grouser per pitch); 0 = from the track")] public float pitch = 0f;
        [Tooltip("grouser width as a share of the pitch")] [Range(0.05f, 0.6f)] public float grouserWidth = 0.25f;
        [Tooltip("m: gap between the two rubber pads of a link (soil squeezes up in it); 0 = single pad")] public float padGap = 0.05f;

        [Header("Field")]
        [Tooltip("texels per side (memory: res^2 x 4 bytes)")] public int resolution = 4096;
        [Tooltip("m per texel")] public float texel = 0.02f;
        [Tooltip("m: ruts fade out at the window's edge over this")] public float edgeFade = 4f;

        [Header("Tessellation (ground material)")]
        [Range(1f, 64f)] public float tessFactor = 64f;
        [Tooltip("px: target triangle size on screen")] public float tessTriangleSize = 8f;
        [Tooltip("m: full tessellation up to here")] public float tessMinDistance = 8f;
        [Tooltip("m: none from here (the ruts stay as normals / colour)")] public float tessMaxDistance = 35f;

        public bool autoAdvance = true;

        RenderTexture field, occ;
        const int OccDiv = 64;   // occupancy cell = 64 texels
        Vector2 winMin; bool hasWin;
        Vector3 fwdLocal = Vector3.forward, upLocal = Vector3.up, sideLocal = Vector3.right;
        Vector3[] contactLocal = new Vector3[0];
        float[] halfLen = new float[0], halfW = new float[0], run = new float[0];
        Vector3[] last = new Vector3[0]; bool[] hasLast = new bool[0];
        float pitchM = 0.2f;
        bool[] fitted = new bool[0]; int[] fitLinks = new int[0];
        // the road wheels of each track (TankTrack): the contact runs from the first to the last of them on the ground
        TankTrack[] trackComp = new TankTrack[0];
        List<TankTrack.Wheel>[] roadWheels = new List<TankTrack.Wheel>[0];
        int[] wheelsDown = new int[0];
        // the last stamp of each track: a tank standing still does not stamp the same rut again every frame
        Vector4[] lastStamp = new Vector4[0]; bool[] hasStamp = new bool[0];
        float refitTimer;
        readonly List<(Renderer r, Material orig, Material deform)> swapped = new List<(Renderer, Material, Material)>();
        readonly List<(Terrain t, Material orig, Material deform)> swappedTerrains = new List<(Terrain, Material, Material)>();
        string kind = "soil";
        // look (set from softness / ground)
        float lift, bite, shoulder, bermW, bermH;
        Vector4 tint, soilCol, berm, misc;
        int kRecenter, kStamp, kRecenterOcc;

        static readonly int idField = Shader.PropertyToID("_Field"), idOcc = Shader.PropertyToID("_Occ"), idOccRes = Shader.PropertyToID("_OccRes"), gOcc = Shader.PropertyToID("_RutOcc"), gOccP = Shader.PropertyToID("_RutOccP"), idRes = Shader.PropertyToID("_Res"), idTexel = Shader.PropertyToID("_Texel"),
            idOld = Shader.PropertyToID("_Old"), idNew = Shader.PropertyToID("_New"), idBox = Shader.PropertyToID("_Box"),
            idS0 = Shader.PropertyToID("_S0"), idS1 = Shader.PropertyToID("_S1"), idS2 = Shader.PropertyToID("_S2"), idS3 = Shader.PropertyToID("_S3"), idS4 = Shader.PropertyToID("_S4"),
            gTex = Shader.PropertyToID("_RutTex"), gWin = Shader.PropertyToID("_RutWindow"), gPar = Shader.PropertyToID("_RutParams"),
            gTint = Shader.PropertyToID("_RutTint"), gSoil = Shader.PropertyToID("_RutSoil"), gBerm = Shader.PropertyToID("_RutBerm"), gMisc = Shader.PropertyToID("_RutMisc"), gTess = Shader.PropertyToID("_RutTess");

        public float WindowSize => resolution * texel;

        void OnEnable() => Setup();
        void OnDisable()
        {
            Shader.SetGlobalVector(gPar, Vector4.zero);
            Shader.SetGlobalTexture(gTex, Texture2D.blackTexture);
            Shader.SetGlobalTexture(gOcc, Texture2D.blackTexture);
            foreach (var s in swapped) { if (s.r) s.r.sharedMaterial = s.orig; if (s.deform) DestroyImmediate(s.deform); }
            swapped.Clear();
            foreach (var s in swappedTerrains) { if (s.t) s.t.materialTemplate = s.orig; if (s.deform) DestroyImmediate(s.deform); }
            swappedTerrains.Clear();
            if (field) { field.Release(); DestroyImmediate(field); field = null; }
            if (occ) { occ.Release(); DestroyImmediate(occ); occ = null; }
            hasWin = false;
        }

        public void Setup()
        {
            // the compute shader ships in the package; the ground shaders are made in the project from its own HDRP
            // (Tools > SMC-AD > 戦車（Tank Rigging） > 轍の地面シェーダーを作る; the editor does it on its own when they are missing)
            if (!compute) compute = Resources.Load<ComputeShader>("TankRigFX/TankTrackRuts");
            if (!groundShader) groundShader = Shader.Find("TankRig/GroundRuts");
            if (!terrainShader) terrainShader = Shader.Find("TankRig/TerrainRuts");
            if (!compute || (!groundShader && !terrainShader)) { Debug.LogWarning("TankTrackRuts: the ruts' compute / ground shaders are missing (Tools > SMC-AD > 戦車（Tank Rigging） > 轍の地面シェーダーを作る)"); return; }
            kRecenter = compute.FindKernel("Recenter"); kStamp = compute.FindKernel("Stamp"); kRecenterOcc = compute.FindKernel("RecenterOcc");
            FindTracks();
            BuildLook();
            SwapGrounds();
            if (!field || field.width != resolution)
            {
                if (field) { field.Release(); DestroyImmediate(field); }
                field = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, useMipMap = false, name = "TrackRuts", hideFlags = HideFlags.DontSave };
                field.Create();
                if (occ) { occ.Release(); DestroyImmediate(occ); }
                occ = new RenderTexture(resolution / OccDiv, resolution / OccDiv, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, filterMode = FilterMode.Point, useMipMap = false, name = "TrackRutsOcc", hideFlags = HideFlags.DontSave };
                occ.Create();
                ClearField();
                hasWin = false;
            }
            PushGlobals();
        }

        public void ClearField()
        {
            for (int k = 0; k < hasStamp.Length; k++) hasStamp[k] = false;   // stamp again (also a tank standing still)
            if (!field) return;
            var prev = RenderTexture.active; RenderTexture.active = field; GL.Clear(false, true, Color.clear);
            if (occ) { RenderTexture.active = occ; GL.Clear(false, true, Color.clear); }
            RenderTexture.active = prev;
            for (int i = 0; i < run.Length; i++) hasLast[i] = false;
        }

        void FindTracks()
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
            // the tracks' own frame when they have one: the fit must not depend on how the tank leans right now
            var tt0 = tracks.Length > 0 && tracks[0] ? tracks[0].GetComponent<TankTrack>() : null;
            if (tt0)
            {
                upLocal = root.InverseTransformDirection(tt0.transform.TransformDirection(tt0.upLocal)).normalized;
                var fl = root.InverseTransformDirection(tt0.transform.TransformDirection(tt0.forwardLocal)).normalized;
                fwdLocal = Vector3.Dot(fl, fwdLocal) < 0f ? -fl : fl;
            }
            sideLocal = Vector3.Cross(upLocal, fwdLocal);
            int n = tracks.Length;
            contactLocal = new Vector3[n]; halfLen = new float[n]; halfW = new float[n]; fitted = new bool[n]; fitLinks = new int[n];
            trackComp = new TankTrack[n]; roadWheels = new List<TankTrack.Wheel>[n]; wheelsDown = new int[n];
            lastStamp = new Vector4[n]; hasStamp = new bool[n];
            if (run.Length != n) { run = new float[n]; last = new Vector3[n]; hasLast = new bool[n]; }
            pitchM = 0.2f;
            foreach (var t in tracks)
                if (t) foreach (var c in t.GetComponents<Component>())
                    if (c && c.GetType().Name == "TankTrack") { var pf = c.GetType().GetField("pitch"); if (pf != null) pitchM = (float)pf.GetValue(c) * Mathf.Abs(t.lossyScale.y); }
            if (pitch > 0f) pitchM = pitch;
            for (int i = 0; i < n; i++)
            {
                float fMin = 1e9f, fMax = -1e9f, uMin = 1e9f, sMin = 1e9f, sMax = -1e9f, lsMin = 1e9f, lsMax = -1e9f;
                foreach (var rr in tracks[i].GetComponentsInChildren<Renderer>(true))
                {
                    var b = rr.bounds;
                    bool link = rr.transform.name.StartsWith("Link_");
                    for (int c = 0; c < 8; c++)
                    {
                        var w = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                        var lp = root.InverseTransformPoint(w);
                        float f = Vector3.Dot(lp, fwdLocal), u = Vector3.Dot(lp, upLocal), s = Vector3.Dot(lp, sideLocal);
                        fMin = Mathf.Min(fMin, f); fMax = Mathf.Max(fMax, f); uMin = Mathf.Min(uMin, u); sMin = Mathf.Min(sMin, s); sMax = Mathf.Max(sMax, s);
                        if (link) { lsMin = Mathf.Min(lsMin, s); lsMax = Mathf.Max(lsMax, s); }
                    }
                }
                if (fMin > fMax) continue;
                bool links = lsMin < lsMax;
                float sc = links ? (lsMin + lsMax) * 0.5f : (sMin + sMax) * 0.5f;
                halfW[i] = (links ? lsMax - lsMin : (sMax - sMin) * 0.8f) * 0.5f * widthScale;
                halfLen[i] = (fMax - fMin) * 0.5f * contactScale;
                contactLocal[i] = fwdLocal * ((fMin + fMax) * 0.5f) + upLocal * uMin + sideLocal * sc;
                fitted[i] = autoFit && FitToLinks(root, tracks[i], i);
                // the length from the road wheels: the belt is pressed into the ground from the first to the last of them
                // (the links curving up round the sprocket and the idler are not), plus half a link at each end
                var tt = tracks[i].GetComponent<TankTrack>();
                trackComp[i] = tt;
                if (autoFit && tt)
                {
                    var roads = new List<TankTrack.Wheel>();
                    foreach (var w in tt.wheels) if (w != null && w.transform && w.role == TankTrack.WheelRole.Road) roads.Add(w);
                    if (roads.Count >= 2)
                    {
                        roadWheels[i] = roads;
                        float a = float.MaxValue, b = float.MinValue;
                        foreach (var w in roads) { float f = Vector3.Dot(root.InverseTransformPoint(w.transform.position), fwdLocal); a = Mathf.Min(a, f); b = Mathf.Max(b, f); }
                        float halfPitch = tt.pitch * Mathf.Abs(tt.transform.lossyScale.y) * 0.5f;
                        halfLen[i] = (b - a) * 0.5f + halfPitch;
                        contactLocal[i] += fwdLocal * ((a + b) * 0.5f - Vector3.Dot(contactLocal[i], fwdLocal));
                    }
                }
                foreach (var c in tracks[i].GetComponents<Component>())
                    if (c && c.GetType().Name == "TankTrack")
                    {
                        var pf = c.GetType().GetField("pitch");
                        if (pf != null) pitchM = (float)pf.GetValue(c) * Mathf.Abs(tracks[i].lossyScale.y);
                    }
            }
            if (pitch > 0f) pitchM = pitch;
        }

        // the real footprint: the links lying on the bottom run, measured from their own mesh in the tank frame.
        // Width and side position from the vertices of their ground face (grousers / pads, not the end connectors above),
        // length from the first to the last of them, height from the lowest of all.
        bool FitToLinks(Transform root, Transform track, int i)
        {
            var mfs = new List<MeshFilter>();
            foreach (var mf in track.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh && mf.transform.name.StartsWith("Link_")) mfs.Add(mf);
            if (mfs.Count < 3) return false;
            var lows = new float[mfs.Count];
            float gMin = 1e9f;
            for (int k = 0; k < mfs.Count; k++)
            {
                var b = mfs[k].sharedMesh.bounds; var tr = mfs[k].transform;
                float lo = 1e9f;
                for (int c = 0; c < 8; c++)
                {
                    var p = root.InverseTransformPoint(tr.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1))));
                    lo = Mathf.Min(lo, Vector3.Dot(p, upLocal));
                }
                lows[k] = lo; gMin = Mathf.Min(gMin, lo);
            }
            float fMin = 1e9f, fMax = -1e9f, sMin = 1e9f, sMax = -1e9f, uMin = 1e9f;
            int count = 0;
            for (int k = 0; k < mfs.Count; k++)
            {
                if (lows[k] > gMin + 0.06f) continue;   // not on the bottom run (the road wheels may sit a few cm apart on rough ground)
                count++;
                var mesh = mfs[k].sharedMesh; var tr = mfs[k].transform;
                Vector3 cen = root.InverseTransformPoint(tr.TransformPoint(mesh.bounds.center));
                float fc = Vector3.Dot(cen, fwdLocal);
                fMin = Mathf.Min(fMin, fc); fMax = Mathf.Max(fMax, fc);
                if (mesh.isReadable)
                {
                    var vs = mesh.vertices;
                    // pass 1: this link's lowest point; pass 2: its ground face (within 1.5 cm of it)
                    float lo = 1e9f;
                    var lp = new Vector3[vs.Length];
                    for (int v = 0; v < vs.Length; v++) { lp[v] = root.InverseTransformPoint(tr.TransformPoint(vs[v])); lo = Mathf.Min(lo, Vector3.Dot(lp[v], upLocal)); }
                    for (int v = 0; v < vs.Length; v++)
                    {
                        if (Vector3.Dot(lp[v], upLocal) > lo + 0.015f) continue;
                        float sv = Vector3.Dot(lp[v], sideLocal);
                        sMin = Mathf.Min(sMin, sv); sMax = Mathf.Max(sMax, sv);
                    }
                    uMin = Mathf.Min(uMin, lo);
                }
                else
                {
                    var b = mesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var p = root.InverseTransformPoint(tr.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1))));
                        float sv = Vector3.Dot(p, sideLocal);
                        sMin = Mathf.Min(sMin, sv); sMax = Mathf.Max(sMax, sv);
                    }
                    uMin = Mathf.Min(uMin, lows[k]);
                }
            }
            if (count < 2 || sMin >= sMax) return false;
            float linkLen = Mathf.Max(pitchM, (fMax - fMin) / Mathf.Max(count - 1, 1));
            halfLen[i] = (fMax - fMin) * 0.5f + linkLen * 0.5f;
            halfW[i] = (sMax - sMin) * 0.5f * widthScale;
            contactLocal[i] = fwdLocal * ((fMin + fMax) * 0.5f) + upLocal * uMin + sideLocal * ((sMin + sMax) * 0.5f);
            fitLinks[i] = count;
            return true;
        }

        [ContextMenu("Fit to the tracks again")] void Refit() { FindTracks(); PushGlobals(); }

        // the look: how deep, how wide the berms, what colour - from the ground under the tank and 'softness'
        void BuildLook()
        {
            var root = tank ? tank : transform;
            Vector3 at = contactLocal.Length > 0 ? root.TransformPoint(contactLocal[0]) : root.position;
            Color g = new Color(0.3f, 0.25f, 0.18f);
            var gm = GroundLook.BaseMaterial(at, root);
            if (gm) { g = GroundLook.AverageAlbedo(gm); DestroyImmediate(gm); }
            float green = autoGround ? Mathf.Clamp01((g.g - Mathf.Max(g.r, g.b)) * 25f) : 0f;
            kind = green > 0.5f ? "grass" : "soil";
            bool grass = kind == "grass";
            float soft = softness * (grass ? 0.6f : 1f);                    // turf holds together: shallower
            lift = Mathf.Lerp(0.012f, maxDepth, soft);
            bite = Mathf.Lerp(0.004f, maxBite, soft);
            shoulder = Mathf.Lerp(0.03f, 0.09f, soft);
            bermW = Mathf.Lerp(0.08f, 0.26f, soft);
            bermH = lift * Mathf.Lerp(0.2f, 0.5f, soft) * (grass ? 0.6f : 1f);
            if (grass)
            {
                tint = new Vector4(0.74f, 0.72f, 0.55f, 0.06f);                 // crushed, yellowed blades
                soilCol = new Vector4(0.11f, 0.085f, 0.06f, 0.45f);            // soil where the grousers tore the turf (in patches)
                berm = new Vector4(0.9f, 0.88f, 0.8f, 0.55f);
            }
            else
            {
                float t = Mathf.Lerp(0.92f, 0.6f, soft);                        // pressed soil: darker, moister
                tint = new Vector4(t, t * 0.97f, t * 0.92f, Mathf.Lerp(0.03f, 0.28f, soft));
                soilCol = new Vector4(g.r * 0.5f, g.g * 0.46f, g.b * 0.42f, 0.35f);
                float bm = Mathf.Lerp(0.95f, 0.8f, soft);                       // fresh turned soil is a little darker
                berm = new Vector4(bm, bm * 0.97f, bm * 0.94f, Mathf.Lerp(0.6f, 0.25f, soft));
            }
            misc = new Vector4(bite, 0.55f, 0.6f, 0f);
        }

        void SwapGrounds()
        {
            SwapTerrains();
            if (!groundShader) return;   // meshes need TankRig/GroundRuts
            if (swapped.Count > 0) { foreach (var s in swapped) ApplyTess(s.deform); return; }
            var list = new List<Renderer>();
            if (grounds != null) foreach (var r in grounds) if (r) list.Add(r);
            if (list.Count == 0)
                foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                {
                    var m = r.sharedMaterial;
                    if (m && m.shader && m.shader.name == "HDRP/Lit" && r.bounds.size.x > 50f && r.bounds.size.z > 50f && r.bounds.size.y < 5f) list.Add(r);
                }
            var tessLit = Shader.Find("HDRP/LitTessellation");
            foreach (var r in list)
            {
                var orig = r.sharedMaterial;
                var m = new Material(tessLit) { name = orig.name + " (ruts)", hideFlags = HideFlags.DontSave };
                m.CopyPropertiesFromMaterial(orig);
                ApplyTess(m);
                HDMaterial.ValidateMaterial(m);   // keywords / passes as LitTessellation ...
                m.shader = groundShader;           // ... then the same shader with ruts
                r.sharedMaterial = m;
                swapped.Add((r, orig, m));
            }
        }

        // Unity Terrain: the same material, drawn by SMC/TerrainRuts (HDRP/TerrainLit + tessellation + ruts)
        void SwapTerrains()
        {
            if (!terrains || !terrainShader || swappedTerrains.Count > 0) return;
            foreach (var t in Terrain.activeTerrains)
            {
                var orig = t.materialTemplate;
                if (!orig || !orig.shader || orig.shader.name != "HDRP/TerrainLit") continue;
                var m = new Material(terrainShader) { name = orig.name + " (ruts)", hideFlags = HideFlags.DontSave };
                m.CopyPropertiesFromMaterial(orig);
                foreach (var k in orig.shaderKeywords) m.EnableKeyword(k);
                t.materialTemplate = m;
                swappedTerrains.Add((t, orig, m));
            }
        }

        void ApplyTess(Material m)
        {
            m.SetFloat("_DisplacementMode", 0f);   // no height-map displacement of its own
            m.SetFloat("_TessellationMode", 0f);
            m.SetFloat("_TessellationFactor", tessFactor);
            m.SetFloat("_TessellationFactorMinDistance", tessMinDistance);
            m.SetFloat("_TessellationFactorMaxDistance", tessMaxDistance);
            m.SetFloat("_TessellationFactorTriangleSize", tessTriangleSize);
            m.SetFloat("_TessellationShapeFactor", 0f);
            m.SetFloat("_TessellationBackFaceCullEpsilon", -1f);
        }

        public int GroundCount => swapped.Count + swappedTerrains.Count;

        void PushGlobals()
        {
            float size = WindowSize;
            Shader.SetGlobalTexture(gTex, field ? (Texture)field : Texture2D.blackTexture);
            Shader.SetGlobalTexture(gOcc, occ ? (Texture)occ : Texture2D.blackTexture);
            Shader.SetGlobalVector(gOccP, new Vector4(texel * OccDiv, resolution / OccDiv, 0f, 0f));
            Shader.SetGlobalVector(gWin, new Vector4(winMin.x + size * 0.5f, winMin.y + size * 0.5f, 1f / size, size * 0.5f - texel));
            Shader.SetGlobalVector(gPar, new Vector4(lift, texel, edgeFade, field && hasWin ? 1f : 0f));
            Shader.SetGlobalVector(gTint, tint);
            Shader.SetGlobalVector(gSoil, soilCol);
            Shader.SetGlobalVector(gBerm, berm);
            Shader.SetGlobalVector(gMisc, misc);
            Shader.SetGlobalVector(gTess, new Vector4(tessFactor, tessMinDistance, tessMaxDistance, tessTriangleSize));
        }

        float GroundY(Vector3 p, Transform ignore)
        {
            var terr = Terrain.activeTerrain;
            if (terr) return terr.SampleHeight(p) + terr.transform.position.y;
            float best = float.MaxValue, y = float.NegativeInfinity;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.5f, Vector3.down, GroundLook.Hits, 4f, ~0, QueryTriggerInteraction.Ignore);   // no garbage per frame
            for (int k = 0; k < n; k++)
            {
                var hit = GroundLook.Hits[k];
                if (ignore && hit.collider.transform.IsChildOf(ignore)) continue;
                if (hit.distance < best) { best = hit.distance; y = hit.point.y; }
            }
            return y;
        }

        /// <summary>Of a track's road wheels, the first and the last on the ground, along fwd (world). False: none down.</summary>
        bool WheelContact(int i, Transform root, Vector3 fwd, out float s0, out float s1)
        {
            s0 = float.MaxValue; s1 = float.MinValue;
            var tt = trackComp[i];
            float sc = Mathf.Abs(tt.transform.lossyScale.y);
            Vector3 up = tt.transform.TransformDirection(tt.upLocal).normalized;
            int down = 0;
            foreach (var w in roadWheels[i])
            {
                if (w == null || !w.transform) continue;
                // the bottom of the belt under this wheel: its rim, the pitch line offset and the link's ground depth below
                Vector3 bottom = w.transform.position - up * ((w.radius + tt.pitchLineOffset + tt.linkGroundDepth) * sc);
                float gy = GroundY(bottom, root);
                if (float.IsNegativeInfinity(gy) || bottom.y - gy > 0.07f) continue;   // lifted off the ground
                float s = Vector3.Dot(w.transform.position, fwd);
                s0 = Mathf.Min(s0, s); s1 = Mathf.Max(s1, s); down++;
            }
            wheelsDown[i] = down;
            return down > 0;
        }

        /// <summary>Press the tracks into the field (in play mode Update does it).</summary>
        public void Tick(float dt)
        {
            if (!field || !compute) return;
            // the rig makes its links when it starts playing: measure again once they are there (a second apart)
            refitTimer -= dt;
            if (autoFit && refitTimer <= 0f)
            {
                bool all = true;
                for (int k = 0; k < fitted.Length; k++) all &= fitted[k];
                if (!all) { refitTimer = 1f; FindTracks(); }
            }
            var root = tank ? tank : transform;
            float size = WindowSize;
            // keep the window around the tank: move it when the tank is a quarter of the way to its edge
            Vector2 tp = new Vector2(root.position.x, root.position.z);
            Vector2 want = new Vector2(Mathf.Round((tp.x - size * 0.5f) / texel) * texel, Mathf.Round((tp.y - size * 0.5f) / texel) * texel);
            if (!hasWin) { winMin = want; hasWin = true; ClearField(); }
            else if (Mathf.Abs(tp.x - (winMin.x + size * 0.5f)) > size * 0.25f || Mathf.Abs(tp.y - (winMin.y + size * 0.5f)) > size * 0.25f)
            {
                compute.SetTexture(kRecenter, idField, field);
                compute.SetInt(idRes, resolution); compute.SetFloat(idTexel, texel);
                compute.SetVector(idOld, winMin); compute.SetVector(idNew, want);
                compute.Dispatch(kRecenter, (resolution + 7) / 8, (resolution + 7) / 8, 1);
                compute.SetTexture(kRecenterOcc, idOcc, occ); compute.SetInt(idOccRes, resolution / OccDiv);
                compute.Dispatch(kRecenterOcc, (resolution / OccDiv + 7) / 8, (resolution / OccDiv + 7) / 8, 1);
                winMin = want;
            }

            Vector3 fwd = root.TransformDirection(fwdLocal); fwd.y = 0f; fwd.Normalize();
            compute.SetTexture(kStamp, idField, field);
            compute.SetTexture(kStamp, idOcc, occ); compute.SetInt(idOccRes, resolution / OccDiv);
            compute.SetInt(idRes, resolution); compute.SetFloat(idTexel, texel);
            for (int i = 0; i < tracks.Length; i++)
            {
                if (!tracks[i] || halfLen[i] <= 0f) continue;
                Vector3 c = root.TransformPoint(contactLocal[i]);
                if (hasLast[i]) run[i] += Vector3.Dot(c - last[i], fwd);
                last[i] = c; hasLast[i] = true;
                float hl = halfLen[i];
                if (roadWheels[i] != null)
                {
                    // only the road wheels on the ground press it in: from the first to the last of those (a wheel lifted
                    // over a bump, the hull rocking back as it pulls away - the end of the track off the ground leaves none)
                    if (!WheelContact(i, root, fwd, out float s0, out float s1)) continue;
                    float halfPitch = trackComp[i].pitch * Mathf.Abs(trackComp[i].transform.lossyScale.y) * 0.5f;
                    float mid = (s0 + s1) * 0.5f;
                    c += fwd * (mid - Vector3.Dot(c, fwd));
                    hl = (s1 - s0) * 0.5f + halfPitch;
                }
                else
                {
                    float gy = GroundY(c, root);
                    if (float.IsNegativeInfinity(gy) || c.y - gy > 0.2f) continue;   // in the air: no rut
                }
                // standing still: the rut is already there
                var stamp = new Vector4(c.x, c.z, Mathf.Atan2(fwd.z, fwd.x), hl);
                if (hasStamp[i] && (stamp - lastStamp[i]).sqrMagnitude < 1e-6f) continue;
                lastStamp[i] = stamp; hasStamp[i] = true;
                float reach = shoulder * 1.3f + bermW * 1.2f + 0.08f;
                float ex = Mathf.Abs(fwd.x) * (hl + 0.05f) + Mathf.Abs(fwd.z) * (halfW[i] + reach);
                float ez = Mathf.Abs(fwd.z) * (hl + 0.05f) + Mathf.Abs(fwd.x) * (halfW[i] + reach);
                int x0 = Mathf.FloorToInt((c.x - ex) / texel), z0 = Mathf.FloorToInt((c.z - ez) / texel);
                int nx = Mathf.CeilToInt(2f * ex / texel) + 1, nz = Mathf.CeilToInt(2f * ez / texel) + 1;
                compute.SetInts(idBox, x0, z0, nx, nz);
                compute.SetVector(idS0, new Vector4(c.x, c.z, fwd.x, fwd.z));
                compute.SetVector(idS1, new Vector4(hl, halfW[i], shoulder, bermW));
                compute.SetVector(idS2, new Vector4(lift, bite, Mathf.Max(pitchM, 0.05f), run[i]));
                compute.SetVector(idS3, new Vector4(grouserWidth, bermH, i * 17.3f, 0.12f));
                compute.SetVector(idS4, new Vector4(softness, padGap * 0.5f, padGap > 0f ? 0.35f : 0f, 0.06f + 0.08f * softness));   // w: the press-in at the ends
                compute.Dispatch(kStamp, (nx + 7) / 8, (nz + 7) / 8, 1);
            }
            PushGlobals();
        }

        public string Stats() => $"ruts: ground {kind}, {swapped.Count} ground renderer(s) + {swappedTerrains.Count} terrain(s) deformed, window {WindowSize:F0} m at {texel * 100f:F0} cm ({resolution}^2 RGHalf, {resolution * (long)resolution * 4 / (1024 * 1024)} MB), depth {lift * 100f:F1} cm, grouser bite {bite * 100f:F1} cm, berm {bermH * 100f:F1} cm x {bermW * 100f:F0} cm, pitch {pitchM:F2} m, tracks {tracks.Length}: " + string.Join(", ", System.Linq.Enumerable.Range(0, halfLen.Length).Select(k => $"[{(k < fitted.Length && fitted[k] ? $"fitted to {fitLinks[k]} links" : "box")}{(k < roadWheels.Length && roadWheels[k] != null ? $", length from {roadWheels[k].Count} road wheels ({wheelsDown[k]} down)" : "")}: {halfLen[k] * 2f:F2} x {halfW[k] * 2f:F2} m at side {Vector3.Dot(contactLocal[k], sideLocal):F2}, up {Vector3.Dot(contactLocal[k], upLocal):F2}]"));

        void Update() { if (autoAdvance) Tick(Time.deltaTime); }
    }
}
