using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>
    /// Base of a sight that <see cref="TankSightRig"/> can switch to. A sight steers its line of sight from the mouse,
    /// places the camera and paints its symbology; the rig does the rest (switching, cursor, laser, ammunition, overlay).
    /// </summary>
    public abstract class TankSight : MonoBehaviour
    {
        [Tooltip("照準器の位置（なくても可）。ないときは砲の付け根の近くに自動で置く")] public Transform eyePoint;
        [Tooltip("自動の位置のとき: 砲の付け根から見た照準器の位置（m、砲塔の向き: 右・上・前）")] public Vector3 eyeOffset = new Vector3(0.55f, 0.4f, 0.25f);
        [Tooltip("オン: 照準カメラを砲に固定する（砲と一緒に旋回・俯仰し、後座では動かない。視界の中心 = 砲の向き。マウスで砲を直接動かす）。" +
                 "オフ: 照準線を砲とは別に動かす（照準線が先に動き、砲が追いかける）")]
        public bool fixedToGun = true;
        [Tooltip("砲に固定のとき: 照準器の視界から自車を隠す（砲身や防盾が視界をふさがないように。影は残る）")] public bool hideOwnTank = true;

        protected TankSightRig rig;

        public abstract SightProfile BaseProfile { get; }
        public string DisplayName => BaseProfile && !string.IsNullOrEmpty(BaseProfile.displayName) ? BaseProfile.displayName : GetType().Name;
        public TankSightRig Rig => rig;
        /// <summary>Extra keys of this sight for the help line.</summary>
        public virtual string HelpKeys => "";

        internal void Bind(TankSightRig r) => rig = r;

        /// <summary>The camera switched to this sight. <paramref name="aimPoint"/> is where the player was aiming.</summary>
        public virtual void Enter(Camera cam, Vector3 aimPoint) { }
        public virtual void Exit(Camera cam) { }

        /// <summary>Mouse / keys (before the tank aims this frame).</summary>
        public abstract void Steer(TankInputState input, float dt);

        /// <summary>Places the camera (after the tank and turret moved).</summary>
        public abstract void PlaceCamera(Camera cam, float dt);

        /// <summary>Builds the symbology. The frame's overlay size and tangents are already set for the camera.</summary>
        public abstract void Paint(SightPainter p, SightFrame frame, Camera cam);

        /// <summary>Overlay set-up for this frame (size, tangents, XR). Default: the camera's screen, 1:1.</summary>
        public virtual void SetupFrame(SightFrame f, Camera cam)
        {
            f.width = Mathf.Max(16, cam.pixelWidth);
            f.height = Mathf.Max(9, cam.pixelHeight);
            float th = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            f.tanHalf = new Vector2(th * cam.aspect, th);
            f.angular = rig && rig.forceAngularOverlay;
            f.sensor = null;
        }

        /// <summary>Turn the line of sight (or the head) to look at a world point. For scripts and tests.</summary>
        public abstract void LookAt(Vector3 point);

        /// <summary>Where the main gun should point (false: let the tank keep its own aim).</summary>
        public abstract bool TryGetAim(out Vector3 point);

        /// <summary>Origin and direction the laser rangefinder fires along.</summary>
        public abstract Ray LaserRay { get; }

        // ------------------------------------------------------------------ helpers

        /// <summary>Eye position: the eye point, or the gun mount plus <see cref="eyeOffset"/> in turret axes.</summary>
        protected Vector3 EyePosition(bool rideOnGun)
        {
            if (eyePoint) return eyePoint.position;
            var t = rig ? rig.tank : null;
            if (!t) return transform.position;
            Transform pivot = t.gun ? t.gun : t.turret ? t.turret : t.transform;
            return pivot.position + rig.TurretFrame(rideOnGun) * eyeOffset;
        }

        /// <summary>Direction the main gun points.</summary>
        protected Vector3 GunForward()
        {
            var t = rig ? rig.tank : null;
            if (!t) return transform.forward;
            Transform m = t.muzzle ? t.muzzle : t.gun ? t.gun : t.transform;
            return m.forward;
        }

        protected static float ScaledSensitivity(SightProfile p, float fov) => (p ? p.sensitivity : 0.12f) * fov / 60f;

        // ------------------------------------------------------------------ fixed to the gun

        Transform mountOf, mountEyeOf;
        bool mountCaptured;
        Vector3 mountEye, mountUp;
        /// <summary>Where the gun is told to point (world direction; gun-fixed sights).</summary>
        protected Vector3 gunCommand;
        /// <summary>The camera is fixed to the gun now (the rig's firing jolt is then a kick that settles back).</summary>
        public virtual bool GunFixed => false;
        protected bool hasGunCommand;

        /// <summary>What the sight is fixed to: the gun mount (elevates; the barrel's recoil does not move it).</summary>
        protected Transform Mount
        {
            get
            {
                var t = rig ? rig.tank : null;
                if (!t) return transform;
                return t.gun ? t.gun : t.barrel && t.barrel.parent ? t.barrel.parent : t.turret ? t.turret : t.transform;
            }
        }

        /// <summary>The point the gun turns about (its aim is worked out from here).</summary>
        protected Vector3 GunPivot => Mount.position;

        /// <summary>
        /// Camera pose of a gun-fixed sight: the eye rides the mount at a fixed place, the view is the bore's direction
        /// (with the mount's roll). Nothing else moves it: not the mouse, not the recoil.
        /// </summary>
        protected void MountPose(out Vector3 eye, out Quaternion rot)
        {
            var m = Mount;
            if (!mountCaptured || mountOf != m || mountEyeOf != eyePoint) CaptureMount(m);
            var t = rig ? rig.tank : null;
            // an eye point already riding the mount (not the recoiling barrel) is used as it is; others keep the place they had
            bool live = eyePoint && eyePoint.IsChildOf(m) && !(t && t.barrel && t.barrel != m && eyePoint.IsChildOf(t.barrel));
            eye = live ? eyePoint.position : m.TransformPoint(mountEye);
            Vector3 f = rig ? rig.GunDirection : m.forward;
            rot = Quaternion.LookRotation(f, m.TransformDirection(mountUp));
        }

        void CaptureMount(Transform m)
        {
            mountOf = m; mountEyeOf = eyePoint; mountCaptured = true;
            mountEye = m.InverseTransformPoint(EyePosition(true));
            var t = rig ? rig.tank : null;
            mountUp = m.InverseTransformDirection(t ? t.transform.up : Vector3.up);
        }

        /// <summary>
        /// Gun-fixed sights: the mouse turns the gun's order (about the turret's axis, then up / down). While the mouse
        /// moves, the order never runs more than a little ahead of the gun (part of the field of view), so the view stops
        /// when the mouse stops - no overshoot when the turret is slower than the hand. Held still, the order stays put in
        /// the world (the gun keeps it while the hull moves). Returns the aim point.
        /// </summary>
        protected Vector3 SteerGun(Vector2 look, float sensitivity, float fov, float range)
        {
            Vector3 gun = rig.GunDirection;
            if (!hasGunCommand) { gunCommand = gun; hasGunCommand = true; }
            if (look != Vector2.zero)
            {
                float lead = Mathf.Clamp(fov * 0.05f, 0.05f, 2f);   // a frame or two of the turret at full speed: it settles at once
                gunCommand = Lead(gun, gunCommand, lead);
                Vector3 up = rig.tank ? rig.tank.transform.up : Vector3.up;
                Vector3 c = Quaternion.AngleAxis(look.x * sensitivity, up) * gunCommand;
                Vector3 right = Vector3.Cross(up, c);
                if (right.sqrMagnitude > 1e-6f)
                {
                    Vector3 c2 = Quaternion.AngleAxis(-look.y * sensitivity, right.normalized) * c;
                    float a = Vector3.Angle(up, c2);
                    if (a > 3f && a < 177f) c = c2;   // never over the top
                }
                gunCommand = Lead(gun, c.normalized, lead);
            }
            return rig.AimAlong(GunPivot, gunCommand, range);
        }

        static Vector3 Lead(Vector3 gun, Vector3 cmd, float maxDeg)
        {
            float a = Vector3.Angle(gun, cmd);
            return a > maxDeg ? Vector3.Slerp(gun, cmd, maxDeg / a).normalized : cmd;
        }

        /// <summary>Gun-fixed sights: order the gun at a world point (scripts, tests).</summary>
        protected void CommandGunAt(Vector3 point)
        {
            Vector3 d = point - GunPivot;
            if (d.sqrMagnitude < 1e-4f) return;
            gunCommand = d.normalized;
            hasGunCommand = true;
        }

        /// <summary>Gun-fixed sights: on entering, start from where the gun points now (the view does not jump).</summary>
        protected void EnterGunFixed()
        {
            gunCommand = rig.GunDirection;
            hasGunCommand = true;
            if (hideOwnTank) rig.SetOwnTankHidden(true);
        }

        /// <summary>A world direction in overlay units for a camera whose vertical half-FOV tangent maps to 540 units × <paramref name="scale"/>.</summary>
        public static bool Project(Camera cam, Vector3 worldDir, float unitsPerTan, out Vector2 p)
        {
            Vector3 l = cam.transform.InverseTransformDirection(worldDir);
            if (l.z <= 1e-4f) { p = default; return false; }
            p = new Vector2(l.x / l.z, l.y / l.z) * unitsPerTan;
            return true;
        }
    }
}
