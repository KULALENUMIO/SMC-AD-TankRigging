using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Sights
{
    /// <summary>
    /// Switches the tank camera between the third-person view and the sights (V), and runs what every sight shares:
    /// cursor, laser rangefinder (R), ammunition (1–4), firing shake, the symbology overlay and the main gun's aim.
    /// Put it on the camera that has the <see cref="TankCamera"/>, together with the sights.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(-50)]
    [AddComponentMenu("Tank Rig/Sights/Tank Sight Rig")]
    public class TankSightRig : MonoBehaviour, ITankAimSource
    {
        public TankController tank;
        [Tooltip("三人称カメラ（なくても可。同じオブジェクトのものを自動で使う）")] public TankCamera thirdPerson;
        [Tooltip("V で順に切り替える照準器。空ならこのオブジェクトに付いているもの")] public TankSight[] sights = new TankSight[0];
        [Tooltip("射撃統制装置が知っている弾種（1〜4 キーで選ぶ）")] public AmmoType[] ammo = AmmoType.DefaultWestern();
        public int ammoIndex;
        [Tooltip("開始時の視点（-1 = 三人称、0〜 = 照準器の番号）")] public int startSight = -1;
        [Tooltip("照準器のとき、操作の説明を出す")] public bool showHelp = true;
        [Tooltip("テスト用: 記号を視線の向きで重ねる（VR と同じ道）")] public bool forceAngularOverlay;

        public int Active { get; private set; } = -1;
        public TankSight Current => Active >= 0 && Active < list.Count ? list[Active] : null;
        public IReadOnlyList<TankSight> Sights => list;
        public AmmoType Ammo => ammo != null && ammo.Length > 0 ? ammo[Mathf.Clamp(ammoIndex, 0, ammo.Length - 1)] : null;

        /// <summary>Last laser range in metres (0 = no return).</summary>
        public float LaserRange { get; private set; }
        /// <summary>The last laser shot saw more than one return.</summary>
        public bool LaserMultiple { get; private set; }
        /// <summary>Seconds since the last laser shot.</summary>
        public float LaserAge { get; private set; } = 999f;
        public bool LaserReady => laserCooldown <= 0f;
        /// <summary>Range set by hand (sights with one magnification: mouse wheel).</summary>
        public float ManualRange { get; set; } = 1000f;
        /// <summary>Range the fire control uses: the laser, else the hand-set range.</summary>
        public float FireControlRange => LaserRange > 0f ? LaserRange : ManualRange;
        public float Shake { get; private set; }

        readonly List<TankSight> list = new List<TankSight>();
        readonly SightPainter painter = new SightPainter();
        Camera cam;
        float savedFov, savedNear;
        int savedMask;
        bool saved;
        float laserCooldown;
        Vector2 shakeDir;
        CustomPassVolume volume;
        readonly List<Renderer> hidden = new List<Renderer>();
        readonly List<ShadowCastingMode> hiddenModes = new List<ShadowCastingMode>();

        public Camera Camera => cam ? cam : cam = GetComponent<Camera>();

        void Awake()
        {
            cam = GetComponent<Camera>();
            if (!thirdPerson) thirdPerson = GetComponent<TankCamera>();
            if (!tank && thirdPerson) tank = thirdPerson.target;
            if (!tank) tank = FindFirstObjectByType<TankController>();
            Collect();
        }

        void Collect()
        {
            list.Clear();
            if (sights != null) foreach (var s in sights) if (s && !list.Contains(s)) list.Add(s);
            if (list.Count == 0) list.AddRange(GetComponents<TankSight>());
            foreach (var s in list) s.Bind(this);
        }

        void OnEnable()
        {
            if (tank)
            {
                tank.GunFired += OnGunFired;
            }
            EnsureVolume();
        }

        void Start()
        {
            if (startSight >= 0) Switch(startSight);
        }

        void OnDisable()
        {
            if (Active >= 0) Switch(-1);
            if (tank)
            {
                if (ReferenceEquals(tank.aimSource, this)) tank.aimSource = null;
                tank.GunFired -= OnGunFired;
            }
        }

        void OnDestroy()
        {
            SightPass.Release(cam);
        }

        void EnsureVolume()
        {
            if (volume) return;
            volume = GetComponent<CustomPassVolume>();
            if (!volume)
            {
                volume = gameObject.AddComponent<CustomPassVolume>();
                volume.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
            }
            volume.isGlobal = true;
            volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
            volume.targetCamera = Camera;
            bool has = false;
            foreach (var p in volume.customPasses) if (p is SightPass) has = true;
            if (!has) volume.customPasses.Add(new SightPass { name = "Tank Sight" });
        }

        void OnGunFired(TankController c, TankController.GunShot shot)
        {
            var p = Current ? Current.BaseProfile : null;
            float k = p ? p.firingShake : 1f;
            if (shot.secondary != null) k *= 0.3f;
            Shake = Mathf.Max(Shake, k * Mathf.Clamp(shot.power, 0.3f, 2f));
            shakeDir = Random.insideUnitCircle.normalized;
        }

        // ------------------------------------------------------------------ switching

        /// <summary>Switch to sight <paramref name="index"/> (-1 = the third-person camera).</summary>
        public void Switch(int index)
        {
            if (list.Count == 0) Collect();
            if (index >= list.Count) index = -1;
            if (index == Active) return;
            var c = Camera;
            Vector3 aim = tank ? tank.AimPoint : c.transform.position + c.transform.forward * 1000f;
            if (Current) Current.Exit(c);
            if (Active < 0 && index >= 0)
            {
                savedFov = c.fieldOfView; savedNear = c.nearClipPlane; savedMask = c.cullingMask; saved = true;
                if (thirdPerson) thirdPerson.enabled = false;
            }
            Active = index;
            if (index < 0)
            {
                var f = SightPass.FrameFor(c);
                f.visible = false;
                SetOwnTankHidden(false);
                if (saved) { c.fieldOfView = savedFov; c.nearClipPlane = savedNear; c.cullingMask = savedMask; saved = false; }
                if (tank) tank.aimSource = null;
                if (thirdPerson)
                {
                    thirdPerson.LookAlong(aim - thirdPerson.transform.position);
                    thirdPerson.enabled = true;
                }
            }
            else
            {
                EnsureVolume();
                if (tank) tank.aimSource = this;
                c.cullingMask = savedMask;
                list[index].Enter(c, aim);
            }
            LockCursor();
        }

        public void Next() => Switch(Active + 1 >= list.Count ? -1 : Active + 1);

        static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            float dt = Time.deltaTime;
            laserCooldown = Mathf.Max(0f, laserCooldown - dt);
            LaserAge += dt;
            Shake = Mathf.MoveTowards(Shake, 0f, dt * 2.5f);
            var input = TankInput.Read();
            if (input.cycleView) Next();
            var s = Current;
            if (!s) return;

            if (input.unlockCursor) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else if (Cursor.lockState != CursorLockMode.Locked && input.click) LockCursor();
            if (Cursor.lockState != CursorLockMode.Locked) { input.look = Vector2.zero; input.scroll = 0f; }

            if (input.ammoSelect > 0 && ammo != null && input.ammoSelect <= ammo.Length) ammoIndex = input.ammoSelect - 1;
            if (input.lase) Lase();
            s.Steer(input, dt);
        }

        void LateUpdate()
        {
            var s = Current;
            var c = Camera;
            if (!s || !c) return;
            s.PlaceCamera(c, Time.deltaTime);
            if (Shake > 0f)
            {
                // a jolt of the whole view, in proportion to the field of view (so it reads the same at any zoom)
                float a = Shake * Shake * c.fieldOfView * 0.05f;
                var j = shakeDir * a + Random.insideUnitCircle * (a * 0.3f);
                c.transform.rotation = c.transform.rotation * Quaternion.Euler(-j.y, j.x, 0f);
            }

            var f = SightPass.FrameFor(c);
            s.SetupFrame(f, c);
            painter.Begin(f.width, f.height);
            s.Paint(painter, f, c);
            if (showHelp) PaintHelp(painter, s);
            painter.Apply(f.mesh);
            f.visible = true;
        }

        void PaintHelp(SightPainter p, TankSight s)
        {
            string keys = "V: 視点   ホイール: 倍率   R: レーザー測距   1〜4: 弾種   " + s.HelpKeys + "   Esc: カーソル解放";
            p.Text(keys, new Vector2(-p.HalfWidth + 16f, SightPainter.Height * 0.5f - 18f), 15f, new Color(1f, 1f, 1f, 0.75f), new Vector2(-1f, 1f), new Color(0f, 0f, 0f, 0.6f));
            p.Text(s.DisplayName, new Vector2(-p.HalfWidth + 16f, SightPainter.Height * 0.5f - 40f), 15f, new Color(1f, 1f, 1f, 0.75f), new Vector2(-1f, 1f), new Color(0f, 0f, 0f, 0.6f));
        }

        // ------------------------------------------------------------------ aim

        bool ITankAimSource.HasAim => TryAim(out _);
        Vector3 ITankAimSource.AimPoint => TryAim(out var p) ? p : tank ? tank.transform.position + tank.transform.forward * 100f : Vector3.zero;

        bool TryAim(out Vector3 p)
        {
            p = default;
            var s = Current;
            return s && s.TryGetAim(out p);
        }

        /// <summary>Where a ray from <paramref name="origin"/> along <paramref name="dir"/> meets the world (or a far point).</summary>
        public Vector3 AimAlong(Vector3 origin, Vector3 dir, float range)
        {
            Transform ignore = tank ? tank.transform : null;
            return TankPhysics.Raycast(origin, dir, range, ~0, ignore, out var hit) ? hit.point : origin + dir * range;
        }

        // ------------------------------------------------------------------ laser

        static readonly RaycastHit[] hits = new RaycastHit[32];

        /// <summary>Fire the laser rangefinder along the current sight's line of sight.</summary>
        public void Lase()
        {
            var s = Current;
            if (!s || laserCooldown > 0f) return;
            var lrf = s.BaseProfile ? s.BaseProfile.rangefinder : new LaserRangefinder();
            if (lrf == null || !lrf.enabled) return;
            laserCooldown = Mathf.Max(0.05f, lrf.cycleTime);
            LaserAge = 0f;
            Ray r = s.LaserRay;
            int n = Physics.RaycastNonAlloc(r, hits, lrf.maxRange, ~0, QueryTriggerInteraction.Ignore);
            float first = float.MaxValue, last = -1f;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (tank && h.collider.transform.IsChildOf(tank.transform)) continue;
                if (h.distance < lrf.minRange) continue;
                count++;
                first = Mathf.Min(first, h.distance);
                last = Mathf.Max(last, h.distance);
            }
            if (count == 0) { LaserRange = 0f; LaserMultiple = false; return; }
            float d = lrf.useReturn == LaserReturn.First ? first : last;
            float step = Mathf.Max(0.1f, lrf.resolution);
            LaserRange = Mathf.Round(d / step) * step;
            LaserMultiple = count > 1 && last - first > step;
        }

        // ------------------------------------------------------------------ tank state for the symbology

        /// <summary>Turret bearing relative to the hull (degrees, + = right).</summary>
        public float TurretAzimuth
        {
            get
            {
                if (!tank || !tank.turret) return 0f;
                Transform hull = tank.transform;
                Vector3 up = hull.up;
                Vector3 a = Vector3.ProjectOnPlane(hull.forward, up), b = Vector3.ProjectOnPlane(GunDirection, up);
                return a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f ? 0f : Vector3.SignedAngle(a, b, up);
            }
        }

        /// <summary>Gun elevation above the hull's plane (degrees).</summary>
        public float GunElevation
        {
            get
            {
                if (!tank) return 0f;
                return 90f - Vector3.Angle(tank.transform.up, GunDirection);
            }
        }

        public Vector3 GunDirection
        {
            get
            {
                if (!tank) return transform.forward;
                Transform m = tank.muzzle ? tank.muzzle : tank.gun ? tank.gun : tank.transform;
                return m.forward;
            }
        }

        public Vector3 GunOrigin
        {
            get
            {
                if (!tank) return transform.position;
                Transform m = tank.muzzle ? tank.muzzle : tank.gun ? tank.gun : tank.transform;
                return m.position;
            }
        }

        public bool Ready => tank && tank.Reload01 >= 1f;

        /// <summary>
        /// Turret axes: forward = the gun's bearing on the hull's plane (or the gun itself when <paramref name="withElevation"/>),
        /// up = the tank's up. The model's own turret / hull transforms can have any axes (imported meshes), the tank root can't.
        /// </summary>
        public Quaternion TurretFrame(bool withElevation)
        {
            Transform root = tank ? tank.transform : transform;
            Vector3 up = root.up;
            Vector3 f = withElevation ? GunDirection : Vector3.ProjectOnPlane(GunDirection, up);
            if (f.sqrMagnitude < 1e-6f) f = root.forward;
            return Quaternion.LookRotation(f, up);
        }

        /// <summary>Compass bearing of a direction (degrees, 0 = +Z "north", clockwise).</summary>
        public static float Bearing(Vector3 dir) => Mathf.Repeat(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, 360f);

        // ------------------------------------------------------------------ see-through armour

        /// <summary>Hide the own tank from the camera (its shadow stays).</summary>
        public void SetOwnTankHidden(bool hide)
        {
            if (hide == hidden.Count > 0) return;
            if (hide)
            {
                if (!tank) return;
                foreach (var r in tank.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    hidden.Add(r);
                    hiddenModes.Add(r.shadowCastingMode);
                    if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    else r.forceRenderingOff = true;
                }
            }
            else
            {
                for (int i = 0; i < hidden.Count; i++)
                {
                    if (!hidden[i]) continue;
                    hidden[i].shadowCastingMode = hiddenModes[i];
                    hidden[i].forceRenderingOff = false;
                }
                hidden.Clear();
                hiddenModes.Clear();
            }
        }
    }
}
