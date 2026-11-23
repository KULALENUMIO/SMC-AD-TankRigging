using System;
using UnityEngine;

namespace TankRig
{
    /// <summary>Which button fires a weapon.</summary>
    public enum WeaponTrigger
    {
        [InspectorName("左クリック（主砲と一緒）")] Main,
        [InspectorName("Space（機銃）")] MachineGun,
        [InspectorName("中クリック / F（副武装）")] Secondary,
    }

    /// <summary>What a machine gun is fixed to (the builder parents its point to that part).</summary>
    public enum MachineGunMount
    {
        [InspectorName("同軸（砲座と一緒に上下）")] Coaxial,
        [InspectorName("砲塔")] Turret,
        [InspectorName("車体")] Hull,
    }

    public enum SecondaryWeapon
    {
        [InspectorName("砲")] Cannon,
        [InspectorName("機銃")] MachineGun,
    }

    /// <summary>Machine gun performance (editable in the builder window).</summary>
    [Serializable]
    public class MachineGunSpec
    {
        [Tooltip("発射レート（発/分）")] public float roundsPerMinute = 750f;
        [Tooltip("弾の広がり（度。照準線からのずれの最大）")] public float spread = 0.5f;
        [Tooltip("射程（m）")] public float range = 1200f;
        [Tooltip("曳光弾の頻度（何発に 1 発か。1 = 全弾）")] public int tracerEvery = 1;
        [Tooltip("曳光弾の速さ（m/s）")] public float tracerSpeed = 300f;
        [Tooltip("弾薬ベルト 1 本の弾数（0 = 無限）")] public int beltSize = 0;
        [Tooltip("ベルト交換の時間（秒）")] public float beltReload = 5f;
        [Tooltip("着弾の土煙・火花の量（1 = 標準）")] public float impactScale = 1f;
        [Tooltip("口径（mm）。リアルな発砲エフェクト（TankFx）の大きさがこれで決まる")] public float caliberMm = 7.62f;

        public MachineGunSpec Clone() => (MachineGunSpec)MemberwiseClone();
    }

    /// <summary>Rate of fire and belt of one machine gun (runtime only).</summary>
    public class GunFeed
    {
        float cooldown, reload;
        int left = -1, shots;

        /// <summary>Rounds left in the belt (-1 before the first shot; unlimited belts stay at 0).</summary>
        public int Left => left;
        public bool Reloading => reload > 0f;
        public float ReloadLeft => reload;

        /// <summary>Rounds to fire this frame (several at a high rate of fire and a low frame rate).</summary>
        public int Tick(MachineGunSpec s, bool held, float dt)
        {
            if (left < 0) left = s.beltSize;
            if (reload > 0f)
            {
                reload -= dt;
                if (reload > 0f) return 0;
                left = s.beltSize;
                cooldown = 0f;
            }
            cooldown -= dt;
            if (!held)
            {
                if (cooldown < 0f) cooldown = 0f; // the next press fires at once
                return 0;
            }
            float interval = 60f / Mathf.Max(1f, s.roundsPerMinute);
            int n = 0;
            while (cooldown <= 0f && n < 16)
            {
                cooldown += interval;
                n++;
                if (s.beltSize > 0 && --left <= 0)
                {
                    reload = Mathf.Max(0.05f, s.beltReload);
                    cooldown = Mathf.Max(cooldown, 0f);
                    break;
                }
            }
            return n;
        }

        /// <summary>True for the rounds that show a tracer (every N-th).</summary>
        public bool NextIsTracer(MachineGunSpec s) => s.tracerEvery <= 1 || shots++ % s.tracerEvery == 0;
    }

    /// <summary>One machine gun: a point (its blue arrow is the firing direction), its spec and its effects.</summary>
    [Serializable]
    public class TankMachineGun
    {
        public string name = "機銃";
        [Tooltip("撃つ位置と向き（青い矢印 = 前）")] public Transform point;
        [Tooltip("何に取り付けるか（組み立てウィンドウがポイントの親を決めるのに使う）")] public MachineGunMount mount = MachineGunMount.Coaxial;
        public WeaponTrigger trigger = WeaponTrigger.MachineGun;
        [Tooltip("照準点に向く範囲（度、0 = 固定）。車体機銃のボールマウントなど")] [Range(0f, 60f)] public float aimCone;
        public MachineGunSpec spec = new MachineGunSpec();

        [Header("エフェクト（完成時に作られる）")]
        public ParticleSystem tracer;
        public ParticleSystem flash;

        [NonSerialized] public Quaternion restLocal = Quaternion.identity;
        [NonSerialized] GunFeed feed;
        public GunFeed Feed => feed ??= new GunFeed();

        public void Init()
        {
            feed = new GunFeed();
            if (point) restLocal = point.localRotation;
        }

        public TankMachineGun Clone()
        {
            var c = (TankMachineGun)MemberwiseClone();
            c.spec = spec != null ? spec.Clone() : new MachineGunSpec();
            c.feed = null;
            return c;
        }
    }

    /// <summary>
    /// A secondary turret (sponson, cupola, second turret...). Every part is optional: without a turret it does not traverse,
    /// without a mount (砲架) it does not elevate, without a barrel it does not recoil; with none of them it is a fixed gun
    /// that fires along its muzzle point.
    /// </summary>
    [Serializable]
    public class TankSecondaryTurret
    {
        public string name = "副砲塔";
        [Tooltip("砲塔: 旋回する部分（なくても可）")] public Transform turret;
        [Tooltip("砲架: 上下（俯仰）に動く部分（なくても可）")] public Transform gun;
        [Tooltip("砲身: 発砲時に後退する部分（なくても可。武器が砲のときだけ動く）")] public Transform barrel;
        [Tooltip("撃つ位置と向き（青い矢印 = 前）")] public Transform muzzle;
        public SecondaryWeapon weapon = SecondaryWeapon.Cannon;
        public WeaponTrigger trigger = WeaponTrigger.Secondary;

        [Header("旋回・俯仰")]
        [Tooltip("砲塔の旋回軸（砲塔のローカル）")] public Vector3 turretAxisLocal = Vector3.up;
        [Tooltip("砲架の俯仰軸（砲架のローカル）")] public Vector3 gunAxisLocal = Vector3.right;
        [Tooltip("旋回できる範囲（度、正面から。左が −、右が +。−180〜180 で全周）")] public float minTraverse = -180f;
        public float maxTraverse = 180f;
        public float minElevation = -8f;
        public float maxElevation = 25f;
        [Tooltip("旋回速度（度/秒）")] public float traverseSpeed = 40f;
        [Tooltip("俯仰速度（度/秒）")] public float elevationSpeed = 25f;

        [Header("砲")]
        public float reloadTime = 2f;
        [Tooltip("火力（主砲の 1 = 標準に対して）。反動・砲身の後退・発砲炎の大きさに比例")] [Range(0.05f, 4f)] public float power = 0.35f;
        public float range = 1500f;
        [Tooltip("口径（mm）。リアルな発砲エフェクト（TankFx）の大きさがこれで決まる")] public float caliberMm = 30f;

        [Header("機銃")]
        public MachineGunSpec machineGun = new MachineGunSpec();

        [Header("自動（完成時に設定）")]
        [Tooltip("初期姿勢での砲の向き（砲塔の親の空間）")] public Vector3 turretRestForward = Vector3.forward;
        [Tooltip("初期姿勢での砲の向き（砲架の親の空間）")] public Vector3 gunRestForward = Vector3.forward;
        [Tooltip("砲身の向き（砲架、なければ砲身の親の空間）。後退はこの逆向き")] public Vector3 barrelForward = Vector3.forward;
        public ParticleSystem muzzleFlash;
        public ParticleSystem muzzleSmoke;
        public Light muzzleLight;
        public ParticleSystem tracer;
        public ParticleSystem mgFlash;

        [NonSerialized] Quaternion turretRest = Quaternion.identity, gunRest = Quaternion.identity, barrelRelRot = Quaternion.identity;
        [NonSerialized] Vector3 barrelRestPos, barrelRelPos;
        [NonSerialized] bool barrelFollows;
        [NonSerialized] float turretAngle, gunAngle, elevSign = 1f, recoil, reloadTimer;
        [NonSerialized] public float flash;
        [NonSerialized] GunFeed feed;

        public GunFeed Feed => feed ??= new GunFeed();
        public bool FullCircle => maxTraverse - minTraverse >= 359.9f;
        public bool Ready => reloadTimer <= 0f;
        public float Reload01 => reloadTime <= 0f ? 1f : 1f - Mathf.Clamp01(reloadTimer / reloadTime);

        /// <summary>Parts that cannot work together are dropped here (the builder window reports them before).</summary>
        public void Init(Transform root, float recoilFxScale = 1f)
        {
            if (gun && gun == turret) gun = null;
            if (barrel && (barrel == gun || barrel == turret || (gun && gun.IsChildOf(barrel)) || (turret && turret.IsChildOf(barrel))))
                barrel = null;
            if (turret) turretRest = turret.localRotation;
            if (gun) gunRest = gun.localRotation;
            barrelFollows = false;
            if (barrel && barrel.parent)
            {
                barrelRestPos = barrel.localPosition;
                // not under the mount: carried along with it from here
                barrelFollows = gun && !barrel.IsChildOf(gun);
                if (barrelFollows)
                {
                    barrelRelPos = gun.InverseTransformPoint(barrel.position);
                    barrelRelRot = Quaternion.Inverse(gun.rotation) * barrel.rotation;
                }
            }
            elevSign = ElevationSign(root);
            if (!muzzle)
            {
                // fire from the front-most part along its rest direction
                var parent = barrel ? barrel : gun ? gun : turret ? turret : root;
                var go = new GameObject(name + "_Muzzle");
                go.transform.SetParent(parent, false);
                go.transform.rotation = Quaternion.LookRotation(RestDirection(root), root.up);
                muzzle = go.transform;
            }
            turretAngle = gunAngle = recoil = reloadTimer = flash = 0f;
            feed = new GunFeed();
            if (muzzleLight) muzzleLight.intensity = 0f;
            float fx = Mathf.Sqrt(Mathf.Max(0.05f, power)) * recoilFxScale;
            if (muzzleFlash) muzzleFlash.transform.localScale = Vector3.one * fx;
            if (muzzleSmoke) muzzleSmoke.transform.localScale = Vector3.one * fx;
        }

        Vector3 RestDirection(Transform root)
        {
            if (gun && gun.parent) return gun.parent.TransformDirection(gunRestForward);
            if (turret && turret.parent) return turret.parent.TransformDirection(turretRestForward);
            return root.forward;
        }

        float ElevationSign(Transform root)
        {
            if (!gun || !gun.parent) return 1f;
            Vector3 axisW = gun.parent.TransformDirection(gunRest * gunAxisLocal);
            Vector3 fwdW = gun.parent.TransformDirection(gunRestForward);
            Vector3 raised = Quaternion.AngleAxis(10f, axisW) * fwdW;
            return Vector3.Dot(raised, root.up) >= Vector3.Dot(fwdW, root.up) ? 1f : -1f;
        }

        /// <summary>Turns the parts that exist towards the aim point (within the limits) and runs the barrel's recoil.</summary>
        public void Aim(Vector3 aimPoint, float dt, float recoilReturn)
        {
            if (turret && turret.parent)
            {
                Transform parent = turret.parent;
                Vector3 axisW = parent.TransformDirection(turretRest * turretAxisLocal).normalized;
                Vector3 a = Vector3.ProjectOnPlane(parent.TransformDirection(turretRestForward), axisW);
                Vector3 b = Vector3.ProjectOnPlane(aimPoint - turret.position, axisW);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f)
                {
                    float target = Vector3.SignedAngle(a, b, axisW);
                    turretAngle = FullCircle
                        ? Mathf.MoveTowardsAngle(turretAngle, target, traverseSpeed * dt)
                        : Mathf.MoveTowards(turretAngle, Mathf.Clamp(target, minTraverse, maxTraverse), traverseSpeed * dt);
                }
                turret.localRotation = turretRest * Quaternion.AngleAxis(turretAngle, turretAxisLocal);
            }

            if (gun && gun.parent)
            {
                Transform parent = gun.parent;
                Vector3 axisW = parent.TransformDirection(gunRest * gunAxisLocal).normalized;
                Vector3 a = Vector3.ProjectOnPlane(parent.TransformDirection(gunRestForward), axisW);
                Vector3 b = Vector3.ProjectOnPlane(aimPoint - gun.position, axisW);
                if (a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f)
                {
                    float targetElev = Mathf.Clamp(Vector3.SignedAngle(a, b, axisW) * elevSign, minElevation, maxElevation);
                    gunAngle = Mathf.MoveTowards(gunAngle, targetElev, elevationSpeed * dt);
                }
                gun.localRotation = gunRest * Quaternion.AngleAxis(gunAngle * elevSign, gunAxisLocal);
            }

            if (barrel && barrel.parent)
            {
                recoil = Mathf.MoveTowards(Mathf.Lerp(recoil, 0f, 1f - Mathf.Exp(-recoilReturn * dt)), 0f, 0.02f * dt);
                Transform space = gun ? gun : barrel.parent;
                Vector3 backW = -space.TransformDirection(barrelForward).normalized * recoil;
                if (barrelFollows)
                    barrel.SetPositionAndRotation(gun.TransformPoint(barrelRelPos) + backW, gun.rotation * barrelRelRot);
                else
                    barrel.localPosition = barrelRestPos + barrel.parent.InverseTransformVector(backW);
            }
            else recoil = 0f;
        }

        public void TickReload(float dt) => reloadTimer = Mathf.Max(0f, reloadTimer - dt);

        /// <summary>Starts the reload and the barrel's recoil (only with a barrel).</summary>
        public void OnFired(float recoilDistance, float maxRecoilDistance)
        {
            reloadTimer = reloadTime;
            float p = Mathf.Max(0f, power);
            if (barrel) recoil = Mathf.Min(recoilDistance * p, maxRecoilDistance);
            flash = Mathf.Min(1f + 0.3f * (p - 1f), 2f);
        }

        public TankSecondaryTurret Clone()
        {
            var c = (TankSecondaryTurret)MemberwiseClone();
            c.machineGun = machineGun != null ? machineGun.Clone() : new MachineGunSpec();
            c.feed = null;
            return c;
        }
    }
}
