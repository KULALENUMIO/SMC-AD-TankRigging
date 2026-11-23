// Numbers for the three sight types. Every value a real sight is described by lives here, so a sight can be
// tuned (or a specific tank reproduced) by editing an asset: Assets > Create > Tank Rig > 照準器 > ...
// Defaults come from public sources; see Documentation~/Sights.md for the sources and which values are guesses.
using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>Settings shared by every sight type.</summary>
    public abstract class SightProfile : ScriptableObject
    {
        [Header("共通")]
        [Tooltip("照準器の名前（画面の表示・切り替え用）")] public string displayName = "照準器";
        [Tooltip("目盛りと読み取りの角度の単位")] public AngleUnit angleUnit = AngleUnit.NatoMil;
        [Tooltip("マウス感度（視野が狭いほど自動で遅くなる）")] public float sensitivity = 0.12f;
        [Tooltip("カメラの手前のクリップ距離（m）。自分の車体の内側が映らないように少し大きめに")] public float nearClip = 0.5f;
        [Tooltip("照準点を探す最大距離（m）")] public float aimRange = 5000f;
        [Tooltip("発砲したときの視界の揺れの強さ（0 で揺れない）")] [Range(0f, 3f)] public float firingShake = 1f;

        [Header("レーザー測距儀")]
        public LaserRangefinder rangefinder = new LaserRangefinder();

        [Header("記号の文字")]
        [Tooltip("文字の大きさ（1080p 画面でのピクセル）")] public float textSize = 20f;
        [Tooltip("線の太さ（1080p 画面でのピクセル）")] public float lineWidth = 1.6f;
    }

    // ------------------------------------------------------------------ optical

    [CreateAssetMenu(menuName = "Tank Rig/照準器/直接照準器（光学）", fileName = "OpticalSight")]
    public class OpticalSightProfile : SightProfile
    {
        [Header("光学")]
        [Tooltip("倍率と実視界（ホイールで切り替え）。1 段だけのときはホイールで手動の距離を合わせる")]
        public ZoomStep[] zoom = { new ZoomStep(3f, 18f), new ZoomStep(10f, 5.5f) };
        [Tooltip("照準線の動き方")] public LineOfSight lineOfSight = LineOfSight.Stabilized;
        [Tooltip("接眼レンズの円の直径（画面の高さに対する割合）")] [Range(0.3f, 1.2f)] public float eyepieceDiameter = 0.92f;
        [Tooltip("円の縁のぼけ（半径に対する割合）")] [Range(0f, 0.3f)] public float eyepieceSoftness = 0.04f;
        [Tooltip("円の外の色")] public Color outsideColor = Color.black;
        [Tooltip("視界の周辺減光（0 でなし）")] [Range(0f, 1f)] public float vignette = 0.35f;
        [Tooltip("視界の色味（光学系の色。白 = そのまま）")] public Color glassTint = new Color(0.97f, 0.98f, 1f);

        [Header("レティクル（照準線）")]
        public ReticleStyle style = ReticleStyle.Western;
        [Tooltip("線の色（昼間）")] public Color reticleColor = new Color(0.02f, 0.02f, 0.02f, 1f);
        [Tooltip("照明を点ける（夜間用。線がこの色で光る）")] public bool illuminated;
        public Color illuminationColor = new Color(1f, 0.25f, 0.15f, 1f);
        [Tooltip("中心の隙間（角度の単位）")] public float centerGap = 0.5f;
        [Tooltip("十字の腕の長さ（角度の単位）。西側")] public float crossLength = 12f;
        [Tooltip("横の目盛りの間隔（角度の単位）")] public float tickSpacing = 5f;
        [Tooltip("横の目盛りを中心から左右どこまで（角度の単位）")] public float tickRange = 20f;
        [Tooltip("目盛りの長さ（角度の単位）")] public float tickLength = 1.5f;
        [Tooltip("山形の高さ（角度の単位）。ソ連・ロシア")] public float chevronHeight = 4f;
        [Tooltip("左右の見越し目盛りの間隔（角度の単位）。ソ連・ロシア")] public float leadTickSpacing = 2f;
        [Tooltip("見越し目盛りの数（片側）。ソ連・ロシア")] public int leadTickCount = 8;

        [Header("測距曲線（ソ連・ロシア）")]
        [Tooltip("測距曲線（目標の高さを挟んで距離を読む）を描く")] public bool stadiaRangefinder = true;
        [Tooltip("想定する目標の高さ（m）")] public float stadiaTargetHeight = 2.7f;
        [Tooltip("曲線の距離（m）")] public float[] stadiaRanges = { 1000f, 1400f, 1800f, 2200f, 2600f, 3000f, 3400f, 3800f };
        [Tooltip("曲線の位置（右端。中心からのずれ。角度の単位、右と上が +）")] public Vector2 stadiaOffset = new Vector2(-24f, -14f);
        [Tooltip("曲線の横幅（角度の単位）")] public float stadiaWidth = 26f;

        [Header("距離尺（ソ連・ロシア）")]
        [Tooltip("弾種ごとの距離尺を描く")] public bool ballisticScales = true;
        [Tooltip("距離尺の位置（左下。中心からのずれ。角度の単位）")] public Vector2 scaleOffset = new Vector2(10f, 10f);
        [Tooltip("距離尺の間隔（角度の単位）")] public float scaleSpacing = 9f;
        [Tooltip("距離尺の高さ（角度の単位）")] public float scaleHeight = 28f;
        [Tooltip("距離尺の目盛り（m）")] public float scaleStep = 100f;
        [Tooltip("数字を入れる間隔（m）")] public float scaleLabelStep = 1000f;

        [Header("表示")]
        [Tooltip("測距の数字を視野の下に出す")] public bool showRange = true;
        [Tooltip("弾種を出す")] public bool showAmmo = true;
        [Tooltip("装填完了ランプを出す")] public bool showReady = true;
        public Color readyColor = new Color(0.3f, 1f, 0.35f, 1f);
        [Tooltip("数字の色")] public Color readoutColor = new Color(1f, 0.3f, 0.2f, 1f);
        [Tooltip("砲が実際に向いている所に小さな印を出す（独立安定化のとき）")] public bool showGunMarker;

        [ContextMenu("プリセット: 西側（3x / 10x）")]
        public void PresetWestern()
        {
            displayName = "砲手照準器（光学）";
            angleUnit = AngleUnit.NatoMil;
            zoom = new[] { new ZoomStep(3f, 18f), new ZoomStep(10f, 5.5f) };
            lineOfSight = LineOfSight.Stabilized;
            style = ReticleStyle.Western;
            reticleColor = new Color(0.02f, 0.02f, 0.02f, 1f);
            centerGap = 0.5f; crossLength = 12f; tickSpacing = 5f; tickRange = 20f; tickLength = 1.5f;
            rangefinder = new LaserRangefinder { minRange = 200f, maxRange = 9990f, resolution = 10f, useReturn = LaserReturn.Last, digits = 4 };
            stadiaRangefinder = false; ballisticScales = false;
        }

        [ContextMenu("プリセット: ソ連・ロシア（8x 固定）")]
        public void PresetSoviet()
        {
            displayName = "砲手照準器（光学・東側）";
            angleUnit = AngleUnit.Thousandth;
            zoom = new[] { new ZoomStep(8f, 9f) };
            lineOfSight = LineOfSight.GunLinked;
            style = ReticleStyle.Soviet;
            reticleColor = new Color(0.03f, 0.03f, 0.03f, 1f);
            centerGap = 0f; chevronHeight = 4f; leadTickSpacing = 2f; leadTickCount = 8; tickLength = 1.5f;
            stadiaRangefinder = true; stadiaTargetHeight = 2.7f;
            ballisticScales = true; scaleStep = 100f; scaleLabelStep = 1000f;
            rangefinder = new LaserRangefinder { minRange = 400f, maxRange = 5000f, resolution = 10f, useReturn = LaserReturn.First, digits = 4 };
        }

        [ContextMenu("プリセット: ソ連・ロシア（2.7x〜12x 可変）")]
        public void PresetSovietZoom()
        {
            PresetSoviet();
            zoom = new[] { new ZoomStep(2.7f, 20f), new ZoomStep(4f, 13.5f), new ZoomStep(6f, 9f), new ZoomStep(8f, 6.8f), new ZoomStep(12f, 4.5f) };
            lineOfSight = LineOfSight.Stabilized;
        }

        public OpticalSightProfile() { displayName = "砲手照準器（光学）"; }
    }

    // ------------------------------------------------------------------ digital display

    public enum SensorType
    {
        [InspectorName("サーマル（熱線映像）")] Thermal,
        [InspectorName("昼間カメラ（カラー）")] DayColour,
        [InspectorName("昼間カメラ（白黒）")] DayMono,
    }

    public enum ImageFit
    {
        [InspectorName("画面に収める（余りは黒帯）")] Fit,
        [InspectorName("画面を埋める（はみ出しは切る）")] Fill,
        [InspectorName("引き伸ばす")] Stretch,
    }

    /// <summary>One camera of the digital sight (thermal, day...). Each has its own optical fields of view.</summary>
    [System.Serializable]
    public class SensorChannel
    {
        [Tooltip("表示名（例: TIS, DAY）")] public string name = "TIS";
        public SensorType type = SensorType.Thermal;
        [Tooltip("光学の視野（水平・度）。広い順に。ホイールでこれと電子ズームを順に切り替える")] public float[] fieldsOfView = { 15f, 2.8f };
        [Tooltip("視野の名前（例: WFOV, NFOV）")] public string[] fieldNames = { "WFOV", "NFOV" };
    }

    [CreateAssetMenu(menuName = "Tank Rig/照準器/デジタル射撃画面（ディスプレイ）", fileName = "DisplaySight")]
    public class DisplaySightProfile : SightProfile
    {
        [Header("画面（モニター）")]
        [Tooltip("モニターの縦横比（横, 縦）。例: 4:3, 5:4, 16:9, 16:10")] public Vector2 monitorAspect = new Vector2(4f, 3f);
        [Tooltip("モニターの高さ（画面の高さに対する割合）")] [Range(0.2f, 1f)] public float monitorHeight = 0.86f;
        [Tooltip("モニターの中心（画面の中心からのずれ。画面の高さに対する割合）")] public Vector2 monitorCenter = Vector2.zero;
        [Tooltip("枠の太さ（1080p のピクセル）")] public float bezel = 26f;
        public Color bezelColor = new Color(0.07f, 0.075f, 0.08f, 1f);
        [Tooltip("モニターの外（車内）の色")] public Color cabinColor = new Color(0.015f, 0.016f, 0.018f, 1f);
        [Tooltip("センサー映像のモニターへの合わせ方")] public ImageFit fit = ImageFit.Fit;

        [Header("センサー")]
        [Tooltip("センサーの解像度（横, 縦）。映像はこの細かさで作られる")] public Vector2Int sensorResolution = new Vector2Int(1024, 768);
        [Tooltip("画素をぼかさずに拡大する（ドットが見える）")] public bool pointFilter;
        [Tooltip("センサーの更新頻度（Hz）。0 = 毎フレーム")] public float frameRate = 30f;
        [Tooltip("カメラの種類（T で切り替え）")]
        public SensorChannel[] channels =
        {
            new SensorChannel { name = "TIS", type = SensorType.Thermal, fieldsOfView = new[] { 15f, 2.8f }, fieldNames = new[] { "WFOV", "NFOV" } },
            new SensorChannel { name = "DAY", type = SensorType.DayColour, fieldsOfView = new[] { 15f, 3.5f }, fieldNames = new[] { "WFOV", "NFOV" } },
        };
        [Tooltip("電子ズームの倍率（いちばん狭い視野のあとに続く）")] public float[] electronicZoom = { 2f, 4f };

        [Header("サーマルの映り方（疑似: 明るさから作る）")]
        [Tooltip("黒が熱い（ブラックホット）")] public bool blackHot;
        [Tooltip("ゲイン（コントラスト）")] [Range(0.2f, 5f)] public float gain = 1.5f;
        [Tooltip("レベル（明るさの中心）")] [Range(0f, 1f)] public float level = 0.45f;
        [Tooltip("ちらつくノイズ")] [Range(0f, 0.3f)] public float noise = 0.05f;
        [Tooltip("縦すじの固定ノイズ")] [Range(0f, 0.2f)] public float fixedPatternNoise = 0.015f;
        [Tooltip("走査線")] [Range(0f, 0.5f)] public float scanlines;
        [Tooltip("映像の色（白黒映像とサーマルに掛ける）")] public Color imageTint = Color.white;
        [Tooltip("周辺減光")] [Range(0f, 1f)] public float vignette = 0.2f;
        [Tooltip("映像の明るさ")] [Range(0.2f, 3f)] public float brightness = 1f;
        [Tooltip("昼間カラーの彩度")] [Range(0f, 1.5f)] public float saturation = 0.85f;

        [Header("記号")]
        public Color symbolColor = new Color(0.92f, 0.95f, 0.92f, 1f);
        [Tooltip("記号の縁取り（明るい映像の上でも読めるように）")] public Color symbolOutline = new Color(0f, 0f, 0f, 0.8f);
        [Tooltip("警告・未装填の色")] public Color warnColor = new Color(1f, 0.75f, 0.2f, 1f);
        [Tooltip("十字の中心の隙間（映像の幅に対する割合）")] public float crossGap = 0.012f;
        [Tooltip("十字の腕の長さ（映像の幅に対する割合）")] public float crossArm = 0.07f;
        [Tooltip("ミル目盛りの間隔（角度の単位）")] public float tickSpacing = 5f;
        [Tooltip("ミル目盛りの範囲（角度の単位、片側）")] public float tickRange = 20f;
        [Tooltip("追尾ゲートの大きさ（映像の幅に対する割合）。0 で出さない")] public float trackGate = 0.045f;
        [Tooltip("砲塔と車体の向きの表示")] public bool showAzimuth = true;
        [Tooltip("砲の上下の目盛り")] public bool showElevation = true;
        [Tooltip("砲が実際に向いている所に小さな印を出す")] public bool showGunMarker = true;
        [Tooltip("安定化の状態の表示（文字）")] public string stabilizationText = "STAB";

        [ContextMenu("プリセット: サーマル 4:3（1024×768）")]
        public void PresetThermal43()
        {
            displayName = "砲手ディスプレイ";
            angleUnit = AngleUnit.NatoMil;
            monitorAspect = new Vector2(4f, 3f); sensorResolution = new Vector2Int(1024, 768); fit = ImageFit.Fit;
            frameRate = 30f;
            electronicZoom = new[] { 2f, 4f };
            rangefinder = new LaserRangefinder { minRange = 200f, maxRange = 9990f, resolution = 5f, useReturn = LaserReturn.Last, digits = 4 };
        }

        [ContextMenu("プリセット: ワイド 16:9（1920×1080）")]
        public void PresetWide()
        {
            PresetThermal43();
            monitorAspect = new Vector2(16f, 9f); sensorResolution = new Vector2Int(1920, 1080); monitorHeight = 0.78f;
        }

        public DisplaySightProfile()
        {
            displayName = "砲手ディスプレイ";
            lineWidth = 2.2f;
            rangefinder = new LaserRangefinder { minRange = 200f, maxRange = 9990f, resolution = 5f, useReturn = LaserReturn.Last, digits = 4 };
        }
    }

    // ------------------------------------------------------------------ helmet (VR goggles)

    [CreateAssetMenu(menuName = "Tank Rig/照準器/全周デジタル射撃画面（VR ゴーグル）", fileName = "HelmetSight")]
    public class HelmetSightProfile : SightProfile
    {
        [Header("ゴーグル")]
        [Tooltip("ゴーグルの表示視野（横, 縦・度）。記号はこの中に出る")] public Vector2 displayFov = new Vector2(40f, 30f);
        [Tooltip("VR を使わないとき（画面で見るとき）のカメラの縦の視野（度）")] public float desktopFov = 60f;
        [Tooltip("車体を透かして外を見る（自分の戦車を消す。影は残る）")] public bool seeThroughArmor = true;
        [Tooltip("外部カメラが映せる下限（度、車体の水平から）。これより下を見ると車体が見える")] public float coverageDown = -20f;
        [Tooltip("外部カメラが映せる上限（度）")] public float coverageUp = 40f;
        [Tooltip("首を回せる範囲（左右・度）。180 で全周")] [Range(30f, 180f)] public float headYawLimit = 180f;
        [Tooltip("首を上下に振れる範囲（度）")] public Vector2 headPitchLimit = new Vector2(-60f, 75f);
        [Tooltip("頭の位置（砲塔の原点からのずれ・m）。0,0,0 なら砲塔の上面から自動で決める")] public Vector3 headOffset = Vector3.zero;

        [Header("照準")]
        [Tooltip("指定した目標がないとき、主砲が視線を追う")] public bool gunFollowsHead = true;
        [Tooltip("目標マーカーを出す最大距離（m）")] public float markerRange = 4000f;

        [Header("記号")]
        public Color symbolColor = new Color(0.35f, 1f, 0.55f, 1f);
        public Color symbolOutline = new Color(0f, 0.08f, 0.02f, 0.6f);
        public Color threatColor = new Color(1f, 0.35f, 0.25f, 1f);
        [Tooltip("方位テープの幅（度、片側）")] public float headingSpan = 30f;
        [Tooltip("方位テープの目盛り（度）")] public float headingTick = 5f;
        [Tooltip("方位テープの数字の間隔（度）")] public float headingLabel = 30f;
        [Tooltip("水平線を出す")] public bool showHorizon = true;
        [Tooltip("主砲の向きの印を出す")] public bool showGunLine = true;
        [Tooltip("車体の外形の線を出す（透かしているとき）")] public bool showHullOutline = true;
        [Tooltip("ミニマップ（周囲の目標）を出す")] public bool showMinimap = true;
        [Tooltip("ミニマップの距離の輪（m）")] public float[] rangeRings = { 500f, 1000f, 2000f };
        [Tooltip("ミニマップの大きさ（1080p のピクセル、半径）")] public float minimapRadius = 90f;

        // a constructor, not Reset(): it also gives instances made with CreateInstance (no asset) these defaults
        public HelmetSightProfile()
        {
            displayName = "全周ゴーグル";
            angleUnit = AngleUnit.Degree;
            nearClip = 0.1f;
            rangefinder = new LaserRangefinder { minRange = 50f, maxRange = 9990f, resolution = 5f, useReturn = LaserReturn.Last, digits = 4 };
        }
    }
}
