using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>
    /// Tools > Tank Rig > 戦車組み立てウィンドウ: pick hull / turret / gun, place the tracks (mirrored) and the effect points
    /// with the mouse in the Scene view, then rig the tank so it can be driven with WASD + mouse.
    /// The steps are tabs; the finish buttons stay at the bottom whatever tab is open.
    /// </summary>
    public partial class TankRigBuilderWindow : EditorWindow
    {
        [MenuItem("Tools/Tank Rig/戦車組み立てウィンドウ", priority = 1)]
        public static void Open()
        {
            var w = GetWindow<TankRigBuilderWindow>("戦車組み立て");
            w.minSize = new Vector2(380f, 480f);
            w.Show();
        }

        enum TargetKind { None, Track, Point, MachineGun, SecondaryMuzzle }

        /// <summary>What the scene view handles edit (or what a click places).</summary>
        struct Target
        {
            public TargetKind kind;
            public TankRigBuilder.PointKind point;
            public int index;
            public static Target Track => new Target { kind = TargetKind.Track };
            public static Target Point(TankRigBuilder.PointKind k) => new Target { kind = TargetKind.Point, point = k };
            public static Target MachineGun(int i) => new Target { kind = TargetKind.MachineGun, index = i };
            public static Target Secondary(int i) => new Target { kind = TargetKind.SecondaryMuzzle, index = i };
            public bool Is(Target o) => kind == o.kind && (kind != TargetKind.Point || point == o.point) && index == o.index;
        }

        /// <summary>The steps, one tab each.</summary>
        enum Page { Parts, Tracks, Suspension, Points, Weapons, Drive }
        static readonly string[] PageLabels = { "① 部位", "② 履帯", "③ サスペンション", "④ ポイント", "⑤ 武装", "⑥ 走行・主砲" };

        GameObject model;
        Transform hull, turret, gun, barrel;
        bool flipForward;
        [SerializeField] TankRigSetup setup; // kept over script reloads / play mode
        [SerializeField] Page page = Page.Tracks;
        [SerializeField] TankRigBuilder.PointKind pointSel;
        Vector2 scroll;
        bool createGround = true, setupCamera = true, linkDetails, showArms = true;
        bool trackFiles, trackNudge, finishOptions;
        float suspensionPreview;
        Target? placing;
        Target focus = Target.Track;
        string status;
        GUIStyle sectionStyle, bigButton, tabStyle;

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            OnSelectionChanged();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            placing = null;
            TankRigBrightView.Restore();
            ResetPreview();
            SetView(ViewMode.Free);
        }

        // the three-view is for editing: give the scene view back before playing
        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode && (view != ViewMode.Free || ThreeViewHeld)) SetView(ViewMode.Free);
        }

        void OnSelectionChanged()
        {
            var go = Selection.activeGameObject;
            if (go)
            {
                var s = go.GetComponentInParent<TankRigSetup>();
                if (s) setup = s;
            }
            Repaint();
        }

        /// <summary>Edit <paramref name="t"/> with the scene handles and open the tab (and the entry) it belongs to.</summary>
        void Select(Target t)
        {
            focus = t;
            switch (t.kind)
            {
                case TargetKind.Track: page = Page.Tracks; break;
                case TargetKind.Point: page = Page.Points; pointSel = t.point; break;
                case TargetKind.MachineGun: page = Page.Weapons; weaponTab = 0; mgSel = t.index; break;
                case TargetKind.SecondaryMuzzle: page = Page.Weapons; weaponTab = 1; stSel = t.index; break;
            }
            Repaint();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ window

        void Styles()
        {
            if (sectionStyle != null) return;
            sectionStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            bigButton = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
            tabStyle = new GUIStyle(EditorStyles.miniButton) { fixedHeight = 22f, fontSize = 11 };
        }

        void Section(string title)
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(title, sectionStyle);
            var r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        static void SubHeader(string title)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        static void Line()
        {
            var r = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        void OnGUI()
        {
            Styles();
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            // no tank to edit (scene changed, play mode...) but still holding the scene view in three-view: give it back
            if ((!setup || playing) && (view != ViewMode.Free || ThreeViewHeld)) SetView(ViewMode.Free);
            if (playing)
                EditorGUILayout.HelpBox("プレイ中は組み立てできません（変更はプレイ終了で消えるため）。■ で止めてから操作してください。", MessageType.Warning);
            using (new EditorGUI.DisabledScope(playing))
            {
                if (!setup)
                {
                    scroll = EditorGUILayout.BeginScrollView(scroll);
                    EditorGUILayout.HelpBox("SMC-AD などで取り込んだ戦車に履帯を組み合わせ、WASD とマウスで操縦できる戦車にします。", MessageType.None);
                    DrawStart();
                    EditorGUILayout.Space(12f);
                    EditorGUILayout.EndScrollView();
                }
                else
                {
                    TankRigBuilder.Migrate(setup);
                    DrawHeader();
                    DrawPageTabs();
                    scroll = EditorGUILayout.BeginScrollView(scroll);
                    EditorGUILayout.Space(4f);
                    switch (page)
                    {
                        case Page.Parts: DrawParts(); break;
                        case Page.Tracks: DrawTracks(); break;
                        case Page.Suspension: DrawSuspension(); break;
                        case Page.Points: DrawPoints(); break;
                        case Page.Weapons: DrawWeapons(); break;
                        case Page.Drive: DrawSettings(); break;
                    }
                    EditorGUILayout.Space(8f);
                    EditorGUILayout.EndScrollView();
                    DrawFinish();
                }
            }
            if (!string.IsNullOrEmpty(status))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(status, MessageType.Info);
                    if (GUILayout.Button(new GUIContent("×", "このメッセージを消す"), GUILayout.Width(22f))) status = null;
                }
            }
        }

        // ------------------------------------------------------------------ start

        void DrawStart()
        {
            Section("戦車モデルと部位");
            EditorGUI.BeginChangeCheck();
            model = (GameObject)EditorGUILayout.ObjectField("戦車モデル", model, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck() && model && !EditorUtility.IsPersistent(model))
                TankRigBuilder.AutoDetect(model, out hull, out turret, out gun, out barrel);

            if (model && EditorUtility.IsPersistent(model))
            {
                EditorGUILayout.HelpBox("シーンに置いた戦車を指定してください。", MessageType.Warning);
                if (GUILayout.Button("このモデルをシーンに置く"))
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    Undo.RegisterCreatedObjectUndo(inst, "戦車をシーンに置く");
                    model = inst;
                    Selection.activeGameObject = inst;
                    TankRigBuilder.AutoDetect(model, out hull, out turret, out gun, out barrel);
                }
                return;
            }
            if (!model && Selection.activeGameObject && !EditorUtility.IsPersistent(Selection.activeGameObject)
                && GUILayout.Button("選択中のオブジェクトを使う"))
            {
                model = Selection.activeGameObject.transform.root.gameObject;
                TankRigBuilder.AutoDetect(model, out hull, out turret, out gun, out barrel);
            }

            using (new EditorGUI.DisabledScope(!model))
            {
                if (GUILayout.Button("部位を自動検出（SMC-AD のジョイントから）"))
                    TankRigBuilder.AutoDetect(model, out hull, out turret, out gun, out barrel);
            }
            hull = (Transform)EditorGUILayout.ObjectField("車体", hull, typeof(Transform), true);
            turret = (Transform)EditorGUILayout.ObjectField("砲塔", turret, typeof(Transform), true);
            gun = (Transform)EditorGUILayout.ObjectField(new GUIContent("砲座（上下）", "必須。主砲の上下（俯仰）で回る部分"), gun, typeof(Transform), true);
            barrel = (Transform)EditorGUILayout.ObjectField(new GUIContent("砲身（後退）", "なくても可。発砲時に前後に後退する部分。指定しないと後退は起きません"), barrel, typeof(Transform), true);
            if (!gun) EditorGUILayout.HelpBox("砲座は必須です。主砲の上下に回る部分を指定してください。", MessageType.Warning);
            string barrelProblem = TankRigBuilder.BarrelProblem(gun, barrel);
            if (barrelProblem != null) EditorGUILayout.HelpBox(barrelProblem, MessageType.Error);
            else if (gun && !barrel) EditorGUILayout.HelpBox("砲身なし: 発砲しても砲身は後退しません（車体の反動・発砲炎はあります）。", MessageType.None);
            flipForward = EditorGUILayout.ToggleLeft(new GUIContent("前後を反転する", "前方向は主砲の向きから自動で決まります。逆になるときだけオンにします"), flipForward);

            EditorGUILayout.Space(6f);
            using (new EditorGUI.DisabledScope(!model || !hull || !gun || barrelProblem != null))
            {
                if (GUILayout.Button("組み立てを始める", bigButton, GUILayout.Height(34f)))
                {
                    setup = TankRigBuilder.CreateSetup(model, hull, turret, gun, barrel, flipForward);
                    AutoAssignTrackAssets();
                    Selection.activeGameObject = setup.gameObject;
                    page = Page.Tracks;
                    status = "組み立て用のルート「" + setup.name + "」を作り、各ポイントを仮配置しました。次は「② 履帯」です。";
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
            }
        }

        // ------------------------------------------------------------------ header and tabs

        /// <summary>The tank being built and the scene view tools (used by several tabs), then the step tabs.</summary>
        void DrawHeader()
        {
            EditorGUILayout.Space(2f);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("組み立て中", setup, typeof(TankRigSetup), true);
            DrawEditTools();
        }

        string PageMark(Page p)
        {
            switch (p)
            {
                case Page.Parts: return setup.gun ? "" : " ⚠";
                case Page.Tracks: return setup.trackLeft ? " ✔" : "";
                case Page.Weapons:
                    foreach (var st in setup.secondaryTurrets)
                        if (st != null && TankRigBuilder.SecondaryProblems(setup, st).Count > 0) return " ⚠";
                    return "";
                default: return "";
            }
        }

        void DrawPageTabs()
        {
            EditorGUILayout.Space(4f);
            var labels = new GUIContent[PageLabels.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = new GUIContent(PageLabels[i] + PageMark((Page)i));
            int p = GUILayout.SelectionGrid((int)page, labels, 3, tabStyle);
            if (p != (int)page)
            {
                page = (Page)p;
                placing = null;
                scroll = Vector2.zero;
                GUI.FocusControl(null);
            }
            Line();
        }

        // ------------------------------------------------------------------ ① parts

        void DrawParts()
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("車体", setup.hull, typeof(Transform), true);
                EditorGUILayout.ObjectField("砲塔", setup.turret, typeof(Transform), true);
                EditorGUILayout.ObjectField(new GUIContent("砲座（上下）"), setup.gun, typeof(Transform), true);
            }
            EditorGUI.BeginChangeCheck();
            var b = (Transform)EditorGUILayout.ObjectField(new GUIContent("砲身（後退）", "なくても可。指定しないと発砲時の後退は起きません"), setup.barrel, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
            {
                string problem = TankRigBuilder.SetBarrel(setup, b);
                status = problem ?? (b ? "砲身を指定しました。砲口は砲身と一緒に動きます。反映するには「戦車を完成させる」を押してください。"
                                       : "砲身を外しました（発砲時の後退なし）。反映するには「戦車を完成させる」を押してください。");
            }
            EditorGUILayout.LabelField("回転軸", setup.axesFromJoints ? "SMC-AD のジョイントから取得" : "形から推定");
            if (!setup.gun) EditorGUILayout.HelpBox("砲座がありません。「別の戦車を組み立てる」から砲座を指定し直してください。", MessageType.Warning);
            else if (!setup.barrel) EditorGUILayout.HelpBox("砲身なし: 発砲しても砲身は後退しません。", MessageType.None);

            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            float bs = EditorGUILayout.FloatField(new GUIContent("ブロックの大きさ（m）", "スナップ・三面図のグリッドの単位。Scrap Mechanic のブロック 1 個 = 0.25 m"), setup.blockSize);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "ブロックの大きさ");
                setup.blockSize = Mathf.Max(0.01f, bs);
                TankRigBuilder.ResetGrid(setup);
                SceneView.RepaintAll();
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("前後を反転", "前方向が逆のとき。ポイントも置き直します")))
                {
                    TankRigBuilder.FlipForward(setup);
                    status = "前後を反転し、ポイントを置き直しました。";
                }
                if (GUILayout.Button(new GUIContent("別の戦車を組み立てる", "この戦車はそのまま残して、最初の画面に戻ります")))
                {
                    SetView(ViewMode.Free);
                    setup = null;
                    model = null;
                    hull = turret = gun = barrel = null;
                    status = null;
                    GUIUtility.ExitGUI();
                }
            }
        }

        // ------------------------------------------------------------------ ② tracks

        void AutoAssignTrackAssets()
        {
            if (!setup) return;
            TankRigAssets.EnsureDefaultTracks();
            Undo.RecordObject(setup, "履帯の指定");
            if (!setup.trackAsset) setup.trackAsset = TankRigAssets.LoadTrack("TankTrack_6W");
            if (!setup.linkAsset) setup.linkAsset = TankRigAssets.LoadTrack("TankTrack_Link");
        }

        void ImportTracksFrom(string src)
        {
            int n = TankRigAssets.ImportTracks(src);
            AutoAssignTrackAssets();
            status = n > 0 ? $"履帯の FBX を {n} 個取り込みました（{TankRigAssets.TrackFolder}）。" : "FBX が見つかりませんでした: " + src;
        }

        /// <summary>Wheel set: 6 or 7 road wheels (or another FBX given below).</summary>
        void DrawWheelSet()
        {
            var six = TankRigAssets.LoadTrack("TankTrack_6W");
            var seven = TankRigAssets.LoadTrack("TankTrack_7W");
            var options = new List<string> { "6 輪", "7 輪" };
            int cur = setup.trackAsset && setup.trackAsset == six ? 0 : setup.trackAsset && setup.trackAsset == seven ? 1 : -1;
            if (cur < 0)
            {
                options.Add(setup.trackAsset ? "その他: " + setup.trackAsset.name : "（未指定）");
                cur = 2;
            }
            int n = EditorGUILayout.Popup(new GUIContent("転輪", "転輪の数。自作の FBX は「FBX の指定・取り込み」から"), cur, options.ToArray());
            if (n == cur || n > 1) return;
            var asset = n == 0 ? six : seven;
            if (!asset)
            {
                status = "その転輪の FBX が取り込まれていません。「FBX の指定・取り込み」の「同梱の履帯を取り込む」を押してください。";
                return;
            }
            Undo.RecordObject(setup, "履帯の指定");
            setup.trackAsset = asset;
            if (!setup.linkAsset) setup.linkAsset = TankRigAssets.LoadTrack("TankTrack_Link");
            if (setup.trackLeft) TankRigBuilder.PlaceTracks(setup, false);
        }

        /// <summary>Track link design (all share the same pitch / width, so they can be swapped at any time).</summary>
        void DrawLinkDesigns()
        {
            var designs = TankRigAssets.LinkDesigns();
            var labels = new List<string>();
            int current = -1;
            for (int i = 0; i < designs.Count; i++)
            {
                labels.Add(designs[i].label);
                if (designs[i].asset == setup.linkAsset) current = i;
            }
            if (current < 0)
            {
                labels.Add(setup.linkAsset ? "その他: " + setup.linkAsset.name : "（未指定）");
                current = labels.Count - 1;
            }
            int n = EditorGUILayout.Popup(new GUIContent("履帯のデザイン", "どれも同じ規格なので、配置した後でもいつでも切り替えられます"), current, labels.ToArray());
            if (n != current && n < designs.Count)
            {
                TankRigBuilder.SetLinkDesign(setup, designs[n].asset);
                status = "履帯のデザインを「" + designs[n].label + "」にしました。";
                SceneView.RepaintAll();
            }
            if (designs.Count < TankRigAssets.BuiltInLinks.Length)
                EditorGUILayout.HelpBox("取り込まれていないデザインがあります。「FBX の指定・取り込み」の「同梱の履帯を取り込む」で全部使えるようになります。", MessageType.None);
        }

        void DrawTrackFiles()
        {
            trackFiles = EditorGUILayout.Foldout(trackFiles, "FBX の指定・取り込み", true);
            if (!trackFiles) return;
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                var ta = (GameObject)EditorGUILayout.ObjectField("転輪一式（FBX）", setup.trackAsset, typeof(GameObject), false);
                var la = (GameObject)EditorGUILayout.ObjectField("履帯リンク（FBX）", setup.linkAsset, typeof(GameObject), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(setup, "履帯の指定");
                    setup.trackAsset = ta;
                    if (la != setup.linkAsset)
                    {
                        if (la && setup.trackLeft) TankRigBuilder.SetLinkDesign(setup, la);
                        else setup.linkAsset = la;
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUI.indentLevel * 15f);
                    if (GUILayout.Button(new GUIContent("同梱の履帯を取り込む", "パッケージに入っている転輪・履帯の FBX を " + TankRigAssets.TrackFolder + " にコピーし直します")))
                        ImportTracksFrom(TankRigAssets.DefaultSourceFolder());
                    if (GUILayout.Button(new GUIContent("フォルダから…", "自分で作った履帯の FBX（TankTrack_*.fbx）があるフォルダを選んで取り込みます")))
                    {
                        string src = EditorUtility.OpenFolderPanel("履帯の FBX があるフォルダ", "", "");
                        if (!string.IsNullOrEmpty(src)) ImportTracksFrom(src);
                    }
                }
            }
        }

        void DrawTracks()
        {
            DrawWheelSet();
            DrawLinkDesigns();
            DrawTrackFiles();

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(!setup.trackAsset || !setup.linkAsset))
            {
                if (GUILayout.Button(setup.trackLeft ? "履帯を置き直す（自動フィット）" : "履帯を配置する（自動フィット）", GUILayout.Height(26f)))
                {
                    TankRigBuilder.PlaceTracks(setup, true);
                    if (SnapStep > 0f) TankRigBuilder.SnapTrack(setup, SnapStep);
                    Select(Target.Track);
                    status = "左右の履帯を置きました。シーンビューで左の履帯を動かすと、右も左右対称に動きます。";
                }
            }
            if (!setup.trackLeft) return;

            SubHeader("位置と大きさ");
            EditorGUI.BeginChangeCheck();
            var o = setup.trackOffset;
            float x = EditorGUILayout.Slider("左右の間隔（片側）", o.x, 0f, 6f);
            float y = EditorGUILayout.Slider("高さ", o.y, -5f, 5f);
            float z = EditorGUILayout.Slider("前後", o.z, -8f, 8f);
            float sc = EditorGUILayout.Slider("大きさ", setup.trackScale, 0.2f, 4f);
            float th = EditorGUILayout.Slider(new GUIContent("厚さ（倍率）", "車軸方向だけの倍率"), setup.trackThickness, 0.3f, 3f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "履帯の調整");
                Undo.RecordObject(setup.trackLeft, "履帯の調整");
                if (setup.trackRight) Undo.RecordObject(setup.trackRight, "履帯の調整");
                bool scaleChanged = !Mathf.Approximately(sc, setup.trackScale);
                setup.trackOffset = new Vector3(x, y, z);
                setup.trackScale = sc;
                setup.trackThickness = th;
                setup.ApplyTrackPose();
                if (SnapStep > 0f && !scaleChanged) TankRigBuilder.SnapTrack(setup, SnapStep);
                if (scaleChanged) TankRigBuilder.ApplyTrackSettings(setup, suspensionPreview); // metres → new local units
                SceneView.RepaintAll();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                bool moving = focus.kind == TargetKind.Track;
                if (GUILayout.Button(new GUIContent(moving ? "● シーンで動かしています" : "シーンで履帯を動かす", "シーンビューに左の履帯の移動ハンドルを出します（右は左右対称に動きます）")))
                    Select(Target.Track);
                if (GUILayout.Button(new GUIContent("自動フィットし直す", "車体の大きさに合わせて位置と大きさを決め直します")))
                {
                    TankRigBuilder.RecordTrackTransforms(setup, "履帯の自動フィット");
                    TankRigBuilder.AutoFitTracks(setup);
                    setup.ApplyTrackPose();
                    TankRigBuilder.ApplyTrackSettings(setup, suspensionPreview); // metres → the new scale's local units
                }
            }
            var tt = setup.trackLeft.GetComponent<TankTrack>();
            if (tt) EditorGUILayout.LabelField("リンク", $"片側 {tt.LinkCount} 枚 / 一周 {tt.PathLength * setup.trackScale:0.00} m");

            trackNudge = EditorGUILayout.Foldout(trackNudge, "ブロック単位で動かす", true);
            if (trackNudge)
                using (new EditorGUI.IndentLevelScope())
                    DrawTrackNudge();

            linkDetails = EditorGUILayout.Foldout(linkDetails, "リンクの詳細（ピッチ・ピンの位置）", true);
            if (linkDetails)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUI.BeginChangeCheck();
                    float pitch = EditorGUILayout.FloatField("ピッチ（リンクの間隔）", setup.linkPitch);
                    float off = EditorGUILayout.FloatField("転輪表面からピンまで", setup.linkPitchLineOffset);
                    float depth = EditorGUILayout.FloatField("ピンから接地面まで", setup.linkGroundDepth);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(setup, "リンクの設定");
                        setup.linkPitch = Mathf.Max(0.02f, pitch);
                        setup.linkPitchLineOffset = off;
                        setup.linkGroundDepth = Mathf.Max(0f, depth);
                        TankRigBuilder.RefreshTracks(setup);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ ③ suspension

        void DrawSuspension()
        {
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.ToggleLeft(new GUIContent("サスペンション（トーションバー）を使う",
                "いまの転輪の位置が「地面に置いたときの静止位置」です。荷重がないと少し下がりながら後ろへ弧を描き、押されると上がります。" +
                "履帯は伸び切ったときにピンと張る長さなので、静止時は上側が少したわみます。"), setup.suspension);
            float droop, bump, arm, angle, sag, sway;
            using (new EditorGUI.DisabledScope(!on))
            {
                droop = EditorGUILayout.Slider(new GUIContent("伸び（荷重なし, m）", "荷重がないときに転輪が下がる量"), setup.suspensionDroop, 0f, 0.4f);
                bump = EditorGUILayout.Slider(new GUIContent("縮み（m）", "押されたときに転輪が上がる量"), setup.suspensionBump, 0f, 0.5f);
                arm = EditorGUILayout.Slider(new GUIContent("アームの長さ（m）", "支点は転輪の後ろ側"), setup.armLength, 0.1f, 1.5f);
                angle = EditorGUILayout.Slider("アームの角度（度）", setup.armAngle, 0f, 45f);
                sag = EditorGUILayout.Slider("履帯のたわみの上限（m）", setup.maxSag, 0f, 0.4f);
                sway = EditorGUILayout.Slider(new GUIContent("車体の揺れ", "加減速・旋回での車体の傾きの強さ（0 で傾かない）"), setup.bodySway, 0f, 3f);
            }
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "サスペンションの設定");
                setup.suspension = on;
                setup.suspensionDroop = droop;
                setup.suspensionBump = bump;
                setup.armLength = arm;
                setup.armAngle = angle;
                setup.maxSag = sag;
                setup.bodySway = sway;
                TankRigBuilder.ApplyTrackSettings(setup, suspensionPreview);
                SceneView.RepaintAll();
            }
            if (!setup.trackLeft)
            {
                EditorGUILayout.HelpBox("履帯を置くと、ここで動きを確かめられます。", MessageType.None);
                return;
            }
            if (!setup.suspension) return;

            SubHeader("確認");
            EditorGUI.BeginChangeCheck();
            float p = EditorGUILayout.Slider("プレビュー（縮み ← 静止 → 伸び）", suspensionPreview, -1f, 1f);
            if (EditorGUI.EndChangeCheck())
            {
                suspensionPreview = Mathf.Abs(p) < 0.04f ? 0f : p;
                TankRigBuilder.ApplyTrackSettings(setup, suspensionPreview);
                SceneView.RepaintAll();
            }
            var tt = setup.trackLeft.GetComponent<TankTrack>();
            if (tt)
                EditorGUILayout.LabelField("履帯のたわみ", tt.CurrentSag > 1e-4f
                    ? $"{tt.CurrentSag * setup.trackScale * 100f:0.0} cm"
                    : "なし（ピンと張っています）");
            bool arms = EditorGUILayout.ToggleLeft("シーンビューにアームの動き（弧）を表示", showArms);
            if (arms != showArms)
            {
                showArms = arms;
                SceneView.RepaintAll();
            }
        }

        void ResetPreview()
        {
            if (suspensionPreview == 0f || !setup) return;
            suspensionPreview = 0f;
            TankRigBuilder.ApplyTrackSettings(setup, 0f);
        }

        // ------------------------------------------------------------------ ④ points

        void DrawPoints()
        {
            EditorGUILayout.HelpBox("ポイントを選んで「クリックで配置」を押し、シーンビューで戦車の表面をクリックします。置いた後は矢印で移動、回転ツール（E）で向きを変えられます。機銃は「⑤ 武装」にあります。", MessageType.None);
            var kinds = TankRigBuilder.AllPoints;
            var labels = new string[kinds.Length];
            int cur = 0;
            for (int i = 0; i < kinds.Length; i++)
            {
                labels[i] = (TankRigBuilder.GetPoint(setup, kinds[i]) ? "✔ " : "－ ") + TankRigBuilder.Label(kinds[i]);
                if (kinds[i] == pointSel) cur = i;
            }
            int n = EditorGUILayout.Popup(new GUIContent("ポイント", "✔ = 配置済み"), cur, labels);
            if (n != cur)
            {
                pointSel = kinds[n];
                placing = null;
                if (TankRigBuilder.GetPoint(setup, pointSel)) Select(Target.Point(pointSel));
            }
            var k = pointSel;
            var t = TankRigBuilder.GetPoint(setup, k);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                PlaceButtons(Target.Point(k), t);
            }
            if (k == TankRigBuilder.PointKind.Sight)
            {
                var kindLabels = new[] { new GUIContent("ディスプレイ式（射撃画面）"), new GUIContent("直接照準式（光学照準器）") };
                int kc = (int)setup.sightKind;
                int kn = EditorGUILayout.Popup(new GUIContent("照準カメラの種類", "Shift で照準するときの見え方。どちらもこのポイントから見ます（V で他の照準器にも切り替え可）"), kc, kindLabels);
                if (kn != kc) { Undo.RecordObject(setup, "照準カメラの種類"); setup.sightKind = (TankRig.Sights.AimSightKind)kn; EditorUtility.SetDirty(setup); }
            }
            if (TankRigBuilder.CanMirror(k))
            {
                bool m = TankRigBuilder.IsMirrored(setup, k);
                bool nm = EditorGUILayout.Toggle(new GUIContent("左右対称", "反対側にも同じポイントを自動で置きます"), m);
                if (nm != m)
                {
                    TankRigBuilder.SetMirrored(setup, k, nm);
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.Space(8f);
            if (GUILayout.Button(new GUIContent("すべて自動で仮配置し直す", "砲口・排気・ライト・機銃を、モデルの形から置き直します")))
            {
                TankRigBuilder.AutoPlacePoints(setup, true);
                SceneView.RepaintAll();
            }
        }

        /// <summary>「クリックで配置」 and 「選ぶ」 for one target.</summary>
        void PlaceButtons(Target target, Transform current)
        {
            bool active = placing.HasValue && placing.Value.Is(target);
            var bg = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.4f, 1f, 0.5f);
            if (GUILayout.Button(new GUIContent(active ? "クリック待ち…" : "クリックで配置", "押してからシーンビューで戦車の表面をクリックします（Esc で中止）")))
            {
                placing = active ? (Target?)null : target;
                SceneView.lastActiveSceneView?.Focus();
            }
            GUI.backgroundColor = bg;
            bool focused = focus.Is(target);
            using (new EditorGUI.DisabledScope(!current))
                if (GUILayout.Button(new GUIContent(focused ? "● 選択中" : "選ぶ", "シーンビューに移動・回転のハンドルを出します"), GUILayout.Width(64f)))
                    Select(target);
        }

        // ------------------------------------------------------------------ ⑥ drive and main gun

        void DrawSettings()
        {
            EditorGUI.BeginChangeCheck();
            int mode = EditorGUILayout.Popup(new GUIContent("走行方式"), setup.physicsDrive ? 0 : 1,
                new[] { "物理演算（車体だけ。履帯は描画）", "物理なし（軽い。AI の大量配置向け）" });
            float tons = setup.massTons;
            if (mode == 0) tons = EditorGUILayout.Slider("車重（トン）", setup.massTons, 1f, 120f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "物理演算の設定");
                setup.physicsDrive = mode == 0;
                setup.massTons = tons;
            }
            EditorGUILayout.HelpBox(setup.physicsDrive
                ? "転輪ごとのトーションバーが車体を支え、左右の履帯の駆動力で走ります。坂・段差・障害物・反動が車体の動きに出ます。"
                : "物理演算なし：地面に沿って滑らかに動きます。", MessageType.None);
            if (setup.physicsDrive && (!setup.trackLeft || !setup.suspension))
                EditorGUILayout.HelpBox("物理演算の走行は転輪のサスペンションで車体を押すので、履帯がない・サスペンションを使わないときは自動で「物理なし」になります。", MessageType.Warning);

            EditorGUI.BeginChangeCheck();
            bool realFx = EditorGUILayout.Toggle(new GUIContent("リアルなエフェクト", "機銃・副砲の発砲（口径で大きさが決まる）、排気、土煙と土塊、履帯の巻き込みを写実的なものにします。主砲は従来のエフェクトのまま。完成後も Tank FX で個別にオン / オフできます"), setup.realisticEffects);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "エフェクトの設定");
                setup.realisticEffects = realFx;
            }

            EditorGUI.BeginChangeCheck();
            SubHeader("走行");
            float maxSpeed = EditorGUILayout.Slider("最高速度（km/h）", setup.maxSpeedKmh, 5f, 120f);
            float rev = EditorGUILayout.Slider("後退速度（km/h）", setup.reverseSpeedKmh, 2f, 60f);
            float acc = EditorGUILayout.Slider("加速（m/s²）", setup.acceleration, 0.5f, 15f);
            float brk = EditorGUILayout.Slider(new GUIContent("ブレーキ（m/s²）", "W / S を離すとこの強さで止まります（実際は履帯の摩擦が上限）"), setup.braking, 1f, 20f);
            float resp = EditorGUILayout.Slider(new GUIContent("操作の反応", "大きいほどキビキビ（1 = 標準）"), setup.responsiveness, 0.3f, 3f);
            float turn = EditorGUILayout.Slider("車体の旋回（度/秒）", setup.turnRate, 5f, 120f);
            SubHeader("主砲");
            float trav = EditorGUILayout.Slider("砲塔の旋回（度/秒）", setup.traverseSpeed, 2f, 120f);
            float minE = setup.minElevation, maxE = setup.maxElevation;
            EditorGUILayout.MinMaxSlider($"俯仰角 {minE:0}°〜{maxE:0}°", ref minE, ref maxE, -30f, 70f);
            float reload = EditorGUILayout.Slider("装填時間（秒）", setup.reloadTime, 0.2f, 15f);
            float power = EditorGUILayout.Slider(new GUIContent("主砲火力", "反動・のけぞり、砲身の後退量、カメラの揺れ、発砲炎の大きさがこれに比例します（1 = 標準）"), setup.gunPower, 0.1f, 4f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "走行・武装の設定");
                setup.maxSpeedKmh = maxSpeed;
                setup.reverseSpeedKmh = rev;
                setup.acceleration = acc;
                setup.braking = brk;
                setup.responsiveness = resp;
                setup.turnRate = turn;
                setup.traverseSpeed = trav;
                setup.minElevation = Mathf.Round(minE);
                setup.maxElevation = Mathf.Round(maxE);
                setup.reloadTime = reload;
                setup.gunPower = power;
            }
        }

        // ------------------------------------------------------------------ finish (always at the bottom)

        void DrawFinish()
        {
            Line();
            EditorGUILayout.Space(2f);
            if (!setup.gun) EditorGUILayout.HelpBox("砲座が指定されていないので完成させられません（① 部位）。", MessageType.Error);
            else if (!setup.trackLeft) EditorGUILayout.LabelField("履帯がまだありません（履帯なしでも完成させられます）。", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!setup.gun))
                    if (GUILayout.Button(new GUIContent("戦車を完成させる", "操縦・照準・武装・エフェクト・ライト・カメラ・物理を組み込みます。設定を変えたら押し直してください"), bigButton, GUILayout.Height(32f)))
                    {
                        suspensionPreview = 0f;
                        var c = TankRigBuilder.Complete(setup, createGround, setupCamera);
                        Selection.activeGameObject = c.gameObject;
                        status = "完成しました。▶ で再生すると、WASD で走行、マウスで照準、左クリックで主砲、Space で機銃、中クリックか F で副武装、右クリックでズーム、L でライトです。設定を変えたら、もう一度「戦車を完成させる」を押してください。";
                    }
                using (new EditorGUI.DisabledScope(!setup.GetComponent<TankController>() || EditorApplication.isPlayingOrWillChangePlaymode))
                    if (GUILayout.Button(new GUIContent("▶ 試す", "プレイして走らせてみます"), GUILayout.Height(32f), GUILayout.Width(80f)))
                    {
                        ResetPreview();
                        EditorApplication.isPlaying = true;
                    }
            }
            string opts = (createGround ? "テスト用の地面あり" : "地面なし") + "・" + (setupCamera ? "カメラを設定" : "カメラはそのまま");
            finishOptions = EditorGUILayout.Foldout(finishOptions, "完成時のオプション（" + opts + "）", true);
            if (finishOptions)
                using (new EditorGUI.IndentLevelScope())
                {
                    createGround = EditorGUILayout.ToggleLeft("テスト用の地面を作る（坂・丘・的・壁・石・丸太・段差つき）", createGround);
                    setupCamera = EditorGUILayout.ToggleLeft("メインカメラを戦車の三人称カメラにする", setupCamera);
                }
            EditorGUILayout.Space(2f);
        }

        // ------------------------------------------------------------------ scene view

        static Color PointColor(TankRigBuilder.PointKind k) => k switch
        {
            TankRigBuilder.PointKind.Muzzle => new Color(1f, 0.45f, 0.2f),
            TankRigBuilder.PointKind.Sight => new Color(0.35f, 1f, 0.45f),
            TankRigBuilder.PointKind.Exhaust => new Color(0.75f, 0.75f, 0.75f),
            TankRigBuilder.PointKind.Headlight => new Color(0.5f, 0.85f, 1f),
            _ => new Color(1f, 0.25f, 0.3f),
        };

        static readonly Color MachineGunColor = new Color(1f, 0.85f, 0.2f);
        static readonly Color SecondaryColor = new Color(1f, 0.4f, 0.85f);

        void OnSceneGUI(SceneView sv)
        {
            if (!setup || EditorApplication.isPlaying) return;
            var root = setup.transform;
            float size = HandleUtility.GetHandleSize(root.position);
            if (view != ViewMode.Free) DrawGrid(sv);

            // front arrow
            Handles.color = new Color(0.2f, 0.8f, 1f, 0.9f);
            Handles.ArrowHandleCap(0, root.position, Quaternion.LookRotation(root.forward, root.up), size * 1.5f, EventType.Repaint);
            Handles.Label(root.position + root.forward * size * 1.6f, "前");

            // tracks: move the left one, the right one follows mirrored
            if (setup.trackLeft)
            {
                string blocks = view != ViewMode.Free || SnapStep > 0f ? "\n" + BlockText(TankRigBuilder.TrackOffsetBlocks(setup)) : "";
                Handles.Label(setup.trackLeft.position + root.up * size * 0.6f, "履帯（左）\n右は左右対称" + blocks);
                if (setup.trackRight) Handles.Label(setup.trackRight.position + root.up * size * 0.6f, "履帯（右）");
                if (focus.kind == TargetKind.Track)
                {
                    if (Drag(setup.trackLeft.position, root.rotation, new Color(0.3f, 1f, 0.6f), out var p))
                    {
                        TankRigBuilder.SetTrackPosition(setup, p);
                        Repaint();
                    }
                }
                else
                {
                    Handles.color = new Color(0.3f, 1f, 0.6f);
                    float hs = HandleUtility.GetHandleSize(setup.trackLeft.position) * 0.15f;
                    if (Handles.Button(setup.trackLeft.position, root.rotation, hs, hs, Handles.CubeHandleCap)) Select(Target.Track);
                }
            }

            // torsion arms: pivot, arm at rest, and the arc the wheel travels (compressed ↔ dropped)
            if (showArms && setup.suspension)
            {
                foreach (var tr in new[] { setup.trackLeft, setup.trackRight })
                {
                    var tt = tr ? tr.GetComponent<TankTrack>() : null;
                    if (!tt) continue;
                    for (int i = 0; i < tt.wheels.Count; i++)
                    {
                        if (!tt.TryGetArm(i, out var pivot, out var rest, out var dropped, out var compressed, out var axle)) continue;
                        Handles.color = new Color(1f, 0.8f, 0.2f, 0.9f);
                        Handles.DrawLine(pivot, rest, 2f);
                        Handles.SphereHandleCap(0, pivot, Quaternion.identity, HandleUtility.GetHandleSize(pivot) * 0.06f, EventType.Repaint);
                        Vector3 from = compressed - pivot;
                        float arc = Vector3.SignedAngle(from, dropped - pivot, axle);
                        Handles.color = new Color(0.3f, 1f, 0.5f, 0.9f);
                        Handles.DrawWireArc(pivot, axle, from, arc, from.magnitude, 2f);
                    }
                }
            }

            // effect points
            foreach (var k in TankRigBuilder.AllPoints)
            {
                var t = TankRigBuilder.GetPoint(setup, k);
                if (!t) continue;
                var mirror = TankRigBuilder.GetMirror(setup, k);
                if (mirror && TankRigBuilder.IsMirrored(setup, k))
                {
                    float hs = HandleUtility.GetHandleSize(mirror.position);
                    Handles.color = PointColor(k);
                    Handles.SphereHandleCap(0, mirror.position, Quaternion.identity, hs * 0.1f, EventType.Repaint);
                    Handles.DrawLine(mirror.position, mirror.position + mirror.forward * hs * 0.9f, 2f);
                }
                PointHandle(Target.Point(k), t, TankRigBuilder.Label(k), PointColor(k), mirror, () => setup.SyncMirrors());
            }

            // machine guns
            for (int i = 0; setup.machineGuns != null && i < setup.machineGuns.Count; i++)
            {
                var mg = setup.machineGuns[i];
                if (mg == null || !mg.point) continue;
                PointHandle(Target.MachineGun(i), mg.point, mg.name, MachineGunColor, null, null);
                if (mg.aimCone > 0f)
                {
                    // the cone it can turn in
                    float hs = HandleUtility.GetHandleSize(mg.point.position) * 0.8f;
                    float r = Mathf.Tan(Mathf.Min(mg.aimCone, 80f) * Mathf.Deg2Rad) * hs;
                    Handles.color = new Color(1f, 0.85f, 0.2f, 0.5f);
                    Handles.DrawWireDisc(mg.point.position + mg.point.forward * hs, mg.point.forward, r);
                }
            }

            // secondary turrets: traverse range and muzzle
            for (int i = 0; setup.secondaryTurrets != null && i < setup.secondaryTurrets.Count; i++)
            {
                var st = setup.secondaryTurrets[i];
                if (st == null) continue;
                if (st.turret && st.muzzle)
                {
                    Vector3 axisW = st.turret.TransformDirection(st.turretAxisLocal).normalized;
                    Vector3 fwd = Vector3.ProjectOnPlane(st.muzzle.forward, axisW);
                    if (fwd.sqrMagnitude > 1e-4f)
                    {
                        fwd.Normalize();
                        float r = HandleUtility.GetHandleSize(st.turret.position) * 0.7f;
                        float min = st.FullCircle ? -180f : st.minTraverse, max = st.FullCircle ? 180f : st.maxTraverse;
                        Handles.color = new Color(1f, 0.4f, 0.85f, 0.55f);
                        Handles.DrawWireArc(st.turret.position, axisW, Quaternion.AngleAxis(min, axisW) * fwd, max - min, r, 2f);
                        if (!st.FullCircle)
                        {
                            Handles.DrawLine(st.turret.position, st.turret.position + Quaternion.AngleAxis(min, axisW) * fwd * r);
                            Handles.DrawLine(st.turret.position, st.turret.position + Quaternion.AngleAxis(max, axisW) * fwd * r);
                        }
                    }
                }
                if (st.muzzle) PointHandle(Target.Secondary(i), st.muzzle, st.name + "の砲口", SecondaryColor, null, null);
            }

            if (placing.HasValue) { TankRigBrightView.Apply(); HandlePlacing(sv); }
            else TankRigBrightView.Restore();
        }

        /// <summary>Line + label for a point; a button to pick it, or move / rotate handles when it is the one being edited.</summary>
        void PointHandle(Target target, Transform t, string label, Color color, Transform mirror, System.Action afterChange)
        {
            var root = setup.transform;
            float hs = HandleUtility.GetHandleSize(t.position);
            Handles.color = color;
            Handles.DrawLine(t.position, t.position + t.forward * hs * 0.9f, 2f);
            Handles.Label(t.position + root.up * hs * 0.25f, label);
            if (!focus.Is(target))
            {
                Handles.color = color;
                if (Handles.Button(t.position, Quaternion.identity, hs * 0.12f, hs * 0.14f, Handles.SphereHandleCap))
                    Select(target);
                return;
            }
            if (Tools.current == Tool.Rotate)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion r;
                if (view != ViewMode.Free)
                {
                    // three-view: turn in the drawing plane only
                    Handles.color = color;
                    r = Handles.Disc(t.rotation, t.position, SceneView.currentDrawingSceneView.camera.transform.forward, hs * 0.8f, false, 0f);
                }
                else r = Handles.RotationHandle(t.rotation, t.position);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(t, "ポイントの回転");
                    if (mirror) Undo.RecordObject(mirror, "ポイントの回転");
                    t.rotation = r;
                    afterChange?.Invoke();
                }
            }
            else if (Drag(t.position, Tools.pivotRotation == PivotRotation.Local ? t.rotation : root.rotation, color, out var p))
            {
                Undo.RecordObject(t, "ポイントの移動");
                if (mirror) Undo.RecordObject(mirror, "ポイントの移動");
                t.position = p;
                afterChange?.Invoke();
            }
            if (view != ViewMode.Free || SnapStep > 0f)
            {
                Vector3 local = root.InverseTransformPoint(t.position) / TankRigBuilder.BlockSize(setup);
                Handles.Label(t.position - root.up * hs * 0.25f, $"左右 {local.x:+0.##;-0.##;0} / 高さ {local.y:0.##} / 前後 {local.z:+0.##;-0.##;0} ブロック");
            }
        }

        void HandlePlacing(SceneView sv)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(id);
            Handles.BeginGUI();
            var r = new Rect(e.mousePosition.x + 14f, e.mousePosition.y + 4f, 420f, 22f);
            GUI.Box(r, "クリックして配置: " + TargetLabel(placing.Value) + "（Esc で中止、B で明るく表示 " + (TankRigBrightView.Bright ? "オン" : "オフ") + "）");
            Handles.EndGUI();
            if (e.type == EventType.MouseMove) sv.Repaint();
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.B) { TankRigBrightView.Bright = !TankRigBrightView.Bright; e.Use(); sv.Repaint(); return; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                placing = null;
                e.Use();
                Repaint();
                return;
            }
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                var filters = setup.model ? setup.model.GetComponentsInChildren<MeshFilter>() : setup.GetComponentsInChildren<MeshFilter>();
                var hidden = TankRigWorkView.RendererTest();   // parts left out of the work view cannot be clicked
                if (hidden != null) filters = System.Array.FindAll(filters, mf => !hidden(mf.GetComponent<Renderer>()));
                if (MeshRaycast.Raycast(ray, filters, out var hit))
                {
                    var target = placing.Value;
                    Vector3 p = hit.point + hit.normal * 0.03f;
                    if (SnapStep > 0f) p = TankRigBuilder.Snap(setup, p, SnapStep);
                    switch (target.kind)
                    {
                        case TargetKind.Point: TankRigBuilder.PlacePoint(setup, target.point, p); break;
                        case TargetKind.MachineGun: TankRigBuilder.PlaceMachineGun(setup, target.index, p, false); break;
                        case TargetKind.SecondaryMuzzle: TankRigBuilder.PlaceSecondaryMuzzle(setup, setup.secondaryTurrets[target.index], p, null); break;
                    }
                    Select(target);
                    placing = null;
                    status = TargetLabel(target) + "を置きました。";
                    Repaint();
                }
                e.Use();
            }
        }

        string TargetLabel(Target t)
        {
            switch (t.kind)
            {
                case TargetKind.Point: return TankRigBuilder.Label(t.point);
                case TargetKind.MachineGun:
                    return setup.machineGuns != null && t.index < setup.machineGuns.Count ? setup.machineGuns[t.index].name : "機銃";
                case TargetKind.SecondaryMuzzle:
                    return (setup.secondaryTurrets != null && t.index < setup.secondaryTurrets.Count ? setup.secondaryTurrets[t.index].name : "副砲塔") + "の砲口";
                default: return "履帯";
            }
        }
    }
}
