// What the ground looks like where something happens: its material (a mesh's own, or the terrain layer that dominates
// there), shaded copies for loose stones and clods, and the average colour of its surface (for dust). Shared by the
// gun's gravel (MuzzleDebris) and the tracks' dust (TankDustFX).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    public static class GroundLook
    {
        /// <summary>Shared hit buffer for the effects' ground lookups (main thread only).</summary>
        public static readonly RaycastHit[] Hits = new RaycastHit[32];

        /// <summary>A new HDRP/Lit material that looks like the ground at 'at' (caller destroys it).</summary>
        public static Material BaseMaterial(Vector3 at, Transform ignore)
        {
            Material baseMat = null;
            var ter = Terrain.activeTerrain;
            if (ter && ter.terrainData && ter.terrainData.terrainLayers != null && ter.terrainData.terrainLayers.Length > 0)
            {
                var layer = DominantLayer(ter, at);
                baseMat = new Material(Shader.Find("HDRP/Lit"));
                if (layer.diffuseTexture) baseMat.SetTexture("_BaseColorMap", layer.diffuseTexture);
                if (layer.normalMapTexture) { baseMat.SetTexture("_NormalMap", layer.normalMapTexture); baseMat.SetFloat("_NormalScale", layer.normalScale); }
                baseMat.SetColor("_BaseColor", Color.white);
                baseMat.SetFloat("_Smoothness", 0.2f);
            }
            else
            {
                Physics.SyncTransforms();
                // the ground itself: the nearest hit that is a big surface (small props, stones, decals lying on it are skipped)
                float best = float.MaxValue; Renderer pick = null;
                foreach (var h in Physics.RaycastAll(at + Vector3.up * 4f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (ignore && h.collider.transform.IsChildOf(ignore)) continue;
                    var rend = h.collider.GetComponent<Renderer>();
                    if (!rend || !rend.sharedMaterial) continue;
                    var ext = rend.bounds.size;
                    if (Mathf.Max(ext.x, ext.z) < 3f) continue;
                    if (h.distance < best) { best = h.distance; pick = rend; }
                }
                if (pick) baseMat = new Material(pick.sharedMaterial);
                if (!baseMat) { baseMat = new Material(Shader.Find("HDRP/Lit")); baseMat.SetColor("_BaseColor", new Color(0.35f, 0.31f, 0.26f)); }
            }
            baseMat.SetTextureScale("_BaseColorMap", Vector2.one);   // the rock meshes carry their own UVs (a small patch of the texture each)
            baseMat.SetTextureOffset("_BaseColorMap", Vector2.zero);
            baseMat.enableInstancing = true;
            return baseMat;
        }

        /// <summary>Three shades of a ground material (loose stones are a little darker / lighter than the ground).</summary>
        public static Material[] Shades(Material baseMat, string name)
        {
            Color bc = baseMat.HasProperty("_BaseColor") ? baseMat.GetColor("_BaseColor") : Color.white;
            float[] shade = { 0.6f, 0.8f, 1.0f };
            var mats = new Material[shade.Length];
            for (int i = 0; i < shade.Length; i++)
            {
                var mm = new Material(baseMat) { name = name + " " + i, hideFlags = HideFlags.DontSave };
                mm.SetColor("_BaseColor", new Color(bc.r * shade[i], bc.g * shade[i], bc.b * shade[i], bc.a));
                if (mm.HasProperty("_Smoothness")) mm.SetFloat("_Smoothness", Mathf.Min(mm.GetFloat("_Smoothness"), 0.25f));
                HDMaterial.ValidateMaterial(mm);
                mats[i] = mm;
            }
            return mats;
        }

        /// <summary>Average surface colour of a ground material: its colour times the mean of its base texture (read from
        /// the texture's smallest mip on the GPU, so the texture need not be readable).</summary>
        public static Color AverageAlbedo(Material m)
        {
            Color c = m && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : new Color(0.35f, 0.31f, 0.26f);
            var tex = m && m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") as Texture2D : null;
            if (tex)
            {
                // draw the texture small into a float target (the sampler decodes sRGB: linear albedo), read it back,
                // average (works for compressed, non-readable textures)
                var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(tex, rt);
                    var req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
                    req.WaitForCompletion();
                    if (!req.hasError)
                    {
                        var data = req.GetData<Vector4>();
                        Vector4 sum = Vector4.zero;
                        for (int i = 0; i < data.Length; i++) sum += data[i];
                        if (data.Length > 0) c *= new Color(sum.x / data.Length, sum.y / data.Length, sum.z / data.Length);
                    }
                }
                catch { }
                finally { RenderTexture.ReleaseTemporary(rt); }
            }
            return new Color(c.r, c.g, c.b, 1f);
        }

        public static TerrainLayer DominantLayer(Terrain ter, Vector3 at)
        {
            var td = ter.terrainData;
            Vector3 local = at - ter.transform.position;
            int x = Mathf.Clamp(Mathf.RoundToInt(local.x / td.size.x * (td.alphamapWidth - 1)), 0, td.alphamapWidth - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt(local.z / td.size.z * (td.alphamapHeight - 1)), 0, td.alphamapHeight - 1);
            var a = td.GetAlphamaps(x, z, 1, 1);
            int best = 0;
            for (int i = 1; i < a.GetLength(2); i++) if (a[0, 0, i] > a[0, 0, best]) best = i;
            return td.terrainLayers[best];
        }

        // lumpy, faceted stones: an icosphere subdivided once, every vertex pushed in or out, a few flat cleavage
        // faces cut off, flattened; hard edges. UVs: a small random patch of the ground texture
        public static Mesh[] MakeRocks(int n)
        {
            var rnd = new System.Random(5);
            float Rf() => (float)rnd.NextDouble();
            var res = new Mesh[n];
            float g = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var baseV = new List<Vector3> { new(-1, g, 0), new(1, g, 0), new(-1, -g, 0), new(1, -g, 0), new(0, -1, g), new(0, 1, g), new(0, -1, -g), new(0, 1, -g), new(g, 0, -1), new(g, 0, 1), new(-g, 0, -1), new(-g, 0, 1) };
            int[] baseT = { 0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1 };
            // one midpoint subdivision (80 faces)
            var verts = new List<Vector3>(); foreach (var b in baseV) verts.Add(b.normalized);
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int i)) return i;
                verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
                mid[key] = verts.Count - 1;
                return verts.Count - 1;
            }
            var tris = new List<int>();
            for (int i = 0; i < baseT.Length; i += 3)
            {
                int a = baseT[i], b = baseT[i + 1], c = baseT[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                tris.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            for (int k = 0; k < n; k++)
            {
                var squash = new Vector3(0.85f + 0.35f * Rf(), 0.55f + 0.3f * Rf(), 0.8f + 0.4f * Rf());
                // cleavage planes: points beyond them are pulled back onto them (flat broken faces)
                var planes = new List<(Vector3 n, float d)>();
                int np = 3 + (int)(Rf() * 3f);
                for (int q = 0; q < np; q++) planes.Add((Random.onUnitSphere, 0.62f + 0.25f * Rf()));
                var vj = new Vector3[verts.Count];
                for (int i = 0; i < verts.Count; i++)
                {
                    Vector3 p = verts[i] * (0.85f + 0.3f * Rf());
                    foreach (var (pn, pd) in planes) { float e = Vector3.Dot(p, pn) - pd; if (e > 0f) p -= pn * e; }
                    vj[i] = Vector3.Scale(p, squash) * 0.5f;   // diameter ~1
                }
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
                Vector2 off = new Vector2(Rf(), Rf());
                for (int i = 0; i < tris.Count; i++)
                {
                    Vector3 p = vj[tris[i]];
                    v.Add(p); idx.Add(v.Count - 1);
                    uv.Add(off + new Vector2(p.x + p.y * 0.37f, p.z - p.y * 0.21f) * 0.08f);
                }
                var m = new Mesh { name = "Gravel " + k, hideFlags = HideFlags.DontSave };
                m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(idx, 0);
                m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
                res[k] = m;
            }
            return res;
        }
    }
}
