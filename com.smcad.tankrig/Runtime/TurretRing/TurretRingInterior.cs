using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig
{
    /// <summary>
    /// Fake interior at the turret ring, shown when the tank is knocked out (turret blown off) or on demand.
    /// Hull side: a disk on the hull roof that shows the ring race, the ring gear and the hull interior below.
    /// Turret side: a disk under the turret plus a basket-shaped proxy that shows the ring, the hanging basket and the
    /// turret's inside above it. The models have no interior, so the interior is drawn by the shader
    /// ("TankRig/Turret Ring Interior": ray marched, procedural, Western layout) and lit by its own fixed light.
    /// Everything scales with <see cref="innerDiameter"/> (default 2.25 m); <see cref="ringWidth"/> is the ring band.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Tank Rig/Turret Ring Interior")]
    public class TurretRingInterior : MonoBehaviour
    {
        const float DesignDiameter = 2.25f;
        const float DesignProxyRadius = 1.08f;   // basket proxy (design metres)
        const float DesignProxyDepth = 1.32f;

        public TankController tank;
        [Tooltip("ターレットリングの内径（m）。内装はこの大きさに合わせて伸び縮みする")] [Min(0.3f)] public float innerDiameter = 2.25f;
        [Tooltip("ターレットリングの幅（m、内径から外側へ）")] [Min(0f)] public float ringWidth = 0.16f;
        [Tooltip("リング面の高さの調整（m）。0 = 砲塔の一番下")] public float heightOffset;

        [Header("表示")]
        [Tooltip("車体側（車体の屋根の穴と車内）を出す")] public bool showHullSide;
        [Tooltip("砲塔側（砲塔の底のリングとバスケット）を出す")] public bool showTurretSide;

        [Header("見た目")]
        [Range(0f, 4f)] public float brightness = 1f;
        public Color paintColor = new Color(0.74f, 0.73f, 0.68f, 1f);
        public Color equipmentColor = new Color(0.36f, 0.38f, 0.29f, 1f);
        public Color floorColor = new Color(0.45f, 0.46f, 0.43f, 1f);
        [Tooltip("シェーダー（空なら TankRig/Turret Ring Interior）")] public Shader shader;

        GameObject hullSide, turretSide;
        Material hullMat, turretMat;
        Mesh hullMesh, turretMesh;

        static readonly int InnerId = Shader.PropertyToID("_InnerDiameter"), WidthId = Shader.PropertyToID("_RingWidth"),
            SideId = Shader.PropertyToID("_Side"), BrightId = Shader.PropertyToID("_Brightness"),
            PaintId = Shader.PropertyToID("_PaintColor"), EquipId = Shader.PropertyToID("_EquipColor"), FloorId = Shader.PropertyToID("_FloorColor");

        public Transform HullSide => hullSide ? hullSide.transform : null;
        public Transform TurretSide => turretSide ? turretSide.transform : null;

        /// <summary>Show or hide the two sides (e.g. from a knock-out effect).</summary>
        public void Show(bool hull, bool turret)
        {
            showHullSide = hull;
            showTurretSide = turret;
            Apply();
        }

        const string ShaderPath = "Packages/com.smcad.tankrig/Runtime/Shaders/Turret Ring Interior.shadergraph";

        void Reset()
        {
            tank = GetComponentInParent<TankController>();
            FindShader();
        }

        // a serialized reference, so the shader goes into builds with the scene
        void FindShader()
        {
#if UNITY_EDITOR
            if (!shader) shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
#endif
        }

        void OnEnable()
        {
            if (!tank) tank = GetComponentInParent<TankController>();
            Rebuild();
        }

        void OnDisable() => Clear();

        void OnValidate()
        {
#if UNITY_EDITOR
            FindShader();
            // objects can't be destroyed / made inside OnValidate: rebuild right after it
            UnityEditor.EditorApplication.delayCall += () => { if (this && isActiveAndEnabled) Rebuild(); };
#endif
        }

        void Clear()
        {
            Kill(ref hullSide); Kill(ref turretSide);
            KillObj(ref hullMat); KillObj(ref turretMat);
            KillObj(ref hullMesh); KillObj(ref turretMesh);
        }

        static void Kill(ref GameObject go) { if (go) { if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); } go = null; }
        static void KillObj<T>(ref T o) where T : Object { if (o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); } o = null; }

        /// <summary>Measure the ring (turret pivot, axis, lowest point of the turret) and rebuild both sides.</summary>
        public void Rebuild()
        {
            Clear();
            if (!tank || !tank.turret) return;
            FindShader();
            var sh = shader ? shader : Shader.Find("TankRig/Turret Ring Interior");
            if (!sh) { Debug.LogWarning("[TankRig] Turret Ring Interior のシェーダーが見つかりません。", this); return; }

            Transform turret = tank.turret;
            Vector3 up = turret.TransformDirection(tank.turretAxisLocal).normalized;
            if (up.sqrMagnitude < 0.5f) up = tank.transform.up;
            if (Vector3.Dot(up, tank.transform.up) < 0f) up = -up;   // the axis may be stored either way round
            // the ring plane: the lowest point of the turret's meshes along the axis
            float low = 0f;
            bool any = false;
            foreach (var mf in turret.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    float d = Vector3.Dot(mf.transform.TransformPoint(c) - turret.position, up);
                    if (!any || d < low) { low = d; any = true; }
                }
            }
            Vector3 center = turret.position + up * (low + heightOffset);
            Vector3 hullFwd = Vector3.ProjectOnPlane(tank.transform.forward, up);
            Transform m = tank.muzzle ? tank.muzzle : tank.gun ? tank.gun : turret;
            Vector3 gunFwd = Vector3.ProjectOnPlane(m.forward, up);
            if (hullFwd.sqrMagnitude < 1e-6f) hullFwd = Vector3.Cross(up, Vector3.right);
            if (gunFwd.sqrMagnitude < 1e-6f) gunFwd = hullFwd;

            float s = innerDiameter / DesignDiameter;
            float rin = innerDiameter * 0.5f, rout = rin + ringWidth;
            hullMesh = DiskMesh(rout, 0.003f);
            turretMesh = BasketMesh(DesignProxyRadius * s, rout, DesignProxyDepth * s);
            hullMat = MakeMaterial(sh, 0f);
            turretMat = MakeMaterial(sh, 1f);
            hullSide = MakeObject("Turret Ring (hull side)", turret.parent ? turret.parent : tank.transform, center, Quaternion.LookRotation(hullFwd, up), hullMesh, hullMat);
            turretSide = MakeObject("Turret Ring (turret side)", turret, center, Quaternion.LookRotation(gunFwd, up), turretMesh, turretMat);
            Apply();
        }

        /// <summary>Push the settings to the materials and the visibility to the renderers.</summary>
        public void Apply()
        {
            foreach (var mat in new[] { hullMat, turretMat })
            {
                if (!mat) continue;
                mat.SetFloat(InnerId, innerDiameter);
                mat.SetFloat(WidthId, ringWidth);
                mat.SetFloat(BrightId, brightness);
                mat.SetColor(PaintId, paintColor);
                mat.SetColor(EquipId, equipmentColor);
                mat.SetColor(FloorId, floorColor);
            }
            if (hullSide) hullSide.SetActive(showHullSide);
            if (turretSide) turretSide.SetActive(showTurretSide);
        }

        Material MakeMaterial(Shader sh, float side)
        {
            var mat = new Material(sh) { name = side > 0.5f ? "Turret Ring (turret side)" : "Turret Ring (hull side)", hideFlags = HideFlags.DontSave };
            mat.SetFloat(SideId, side);
            HDMaterial.ValidateMaterial(mat);
            return mat;
        }

        static GameObject MakeObject(string name, Transform parent, Vector3 pos, Quaternion rot, Mesh mesh, Material mat)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave | HideFlags.NotEditable };
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            // object space must be metres: undo any scale of the parents
            go.transform.localScale = Vector3.one;
            var ls = go.transform.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(1e-4f, ls.x), 1f / Mathf.Max(1e-4f, ls.y), 1f / Mathf.Max(1e-4f, ls.z));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        // ------------------------------------------------------------------ meshes (object space = metres, y = ring axis)

        const int Segments = 128;

        /// <summary>A disk facing up at height <paramref name="y"/>.</summary>
        static Mesh DiskMesh(float radius, float y)
        {
            var b = new MeshBuilder();
            int c = b.V(new Vector3(0f, y, 0f), Vector3.up);
            int first = -1, prev = -1;
            for (int i = 0; i <= Segments; i++)
            {
                float a = i * Mathf.PI * 2f / Segments;
                int v = i == Segments ? first : b.V(new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius), Vector3.up);
                if (i == 0) first = v; else b.Tri(c, prev, v, Vector3.up);
                prev = v;
            }
            return b.Mesh("Turret Ring Disk");
        }

        /// <summary>Under the turret: a downward ring from the proxy to the outer edge of the ring, the proxy's side and its bottom.</summary>
        static Mesh BasketMesh(float proxy, float outer, float depth)
        {
            var b = new MeshBuilder();
            const float top = -0.003f;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * Mathf.PI * 2f / Segments, a1 = (i + 1) * Mathf.PI * 2f / Segments;
                Vector3 d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)), d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                // ring band underside (faces down)
                b.Quad(d0 * proxy + Vector3.up * top, d1 * proxy + Vector3.up * top, d1 * outer + Vector3.up * top, d0 * outer + Vector3.up * top, Vector3.down);
                // proxy side (faces out)
                b.Quad(d0 * proxy + Vector3.up * top, d1 * proxy + Vector3.up * top, d1 * proxy - Vector3.up * depth, d0 * proxy - Vector3.up * depth, (d0 + d1).normalized);
                // bottom (faces down)
                b.Tri(new Vector3(0f, -depth, 0f), d0 * proxy - Vector3.up * depth, d1 * proxy - Vector3.up * depth, Vector3.down);
            }
            return b.Mesh("Turret Ring Basket");
        }

        class MeshBuilder
        {
            readonly System.Collections.Generic.List<Vector3> v = new System.Collections.Generic.List<Vector3>();
            readonly System.Collections.Generic.List<Vector3> n = new System.Collections.Generic.List<Vector3>();
            readonly System.Collections.Generic.List<int> t = new System.Collections.Generic.List<int>();

            public int V(Vector3 p, Vector3 normal) { v.Add(p); n.Add(normal); return v.Count - 1; }

            /// <summary>A triangle whose front face looks along <paramref name="facing"/> (Unity: clockwise seen from the front).</summary>
            public void Tri(int a, int b, int c, Vector3 facing)
            {
                if (Vector3.Dot(Vector3.Cross(v[b] - v[a], v[c] - v[a]), facing) < 0f) (b, c) = (c, b);
                t.Add(a); t.Add(b); t.Add(c);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 facing) => Tri(V(a, facing), V(b, facing), V(c, facing), facing);

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing)
            {
                int ia = V(a, facing), ib = V(b, facing), ic = V(c, facing), id = V(d, facing);
                Tri(ia, ib, ic, facing);
                Tri(ia, ic, id, facing);
            }

            public Mesh Mesh(string name)
            {
                var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetTriangles(t, 0);
                m.RecalculateBounds();
                return m;
            }
        }
    }
}
