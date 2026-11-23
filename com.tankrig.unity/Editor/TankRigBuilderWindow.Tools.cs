using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Builder window: three-view mode, block snapping, and the weapons tab (machine guns, secondary turrets).</summary>
    public partial class TankRigBuilderWindow
    {
        enum ViewMode { Free, Front, Side, Top }

        [System.Serializable]
        struct SavedView
        {
            public bool valid, ortho, locked;
            public Vector3 pivot;
            public Quaternion rotation;
            public float size;
            public int sceneView; // instance id of the scene view that was switched
        }

        const string SnapPref = "TankRig.SnapMode";
        // set while this window holds a scene view in three-view (rotation locked, orthographic). Kept for the editor
        // session, so the view can still be given back after a script reload or play mode reset the window.
        const string LockKey = "TankRig.ThreeViewLocked";
        // serialized: survives script reloads and entering play mode
        [SerializeField] ViewMode view;
        [SerializeField] SavedView savedView;
        // weapons tab: machine guns (0) or secondary turrets (1), and the entry being edited in each
        [SerializeField] int weaponTab, mgSel, stSel;
        bool mgSpecFold = true, stSpecFold = true;
        // a spec preset picked from a dropdown menu (applied in the next OnGUI, where the serialized property exists)
        string presetPath;
        int presetKind = -1;
        int snapMode = -1;

        int SnapMode
        {
            get
            {
                if (snapMode < 0) snapMode = Mathf.Clamp(EditorPrefs.GetInt(SnapPref, 0), 0, 3);
                return snapMode;
            }
        }

        /// <summary>Snap step in metres (0 = off): 1, 1/2 or 1/4 of a Scrap Mechanic block.</summary>
        float SnapStep => !setup || SnapMode == 0 ? 0f : TankRigBuilder.BlockSize(setup) / (SnapMode == 1 ? 1f : SnapMode == 2 ? 2f : 4f);

        static string BlockText(Vector3 b) => $"左右 ±{b.x:0.##} / 高さ {b.y:0.##} / 前後 {b.z:+0.##;-0.##;0} ブロック";

        // ------------------------------------------------------------------ edit tools

        /// <summary>One row: scene view (free / front / side / top) and block snapping.</summary>
        void DrawEditTools()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent("視点", "正面・側面・上面は正投影で向きを固定し、その面の中だけで動かせます（三面図）。もう一度押すか「自由」で元の視点に戻ります"),
                    GUILayout.Width(30f));
                // buttons, not a toolbar: 「自由」 must work even when it already looks selected
                string[] labels = { "自由", "正面", "側面", "上面" };
                for (int i = 0; i < labels.Length; i++)
                {
                    var style = i == 0 ? EditorStyles.miniButtonLeft : i == labels.Length - 1 ? EditorStyles.miniButtonRight : EditorStyles.miniButtonMid;
                    bool on = (int)view == i;
                    // clicking the selected one (also 「自由」) goes back to the free view
                    if (GUILayout.Toggle(on, labels[i], style, GUILayout.MinWidth(34f)) != on)
                        SetView(on ? ViewMode.Free : (ViewMode)i);
                }
                GUILayout.Space(8f);
                GUILayout.Label(new GUIContent("スナップ", "Scrap Mechanic のブロック単位で、履帯・ポイント・機銃・砲口の位置を合わせます。グリッドの原点はモデルのブロックの角から自動で求めます"),
                    GUILayout.Width(46f));
                int sm = EditorGUILayout.Popup(SnapMode, new[] { "なし", "1 ブロック", "1/2 ブロック", "1/4 ブロック" }, GUILayout.MinWidth(70f));
                if (sm != SnapMode)
                {
                    snapMode = sm;
                    EditorPrefs.SetInt(SnapPref, sm);
                    SceneView.RepaintAll();
                }
            }
            if (view != ViewMode.Free)
                EditorGUILayout.HelpBox("三面図: 四角いハンドルでこの面の中だけ動きます（奥行きはそのまま）。回転ツール（E）は面の中で回ります。グリッドは 1 ブロック、濃い線は 1 m ごと。", MessageType.None);
        }

        Bounds TankBounds()
        {
            var b = new Bounds(setup.transform.position, Vector3.one);
            bool first = true;
            foreach (var r in setup.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static bool ThreeViewHeld => SessionState.GetBool(LockKey, false);

        // A script reload (also entering play mode) saves the scene view before this window lets it go and restores it
        // afterwards, still locked. So the released state is written down and applied again once the reload is done.
        const string PendingKey = "TankRig.ThreeViewRelease";

        [System.Serializable]
        struct PendingRelease
        {
            public int sceneView;
            public bool locked, ortho, hasAngle;
            public Vector3 pivot;
            public Quaternion rotation;
            public float size;
            public double time;
        }

        static void Apply(SceneView sv, PendingRelease p)
        {
            sv.isRotationLocked = p.locked;
            if (p.hasAngle) sv.LookAt(p.pivot, p.rotation, p.size, p.ortho, true);
            else sv.orthographic = p.ortho;
            sv.Repaint();
        }

        [InitializeOnLoadMethod]
        static void ApplyPendingRelease()
        {
            string json = SessionState.GetString(PendingKey, "");
            if (string.IsNullOrEmpty(json)) return;
            SessionState.EraseString(PendingKey);
            var p = JsonUtility.FromJson<PendingRelease>(json);
            if (EditorApplication.timeSinceStartup - p.time > 120.0) return; // stale: no reload followed the release
            EditorApplication.delayCall += () =>
            {
                foreach (SceneView v in SceneView.sceneViews)
                    if (v && v.GetInstanceID() == p.sceneView) Apply(v, p);
            };
        }

        SceneView ThreeViewSceneView()
        {
            foreach (SceneView v in SceneView.sceneViews)
                if (v && v.GetInstanceID() == savedView.sceneView) return v;
            return SceneView.lastActiveSceneView;
        }

        /// <summary>
        /// Gives the scene view back: the angle, zoom, projection and rotation lock from before the three-view. Works even if
        /// the saved angle was lost (then it unlocks and goes back to perspective).
        /// </summary>
        void ReleaseView()
        {
            var sv = ThreeViewSceneView();
            if (sv && (savedView.valid || ThreeViewHeld))
            {
                // without the saved angle (lost somehow): at least unlock it and go back to perspective
                var p = new PendingRelease
                {
                    sceneView = sv.GetInstanceID(),
                    locked = savedView.valid && savedView.locked,
                    ortho = savedView.valid && savedView.ortho,
                    hasAngle = savedView.valid,
                    pivot = savedView.pivot,
                    rotation = savedView.rotation,
                    size = savedView.size,
                    time = EditorApplication.timeSinceStartup,
                };
                Apply(sv, p);
                SessionState.SetString(PendingKey, JsonUtility.ToJson(p));
                // no reload in the next seconds: nothing to re-apply (a reload drops this callback, keeping the note)
                double until = EditorApplication.timeSinceStartup + 3.0;
                void Forget()
                {
                    if (EditorApplication.timeSinceStartup < until) return;
                    EditorApplication.update -= Forget;
                    SessionState.EraseString(PendingKey);
                }
                EditorApplication.update += Forget;
            }
            savedView.valid = false;
            SessionState.SetBool(LockKey, false);
            view = ViewMode.Free;
        }

        void SetView(ViewMode m)
        {
            if (m == ViewMode.Free)
            {
                ReleaseView();
                return;
            }
            var sv = view == ViewMode.Free ? SceneView.lastActiveSceneView : ThreeViewSceneView();
            if (!sv || !setup)
            {
                ReleaseView();
                return;
            }
            if (view == ViewMode.Free && !savedView.valid)
                savedView = new SavedView
                {
                    valid = true, pivot = sv.pivot, rotation = sv.rotation, size = sv.size, ortho = sv.orthographic,
                    locked = sv.isRotationLocked, sceneView = sv.GetInstanceID(),
                };
            SessionState.SetBool(LockKey, true);
            var root = setup.transform;
            var b = TankBounds();
            Quaternion rot = m switch
            {
                ViewMode.Front => Quaternion.LookRotation(-root.forward, root.up),
                ViewMode.Side => Quaternion.LookRotation(root.right, root.up),
                _ => Quaternion.LookRotation(-root.up, root.forward),
            };
            sv.isRotationLocked = true;
            sv.LookAt(b.center, rot, Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 1.3f, true, false);
            view = m;
            sv.Repaint();
        }

        /// <summary>
        /// Move handle: in three-view a square that slides in the drawing plane (depth kept), else the normal move handle.
        /// Snaps to the block grid when snapping is on.
        /// </summary>
        bool Drag(Vector3 pos, Quaternion frame, Color color, out Vector3 result)
        {
            var sv = SceneView.currentDrawingSceneView;
            bool plane = view != ViewMode.Free && sv;
            Vector3 viewDir = sv ? sv.camera.transform.forward : Vector3.forward;
            EditorGUI.BeginChangeCheck();
            if (plane)
            {
                Handles.color = color;
                float hs = HandleUtility.GetHandleSize(pos) * 0.16f;
                result = Handles.Slider2D(pos, -viewDir, sv.camera.transform.right, sv.camera.transform.up, hs, Handles.RectangleHandleCap, Vector2.zero);
            }
            else result = Handles.PositionHandle(pos, frame);
            if (!EditorGUI.EndChangeCheck()) return false;
            if (SnapStep > 0f) result = TankRigBuilder.Snap(setup, result, SnapStep);
            if (plane) result -= viewDir * Vector3.Dot(result - pos, viewDir);
            return true;
        }

        static int Dominant(Vector3 v)
        {
            v = new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
            return v.x >= v.y && v.x >= v.z ? 0 : v.y >= v.z ? 1 : 2;
        }

        /// <summary>Block grid in the drawing plane of the three-view (along the model's axes, through the tank's centre).</summary>
        void DrawGrid(SceneView sv)
        {
            if (Event.current.type != EventType.Repaint || !sv.camera) return;
            float block = TankRigBuilder.BlockSize(setup);
            float step = SnapStep > 0f ? SnapStep : block;
            var frame = TankRigBuilder.GridFrame(setup);
            var inv = frame.inverse;
            Vector3 ph = TankRigBuilder.GridPhase(setup);
            var cam = sv.camera.transform;
            int ax = Dominant(inv.MultiplyVector(cam.right)), ay = Dominant(inv.MultiplyVector(cam.up));
            if (ax == ay) return;
            int az = 3 - ax - ay;
            var b = TankBounds();
            b.Expand(2f);
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = inv.MultiplyPoint3x4(c);
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            while ((hi[ax] - lo[ax] + hi[ay] - lo[ay]) / step > 500f) step *= 2f;
            float depth = (lo[az] + hi[az]) * 0.5f;
            foreach (var (a, o) in new[] { (ax, ay), (ay, ax) })
            {
                int first = Mathf.CeilToInt((lo[a] - ph[a]) / step), last = Mathf.FloorToInt((hi[a] - ph[a]) / step);
                for (int k = first; k <= last; k++)
                {
                    float v = k * step + ph[a];
                    float blocks = k * step / block;
                    bool onBlock = Mathf.Abs(blocks - Mathf.Round(blocks)) < 1e-3f;
                    bool major = onBlock && Mathf.RoundToInt(blocks) % 4 == 0;
                    Handles.color = new Color(0.4f, 0.8f, 1f, major ? 0.35f : onBlock ? 0.17f : 0.07f);
                    Vector3 p0 = Vector3.zero;
                    p0[a] = v;
                    p0[o] = lo[o];
                    p0[az] = depth;
                    Vector3 p1 = p0;
                    p1[o] = hi[o];
                    Handles.DrawLine(frame.MultiplyPoint3x4(p0), frame.MultiplyPoint3x4(p1));
                }
            }
        }

        void DrawTrackNudge()
        {
            float step = SnapStep > 0f ? SnapStep : TankRigBuilder.BlockSize(setup);
            string unit = SnapMode == 2 ? "1/2 ブロック" : SnapMode == 3 ? "1/4 ブロック" : "1 ブロック";
            EditorGUILayout.LabelField("いまの位置", BlockText(TankRigBuilder.TrackOffsetBlocks(setup)));
            var rows = new[]
            {
                new[] { ("外へ", new Vector3(1, 0, 0)), ("上へ", new Vector3(0, 1, 0)), ("前へ", new Vector3(0, 0, 1)) },
                new[] { ("内へ", new Vector3(-1, 0, 0)), ("下へ", new Vector3(0, -1, 0)), ("後ろへ", new Vector3(0, 0, -1)) },
            };
            for (int r = 0; r < rows.Length; r++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (r == 0) EditorGUILayout.PrefixLabel(new GUIContent(unit + "ずつ", "スナップの細かさ（なしのときは 1 ブロック）で、グリッドに合わせながら動かします"));
                    else EditorGUILayout.PrefixLabel(" ");
                    foreach (var (label, dir) in rows[r])
                        if (GUILayout.Button(label, EditorStyles.miniButton))
                        {
                            TankRigBuilder.NudgeTrack(setup, dir, step);
                            SceneView.RepaintAll();
                        }
                }
            }
            if (SnapStep > 0f)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(" ");
                    if (GUILayout.Button("いまの位置をグリッドに合わせる", EditorStyles.miniButton))
                    {
                        TankRigBuilder.SnapTrack(setup, SnapStep);
                        SceneView.RepaintAll();
                    }
                }
        }

        // ------------------------------------------------------------------ ⑤ weapons

        void DrawWeapons()
        {
            var so = new SerializedObject(setup);
            System.Action pending = null;
            int tab = GUILayout.Toolbar(weaponTab, new[] { $"機銃（{setup.machineGuns.Count}）", $"副砲塔（{setup.secondaryTurrets.Count}）" }, GUILayout.Height(22f));
            if (tab != weaponTab)
            {
                weaponTab = tab;
                placing = null;
            }
            EditorGUILayout.Space(4f);
            if (weaponTab == 0) DrawMachineGuns(so, ref pending);
            else DrawSecondaryTurrets(so, ref pending);

            so.ApplyModifiedProperties();
            if (pending != null)
            {
                pending();
                SceneView.RepaintAll();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>Which entry to edit, a 「+ 追加」 button (menu or single action) and 「削除」.</summary>
        int EntryPicker(string[] labels, int sel, string empty, bool addIsMenu, System.Action<Rect> add, string addTip, string removeTip, out bool remove)
        {
            remove = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (labels.Length > 0) sel = EditorGUILayout.Popup(Mathf.Clamp(sel, 0, labels.Length - 1), labels);
                else GUILayout.Label(empty, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                var addContent = new GUIContent("+ 追加", addTip);
                if (addIsMenu)
                {
                    var addRect = GUILayoutUtility.GetRect(addContent, EditorStyles.miniPullDown, GUILayout.Width(64f));
                    if (EditorGUI.DropdownButton(addRect, addContent, FocusType.Passive)) add(addRect);
                }
                else if (GUILayout.Button(addContent, GUILayout.Width(64f))) add(default);
                using (new EditorGUI.DisabledScope(labels.Length == 0))
                    remove = GUILayout.Button(new GUIContent("削除", removeTip), GUILayout.Width(44f));
            }
            return sel;
        }

        void DrawMachineGuns(SerializedObject so, ref System.Action pending)
        {
            var list = setup.machineGuns;
            var labels = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                labels[i] = list[i] == null ? "（空）" : $"{(list[i].point ? "✔" : "－")} {list[i].name}（{TankRigBuilder.MountLabel(list[i].mount)}）";
            int sel = EntryPicker(labels, mgSel, "機銃はありません", true, AddMachineGunMenu,
                "同軸・砲塔・車体の機銃を追加します", "選んでいる機銃を削除します（Ctrl+Z で戻せます）", out bool remove);
            if (sel != mgSel && sel < list.Count) Select(Target.MachineGun(sel));
            mgSel = Mathf.Clamp(sel, 0, Mathf.Max(0, list.Count - 1));
            if (list.Count == 0)
            {
                EditorGUILayout.HelpBox("「+ 追加」で同軸（砲座と一緒に上下）・砲塔（砲塔と一緒に旋回）・車体（固定）の機銃を、いくつでも追加できます。", MessageType.None);
                return;
            }
            int idx = mgSel;
            if (remove)
                pending += () =>
                {
                    TankRigBuilder.RemoveMachineGun(setup, idx);
                    mgSel = Mathf.Max(0, idx - 1);
                    focus = Target.Track;
                    placing = null;
                };
            var mg = list[idx];
            var e = so.FindProperty("machineGuns").GetArrayElementAtIndex(idx);
            if (mg == null) return;

            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(e.FindPropertyRelative("name"), new GUIContent("名前"));
            EditorGUI.BeginChangeCheck();
            var mount = (MachineGunMount)EditorGUILayout.EnumPopup(new GUIContent("取り付け", "同軸 = 砲座と一緒に上下、砲塔 = 砲塔と一緒に旋回、車体 = 車体に固定"), mg.mount);
            if (EditorGUI.EndChangeCheck()) pending += () => TankRigBuilder.SetMachineGunMount(setup, idx, mount);
            EditorGUILayout.PropertyField(e.FindPropertyRelative("trigger"), new GUIContent("発射ボタン"));
            EditorGUILayout.Slider(e.FindPropertyRelative("aimCone"), 0f, 60f,
                new GUIContent("照準に向く範囲（度）", "0 = 固定。車体機銃のボールマウントのように、照準点の方へこの角度まで向きます"));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("位置");
                PlaceButtons(Target.MachineGun(idx), mg.point);
            }
            mgSpecFold = EditorGUILayout.Foldout(mgSpecFold, "性能（発射レート・弾・ベルト）", true);
            if (mgSpecFold)
                using (new EditorGUI.IndentLevelScope())
                    DrawSpec(e.FindPropertyRelative("spec"));
        }

        void AddMachineGunMenu(Rect at)
        {
            var menu = new GenericMenu();
            foreach (var m in new[] { MachineGunMount.Coaxial, MachineGunMount.Turret, MachineGunMount.Hull })
            {
                bool can = m == MachineGunMount.Hull || (m == MachineGunMount.Turret ? setup.turret : setup.gun);
                string tip = m == MachineGunMount.Coaxial ? "（砲座と一緒に上下）" : m == MachineGunMount.Turret ? "（砲塔と一緒に旋回）" : "（車体に固定）";
                var content = new GUIContent(TankRigBuilder.MountLabel(m) + "機銃 " + tip);
                if (!can)
                {
                    menu.AddDisabledItem(content);
                    continue;
                }
                var mount = m;
                menu.AddItem(content, false, () =>
                {
                    if (!setup) return;
                    TankRigBuilder.AddMachineGun(setup, mount);
                    int n = setup.machineGuns.Count - 1;
                    Select(Target.MachineGun(n));
                    status = setup.machineGuns[n].name + "を追加しました。「クリックで配置」で位置を決められます。";
                });
            }
            menu.DropDown(at);
        }

        void DrawSecondaryTurrets(SerializedObject so, ref System.Action pending)
        {
            var list = setup.secondaryTurrets;
            var labels = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                labels[i] = s == null ? "（空）"
                    : $"{(TankRigBuilder.SecondaryProblems(setup, s).Count > 0 ? "⚠" : "✔")} {s.name}（{(s.weapon == SecondaryWeapon.Cannon ? "砲" : "機銃")}）";
            }
            int sel = EntryPicker(labels, stSel, "副砲塔はありません", false, _ =>
                {
                    if (!setup) return;
                    TankRigBuilder.AddSecondaryTurret(setup);
                    int n = setup.secondaryTurrets.Count - 1;
                    Select(Target.Secondary(n));
                    status = setup.secondaryTurrets[n].name + "を追加しました。砲塔・砲架・砲身（どれもなくて可）をシーンからドラッグして指定してください。";
                    GUIUtility.ExitGUI();
                },
                "副砲塔を追加します", "選んでいる副砲塔を一覧から外します（モデルはそのまま。Ctrl+Z で戻せます）", out bool remove);
            if (sel != stSel && sel < list.Count) Select(Target.Secondary(sel));
            stSel = Mathf.Clamp(sel, 0, Mathf.Max(0, list.Count - 1));
            if (list.Count == 0)
            {
                EditorGUILayout.HelpBox("「+ 追加」で副砲塔を追加できます。砲塔（旋回）・砲架（上下）・砲身（後退）はどれも空のままで構いません。ないものは動かないだけです（全部空なら砲口の向きに撃つ固定砲）。", MessageType.None);
                return;
            }
            int idx = stSel;
            if (remove)
                pending += () =>
                {
                    TankRigBuilder.RemoveSecondaryTurret(setup, idx);
                    stSel = Mathf.Max(0, idx - 1);
                    focus = Target.Track;
                    placing = null;
                };
            var st = list[idx];
            var e = so.FindProperty("secondaryTurrets").GetArrayElementAtIndex(idx);
            if (st == null) return;
            var problems = TankRigBuilder.SecondaryProblems(setup, st);
            bool cannon = st.weapon == SecondaryWeapon.Cannon;

            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(e.FindPropertyRelative("name"), new GUIContent("名前"));
            EditorGUILayout.PropertyField(e.FindPropertyRelative("weapon"), new GUIContent("武器"));
            EditorGUILayout.PropertyField(e.FindPropertyRelative("trigger"), new GUIContent("発射ボタン"));

            SubHeader("部位（どれもなくて可）");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(e.FindPropertyRelative("turret"), new GUIContent("砲塔（旋回）", "なくても可。左右に旋回する部分"));
            EditorGUILayout.PropertyField(e.FindPropertyRelative("gun"), new GUIContent("砲架（上下）", "なくても可。上下（俯仰）に動く部分"));
            EditorGUILayout.PropertyField(e.FindPropertyRelative("barrel"), new GUIContent("砲身（後退）", "なくても可。発砲時に後退する部分（武器が砲のときだけ）"));
            if (EditorGUI.EndChangeCheck())
                pending += () =>
                {
                    TankRigBuilder.RefreshSecondary(setup, setup.secondaryTurrets[idx]);
                    status = setup.secondaryTurrets[idx].name + "の部位を変えたので、回転軸と砲口を置き直しました。";
                };
            foreach (var p in problems) EditorGUILayout.HelpBox(p + "（完成させるときはこの部位を外します）", MessageType.Error);
            EditorGUILayout.LabelField("動き", $"旋回 {(st.turret ? "あり" : "なし")}　上下 {(st.gun ? "あり" : "なし")}　後退 {(st.barrel && cannon ? "あり" : "なし")}");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("砲口");
                PlaceButtons(Target.Secondary(idx), st.muzzle);
                if (GUILayout.Button(new GUIContent("自動", "砲身・砲架の先端に置き直します"), GUILayout.Width(44f)))
                    pending += () => TankRigBuilder.AutoPlaceSecondaryMuzzle(setup, setup.secondaryTurrets[idx]);
            }

            if (st.turret)
            {
                SubHeader("旋回");
                var minT = e.FindPropertyRelative("minTraverse");
                var maxT = e.FindPropertyRelative("maxTraverse");
                bool full = maxT.floatValue - minT.floatValue >= 359.9f;
                bool nf = EditorGUILayout.Toggle("全周旋回", full);
                if (nf != full)
                {
                    minT.floatValue = nf ? -180f : -45f;
                    maxT.floatValue = nf ? 180f : 45f;
                }
                else if (!full)
                {
                    float a = minT.floatValue, b = maxT.floatValue;
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.MinMaxSlider(new GUIContent($"旋回範囲 {a:0}°〜{b:0}°", "砲口のいまの向きが 0°。左が −、右が +"), ref a, ref b, -180f, 180f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        minT.floatValue = Mathf.Round(a);
                        maxT.floatValue = Mathf.Round(b);
                    }
                }
                EditorGUILayout.Slider(e.FindPropertyRelative("traverseSpeed"), 2f, 180f, new GUIContent("旋回速度（度/秒）"));
            }
            if (st.gun)
            {
                SubHeader("上下");
                var minE = e.FindPropertyRelative("minElevation");
                var maxE = e.FindPropertyRelative("maxElevation");
                float a = minE.floatValue, b = maxE.floatValue;
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.MinMaxSlider(new GUIContent($"俯仰角 {a:0}°〜{b:0}°"), ref a, ref b, -30f, 80f);
                if (EditorGUI.EndChangeCheck())
                {
                    minE.floatValue = Mathf.Round(a);
                    maxE.floatValue = Mathf.Round(b);
                }
                EditorGUILayout.Slider(e.FindPropertyRelative("elevationSpeed"), 2f, 120f, new GUIContent("俯仰速度（度/秒）"));
            }

            stSpecFold = EditorGUILayout.Foldout(stSpecFold, cannon ? "性能（装填・火力・射程）" : "性能（発射レート・弾・ベルト）", true);
            if (stSpecFold)
                using (new EditorGUI.IndentLevelScope())
                {
                    if (cannon)
                    {
                        EditorGUILayout.Slider(e.FindPropertyRelative("reloadTime"), 0.2f, 15f, new GUIContent("装填時間（秒）"));
                        EditorGUILayout.Slider(e.FindPropertyRelative("power"), 0.05f, 4f,
                            new GUIContent("火力", "主砲の 1 = 標準に対して。車体への反動、砲身の後退量、カメラの揺れ、発砲炎の大きさに比例"));
                        EditorGUILayout.Slider(e.FindPropertyRelative("range"), 100f, 3000f, new GUIContent("射程（m）"));
                        EditorGUILayout.Slider(e.FindPropertyRelative("caliberMm"), 12.7f, 60f, new GUIContent("口径（mm）", "リアルなエフェクトの発砲炎・煙・光の大きさがこれで決まります"));
                    }
                    else DrawSpec(e.FindPropertyRelative("machineGun"));
                }
        }

        static readonly string[] PresetNames = { "軽機関銃", "重機関銃", "弾数無限" };

        /// <summary>Machine gun performance, with presets in a dropdown.</summary>
        void DrawSpec(SerializedProperty spec)
        {
            if (presetKind >= 0 && presetPath == spec.propertyPath)
            {
                switch (presetKind)
                {
                    case 0: SetSpec(spec, 800f, 0.5f, 1000f, 3, 750f, 250, 6f, 1f); break;
                    case 1: SetSpec(spec, 500f, 0.35f, 1800f, 4, 850f, 100, 8f, 2f); break;
                    default: SetSpec(spec, 750f, 0.5f, 1200f, 1, 300f, 0, 5f, 1f); break;
                }
                status = "機銃の性能を「" + PresetNames[Mathf.Clamp(presetKind, 0, 2)] + "」のひな形にしました。";
                presetKind = -1;
            }
            var r = EditorGUILayout.GetControlRect();
            r = EditorGUI.PrefixLabel(r, new GUIContent("ひな形", "下の値をまとめて設定します"));
            if (EditorGUI.DropdownButton(r, new GUIContent("選ぶ…"), FocusType.Passive))
            {
                var menu = new GenericMenu();
                string path = spec.propertyPath;
                for (int i = 0; i < PresetNames.Length; i++)
                {
                    int k = i;
                    menu.AddItem(new GUIContent(PresetNames[i]), false, () =>
                    {
                        presetPath = path;
                        presetKind = k;
                        Repaint();
                    });
                }
                menu.DropDown(r);
            }
            EditorGUILayout.Slider(spec.FindPropertyRelative("roundsPerMinute"), 60f, 1500f, new GUIContent("発射レート（発/分）"));
            EditorGUILayout.Slider(spec.FindPropertyRelative("spread"), 0f, 5f, new GUIContent("弾の広がり（度）"));
            EditorGUILayout.Slider(spec.FindPropertyRelative("range"), 50f, 3000f, new GUIContent("射程（m）"));
            EditorGUILayout.IntSlider(spec.FindPropertyRelative("tracerEvery"), 1, 10, new GUIContent("曳光弾（何発に 1 発）"));
            EditorGUILayout.Slider(spec.FindPropertyRelative("tracerSpeed"), 50f, 1500f, new GUIContent("曳光弾の速さ（m/s）"));
            var belt = spec.FindPropertyRelative("beltSize");
            EditorGUILayout.IntSlider(belt, 0, 1000, new GUIContent("ベルトの弾数（0 = 無限）"));
            using (new EditorGUI.DisabledScope(belt.intValue <= 0))
                EditorGUILayout.Slider(spec.FindPropertyRelative("beltReload"), 0.5f, 20f, new GUIContent("ベルト交換（秒）"));
            EditorGUILayout.Slider(spec.FindPropertyRelative("impactScale"), 0f, 3f, new GUIContent("着弾の土煙・火花の量"));
            EditorGUILayout.Slider(spec.FindPropertyRelative("caliberMm"), 5.56f, 20f, new GUIContent("口径（mm）", "リアルなエフェクトの発砲炎・煙・光の大きさがこれで決まります"));
        }

        static void SetSpec(SerializedProperty spec, float rpm, float spread, float range, int tracerEvery, float tracerSpeed, int belt, float reload, float impact)
        {
            spec.FindPropertyRelative("roundsPerMinute").floatValue = rpm;
            spec.FindPropertyRelative("spread").floatValue = spread;
            spec.FindPropertyRelative("range").floatValue = range;
            spec.FindPropertyRelative("tracerEvery").intValue = tracerEvery;
            spec.FindPropertyRelative("tracerSpeed").floatValue = tracerSpeed;
            spec.FindPropertyRelative("beltSize").intValue = belt;
            spec.FindPropertyRelative("beltReload").floatValue = reload;
            spec.FindPropertyRelative("impactScale").floatValue = impact;
        }
    }
}
