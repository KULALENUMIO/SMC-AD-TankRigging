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
