using UnityEngine;

namespace TankRig
{
    /// <summary>
    /// Third-person tank camera in the style of War Thunder: the mouse turns the camera (not the hull),
    /// the turret follows the point at the screen centre, right mouse zooms, the wheel changes the distance.
    /// Draws a centre reticle, a marker where the gun currently points, speed and reload.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("Tank Rig/Tank Camera")]
    public class TankCamera : MonoBehaviour
    {
        public TankController target;
        public float distance = 14f;
        public float minDistance = 6f;
        public float maxDistance = 30f;
        [Tooltip("戦車の原点からの注視点の高さ")] public float height = 3f;
        public float sensitivity = 0.12f;
        public float minPitch = -15f;
        public float maxPitch = 60f;
        public float normalFov = 60f;
        public float aimFov = 18f;
        public float collisionRadius = 0.3f;
        public LayerMask collisionMask = ~0;
        public float aimRange = 2000f;
        public bool showHud = true;
        [Tooltip("C で砲塔を止めて見回す: オフ = 押している間だけ / オン = 押すたびに切り替え")] public bool freeLookToggle = false;

        /// <summary>Looking round freely: the turret is held where it was.</summary>
        public bool FreeLook { get; private set; }

        public Vector3 AimPoint { get; private set; }

        Camera cam;
        float yaw, pitch = 12f, shake, fov;
        static Texture2D white;

        void Start()
        {
            cam = GetComponent<Camera>();
            if (target) yaw = target.transform.eulerAngles.y;
            fov = normalFov;
            if (cam.farClipPlane < aimRange) cam.farClipPlane = aimRange;
            Lock();
        }

        static void Lock()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // give the mouse back when this camera stops driving it (switched off, scene change, menu)
        void OnDisable()
        {
            if (FreeLook) SetFreeLook(false, false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        static string BeltText(GunFeed feed, MachineGunSpec spec)
        {
            if (spec == null || spec.beltSize <= 0) return "射撃可";
            if (feed.Reloading) return $"ベルト交換中 {feed.ReloadLeft:0.0} 秒";
            return $"残り {(feed.Left < 0 ? spec.beltSize : feed.Left)} / {spec.beltSize} 発";
        }

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        /// <summary>Turn the orbit so the camera looks along <paramref name="dir"/> (e.g. after leaving a sight).</summary>
        public void LookAlong(Vector3 dir)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg + 8f, minPitch, maxPitch);
        }

        /// <summary>Free look on / off (C). Back off: the camera turns back to the gun (<paramref name="lookBack"/>).</summary>
        public void SetFreeLook(bool on, bool lookBack = true)
        {
            FreeLook = on;
            if (target) target.holdTurret = on;
            // back from looking round: the camera turns back to the gun (the turret does not swing to the camera)
            if (!on && lookBack && target)
            {
                Transform m = target.muzzle ? target.muzzle : target.gun ? target.gun : target.transform;
                LookAlong(m.forward);
            }
        }

        void LateUpdate()
        {
            if (!target) return;
            float dt = Time.deltaTime;
            var input = TankInput.Read();
            bool wantFree = freeLookToggle ? (input.freeLookPressed ? !FreeLook : FreeLook) : input.freeLook;
            if (wantFree != FreeLook) SetFreeLook(wantFree, true);
            if (input.unlockCursor)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Cursor.lockState != CursorLockMode.Locked && input.click)
            {
                Lock();
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                float zoomScale = fov / normalFov;
                yaw += input.look.x * sensitivity * zoomScale;
                pitch = Mathf.Clamp(pitch - input.look.y * sensitivity * zoomScale, minPitch, maxPitch);
                distance = Mathf.Clamp(distance - input.scroll * 2f, minDistance, maxDistance);
            }
            fov = Mathf.Lerp(fov, input.aim ? aimFov : normalFov, 1f - Mathf.Exp(-10f * dt));
            cam.fieldOfView = fov;

            Vector3 pivot = target.transform.position + Vector3.up * height;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = rot * Vector3.back;
            float d = distance;
            if (TankPhysics.SphereCast(pivot, collisionRadius, back, distance, collisionMask, target.transform, out RaycastHit wall))
                d = Mathf.Max(0.5f, wall.distance - 0.1f);
            Vector3 shakeOffset = Random.insideUnitSphere * (shake * 0.3f);
            shake = Mathf.MoveTowards(shake, 0f, dt * 1.5f);
            transform.SetPositionAndRotation(pivot + back * d + shakeOffset, rot);

            AimPoint = TankPhysics.Raycast(transform.position, transform.forward, aimRange, collisionMask, target.transform, out RaycastHit hit)
                ? hit.point
                : transform.position + transform.forward * aimRange;
        }

        void OnGUI()
        {
            if (!showHud || !target || !cam) return;
            if (white == null) white = Texture2D.whiteTexture;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;

            // centre reticle
            GUI.color = new Color(1f, 1f, 1f, 0.8f);
            GUI.DrawTexture(new Rect(cx - 12, cy - 1, 8, 2), white);
            GUI.DrawTexture(new Rect(cx + 4, cy - 1, 8, 2), white);
            GUI.DrawTexture(new Rect(cx - 1, cy + 4, 2, 8), white);

            // where the gun actually points
            if (target.muzzle)
            {
                float dist = Vector3.Distance(target.muzzle.position, AimPoint);
                Vector3 sp = cam.WorldToScreenPoint(target.muzzle.position + target.muzzle.forward * dist);
                if (sp.z > 0f)
                {
                    float x = sp.x, y = Screen.height - sp.y;
                    GUI.color = target.Reload01 >= 1f ? new Color(0.4f, 1f, 0.4f, 0.9f) : new Color(1f, 0.6f, 0.2f, 0.9f);
                    GUI.DrawTexture(new Rect(x - 9, y - 9, 18, 2), white);
                    GUI.DrawTexture(new Rect(x - 9, y + 7, 18, 2), white);
                    GUI.DrawTexture(new Rect(x - 9, y - 9, 2, 18), white);
                    GUI.DrawTexture(new Rect(x + 7, y - 9, 2, 18), white);
                }
            }

            // where each secondary turret points (smaller marker)
            if (target.secondaryTurrets != null)
                foreach (var st in target.secondaryTurrets)
                {
                    if (st == null || !st.muzzle) continue;
                    float dist = Vector3.Distance(st.muzzle.position, AimPoint);
                    Vector3 sp = cam.WorldToScreenPoint(st.muzzle.position + st.muzzle.forward * dist);
                    if (sp.z <= 0f) continue;
                    float x = sp.x, y = Screen.height - sp.y;
                    bool ready = st.weapon == SecondaryWeapon.MachineGun ? !st.Feed.Reloading : st.Ready;
                    GUI.color = ready ? new Color(0.5f, 0.9f, 1f, 0.85f) : new Color(1f, 0.6f, 0.2f, 0.85f);
                    GUI.DrawTexture(new Rect(x - 5, y - 1, 10, 2), white);
                    GUI.DrawTexture(new Rect(x - 1, y - 5, 2, 10), white);
                }

            GUI.color = Color.white;
            float line = Screen.height - 32;
            GUI.Label(new Rect(16, line, 400, 22), target.Reload01 >= 1f ? "主砲: 装填完了" : $"主砲: 装填中 {target.Reload01 * 100f:0}%");
            if (target.secondaryTurrets != null)
                foreach (var st in target.secondaryTurrets)
                {
                    if (st == null) continue;
                    line -= 22;
                    string state = st.weapon == SecondaryWeapon.MachineGun
                        ? BeltText(st.Feed, st.machineGun)
                        : st.Ready ? "装填完了" : $"装填中 {st.Reload01 * 100f:0}%";
                    GUI.Label(new Rect(16, line, 400, 22), $"{st.name}: {state}");
                }
            if (target.machineGuns != null)
                foreach (var mg in target.machineGuns)
                {
                    if (mg == null || mg.spec == null || mg.spec.beltSize <= 0) continue;
                    line -= 22;
                    GUI.Label(new Rect(16, line, 400, 22), $"{mg.name}: {BeltText(mg.Feed, mg.spec)}");
                }
            GUI.Label(new Rect(16, line - 22, 400, 22), $"速度 {target.Speed * 3.6f:0} km/h");
            GUI.Label(new Rect(16, 12, 1400, 22), "W/S: 前進・後退   A/D: 旋回   マウス: 照準   左クリック: 主砲   Space: 機銃   中クリック/F: 副武装   右クリック: ズーム   ホイール: 距離   L: ライト   C: 見回す   "
                + (GetComponent<Sights.TankSightRig>() ? "Shift: 照準   V: 照準器   " : "") + "Esc: カーソル解放");
            if (FreeLook) GUI.Label(new Rect(cx - 60, cy + 40, 200, 22), "見回し中（砲塔固定）");
        }
    }
}
