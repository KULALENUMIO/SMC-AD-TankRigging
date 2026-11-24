// SPDX-License-Identifier: MIT
// Tank Rigging work view: a second scene view in its own window, just for one vehicle. Every scene view tool works in it as
// usual (decorations, the Tank Rigging points), but this view can
//   - show only the vehicle (the ground and everything else are left out of this view only),
//   - leave out parts of the vehicle (the turret, the gun ...) or picked objects, to reach what is under them,
//   - be bright (no shadows, even light: HDRP's MatCap) without touching the main scene view,
//   - jump to fixed angles (oblique, front, back, left, right, top; the straight ones orthographic).
// Leaving things out is done per view: just before HDRP culls this view's camera the renderers are switched off
// (Renderer.forceRenderingOff, not saved with the scene) and they are switched on again before any other camera.
// Mouse picking of the tools asks HiddenHere so a left-out part cannot be clicked through.
// A copy of SMC-AD's TankRigWorkView.cs (kept separate so the plugin does not depend on SMC-AD; keep the two in step).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.Rendering;

namespace TankRig.Editor
{
    public class TankRigWorkView : SceneView
    {
        const string Title = "作業ビュー";
        public enum Angle { Oblique, Front, Back, Left, Right, Top }
        static readonly string[] AngleNames = { "斜め", "前", "後", "左", "右", "上" };

        [SerializeField] Transform target;
        [SerializeField] bool onlyVehicle = true;
        [SerializeField] bool bright = true;
        [SerializeField] List<Transform> hiddenParts = new List<Transform>();
        [SerializeField] List<GameObject> hiddenObjects = new List<GameObject>();
        [NonSerialized] HashSet<Renderer> hiddenCache;
        [NonSerialized] List<(string label, Transform t)> partsCache;

        public Transform Target => target;

        /// <summary>Opens (or brings up) the work view on this vehicle.</summary>
        public static TankRigWorkView Open(Transform root)
        {
            TankRigWorkView w = null;
            foreach (SceneView sv in sceneViews) if (sv is TankRigWorkView x) { w = x; break; }
            bool fresh = w == null;
            if (fresh)
            {
                w = CreateWindow<TankRigWorkView>(Title);
                var main = EditorGUIUtility.GetMainWindowPosition();
                w.position = new Rect(main.x + main.width * 0.45f, main.y + 90f, Mathf.Min(900f, main.width * 0.5f), Mathf.Min(640f, main.height * 0.7f));
            }
            w.titleContent = new GUIContent(Title, EditorGUIUtility.IconContent("SceneViewCamera").image, "車両だけを映す作業用のシーンビュー");
            if (root != null && w.target != root)
            {
                w.target = root;
                w.hiddenParts.Clear();
                w.hiddenObjects.Clear();
                fresh = true;
            }
            w.sceneLighting = !w.bright;
            w.Dirty();
            w.Show();
            w.Focus();
            if (fresh) EditorApplication.delayCall += () => { if (w != null) w.Look(Angle.Oblique); };
            return w;
        }

        public void Dirty()
        {
            hiddenCache = null;
            partsCache = null;
            Repaint();
        }

        // ------------------------------------------------------------------ angles

        public void Look(Angle a)
        {
            if (target == null) return;
            var b = VehicleBounds(target);
            var f = Vector3.ProjectOnPlane(Forward(target), Vector3.up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();
            var r = Vector3.Cross(Vector3.up, f);
            Quaternion rot;
            switch (a)
            {
                case Angle.Front: rot = Quaternion.LookRotation(-f, Vector3.up); break;
                case Angle.Back: rot = Quaternion.LookRotation(f, Vector3.up); break;
                case Angle.Left: rot = Quaternion.LookRotation(r, Vector3.up); break;
                case Angle.Right: rot = Quaternion.LookRotation(-r, Vector3.up); break;
                case Angle.Top: rot = Quaternion.LookRotation(Vector3.down, f); break;
                default: rot = Quaternion.LookRotation(-(f * 0.75f + r * 0.65f + Vector3.up * 0.55f).normalized, Vector3.up); break;
            }
            float size = a == Angle.Oblique ? b.extents.magnitude * 0.9f : Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 1.15f;
            LookAt(b.center, rot, Mathf.Max(size, 0.5f), a != Angle.Oblique, false);
            Repaint();
        }

        public static Bounds VehicleBounds(Transform root)
        {
            var b = new Bounds(root.position, Vector3.one);
            bool first = true;
            foreach (var rd in root.GetComponentsInChildren<Renderer>())
            {
                if (rd is ParticleSystemRenderer || !rd.enabled) continue;
                if (first) { b = rd.bounds; first = false; }
                else b.Encapsulate(rd.bounds);
            }
            return b;
        }

        /// <summary>The vehicle's forward: its rig's (anything with a "turret" field, e.g. the Tank Rigging controller), else the root's.</summary>
        static Vector3 Forward(Transform root)
        {
            foreach (var c in root.GetComponentsInParent<Component>(true))
                if (c != null && c.GetType().Name == "TankController") return c.transform.forward;
            return root.forward;
        }

        // ------------------------------------------------------------------ what is left out

        /// <summary>The vehicle's moving parts: the hull (root) first, then the rig's turret / gun / barrel / secondary turrets
        /// (read by reflection from anything with a Transform field "turret") and SMC-AD joints.</summary>
        public static List<(string label, Transform t)> Parts(Transform root)
        {
            var list = new List<(string, Transform)> { ("車体", root) };
            void Add(Transform t, string label) { if (t != null && t != root && !list.Any(p => p.Item2 == t)) list.Add((label, t)); }
            Transform Field(object o, string name) => o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) as Transform;
            var seen = new HashSet<Component>();
            var cands = root.GetComponentsInParent<Component>(true).Concat(root.GetComponentsInChildren<Component>(true));
            foreach (var c in cands)
            {
                if (c == null || !seen.Add(c)) continue;
                var tf = c.GetType().GetField("turret", BindingFlags.Public | BindingFlags.Instance);
                if (tf == null || tf.FieldType != typeof(Transform)) continue;
                Add(Field(c, "turret"), "砲塔");
                Add(Field(c, "gun"), "砲架");
                Add(Field(c, "barrel"), "砲身");
                if (c.GetType().GetField("secondaryTurrets", BindingFlags.Public | BindingFlags.Instance)?.GetValue(c) is IEnumerable sts)
                {
                    int i = 1;
                    foreach (var st in sts)
                    {
                        if (st == null) continue;
                        string name = st.GetType().GetField("name")?.GetValue(st) as string;
                        string head = string.IsNullOrEmpty(name) ? $"副砲塔 {i}" : name;
                        Add(Field(st, "turret"), head);
                        Add(Field(st, "gun"), head + " 砲架");
                        Add(Field(st, "barrel"), head + " 砲身");
                        i++;
                    }
                }
            }
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "SMJoint") Add(c.transform, "関節 " + c.name);
            return list;
        }

        List<(string label, Transform t)> PartList() => partsCache ??= target != null ? Parts(target) : new List<(string, Transform)>();

        /// <summary>Whether a transform of the vehicle is left out: a picked object above it, a left-out part above it, or it
        /// belongs to the hull (no part above it) while the hull is left out. Outside the vehicle: when only the vehicle is shown.</summary>
        public bool IsTransformHidden(Transform t)
        {
            if (t == null || target == null) return false;
            if (!t.IsChildOf(target)) return onlyVehicle;
            return Hidden(t, target, PartList(), hiddenParts, hiddenObjects);
        }

        public bool IsRendererHidden(Renderer r) => r != null && HiddenSet().Contains(r);

        static bool Hidden(Transform t, Transform root, List<(string label, Transform t)> parts, List<Transform> hiddenParts, List<GameObject> hiddenObjects)
        {
            bool inPart = false;
            for (var x = t; x != null; x = x.parent)
            {
                if (hiddenObjects.Count > 0 && hiddenObjects.Contains(x.gameObject)) return true;
                if (x == root) break;
                if (!inPart || hiddenParts.Count > 0)
                    foreach (var p in parts)
                        if (p.t == x && x != root)
                        {
                            if (hiddenParts.Contains(x)) return true;
                            inPart = true;
                            break;
                        }
            }
            return !inPart && hiddenParts.Contains(root);
        }

        /// <summary>The renderers this view leaves out (made once, again after the hierarchy changes).</summary>
        public HashSet<Renderer> HiddenSet()
        {
            if (hiddenCache != null) return hiddenCache;
            hiddenCache = Compute(target, onlyVehicle, PartList(), hiddenParts, hiddenObjects);
            return hiddenCache;
        }

        /// <summary>Which renderers a view of this vehicle leaves out (static for tests).</summary>
        public static HashSet<Renderer> Compute(Transform root, bool onlyVehicle, List<(string label, Transform t)> parts, List<Transform> hiddenParts, List<GameObject> hiddenObjects)
        {
            var set = new HashSet<Renderer>();
            if (root == null) return set;
            hiddenParts.RemoveAll(x => x == null);
            hiddenObjects.RemoveAll(x => x == null);
            var mine = root.GetComponentsInChildren<Renderer>(true);
            if (onlyVehicle)
            {
                var own = new HashSet<Renderer>(mine);
                foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (!own.Contains(r)) set.Add(r);
            }
            if (hiddenParts.Count > 0 || hiddenObjects.Count > 0)
                foreach (var r in mine)
                    if (Hidden(r.transform, root, parts, hiddenParts, hiddenObjects)) set.Add(r);
            return set;
        }

        // ------------------------------------------------------------------ for the tools' mouse picking

        static Func<Renderer, bool> RendererTestOf(SceneView sv)
        {
            if (sv == null) return null;
            if (sv is TankRigWorkView w) return w.IsRendererHidden;
            // the other plugin's copy of this window: same method names
            var m = sv.GetType().GetMethod("IsRendererHidden", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Renderer) }, null);
            return m == null ? null : r => (bool)m.Invoke(sv, new object[] { r });
        }

        static Func<Transform, bool> TransformTestOf(SceneView sv)
        {
            if (sv == null) return null;
            if (sv is TankRigWorkView w) return w.IsTransformHidden;
            var m = sv.GetType().GetMethod("IsTransformHidden", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Transform) }, null);
            return m == null ? null : t => (bool)m.Invoke(sv, new object[] { t });
        }

        /// <summary>A test for "left out in the scene view being drawn / clicked now" (null: nothing is left out there).</summary>
        public static Func<Renderer, bool> RendererTest() => RendererTestOf(currentDrawingSceneView);

        public static Func<Transform, bool> TransformTest() => TransformTestOf(currentDrawingSceneView);

        // ------------------------------------------------------------------ controls (an overlay of this window)

        internal void Controls()
        {
            if (target == null)
            {
                EditorGUILayout.LabelField("車両が決まっていません", EditorStyles.miniLabel);
                if (GUILayout.Button("選んでいる車両にする")) SetFromSelection();
                return;
            }
            GUILayout.Label(target.name, EditorStyles.miniBoldLabel, GUILayout.MaxWidth(240f));
            using (new GUILayout.HorizontalScope())
                for (int i = 0; i < AngleNames.Length; i++)
                    if (GUILayout.Button(AngleNames[i], i == 0 ? EditorStyles.miniButtonLeft : i == AngleNames.Length - 1 ? EditorStyles.miniButtonRight : EditorStyles.miniButtonMid, GUILayout.MinWidth(30f)))
                        Look((Angle)i);
            using (new GUILayout.HorizontalScope())
            {
                bool o = GUILayout.Toggle(orthographic, new GUIContent("平行投影", "遠近をなくす"), EditorStyles.miniButtonLeft);
                if (o != orthographic) orthographic = o;
                if (GUILayout.Button(new GUIContent("合わせる", "車両全体が入るように"), EditorStyles.miniButtonRight)) FrameVehicle();
            }
            using (new GUILayout.HorizontalScope())
            {
                bool b = GUILayout.Toggle(bright, new GUIContent("明るく", "影をなくして一様に明るく（このビューだけ）"), EditorStyles.miniButtonLeft);
                if (b != bright) { bright = b; sceneLighting = !b; Repaint(); }
                using (new EditorGUI.DisabledScope(!bright))
                    TankRigMatCap.Colour = GUILayout.Toggle(TankRigMatCap.Colour, new GUIContent("色", "明るくしたときに材質の色も出す（暗い塗装はそのまま暗く見える）"), EditorStyles.miniButtonMid);
                bool ov = GUILayout.Toggle(onlyVehicle, new GUIContent("車両だけ", "地面や他の物をこのビューに映さない"), EditorStyles.miniButtonRight);
                if (ov != onlyVehicle) { onlyVehicle = ov; Dirty(); }
            }
            var parts = PartList();
            GUILayout.Label("部位（チェックを外すと映さない）", EditorStyles.miniLabel);
            foreach (var (label, t) in parts)
            {
                if (t == null) continue;
                bool shown = !hiddenParts.Contains(t);
                bool s = GUILayout.Toggle(shown, label, GUILayout.MaxWidth(240f));
                if (s != shown)
                {
                    if (s) hiddenParts.Remove(t); else hiddenParts.Add(t);
                    Dirty();
                }
            }
            using (new GUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(Selection.transforms.Length == 0))
                    if (GUILayout.Button(new GUIContent("選んだ物を隠す", "選んでいる物をこのビューに映さない"), EditorStyles.miniButtonLeft))
                    {
                        foreach (var t in Selection.transforms) if (!hiddenObjects.Contains(t.gameObject)) hiddenObjects.Add(t.gameObject);
                        Dirty();
                    }
                using (new EditorGUI.DisabledScope(hiddenObjects.Count == 0 && hiddenParts.Count == 0))
                    if (GUILayout.Button(new GUIContent(hiddenObjects.Count > 0 ? $"全部映す（{hiddenObjects.Count}）" : "全部映す", "隠した部位・物をすべて戻す"), EditorStyles.miniButtonRight))
                    {
                        hiddenObjects.Clear(); hiddenParts.Clear(); Dirty();
                    }
            }
        }

        void FrameVehicle()
        {
            var b = VehicleBounds(target);
            Frame(b, false);
        }

        void SetFromSelection()
        {
            var t = Selection.activeTransform;
            if (t == null) return;
            Transform root = t.root;
            for (var x = t; x != null; x = x.parent)
                if (x.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "SMBlueprint")) { root = x; break; }
            target = root;
            hiddenParts.Clear(); hiddenObjects.Clear();
            Dirty();
            Look(Angle.Oblique);
        }

        // ------------------------------------------------------------------ leaving renderers out of this view

        [InitializeOnLoadMethod]
        static void Hook()
        {
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endContextRendering += (c, l) => ShowAll();
            AssemblyReloadEvents.beforeAssemblyReload += ShowAll;
            EditorApplication.playModeStateChanged += _ => ShowAll();
            EditorApplication.hierarchyChanged += () => { foreach (SceneView sv in sceneViews) if (sv is TankRigWorkView w) w.Dirty(); };
            EditorApplication.update += Watch;
        }

        static readonly List<Renderer> off = new List<Renderer>();

        /// <summary>Extra cameras to leave renderers out of (for tests; scene views use their own window).</summary>
        public static readonly Dictionary<Camera, ICollection<Renderer>> Extra = new Dictionary<Camera, ICollection<Renderer>>();

        static void BeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            ShowAll();
            ICollection<Renderer> hide = null;
            if (!Extra.TryGetValue(cam, out hide))
                foreach (SceneView sv in sceneViews)
                    if (sv is TankRigWorkView w && w.camera == cam) { hide = w.HiddenSet(); break; }
            if (hide == null) return;
            foreach (var r in hide)
                if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; off.Add(r); }
        }

        /// <summary>Switches back on everything that was left out (after every frame and before anything else renders).</summary>
        public static void ShowAll()
        {
            foreach (var r in off) if (r != null) r.forceRenderingOff = false;
            off.Clear();
        }

        // the MatCap colour preference is held while a bright work view is open
        static void Watch()
        {
            bool want = false;
            foreach (SceneView sv in sceneViews)
            {
                if (!(sv is TankRigWorkView w)) continue;
                if (w.titleContent == null || w.titleContent.text != Title)   // a script reload names it "Scene" again
                    w.titleContent = new GUIContent(Title, EditorGUIUtility.IconContent("SceneViewCamera").image, "車両だけを映す作業用のシーンビュー");
                if (w.bright && !w.sceneLighting) want = true;
            }
            if (want) TankRigMatCap.Hold("rigworkview"); else TankRigMatCap.Release("rigworkview");
        }
    }

    [Overlay(typeof(TankRigWorkView), "tankrig-workview", "作業ビュー", true)]
    public class TankRigWorkViewOverlay : IMGUIOverlay
    {
        public override void OnGUI()
        {
            if (containerWindow is TankRigWorkView w) w.Controls();
        }
    }
}
