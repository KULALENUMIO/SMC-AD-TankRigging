using UnityEngine;

namespace TankRig
{
    /// <summary>
    /// Drivable tank. Two drive modes:
    ///  - Physics (needs a Rigidbody): only the hull is simulated. Each road wheel casts one ray; its torsion bar pushes the hull
    ///    as a spring-damper sized so the model's wheel positions are the static ride height. Each track side pushes forward at its
    ///    contacts (limited by grip x load), so slopes, steps, recoil and skid steering come out of the physics. The tracks only render.
    ///  - Kinematic: no rigidbody; 4 ground probes give height and tilt (cheap, e.g. for many AI tanks).
    /// The turret and gun turn towards the camera's aim point at limited speeds, left click fires the main gun, Space the machine gun.
    /// </summary>
    [AddComponentMenu("Tank Rig/Tank Controller")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)] // move the hull before the tracks let their wheels find the ground
    public partial class TankController : MonoBehaviour
    {
        [Header("部位")]
        public Transform hull;
        public Transform turret;
        [Tooltip("砲座: 上下（俯仰）に動く部分")] public Transform gun;
        [Tooltip("砲身（なくても可）: 発砲時に後退する部分（砲座の子）。ないときは後退しない")] public Transform barrel;
        [Tooltip("砲塔の旋回軸（砲塔のローカル）")] public Vector3 turretAxisLocal = Vector3.up;
        [Tooltip("砲座の俯仰軸（砲座のローカル）")] public Vector3 gunAxisLocal = Vector3.right;
        [Tooltip("初期姿勢での砲身の向き（砲塔の親の空間）")] public Vector3 turretRestForward = Vector3.forward;
        [Tooltip("初期姿勢での砲身の向き（砲座の親の空間）")] public Vector3 gunRestForward = Vector3.forward;
        [Tooltip("砲身の向き（砲座の空間）。後退はこの逆向き")] public Vector3 barrelForward = Vector3.forward;
        public TankTrack leftTrack;
        public TankTrack rightTrack;

        [Header("ポイント")]
        public Transform muzzle;
        [Tooltip("旧形式（v0.6 まで）の機銃。machineGuns が空のときだけ使う")] [HideInInspector] public Transform machineGun;
        public Transform[] exhausts = new Transform[0];
        public Transform[] headlights = new Transform[0];
        public Transform[] rearLights = new Transform[0];

        [Header("走行")]
        public float maxForwardSpeed = 11f;
        public float maxReverseSpeed = 4f;
        public float acceleration = 5f;
        public float braking = 12f;
        [Tooltip("旋回速度（度/秒）")] public float turnRate = 38f;
        [Tooltip("ルートから見た接地面の高さ（履帯の底）")] public float groundOffset;
        public float halfTrackWidth = 1.5f;
        public float halfTrackLength = 3f;
        public LayerMask groundMask = ~0;
        public float suspensionSmoothing = 8f;
        public float gravity = 20f;

        public enum DriveMode { Kinematic, Physics }

        [Header("物理演算（車体のみ。履帯は描画だけ）")]
        [Tooltip("Physics: Rigidbody で車体を物理演算 / Kinematic: 物理なしで地面に沿わせる")] public DriveMode driveMode = DriveMode.Physics;
        [Tooltip("前後方向の摩擦係数（登坂力の上限）")] public float tractionGrip = 1.0f;
        [Tooltip("横方向の摩擦係数（横滑りのしにくさ）")] public float lateralGrip = 1.1f;
        [Tooltip("エンジンの最大牽引力（車重に対する比率）")] public float maxTractiveEffort = 0.7f;
        [Tooltip("トーションバーの減衰比")] public float suspensionDamping = 0.45f;
        [Tooltip("縮み切ったときのバンプストップの硬さ（バネに対する倍率）")] public float bumpStopStiffness = 6f;
        [Tooltip("目標速度へ追従する速さ（前後）")] public float driveResponse = 6f;
        [Tooltip("旋回の効きの速さ（A / D を離すとすぐ止まる）")] public float turnResponse = 10f;
        [Tooltip("転輪が押し上げられる速さの上限（m/s）。障害物の角での跳ね上がりを防ぐ")] public float wheelRiseSpeed = 5f;
        [Tooltip("縮み切った転輪の上に障害物が来たとき、車体のその角を持ち上げる速さの上限（m/s）")] public float bumpLiftSpeed = 4f;
        [Tooltip("エンジン出力（kW / t）。坂道や高速域で牽引力が出力で頭打ちになる")] public float powerPerTon = 25f;
        [Tooltip("ダンパーに使う速度の上限（m/s）")] public float maxDamperSpeed = 1.5f;
        [Tooltip("1 輪が支える力の上限（静止荷重の何倍か）")] public float maxWheelLoad = 5f;
        [Tooltip("接地中（と離れた直後）の上向き速度の上限（m/s）")] public float maxRiseSpeed = 2.5f;
        [Tooltip("接地中（と離れた直後）の前後・左右に傾く速さの上限（rad/s）")] public float maxTiltRate = 1.6f;
        [Tooltip("横滑りを止める速さ")] public float lateralResponse = 6f;
        [Tooltip("発砲の反動（N·s）")] public float recoilImpulse = 35000f;
        [Tooltip("段差・丸太に当たった履帯がベルトで車体を引き上げる力（当たる力に対する比率）")] public float climbGrip = 0.9f;

        [Header("サスペンション（車体の揺れ）")]
        [Tooltip("車体の揺れの強さ（0 で揺れない）。旋回時の傾きは Physics モードでも使う")] [Range(0f, 3f)] public float bodySway = 1f;
        [Tooltip("加速・減速 1 m/s² あたりの前後の傾き（度。Kinematic モード）")] public float pitchPerAccel = 0.5f;
        [Tooltip("旋回時、横方向の加速度 1 m/s² あたりに外側へ傾く角度（度）。Physics / Kinematic 両方")] public float rollPerLateral = 0.5f;
        [Tooltip("旋回時の傾きの上限（度）")] public float maxTurnLean = 6f;
        public float swayStiffness = 55f;
        public float swayDamping = 9f;
        [Tooltip("着地したときの沈み込みの強さ")] public float landingBounce = 0.5f;
        public float bounceStiffness = 80f;
        public float bounceDamping = 11f;
        [Tooltip("沈み込みの上限（m）")] public float maxBounce = 0.15f;

        [Header("砲塔・主砲")]
        public float traverseSpeed = 30f;
        public float elevationSpeed = 18f;
        public float minElevation = -8f;
        public float maxElevation = 20f;

        [Header("武装")]
        public float reloadTime = 3f;
        [Tooltip("主砲火力（1 = 標準）。車体への反動・のけぞり、砲身の後退量、カメラの揺れ、発砲炎と煙の大きさがこれに比例する")]
        [Range(0.1f, 4f)] public float gunPower = 1f;
        [Tooltip("火力 1 のときの砲身の後退量（m）")] public float recoilDistance = 0.35f;
        [Tooltip("砲身の後退量の上限（m）")] public float maxRecoilDistance = 0.8f;
        [Tooltip("砲身が元の位置に戻る速さ")] public float recoilReturn = 3.5f;
        [HideInInspector] public float machineGunInterval = 0.08f;
        [Tooltip("主砲の射程（m）")] public float range = 1500f;
        [Tooltip("主砲の発砲光の明るさ（ルーメン、HDRP）")] public float muzzleLightIntensity = 800000f;

        [Header("機銃（いくつでも）")]
        public TankMachineGun[] machineGuns = new TankMachineGun[0];

        [Header("副砲塔（いくつでも。砲塔・砲架・砲身はどれもなくて可）")]
        public TankSecondaryTurret[] secondaryTurrets = new TankSecondaryTurret[0];

        [Header("エフェクト")]
        [Tooltip("組み込みのエフェクト（発砲炎・煙・光・曳光弾・着弾・排気煙）を使う。別のエフェクト（GunFired / RoundFired を使うもの）に任せるときはオフ")]
        public bool builtInEffects = true;
        public ParticleSystem[] exhaustSmoke = new ParticleSystem[0];
        /// <summary>Set by a replacement effect (TankFx): the built-in machine-gun / secondary-gun flashes stay off. Not serialized.</summary>
        [System.NonSerialized] public bool externalGunFlashes;
        /// <summary>Set by a replacement effect (TankFx): the built-in exhaust smoke stays off. Not serialized.</summary>
        [System.NonSerialized] public bool externalExhaust;
        /// <summary>Set by a replacement effect (TankFx): the built-in main-gun flash, smoke and light stay off. Not serialized.</summary>
        [System.NonSerialized] public bool externalMainGun;
        public ParticleSystem muzzleFlash;
        public ParticleSystem muzzleSmoke;
        [HideInInspector] public ParticleSystem machineGunTracer;
        [HideInInspector] public ParticleSystem machineGunFlash;
        public ParticleSystem impactDust;
        public ParticleSystem impactSparks;
        public Light muzzleLight;
        public Light[] headlightLights = new Light[0];
        public Light[] rearLightLights = new Light[0];
        public float exhaustIdleRate = 5f;
        public float exhaustMaxRate = 28f;
        public bool lightsOn;

        [Header("カメラ")]
        public TankCamera aimCamera;

        /// <summary>Overrides where the turret and gun aim (sights). Not serialized; set at run time.</summary>
        [System.NonSerialized] public ITankAimSource aimSource;

        [Header("外部入力（AI・ネットワーク・テスト用）")]
        [Tooltip("オンにするとキーボード・マウスの代わりに externalInput で操縦する")] public bool useExternalInput;
        public TankInputState externalInput;
        [Tooltip("外部入力のときの照準点（ワールド）。カメラがない場合や外部入力時に使う")] public Vector3 externalAimPoint;

        public float Speed { get; private set; }
        /// <summary>This frame's input (keyboard / mouse or externalInput). For effects that follow the driver (engine load...).</summary>
        public TankInputState CurrentInput => lastInput;
        public Vector3 AimPoint { get; private set; }
        public float Reload01 => reloadTime <= 0f ? 1f : 1f - Mathf.Clamp01(reloadTimer / reloadTime);

        Quaternion turretRest = Quaternion.identity, gunRest = Quaternion.identity;
        Vector3 barrelRestPos;          // child of the mount: local position at rest
        bool barrelFollows;             // not a child of the mount: carried along with it from here
        Vector3 barrelRelPos;
        Quaternion barrelRelRot = Quaternion.identity;
        float yaw, turretAngle, gunAngle, elevSign = 1f, vy, reloadTimer, recoil, flash;
        Vector3 surfaceUp = Vector3.up;
        Vector3 basePos;
        float lastSpeed, swayPitch, swayPitchVel, swayRoll, swayRollVel, bounce, bounceVel;
        bool isBraking;
        float[] rearBase;
        readonly Vector3[] probeHits = new Vector3[4];

        // physics
        Rigidbody rb;
        bool physics;
        TankInputState lastInput;
        readonly TankTrack[] sides = new TankTrack[2];
        readonly Matrix4x4[] trackToRoot = new Matrix4x4[2];
        readonly float[][] prevTravel = new float[2][];
        int suspendedWheels;
        float commandSpeed;
        float climbMaxLocalY = 1f;
        float climbForceSum, sinceGrounded;
        Vector3 climbDirSum, climbPointSum;
        // this step's wheel contacts (for the drive forces)
        readonly Vector3[] contactPoint = new Vector3[64], contactNormal = new Vector3[64];
        readonly float[] contactLoad = new float[64];
        readonly int[] contactSide = new int[64];
        int contactN;
        readonly Vector3[] sidePoint = new Vector3[2];
        readonly float[] sideLoad = new float[2];
        readonly int[] sideCount = new int[2];

        Vector4 debugDrive;
        /// <summary>Last drive forces and their limits (N): left, right, left limit, right limit. For tuning / tests.</summary>
        public Vector4 DebugDrive => debugDrive;

        /// <summary>Number of road wheels touching the ground (physics mode).</summary>
        public int GroundedWheels { get; private set; }
        public Rigidbody Body => rb;

        void Awake()
        {
            if (turret) turretRest = turret.localRotation;
            if (gun) gunRest = gun.localRotation;
            if (barrel)
            {
                barrelRestPos = barrel.localPosition;
                barrelFollows = gun && !barrel.IsChildOf(gun) && !gun.IsChildOf(barrel);
                if (barrelFollows)
                {
                    barrelRelPos = gun.InverseTransformPoint(barrel.position);
                    barrelRelRot = Quaternion.Inverse(gun.rotation) * barrel.rotation;
                }
            }
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            yaw = f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f).eulerAngles.y : 0f;
            surfaceUp = transform.up;
            basePos = transform.position;
            elevSign = ComputeElevationSign();
            if (muzzleLight) muzzleLight.intensity = 0f;
            // bigger gun, bigger flash and smoke (the systems scale with their transform)
            float fxScale = Mathf.Sqrt(Mathf.Max(0.05f, gunPower));
            if (muzzleFlash) muzzleFlash.transform.localScale = Vector3.one * fxScale;
            if (muzzleSmoke) muzzleSmoke.transform.localScale = Vector3.one * fxScale;
            if (rearLightLights != null)
            {
                rearBase = new float[rearLightLights.Length];
                for (int i = 0; i < rearLightLights.Length; i++) rearBase[i] = rearLightLights[i] ? rearLightLights[i].intensity : 0f;
            }
            SetLights(lightsOn);
            InitWeapons();
        }

        // after every track's OnEnable has built its suspension arms
        void Start() => SetupPhysics();

        void SetupPhysics()
        {
            rb = GetComponent<Rigidbody>();
            sides[0] = leftTrack;
            sides[1] = rightTrack;
            suspendedWheels = 0;
            for (int s = 0; s < 2; s++)
            {
                var t = sides[s];
                if (!t) continue;
                for (int i = 0; i < t.ArmCount; i++) if (t.IsSuspended(i)) suspendedWheels++;
            }
            physics = driveMode == DriveMode.Physics && rb != null;
            if (physics && suspendedWheels == 0)
            {
                // the physics drive pushes through the road wheels' torsion bars: without tracks / suspension it could not move
                Debug.LogWarning($"[TankRig] {name}: 履帯（サスペンション付きの転輪）がないので、物理演算なし（Kinematic）で走ります。", this);
                physics = false;
            }
            for (int s = 0; s < 2; s++)
            {
                var t = sides[s];
                if (!t) continue;
                t.ignoreRoot = transform;
                t.externalSuspension = physics;
                trackToRoot[s] = transform.worldToLocalMatrix * t.transform.localToWorldMatrix;
                prevTravel[s] = new float[t.ArmCount];
            }
            if (rb)
            {
                rb.isKinematic = !physics;
                if (physics)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.maxDepenetrationVelocity = 2f; // default 10 m/s launches the hull out of obstacle edges
                }
                else rb.interpolation = RigidbodyInterpolation.None; // kinematic mode writes the transform itself every frame
            }

            // top of the tracks (root space): contacts below it can be climbed by the belt
            climbMaxLocalY = groundOffset + 1f;
            foreach (var t in sides)
            {
                if (!t) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>())
                {
                    var b = r.bounds;
                    climbMaxLocalY = Mathf.Max(climbMaxLocalY, transform.InverseTransformPoint(b.center + Vector3.up * b.extents.y).y);
                }
            }
        }

        float ComputeElevationSign()
        {
            if (!gun || !gun.parent) return 1f;
            Vector3 axisW = gun.parent.TransformDirection(gunRest * gunAxisLocal);
            Vector3 fwdW = gun.parent.TransformDirection(gunRestForward);
            Vector3 raised = Quaternion.AngleAxis(10f, axisW) * fwdW;
            return Vector3.Dot(raised, transform.up) >= Vector3.Dot(fwdW, transform.up) ? 1f : -1f;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            TankInputState input;
            if (useExternalInput)
            {
                input = externalInput;
                externalInput.fireMain = false; // one shot per request
                externalInput.toggleLights = false;
            }
            else
            {
                input = TankInput.Read();
                // with the third-person camera, a click first captures the mouse; it should not fire as well
                if (aimCamera && Cursor.lockState != CursorLockMode.Locked)
                {
                    input.fireMain = false;
                    input.holdMain = false;
                    input.fireMachineGun = false;
                    input.fireSecondary = false;
                }
            }
            lastInput = input;
            if (!physics) Drive(input, dt);
            Aim(dt);
            Weapons(input, dt);
            UpdateEffects(input, dt);
        }

        void FixedUpdate()
        {
            if (physics) PhysicsDrive(lastInput, Time.fixedDeltaTime);
        }

        /// <summary>
        /// The track colliders do not turn, but a real belt pressed against a step or a log grips it and pulls the hull up.
        /// Contacts below the top of the tracks that face the driving direction are only collected here (there can be many
        /// per step); <see cref="ApplyClimb"/> turns them into one limited force in the next physics step.
        /// </summary>
        void OnCollisionStay(Collision collision)
        {
            if (!physics || Mathf.Abs(commandSpeed) < 0.2f) return;
            float dt = Time.fixedDeltaTime;
            Quaternion rot = rb.rotation;
            Vector3 up = rot * Vector3.up, fwd = rot * Vector3.forward;
            float dir = Mathf.Sign(commandSpeed);
            for (int i = 0; i < collision.contactCount; i++)
            {
                var cp = collision.GetContact(i);
                // anything of ours touching below the top of the tracks (the belt / the lower glacis just above it)
                if (transform.InverseTransformPoint(cp.point).y > climbMaxLocalY) continue;
                Vector3 n = cp.normal; // pushes the tank away from the obstacle
                if (Mathf.Abs(Vector3.Dot(n, up)) > 0.75f) continue;       // resting on top of it: nothing to climb
                if (-Vector3.Dot(n, fwd) * dir < 0.2f) continue;            // not in the driving direction
                Vector3 t = Vector3.ProjectOnPlane(up, n);
                if (t.sqrMagnitude < 1e-4f) continue;
                float normalForce = cp.impulse.magnitude / dt;
                climbForceSum += normalForce;
                climbDirSum += t.normalized * normalForce;
                climbPointSum += cp.point * normalForce;
            }
        }

        /// <summary>One climbing force per step: grip x contact force, at most part of the engine, fading out as the hull rises fast.</summary>
        void ApplyClimb(float engineTotal)
        {
            if (climbForceSum > 1f)
            {
                float rise = Vector3.Dot(rb.linearVelocity, Vector3.up);
                float fade = Mathf.Clamp01(1f - (rise - 0.5f) / 1f); // full below 0.5 m/s up, none above 1.5 m/s
                float force = Mathf.Min(climbGrip * climbForceSum, engineTotal * 0.8f) * fade;
                if (force > 0f)
                    rb.AddForceAtPosition(climbDirSum.normalized * force, climbPointSum / climbForceSum);
            }
            climbForceSum = 0f;
            climbDirSum = climbPointSum = Vector3.zero;
        }

        // ------------------------------------------------------------------ physics driving

        void PhysicsDrive(TankInputState input, float dt)
        {
            float mass = rb.mass;
            float g = -Physics.gravity.y;
            // normalized: after hard impulses PhysX can hand back a rotation slightly off unit length, which TRS rejects
            Quaternion rot = rb.rotation.normalized;
            Vector3 up = rot * Vector3.up;
            var rootToWorld = Matrix4x4.TRS(rb.position, rot, Vector3.one);
            float engine = maxTractiveEffort * mass * g * 0.5f; // per side
            ApplyClimb(engine * 2f);

            // torsion bars: one ray per road wheel, spring-damper sized so the model's wheel position is the static ride height
            Vector3 normalSum = Vector3.zero, pointAll = Vector3.zero;
            float loadAll = 0f;
            int contactsAll = 0;
            for (int s = 0; s < 2; s++) { sidePoint[s] = Vector3.zero; sideLoad[s] = 0f; sideCount[s] = 0; }
            contactN = 0;
            float perWheelMass = mass / Mathf.Max(1, suspendedWheels);
            float rollStiffness = 0f; // N·m/rad of the loaded torsion bars, for the turn lean
            Vector3 hullRight = rot * Vector3.right, com = rb.worldCenterOfMass;
            for (int s = 0; s < 2; s++)
            {
                var t = sides[s];
                if (!t) continue;
                if (prevTravel[s] == null || prevTravel[s].Length != t.ArmCount) prevTravel[s] = new float[t.ArmCount];
                var m = rootToWorld * trackToRoot[s];
                for (int i = 0; i < t.ArmCount; i++)
                {
                    if (t.IsEndWheel(i))
                    {
                        EndWheelContact(t, i, m, s, perWheelMass, g, engine, dt);
                        continue;
                    }
                    if (!t.IsSuspended(i)) continue;
                    if (!t.Probe(i, m, out var c))
                    {
                        prevTravel[s][i] = 0f;
                        continue;
                    }
                    float k = perWheelMass * g / Mathf.Max(0.01f, c.restTravel);
                    float damper = 2f * suspensionDamping * Mathf.Sqrt(k * perWheelMass);
                    float side = Vector3.Dot(c.point - com, hullRight);
                    rollStiffness += k * side * side;
                    // a wheel has mass: when its ray suddenly lands on top of an obstacle it is pushed up over a few steps,
                    // not teleported (a jump of the travel would make the damper kick the hull into the air)
                    float travel = Mathf.Min(c.travel, prevTravel[s][i] + wheelRiseSpeed * dt);
                    float vel = Mathf.Clamp((travel - prevTravel[s][i]) / dt, -maxDamperSpeed, maxDamperSpeed);
                    prevTravel[s][i] = travel;
                    float f = k * travel + damper * vel;
                    if (travel > c.maxTravel) f += k * bumpStopStiffness * (travel - c.maxTravel);
                    f = Mathf.Clamp(f, 0f, perWheelMass * g * maxWheelLoad);
                    rb.AddForceAtPosition(up * f, c.point);
                    // past the bump stop the arm is solid: the hull itself has to come up over the obstacle (otherwise the
                    // wheel and the belt sink into it). Lift that corner at a limited speed instead of with a huge force.
                    if (c.travel > c.maxTravel)
                    {
                        float want = Mathf.Min((c.travel - c.maxTravel) / 0.08f, bumpLiftSpeed);
                        float have = Vector3.Dot(rb.GetPointVelocity(c.point), up);
                        if (have < want) rb.AddForceAtPosition(up * ((want - have) * perWheelMass * 0.8f), c.point, ForceMode.Impulse);
                    }
                    if (contactN < contactPoint.Length)
                    {
                        contactPoint[contactN] = c.point;
                        contactNormal[contactN] = c.normal;
                        contactLoad[contactN] = f;
                        contactSide[contactN] = s;
                        contactN++;
                    }
                    sidePoint[s] += c.point;
                    sideLoad[s] += f;
                    sideCount[s]++;
                    normalSum += c.normal;
                    pointAll += c.point;
                    loadAll += f;
                    contactsAll++;
                }
            }
            GroundedWheels = contactsAll;
            if (contactsAll > 0) sinceGrounded = 0f; else sinceGrounded += dt;
            Stabilize(mass, dt);

            // speed command: ramps up with the acceleration setting; releasing the key (or pressing the other way) brakes at once
            float target = input.throttle > 0f ? input.throttle * maxForwardSpeed : input.throttle * maxReverseSpeed;
            Vector3 hullFwd = rot * Vector3.forward;
            float measured = Vector3.Dot(rb.linearVelocity, hullFwd);
            bool released = Mathf.Abs(target) < 0.01f;
            bool against = !released && Mathf.Abs(measured) > 0.3f && Mathf.Sign(target) != Mathf.Sign(measured);
            isBraking = (released || against) && Mathf.Abs(measured) > 0.3f;
            if (released || against) commandSpeed = 0f;
            else commandSpeed = Mathf.MoveTowards(commandSpeed, target, acceleration * dt);
            // blocked (wall, too steep): do not wind the command up far beyond what the tank really does
            commandSpeed = Mathf.Clamp(commandSpeed, measured - 2.5f, measured + 2.5f);
            if (released) commandSpeed = 0f;
            Speed = measured;

            float yawRate = input.steer * turnRate * Mathf.Deg2Rad * Mathf.Lerp(1f, 0.65f, Mathf.Abs(measured) / Mathf.Max(0.1f, maxForwardSpeed));
            float vL = commandSpeed + yawRate * halfTrackWidth;
            float vR = commandSpeed - yawRate * halfTrackWidth;

            if (contactsAll == 0)
            {
                // in the air: the tracks spin freely
                if (leftTrack) leftTrack.speed = vL;
                if (rightTrack) rightTrack.speed = vR;
                return;
            }

            Vector3 n = normalSum.normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(hullFwd, n).normalized;
            Vector3 right = Vector3.Cross(n, fwd);

            // each side's belt speed over the ground: every loaded wheel, along its own ground slope, weighted by its load
            float vLm = vL, vRm = vR;
            float wL = 0f, wR = 0f, sL = 0f, sR = 0f;
            for (int i = 0; i < contactN; i++)
            {
                Vector3 tan = Vector3.ProjectOnPlane(hullFwd, contactNormal[i]).normalized;
                float v = Vector3.Dot(rb.GetPointVelocity(contactPoint[i]), tan) * contactLoad[i];
                if (contactSide[i] == 0) { sL += v; wL += contactLoad[i]; } else { sR += v; wR += contactLoad[i]; }
            }
            if (wL > 1e-3f) vLm = sL / wL;
            if (wR > 1e-3f) vRm = sR / wR;

            // two controllers: forward speed (common) and turning (difference) — turning reacts faster, so a released
            // A / D stops the rotation at once while the speed still follows the acceleration / braking feel
            float common = commandSpeed - (vLm + vRm) * 0.5f;
            float diff = yawRate * halfTrackWidth - (vLm - vRm) * 0.5f;
            float half = mass * 0.5f;
            float turnF = half * turnResponse * diff;     // + on the left, - on the right
            // speed: proportional control plus the slope's pull, so the tank holds its speed (or stands) on a hill
            // instead of creeping until the speed error balances gravity
            float speedF = half * driveResponse * common - half * Vector3.Dot(Physics.gravity, fwd);
            float brakeLimit = half * braking;
            float power = powerPerTon * 1000f * (mass / 1000f) * 0.5f; // W per side

            // force each side can give in the direction it would push (engine forwards: tractive effort and power;
            // brakes against its motion; grip)
            float LimitFor(int s, float desired, float vm) =>
                sideCount[s] == 0 ? 0f
                : Mathf.Min(desired * vm < 0f && Mathf.Abs(vm) > 0.05f ? brakeLimit : Mathf.Min(engine, power / Mathf.Max(1.5f, Mathf.Abs(vm))),
                            tractionGrip * sideLoad[s]);
            float limL = LimitFor(0, speedF + turnF, vLm), limR = LimitFor(1, speedF - turnF, vRm);

            // steering first: accelerating (both sides at full force) must not swallow the left / right difference.
            // The turn share gets up to 70 % of the smaller side's budget, the speed share takes what is left.
            float turnCap = Mathf.Min(limL, limR) * 0.7f;
            turnF = Mathf.Clamp(turnF, -turnCap, turnCap);
            float speedLo = Mathf.Max(-limL - turnF, -limR + turnF);
            float speedHi = Mathf.Min(limL - turnF, limR + turnF);
            speedF = speedLo <= speedHi ? Mathf.Clamp(speedF, speedLo, speedHi) : 0f;
            float fL = speedF + turnF, fR = speedF - turnF;

            for (int s = 0; s < 2; s++)
            {
                var t = sides[s];
                float vm = s == 0 ? vLm : vRm;
                if (sideCount[s] == 0)
                {
                    if (t) t.speed = s == 0 ? vL : vR;
                    continue;
                }
                float limit = s == 0 ? limL : limR;
                float applied = Mathf.Clamp(s == 0 ? fL : fR, -limit, limit);
                // the belt pushes where it is loaded: shared between this side's wheels by their load, each along its
                // own contact slope (a wheel on a step's edge pushes up it, the others along the flat)
                float load = Mathf.Max(1e-3f, sideLoad[s]);
                for (int i = 0; i < contactN; i++)
                {
                    if (contactSide[i] != s) continue;
                    Vector3 tan = Vector3.ProjectOnPlane(hullFwd, contactNormal[i]).normalized;
                    rb.AddForceAtPosition(tan * (applied * contactLoad[i] / load), contactPoint[i]);
                }
                if (s == 0) { debugDrive.x = applied; debugDrive.z = limit; } else { debugDrive.y = applied; debugDrive.w = limit; }
                if (t) t.speed = vm;
            }

            // sideways grip: kill lateral sliding (leans the hull out in turns). Applied at the contact height but level
            // with the centre of mass along the tank, so it never brakes the turning (tracks placed off-centre)
            Vector3 centre = pointAll / contactsAll;
            centre += fwd * Vector3.Dot(rb.worldCenterOfMass - centre, fwd);
            float vLat = Vector3.Dot(rb.GetPointVelocity(centre), right);
            float latLimit = lateralGrip * loadAll;
            // plus the side slope's pull, so a tank on a side slope does not slide down it slowly
            float latForce = -vLat * mass * lateralResponse - mass * Vector3.Dot(Physics.gravity, right);
            rb.AddForceAtPosition(right * Mathf.Clamp(latForce, -latLimit, latLimit), centre);

            // turn lean: the hull rolls out of the turn on its torsion bars. The grip force above already does a little
            // (it acts below the centre of mass), but a real hull sits high on soft bars and leans far more. Add the roll
            // moment that the bars turn into rollPerLateral degrees per m/s² of sideways acceleration.
            if (bodySway > 0f && rollPerLateral > 0f && rollStiffness > 0f)
            {
                float yawMeasured = Vector3.Dot(rb.angularVelocity, n);
                float aLat = Mathf.Clamp(measured * yawMeasured, -lateralGrip * g, lateralGrip * g);
                float lean = Mathf.Clamp(aLat * rollPerLateral * bodySway, -maxTurnLean, maxTurnLean) * Mathf.Deg2Rad;
                rb.AddTorque(hullFwd * (rollStiffness * lean));
            }
        }

        /// <summary>
        /// Sprocket / idler meeting a step or a log: a stiff spring pushes the hull away from the obstacle (out of the wheel's
        /// circle, i.e. up and back), and the belt wrapped round the wheel grips the edge and pulls the hull up while driving into it.
        /// Being a spring with a rate-limited penetration, it lifts the tank over the edge instead of bouncing it off a rigid collider.
        /// </summary>
        void EndWheelContact(TankTrack t, int i, Matrix4x4 m, int side, float perWheelMass, float g, float engine, float dt)
        {
            float prev = prevTravel[side][i];
            if (!t.ProbeEnd(i, m, out Vector3 point, out Vector3 push, out float pen))
            {
                prevTravel[side][i] = 0f;
                return;
            }
            pen = Mathf.Min(pen, prev + wheelRiseSpeed * dt);
            float k = perWheelMass * g / 0.07f;
            float damper = 2f * 0.7f * Mathf.Sqrt(k * perWheelMass); // well damped: lift, do not bounce
            float vel = Mathf.Clamp((pen - prev) / dt, -maxDamperSpeed, maxDamperSpeed);
            prevTravel[side][i] = pen;
            float f = Mathf.Clamp(k * pen + damper * vel, 0f, perWheelMass * g * maxWheelLoad * 1.5f);
            if (f <= 0f) return;
            rb.AddForceAtPosition(push * f, point);

            // belt traction up the obstacle while driving into it
            if (Mathf.Abs(commandSpeed) < 0.2f) return;
            Vector3 up = rb.rotation * Vector3.up, fwd = rb.rotation * Vector3.forward;
            if (-Vector3.Dot(push, fwd) * Mathf.Sign(commandSpeed) < 0.1f) return;
            Vector3 tangent = Vector3.ProjectOnPlane(up, push);
            if (tangent.sqrMagnitude < 1e-4f) return;
            float rise = rb.linearVelocity.y;
            float fade = Mathf.Clamp01(1f - (rise - 0.5f) / 1f);
            rb.AddForceAtPosition(tangent.normalized * (Mathf.Min(climbGrip * f, engine) * fade), point);
        }

        /// <summary>
        /// A rigid hull that hits an edge at speed turns its forward speed into upward speed (what a real tank's suspension
        /// and belt soak up). While on the ground and just after leaving it, cap the upward speed and the pitch / roll rate.
        /// Driving up steps, ramps and jumps off a ramp stay below the caps.
        /// </summary>
        void Stabilize(float mass, float dt)
        {
            if (sinceGrounded > 0.25f) return;
            float rise = rb.linearVelocity.y;
            if (rise > maxRiseSpeed) rb.AddForce(Vector3.down * ((rise - maxRiseSpeed) * mass / dt));
            Vector3 w = rb.angularVelocity;
            Vector3 yaw = Vector3.Project(w, Vector3.up);
            Vector3 tilt = w - yaw;
            if (tilt.magnitude > maxTiltRate)
                rb.angularVelocity = yaw + tilt.normalized * maxTiltRate;
        }

        // ------------------------------------------------------------------ driving

        void Drive(TankInputState input, float dt)
        {
            float target = input.throttle > 0f ? input.throttle * maxForwardSpeed : input.throttle * maxReverseSpeed;
            bool against = Mathf.Abs(Speed) > 0.1f && Mathf.Abs(target) > 0.01f && Mathf.Sign(target) != Mathf.Sign(Speed);
            isBraking = against || (Mathf.Abs(target) < 0.01f && Mathf.Abs(Speed) > 0.1f);
            Speed = Mathf.MoveTowards(Speed, target, (isBraking ? braking : acceleration) * dt);

            float yawRate = input.steer * turnRate * Mathf.Lerp(1f, 0.65f, Mathf.Abs(Speed) / Mathf.Max(0.1f, maxForwardSpeed));
            yaw += yawRate * dt;
            Quaternion yawRot = Quaternion.Euler(0f, yaw, 0f);
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, surfaceUp) * yawRot;
            Vector3 forward = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;
            Vector3 pos = basePos;

            // walls: stop instead of driving through steep obstacles
            Vector3 move = forward * (Speed * dt);
            if (move.sqrMagnitude > 1e-10f)
            {
                float radius = Mathf.Max(0.2f, halfTrackWidth * 0.8f);
                Vector3 origin = pos + surfaceUp * (groundOffset + radius + 0.6f);
                if (TankPhysics.SphereCast(origin, radius, move.normalized, halfTrackLength + move.magnitude, groundMask, transform, out RaycastHit wall)
                    && Vector3.Dot(wall.normal, Vector3.up) < 0.55f)
                {
                    Speed = 0f;
                    move = Vector3.zero;
                }
            }
            pos += move;

            // ground: 4 probes at the track corners
            int hits = 0;
            float sumY = 0f;
            bool all = true;
            for (int k = 0; k < 4; k++)
            {
                float ox = (k % 2 == 0 ? -1f : 1f) * halfTrackWidth;
                float oz = (k < 2 ? 1f : -1f) * halfTrackLength;
                Vector3 corner = pos + right * ox + forward * oz;
                Vector3 o = new Vector3(corner.x, corner.y + groundOffset + 4f, corner.z);
                if (TankPhysics.Raycast(o, Vector3.down, 12f, groundMask, transform, out RaycastHit h))
                {
                    probeHits[k] = h.point;
                    sumY += h.point.y;
                    hits++;
                }
                else all = false;
            }
            if (hits >= 2)
            {
                Vector3 n = Vector3.up;
                if (all)
                {
                    // 0 = front-left, 1 = front-right, 2 = rear-left, 3 = rear-right
                    n = Vector3.Cross(probeHits[0] - probeHits[3], probeHits[1] - probeHits[2]);
                    if (n.y < 0f) n = -n;
                    n.Normalize();
                }
                float targetY = sumY / hits - groundOffset;
                if (pos.y > targetY + 0.05f || vy < -0.5f)
                {
                    vy -= gravity * dt;
                    pos.y += vy * dt;
                    if (pos.y <= targetY)
                    {
                        pos.y = targetY;
                        // landing: the torsion bars give way, then spring back
                        if (vy < -1f) bounceVel += vy * landingBounce;
                        vy = 0f;
                    }
                }
                else
                {
                    vy = 0f;
                    pos.y = Mathf.Lerp(pos.y, targetY, 1f - Mathf.Exp(-suspensionSmoothing * 2f * dt));
                }
                surfaceUp = Vector3.Slerp(surfaceUp, n, 1f - Mathf.Exp(-suspensionSmoothing * dt));
            }
            else
            {
                vy -= gravity * dt;
                pos.y += vy * dt;
                surfaceUp = Vector3.Slerp(surfaceUp, Vector3.up, dt);
            }

            basePos = pos;
            float w = yawRate * Mathf.Deg2Rad;

            // body sway on the torsion bars: nose up when accelerating, down when braking, lean out in turns
            float accel = (Speed - lastSpeed) / dt;
            lastSpeed = Speed;
            float targetPitch = Mathf.Clamp(-accel * pitchPerAccel * bodySway, -4f, 4f);
            float targetRoll = Mathf.Clamp(Speed * w * rollPerLateral * bodySway, -maxTurnLean, maxTurnLean);
            Spring(ref swayPitch, ref swayPitchVel, targetPitch, swayStiffness, swayDamping, dt);
            Spring(ref swayRoll, ref swayRollVel, targetRoll, swayStiffness, swayDamping, dt);
            Spring(ref bounce, ref bounceVel, 0f, bounceStiffness, bounceDamping, dt);
            bounce = Mathf.Clamp(bounce, -maxBounce, maxBounce);

            transform.SetPositionAndRotation(pos + surfaceUp * bounce,
                Quaternion.FromToRotation(Vector3.up, surfaceUp) * yawRot * Quaternion.Euler(swayPitch, 0f, swayRoll));

            if (leftTrack) leftTrack.speed = Speed + w * halfTrackWidth;
            if (rightTrack) rightTrack.speed = Speed - w * halfTrackWidth;
        }

        /// <summary>Moves the tank (e.g. respawn). It falls to the ground from there.</summary>
        public void Teleport(Vector3 position, float yawDegrees)
        {
            basePos = position;
            yaw = yawDegrees;
            vy = 0f;
            Speed = 0f;
            commandSpeed = 0f;
            var rot = physics ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.FromToRotation(Vector3.up, surfaceUp) * Quaternion.Euler(0f, yaw, 0f);
            if (physics)
            {
                rb.position = position;
                rb.rotation = rot;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                for (int s = 0; s < 2; s++) if (prevTravel[s] != null) System.Array.Clear(prevTravel[s], 0, prevTravel[s].Length);
            }
            transform.SetPositionAndRotation(position, rot);
        }

        static void Spring(ref float x, ref float v, float target, float stiffness, float damping, float dt)
        {
            v += (-stiffness * (x - target) - damping * v) * dt;
            x += v * dt;
        }

        // ------------------------------------------------------------------ turret / gun

        void Aim(float dt)
        {
            if (!useExternalInput && aimSource != null && aimSource.HasAim)
                AimPoint = aimSource.AimPoint;
            else if (useExternalInput || !aimCamera)
                AimPoint = externalAimPoint != Vector3.zero ? externalAimPoint : transform.position + transform.forward * 100f;
            else
                AimPoint = aimCamera.AimPoint;

            if (turret && turret.parent)
            {
                Transform parent = turret.parent;
                Vector3 axisW = parent.TransformDirection(turretRest * turretAxisLocal).normalized;
                Vector3 a = Vector3.ProjectOnPlane(parent.TransformDirection(turretRestForward), axisW);
                Vector3 b = Vector3.ProjectOnPlane(AimPoint - turret.position, axisW);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f)
                    turretAngle = Mathf.MoveTowardsAngle(turretAngle, Vector3.SignedAngle(a, b, axisW), traverseSpeed * dt);
                turret.localRotation = turretRest * Quaternion.AngleAxis(turretAngle, turretAxisLocal);
            }

            if (gun && gun.parent)
            {
                Transform parent = gun.parent;
                Vector3 axisW = parent.TransformDirection(gunRest * gunAxisLocal).normalized;
                Vector3 a = Vector3.ProjectOnPlane(parent.TransformDirection(gunRestForward), axisW);
                Vector3 b = Vector3.ProjectOnPlane(AimPoint - gun.position, axisW);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f)
                {
                    float targetElev = Mathf.Clamp(Vector3.SignedAngle(a, b, axisW) * elevSign, minElevation, maxElevation);
                    gunAngle = Mathf.MoveTowards(gunAngle, targetElev, elevationSpeed * dt);
                }
                gun.localRotation = gunRest * Quaternion.AngleAxis(gunAngle * elevSign, gunAxisLocal);
            }

            // the barrel slides back along itself when firing (the mount only elevates); no barrel, no recoil movement
            if (barrel && barrel.parent)
            {
                // counter-recoil: the barrel runs back out, quickly at first, then easing in
                recoil = Mathf.MoveTowards(Mathf.Lerp(recoil, 0f, 1f - Mathf.Exp(-recoilReturn * dt)), 0f, 0.02f * dt);
                Transform space = gun ? gun : barrel.parent;
                Vector3 backW = -space.TransformDirection(barrelForward).normalized * recoil;
                if (barrelFollows)
                    barrel.SetPositionAndRotation(gun.TransformPoint(barrelRelPos) + backW, gun.rotation * barrelRelRot);
                else
                    barrel.localPosition = barrelRestPos + barrel.parent.InverseTransformVector(backW);
            }
            else recoil = 0f;

            AimSecondaryWeapons(dt);
        }

        // ------------------------------------------------------------------ weapons

        void Weapons(TankInputState input, float dt)
        {
            reloadTimer = Mathf.Max(0f, reloadTimer - dt);
            if (input.fireMain && reloadTimer <= 0f) FireMain();
            TickSecondaryWeapons(input, dt);
        }

        public void FireMain()
        {
            reloadTimer = reloadTime;
            float p = Mathf.Max(0f, gunPower);
            if (barrel) recoil = Mathf.Min(recoilDistance * p, maxRecoilDistance);
            flash = Mathf.Min(1f + 0.3f * (p - 1f), 2f);
            if (builtInEffects && !externalMainGun && muzzleFlash) muzzleFlash.Play(true);
            if (builtInEffects && !externalMainGun && muzzleSmoke) muzzleSmoke.Play(true);
            if (aimCamera) aimCamera.Shake(0.4f * p);
            Transform m = muzzle ? muzzle : gun ? gun : transform;
            Kick(m, p);
            bool didHit = TankPhysics.Raycast(m.position, m.forward, range, groundMask, transform, out RaycastHit hit);
            if (didHit && builtInEffects)
            {
                Emit(impactDust, hit.point, 30);
                Emit(impactSparks, hit.point, 20);
            }
            GunFired?.Invoke(this, new GunShot { muzzle = m, power = p, hit = didHit, hitInfo = hit });
        }

        /// <summary>Recoil of a gun of power <paramref name="p"/> fired from <paramref name="m"/> (hull impulse, or the sway springs).</summary>
        void Kick(Transform m, float p)
        {
            if (physics) rb.AddForceAtPosition(-m.forward * (recoilImpulse * p), m.position, ForceMode.Impulse);
            else
            {
                // kinematic mode: kick the hull's sway springs instead (nose up / back, more for a stronger gun)
                Vector3 back = transform.InverseTransformDirection(-m.forward);
                swayPitchVel -= 40f * p * Mathf.Max(0.2f, -back.z);
                swayRollVel -= 40f * p * back.x; // gun to the right: the top of the hull leans left
            }
        }

        /// <summary>One round from every machine gun on the Space trigger (ignores the rate of fire; for scripts).</summary>
        public void FireMachineGun()
        {
            foreach (var mg in machineGuns)
                if (mg != null && mg.point && mg.trigger == WeaponTrigger.MachineGun)
                    FireRound(mg.point, mg.spec, mg.tracer, mg.flash, mg.Feed);
        }

        static void Emit(ParticleSystem ps, Vector3 worldPos, int count)
        {
            if (!ps) return;
            var ep = new ParticleSystem.EmitParams { position = worldPos, applyShapeToPosition = true };
            ps.Emit(ep, count);
        }

        // ------------------------------------------------------------------ effects

        void UpdateEffects(TankInputState input, float dt)
        {
            flash = Mathf.MoveTowards(flash, 0f, dt * 10f);
            if (muzzleLight) muzzleLight.intensity = builtInEffects && !externalMainGun ? flash * muzzleLightIntensity : 0f;
            foreach (var st in secondaryTurrets)
            {
                if (st == null) continue;
                st.flash = Mathf.MoveTowards(st.flash, 0f, dt * 10f);
                if (st.muzzleLight) st.muzzleLight.intensity = externalGunFlashes ? 0f : st.flash * muzzleLightIntensity * 0.6f;
            }

            float load = Mathf.Clamp01(Mathf.Abs(input.throttle) * 0.6f + Mathf.Abs(Speed) / Mathf.Max(0.1f, maxForwardSpeed) * 0.4f);
            if (exhaustSmoke != null)
                foreach (var ps in exhaustSmoke)
                {
                    if (!ps) continue;
                    var em = ps.emission;
                    em.rateOverTime = builtInEffects && !externalExhaust ? Mathf.Lerp(exhaustIdleRate, exhaustMaxRate, load) : 0f;
                }

            if (input.toggleLights) SetLights(!lightsOn);
            if (rearLightLights != null && rearBase != null)
                for (int i = 0; i < rearLightLights.Length && i < rearBase.Length; i++)
                    if (rearLightLights[i]) rearLightLights[i].intensity = rearBase[i] * (isBraking ? 3f : 1f);
        }

        public void SetLights(bool on)
        {
            lightsOn = on;
            if (headlightLights != null) foreach (var l in headlightLights) if (l) l.enabled = on;
            if (rearLightLights != null) foreach (var l in rearLightLights) if (l) l.enabled = on;
        }
    }
}
