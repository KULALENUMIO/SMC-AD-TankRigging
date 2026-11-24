using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Sights
{
    /// <summary>
    /// Digital fire-control display: a stabilised sensor (thermal or day camera) renders at its own resolution and
    /// frame rate, and the picture is shown on a monitor of any aspect ratio with fire-control symbology
    /// (cross, mil scale, tracking gate, range, ammunition, ready, turret/hull bearing, gun elevation, sensor state).
    /// Optical fields of view and electronic zoom are stepped with the wheel; T changes camera, B the polarity.
    /// </summary>
    [AddComponentMenu("Tank Rig/Sights/Display Sight")]
    public class DisplaySight : TankSight
    {
        [Tooltip("数値（なければ既定値: サーマル 4:3、1024×768）")] public DisplaySightProfile profile;

        DisplaySightProfile fallback;
        int channel, step;
        bool blackHot;
        float yaw, pitch;
        Vector3 aimPoint;
        bool hasAim;
        Camera sensorCam;
        RenderTexture sensorRT;
        float frameTimer;
        Rect imageRect;

        public DisplaySightProfile Profile => profile ? profile : fallback ? fallback : fallback = ScriptableObject.CreateInstance<DisplaySightProfile>();
        public override SightProfile BaseProfile => Profile;
        public override string HelpKeys => "T: カメラ切替   B: 白黒反転";
        public override bool GunFixed => fixedToGun;
        public Camera SensorCamera => sensorCam;

        SensorChannel Channel => Profile.channels != null && Profile.channels.Length > 0
            ? Profile.channels[Mathf.Clamp(channel, 0, Profile.channels.Length - 1)] : new SensorChannel();

        int StepCount => Mathf.Max(1, Channel.fieldsOfView.Length) + (Profile.electronicZoom != null ? Profile.electronicZoom.Length : 0);

        /// <summary>Optical horizontal FOV (degrees) and electronic zoom of the current step.</summary>
        void StepFov(out float fov, out float eZoom, out string label)
        {
            var ch = Channel;
            int n = Mathf.Max(1, ch.fieldsOfView.Length);
            int s = Mathf.Clamp(step, 0, StepCount - 1);
            if (s < n)
            {
                fov = ch.fieldsOfView.Length > 0 ? ch.fieldsOfView[s] : 15f;
                eZoom = 1f;
                label = ch.fieldNames != null && s < ch.fieldNames.Length ? ch.fieldNames[s] : $"FOV{s + 1}";
            }
            else
            {
                fov = ch.fieldsOfView.Length > 0 ? ch.fieldsOfView[n - 1] : 15f;
                eZoom = Mathf.Max(1f, Profile.electronicZoom[s - n]);
                label = (ch.fieldNames != null && n - 1 < ch.fieldNames.Length ? ch.fieldNames[n - 1] : "FOV") + $" E{eZoom:0.#}x";
            }
        }

        /// <summary>Horizontal field of view (degrees) currently shown on the monitor.</summary>
        public float ShownFov
        {
            get
            {
                StepFov(out float fov, out float ez, out _);
                return 2f * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / ez) * Mathf.Rad2Deg;
            }
        }

        public void SetStep(int channelIndex, int stepIndex)
        {
            channel = Mathf.Clamp(channelIndex, 0, Mathf.Max(0, Profile.channels.Length - 1));
            step = Mathf.Clamp(stepIndex, 0, StepCount - 1);
        }

        public override void Enter(Camera cam, Vector3 aim)
        {
            blackHot = Profile.blackHot;
            if (fixedToGun)
            {
                EnterGunFixed();
                aimPoint = rig.AimAlong(GunPivot, gunCommand, Profile.aimRange);
                hasAim = true;
            }
            else
            {
                Vector3 d = aim - EyePosition(false);
                if (d.sqrMagnitude < 1f) d = GunForward();
                yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                pitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            EnsureSensor(cam);
            sensorCam.enabled = true;
            frameTimer = 0f;
            // the screen around the monitor is the inside of the tank: don't draw the world twice
            cam.cullingMask = 0;
        }

        public override void Exit(Camera cam)
        {
            if (sensorCam) sensorCam.enabled = false;
            if (fixedToGun && hideOwnTank) rig.SetOwnTankHidden(false);
        }

        void OnDestroy()
        {
            if (sensorRT) { sensorRT.Release(); Destroy(sensorRT); }
            if (sensorCam) Destroy(sensorCam.gameObject);
        }

        void EnsureSensor(Camera main)
        {
            var p = Profile;
            var res = new Vector2Int(Mathf.Clamp(p.sensorResolution.x, 64, 4096), Mathf.Clamp(p.sensorResolution.y, 64, 4096));
            if (!sensorRT || sensorRT.width != res.x || sensorRT.height != res.y)
            {
                if (sensorRT) { sensorRT.Release(); Destroy(sensorRT); }
                sensorRT = new RenderTexture(res.x, res.y, 24, RenderTextureFormat.ARGB32) { name = "Sight Sensor", hideFlags = HideFlags.DontSave };
                sensorRT.Create();
            }
            sensorRT.filterMode = p.pointFilter ? FilterMode.Point : FilterMode.Bilinear;
            if (!sensorCam)
            {
                var go = new GameObject("Sight Sensor Camera") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                sensorCam = go.AddComponent<Camera>();
                var hd = go.AddComponent<HDAdditionalCameraData>();
                var src = main.GetComponent<HDAdditionalCameraData>();
                if (src) src.CopyTo(hd);
                sensorCam.CopyFrom(main);
                sensorCam.depth = main.depth - 1;
            }
            sensorCam.targetTexture = sensorRT;
            sensorCam.cullingMask = rig && rig.Camera ? SavedMask(main) : ~0;
            sensorCam.nearClipPlane = p.nearClip;
            sensorCam.farClipPlane = Mathf.Max(main.farClipPlane, p.aimRange);
        }

        // the main camera's mask before this sight cleared it
        static int SavedMask(Camera main) => main.cullingMask == 0 ? ~0 : main.cullingMask;

        public override void Steer(TankInputState input, float dt)
        {
            var p = Profile;
            if (input.sensorMode && p.channels.Length > 1)
            {
                channel = (channel + 1) % p.channels.Length;
                step = Mathf.Clamp(step, 0, StepCount - 1);
            }
            if (input.polarity) blackHot = !blackHot;
            if (input.scroll != 0f) step = Mathf.Clamp(step + (input.scroll > 0f ? 1 : -1), 0, StepCount - 1);
            float s = ScaledSensitivity(p, ShownFov * 0.75f);
            if (fixedToGun)
            {
                aimPoint = SteerGun(input.look, s, ShownFov, p.aimRange);
                hasAim = true;
                return;
            }
            yaw += input.look.x * s;
            pitch = Mathf.Clamp(pitch - input.look.y * s, -30f, 40f);
            aimPoint = rig.AimAlong(EyePosition(false), Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward, p.aimRange);
            hasAim = true;
        }

        public override void PlaceCamera(Camera cam, float dt)
        {
            Vector3 eye;
            Quaternion rot;
            if (fixedToGun) MountPose(out eye, out rot);   // fixed to the gun: only the gun moves it
            else { eye = EyePosition(false); rot = Quaternion.Euler(pitch, yaw, 0f); }
            cam.transform.SetPositionAndRotation(eye, rot);
            if (!sensorCam || !sensorRT) EnsureSensor(cam);
            var p = Profile;
            StepFov(out float fov, out _, out _);
            float aspect = sensorRT.width / (float)sensorRT.height;
            sensorCam.aspect = aspect;
            sensorCam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
            sensorCam.transform.SetPositionAndRotation(eye, cam.transform.rotation);   // the rig's firing shake included
            // the sensor's own frame rate: render only when a new frame is due
            if (p.frameRate > 0f)
            {
                frameTimer -= dt;
                bool due = frameTimer <= 0f;
                if (due) frameTimer += 1f / p.frameRate;
                if (frameTimer < -0.25f) frameTimer = 0f;
                sensorCam.enabled = due;
            }
            else sensorCam.enabled = true;
        }

        public override void LookAt(Vector3 point)
        {
            if (fixedToGun) { CommandGunAt(point); aimPoint = point; hasAim = true; return; }
            Vector3 d = point - EyePosition(false);
            if (d.sqrMagnitude < 1e-4f) return;
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            aimPoint = point;
            hasAim = true;
        }

        public override bool TryGetAim(out Vector3 point)
        {
            point = aimPoint;
            return hasAim;
        }

        public override Ray LaserRay
        {
            get
            {
                var t = sensorCam ? sensorCam.transform : rig.Camera.transform;
                return new Ray(t.position, t.forward);
            }
        }

        public override void SetupFrame(SightFrame f, Camera cam)
        {
            base.SetupFrame(f, cam);
            var p = Profile;
            var ch = Channel;
            f.sensor = sensorRT;
            int mode = ch.type == SensorType.Thermal ? 2 : ch.type == SensorType.DayMono ? 1 : 0;
            f.sensorParams = new Vector4(mode, blackHot ? 1f : 0f, ch.type == SensorType.Thermal ? p.gain : 1f, ch.type == SensorType.Thermal ? p.level : 0.5f);
            f.sensorParams2 = new Vector4(ch.type == SensorType.DayColour ? p.noise * 0.2f : p.noise, ch.type == SensorType.Thermal ? p.fixedPatternNoise : 0f, p.scanlines, Time.time * 37.0f % 1000f);
            f.sensorParams3 = new Vector4(p.vignette, 0f, p.saturation, p.brightness);
            f.sensorSize = sensorRT ? new Vector4(sensorRT.width, sensorRT.height, 1f / sensorRT.width, 1f / sensorRT.height) : Vector4.one;
            f.sensorTint = mode == 0 ? Color.white : p.imageTint;
        }

        // ------------------------------------------------------------------ symbology

        public override void Paint(SightPainter g, SightFrame f, Camera cam)
        {
            var p = Profile;
            float H = SightPainter.Height;
            // monitor: inner (picture) and outer (with the bezel) rectangles, shrunk to fit the screen
            float aspect = Mathf.Max(0.2f, p.monitorAspect.x / Mathf.Max(0.01f, p.monitorAspect.y));
            float mh = H * p.monitorHeight, mw = mh * aspect;
            float maxW = g.HalfWidth * 2f - 2f * p.bezel - 8f;
            if (mw > maxW) { mw = maxW; mh = mw / aspect; }
            Vector2 c = p.monitorCenter * H;
            var inner = new Rect(c.x - mw * 0.5f, c.y - mh * 0.5f, mw, mh);
            var outer = new Rect(inner.x - p.bezel, inner.y - p.bezel, inner.width + 2f * p.bezel, inner.height + 2f * p.bezel);
            g.FillOutside(outer, p.cabinColor);
            g.Fill(outer, p.bezelColor);
            g.Box(new Rect(outer.x + 3f, outer.y + 3f, outer.width - 6f, outer.height - 6f), 1.5f, new Color(0.2f, 0.21f, 0.22f, 1f));
            g.Fill(inner, Color.black);

            // the picture: sensor aspect into the monitor; electronic zoom crops the middle
            StepFov(out float fov, out float ez, out string fovLabel);
            float sa = sensorRT ? sensorRT.width / (float)sensorRT.height : 4f / 3f;
            Rect img = inner;
            Rect uv = new Rect(0f, 0f, 1f, 1f);
            if (p.fit == ImageFit.Fit)
            {
                if (sa > aspect) { float h = mw / sa; img = new Rect(inner.x, c.y - h * 0.5f, mw, h); }
                else { float w = mh * sa; img = new Rect(c.x - w * 0.5f, inner.y, w, mh); }
            }
            else if (p.fit == ImageFit.Fill)
            {
                if (sa > aspect) { float u = aspect / sa; uv = new Rect(0.5f - u * 0.5f, 0f, u, 1f); }
                else { float v = sa / aspect; uv = new Rect(0f, 0.5f - v * 0.5f, 1f, v); }
            }
            uv = new Rect(0.5f + (uv.x - 0.5f) / ez, 0.5f + (uv.y - 0.5f) / ez, uv.width / ez, uv.height / ez);
            g.Sensor(img, uv, Color.white);
            imageRect = img;

            // angles on the picture: the visible horizontal tangent spans the image width
            float tanVisible = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * uv.width;   // uv.width already includes the e-zoom
            float upt = img.width * 0.5f / Mathf.Max(1e-4f, tanVisible);
            float unitRad = SightMath.Rad(p.angleUnit);
            float U(float a) => Mathf.Tan(a * unitRad) * upt;
            Vector2 o = img.center;
            float lw = p.lineWidth, ts = p.textSize;
            Color sc = p.symbolColor, ol = p.symbolOutline;

            // cross with a centre gap, mil scale on the horizontal line, tracking gate
            float gap = img.width * p.crossGap, arm = img.width * p.crossArm;
            foreach (var col in new[] { ol, sc })
            {
                float w = col == ol ? lw + 2f : lw;
                g.Line(o + new Vector2(-gap - arm, 0f), o + new Vector2(-gap, 0f), w, col);
                g.Line(o + new Vector2(gap, 0f), o + new Vector2(gap + arm, 0f), w, col);
                g.Line(o + new Vector2(0f, -gap - arm), o + new Vector2(0f, -gap), w, col);
                g.Line(o + new Vector2(0f, gap), o + new Vector2(0f, gap + arm * 0.6f), w, col);
            }
            if (p.tickSpacing > 0f)
            {
                int n = Mathf.FloorToInt(p.tickRange / p.tickSpacing + 0.001f);
                for (int i = 1; i <= n; i++)
                {
                    float x = U(i * p.tickSpacing);
                    if (x > img.width * 0.48f) break;
                    if (x <= gap + arm) continue;
                    float h = i % 2 == 0 ? 10f : 6f;
                    g.Line(o + new Vector2(x, -h), o + new Vector2(x, h), lw, sc);
                    g.Line(o + new Vector2(-x, -h), o + new Vector2(-x, h), lw, sc);
                }
            }
            if (p.trackGate > 0f)
            {
                float gs = Mathf.Max(12f, img.width * p.trackGate);
                g.Brackets(new Rect(o.x - gs, o.y - gs * 0.75f, gs * 2f, gs * 1.5f), gs * 0.35f, lw, sc);
            }
            if (p.showGunMarker && !fixedToGun && rig.tank && sensorCam)
            {
                float dist = Mathf.Max(50f, Vector3.Distance(rig.GunOrigin, aimPoint));
                Vector3 gd = rig.GunOrigin + rig.GunDirection * dist - sensorCam.transform.position;
                Vector3 l = sensorCam.transform.InverseTransformDirection(gd);
                if (l.z > 0f)
                {
                    var gp = o + new Vector2(l.x / l.z, l.y / l.z) * upt;
                    if (img.Contains(gp) && (gp - o).magnitude > 3f) g.Circle(gp, 7f, lw, sc);
                }
            }

            // text rows inside the picture
            float m = 14f;
            var align = new Vector2(-1f, 1f);
            string pol = Channel.type == SensorType.Thermal ? (blackHot ? "BH" : "WH") : "";
            g.Text($"{Channel.name}  {fovLabel}  {pol}", new Vector2(img.xMin + m, img.yMax - m), ts, sc, align, ol);
            g.Text($"{ShownFov:0.0}°", new Vector2(img.xMin + m, img.yMax - m - ts * 1.3f), ts * 0.85f, sc, align, ol);

            float by = img.yMin + m + ts * 0.6f;
            var lrf = p.rangefinder;
            if (lrf.enabled)
            {
                string rng = SightMath.RangeText(rig.LaserRange, lrf.digits);
                float bw = g.TextWidth(rng, ts * 1.4f) + 24f;
                var box = new Rect(o.x - bw * 0.5f, by - ts * 0.9f, bw, ts * 1.8f);
                g.Fill(box, new Color(0f, 0f, 0f, 0.45f));
                g.Box(box, lw, sc);
                g.Text(rng, new Vector2(o.x, by), ts * 1.4f, sc, Vector2.zero);
                if (rig.LaserMultiple) g.Text("M", new Vector2(box.xMax + 8f, by), ts, p.warnColor, new Vector2(-1f, 0f), ol);
                if (rig.LaserAge < 0.6f) g.Text("LRF", new Vector2(box.xMin - 8f, by), ts, sc, new Vector2(1f, 0f), ol);
            }
            if (rig.Ammo != null) g.Text(rig.Ammo.name, new Vector2(img.xMin + m, by), ts, sc, new Vector2(-1f, 0f), ol);
            if (rig.tank)
            {
                string st = rig.Ready ? "RDY" : $"LOAD {rig.tank.Reload01 * 100f:0}%";
                g.Text(st, new Vector2(img.xMin + m, by + ts * 1.4f), ts, rig.Ready ? sc : p.warnColor, new Vector2(-1f, 0f), ol);
            }
            if (!string.IsNullOrEmpty(p.stabilizationText)) g.Text(p.stabilizationText, new Vector2(img.xMax - m, by), ts, sc, new Vector2(1f, 0f), ol);
            float elU = rig.GunElevation * Mathf.Deg2Rad / unitRad;
            g.Text($"EL {elU:+0.0;-0.0} {SightMath.Short(p.angleUnit)}", new Vector2(img.xMax - m, by + ts * 1.4f), ts * 0.85f, sc, new Vector2(1f, 0f), ol);

            if (p.showAzimuth) Azimuth(g, new Vector2(img.xMax - 60f, img.yMax - 60f), 40f, lw, sc, ol, p);
            if (p.showElevation) Elevation(g, new Rect(img.xMax - 34f, o.y - img.height * 0.25f, 14f, img.height * 0.5f), lw, sc, ol);
        }

        /// <summary>Turret-up bearing indicator: the turret points up, the hull turns under it; the number is the turret bearing.</summary>
        void Azimuth(SightPainter g, Vector2 c, float r, float lw, Color sc, Color ol, DisplaySightProfile p)
        {
            float az = rig.TurretAzimuth;
            g.Circle(c, r, lw, sc);
            for (int i = 0; i < 12; i++)
            {
                float a = i * 30f * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                g.Line(c + d * (r - (i % 3 == 0 ? 8f : 4f)), c + d * r, lw, sc);
            }
            // hull: a rectangle turned by -azimuth (the turret is "up")
            float rad = -az * Mathf.Deg2Rad;
            Vector2 R(Vector2 v) => c + new Vector2(v.x * Mathf.Cos(rad) + v.y * Mathf.Sin(rad), -v.x * Mathf.Sin(rad) + v.y * Mathf.Cos(rad));
            float hw = r * 0.35f, hl = r * 0.6f;
            var pts = new[] { R(new Vector2(-hw, -hl)), R(new Vector2(hw, -hl)), R(new Vector2(hw, hl)), R(new Vector2(-hw, hl)) };
            g.Polyline(pts, true, lw, sc);
            g.Line(R(new Vector2(-hw * 0.6f, hl * 0.75f)), R(new Vector2(0f, hl)), lw, sc);
            g.Line(R(new Vector2(hw * 0.6f, hl * 0.75f)), R(new Vector2(0f, hl)), lw, sc);
            g.Line(c, c + new Vector2(0f, r * 0.95f), lw * 2f, sc);
            g.Ring(c, 0f, 4f, sc, sc);
            float azU = Mathf.Repeat(az, 360f) * Mathf.Deg2Rad / SightMath.Rad(p.angleUnit);
            g.Text(p.angleUnit == AngleUnit.Degree ? $"{Mathf.Repeat(az, 360f):000}" : $"{azU:0000}", c + new Vector2(0f, -r - 14f), p.textSize * 0.8f, sc, new Vector2(0f, 0f), ol);
        }

        /// <summary>Gun elevation scale (−10° to +20°) with a pointer.</summary>
        void Elevation(SightPainter g, Rect r, float lw, Color sc, Color ol)
        {
            const float lo = -10f, hi = 20f;
            g.Line(new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), lw, sc);
            for (float e = lo; e <= hi + 0.1f; e += 5f)
            {
                float y = Mathf.Lerp(r.yMin, r.yMax, (e - lo) / (hi - lo));
                g.Line(new Vector2(r.xMax - (e == 0f ? r.width : r.width * 0.5f), y), new Vector2(r.xMax, y), lw, sc);
            }
            float ey = Mathf.Lerp(r.yMin, r.yMax, Mathf.InverseLerp(lo, hi, rig.GunElevation));
            g.FillPoly(new[] { new Vector2(r.xMin - 12f, ey + 6f), new Vector2(r.xMin - 2f, ey), new Vector2(r.xMin - 12f, ey - 6f) }, sc);
        }
    }
}
