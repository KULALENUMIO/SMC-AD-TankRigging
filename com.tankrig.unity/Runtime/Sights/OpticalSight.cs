using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>
    /// Direct optical sight (the gunner's telescope): the camera looks from the sight through a circular eyepiece at
    /// the magnification's true field of view, with an etched reticle (Western cross + mil scale, or Soviet chevron
    /// with stadiametric curve and ballistic range scales), the laser range and the ready lamp.
    /// </summary>
    [AddComponentMenu("Tank Rig/Sights/Optical Sight")]
    public class OpticalSight : TankSight
    {
        [Tooltip("数値（なければ既定値: 西側 3x / 10x）")] public OpticalSightProfile profile;

        OpticalSightProfile fallback;
        int zoomIndex;
        float fovNow = -1f;
        bool illuminated;
        float yaw, pitch;            // commanded line of sight (world, degrees)
        Vector3 aimPoint;
        bool hasAim;

        public OpticalSightProfile Profile => profile ? profile : fallback ? fallback : fallback = ScriptableObject.CreateInstance<OpticalSightProfile>();
        public override SightProfile BaseProfile => Profile;
        public override string HelpKeys => "N: 照明";
        public override bool GunFixed => fixedToGun;
        public int ZoomIndex { get => zoomIndex; set => zoomIndex = Mathf.Clamp(value, 0, Mathf.Max(0, Profile.zoom.Length - 1)); }
        bool Linked => Profile.lineOfSight == LineOfSight.GunLinked;

        ZoomStep Zoom => Profile.zoom != null && Profile.zoom.Length > 0 ? Profile.zoom[Mathf.Clamp(zoomIndex, 0, Profile.zoom.Length - 1)] : new ZoomStep();

        /// <summary>Camera vertical FOV that shows the step's true field of view across the eyepiece circle.</summary>
        float CameraFov(ZoomStep z)
        {
            float half = Mathf.Tan(Mathf.Max(0.1f, z.fieldOfView) * 0.5f * Mathf.Deg2Rad) / Mathf.Max(0.05f, Profile.eyepieceDiameter);
            return Mathf.Clamp(2f * Mathf.Atan(half) * Mathf.Rad2Deg, 0.2f, 120f);
        }

        public override void Enter(Camera cam, Vector3 aim)
        {
            illuminated = Profile.illuminated;
            fovNow = CameraFov(Zoom);
            cam.nearClipPlane = Profile.nearClip;
            if (fixedToGun)
            {
                EnterGunFixed();
                aimPoint = rig.AimAlong(GunPivot, gunCommand, Profile.aimRange);
                hasAim = true;
                return;
            }
            Vector3 d = aim - EyePosition(Linked);
            if (d.sqrMagnitude < 1f) d = GunForward();
            yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            pitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        public override void Steer(TankInputState input, float dt)
        {
            var p = Profile;
            if (input.illumination) illuminated = !illuminated;
            if (input.scroll != 0f)
            {
                if (p.zoom.Length > 1) ZoomIndex = zoomIndex + (input.scroll > 0f ? 1 : -1);
                else rig.ManualRange = Mathf.Clamp(rig.ManualRange + Mathf.Sign(input.scroll) * 100f, 0f, 9900f);
            }
            float s = ScaledSensitivity(p, fovNow > 0f ? fovNow : 20f);
            if (fixedToGun)
            {
                aimPoint = SteerGun(input.look, s, fovNow > 0f ? fovNow : 20f, p.aimRange);
                hasAim = true;
                return;
            }
            yaw += input.look.x * s;
            pitch = Mathf.Clamp(pitch - input.look.y * s, -30f, 40f);
            Vector3 dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            aimPoint = rig.AimAlong(EyePosition(Linked), dir, p.aimRange);
            hasAim = true;
        }

        public override void PlaceCamera(Camera cam, float dt)
        {
            fovNow = Mathf.Lerp(fovNow < 0f ? CameraFov(Zoom) : fovNow, CameraFov(Zoom), 1f - Mathf.Exp(-14f * dt));
            cam.fieldOfView = fovNow;
            Vector3 eye;
            Quaternion rot;
            if (fixedToGun) MountPose(out eye, out rot);   // fixed to the gun: only the gun moves it
            else if (Linked)
            {
                // the sight is fixed to the gun: it shows where the gun points, rolling with the turret
                eye = EyePosition(true);
                rot = rig.TurretFrame(true);
            }
            else { eye = EyePosition(false); rot = Quaternion.Euler(pitch, yaw, 0f); }
            cam.transform.SetPositionAndRotation(eye, rot);
        }

        public override void Exit(Camera cam)
        {
            if (fixedToGun && hideOwnTank) rig.SetOwnTankHidden(false);
        }

        public override void LookAt(Vector3 point)
        {
            if (fixedToGun) { CommandGunAt(point); aimPoint = point; hasAim = true; return; }
            Vector3 d = point - EyePosition(Linked);
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
                var c = rig.Camera;
                return new Ray(c.transform.position, c.transform.forward);
            }
        }

        // ------------------------------------------------------------------ symbology

        public override void Paint(SightPainter g, SightFrame f, Camera cam)
        {
            var p = Profile;
            float upt = SightPainter.Height * 0.5f / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);   // units per tangent
            float unitRad = SightMath.Rad(p.angleUnit);
            float U(float a) => Mathf.Tan(a * unitRad) * upt;                                         // reticle units → overlay units
            float lw = p.lineWidth;
            float r = SightPainter.Height * 0.5f * p.eyepieceDiameter;
            Color line = illuminated ? p.illuminationColor : p.reticleColor;

            // field: optics tint and fall-off towards the edge, then the eyepiece
            if (p.glassTint != Color.white)
            {
                var t = p.glassTint; t.a = 0.06f;
                g.Ring(Vector2.zero, 0f, r, t, t);
            }
            if (p.vignette > 0f) g.Ring(Vector2.zero, r * 0.55f, r, new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, p.vignette));
            if (illuminated) g.Ring(Vector2.zero, 0f, r, new Color(0f, 0f, 0f, 0.25f), new Color(0f, 0f, 0f, 0.25f));

            // reticle: the line of sight is the centre. A gun-linked sight's centre is the gun itself.
            if (p.style == ReticleStyle.Western) Western(g, p, U, lw, line);
            else Soviet(g, p, U, lw, line, upt, unitRad);

            if (p.showGunMarker && !Linked && !fixedToGun && rig.tank)
            {
                float dist = Vector3.Distance(rig.GunOrigin, aimPoint);
                if (Project(cam, rig.GunOrigin + rig.GunDirection * dist - cam.transform.position, upt, out var gp) && gp.magnitude < r)
                    g.Circle(gp, U(1f) + 4f, lw, line);
            }

            g.CircleMask(Vector2.zero, r, r * p.eyepieceSoftness, p.outsideColor);

            // readouts in the lower part of the field (as if projected into the eyepiece)
            float ts = p.textSize;
            var rc = p.readoutColor;
            float y = -r * 0.80f;
            if (p.showRange && p.rangefinder.enabled)
            {
                string rng = SightMath.RangeText(rig.LaserRange, p.rangefinder.digits);
                g.Text(rng, new Vector2(0f, y), ts * 1.25f, rc, new Vector2(0f, 0f));
                if (rig.LaserMultiple) g.Text("M", new Vector2(g.TextWidth(rng, ts * 1.25f) * 0.5f + 10f, y), ts, rc, new Vector2(-1f, 0f));
            }
            if (p.showAmmo && rig.Ammo != null) g.Text(rig.Ammo.code, new Vector2(-r * 0.30f, y), ts, rc, new Vector2(1f, 0f));
            if (p.showReady)
            {
                var lamp = rig.Ready ? p.readyColor : new Color(p.readyColor.r * 0.2f, p.readyColor.g * 0.2f, p.readyColor.b * 0.2f, 0.6f);
                g.Ring(new Vector2(r * 0.30f, y), 0f, 6f, lamp, lamp);
            }
            if (p.zoom.Length > 1 || p.style == ReticleStyle.Soviet)
            {
                string z = p.zoom.Length > 1 ? $"{Zoom.magnification:0.#}x" : $"{rig.ManualRange:0} м";
                g.Text(z, new Vector2(r * 0.62f, -r * 0.62f), ts * 0.85f, new Color(line.r, line.g, line.b, 0.8f), new Vector2(0f, 0f));
            }
        }

        static void Western(SightPainter g, OpticalSightProfile p, System.Func<float, float> U, float lw, Color c)
        {
            float gap = U(p.centerGap), arm = U(p.crossLength);
            g.Line(new Vector2(-gap - arm, 0f), new Vector2(-gap, 0f), lw, c);
            g.Line(new Vector2(gap, 0f), new Vector2(gap + arm, 0f), lw, c);
            g.Line(new Vector2(0f, -gap - arm), new Vector2(0f, -gap), lw, c);
            g.Line(new Vector2(0f, gap), new Vector2(0f, gap + arm * 0.5f), lw, c);
            g.Ring(Vector2.zero, 0f, Mathf.Max(lw, 1.2f), c, c);
            // horizontal mil scale beyond the cross arms, long marks every second tick, thick bars at the ends
            float tick = U(p.tickLength);
            if (p.tickSpacing > 0f)
            {
                int n = Mathf.FloorToInt(p.tickRange / p.tickSpacing + 0.001f);
                for (int i = 1; i <= n; i++)
                {
                    float a = i * p.tickSpacing;
                    if (a < p.centerGap + p.crossLength * 0.5f) continue;
                    float h = (i % 2 == 0 ? 1.6f : 1f) * tick;
                    float x = U(a);
                    g.Line(new Vector2(x, 0f), new Vector2(x, -h), lw, c);
                    g.Line(new Vector2(-x, 0f), new Vector2(-x, -h), lw, c);
                }
                float end = U(n * p.tickSpacing);
                g.Line(new Vector2(U(p.centerGap + p.crossLength), 0f), new Vector2(end, 0f), lw, c);
                g.Line(new Vector2(-U(p.centerGap + p.crossLength), 0f), new Vector2(-end, 0f), lw, c);
                g.Line(new Vector2(end, 0f), new Vector2(end + U(p.crossLength), 0f), lw * 3f, c);
                g.Line(new Vector2(-end, 0f), new Vector2(-end - U(p.crossLength), 0f), lw * 3f, c);
            }
        }

        void Soviet(SightPainter g, OpticalSightProfile p, System.Func<float, float> U, float lw, Color c, float upt, float unitRad)
        {
            // central chevron (the aim mark) with a short post below
            float h = U(p.chevronHeight);
            g.Line(new Vector2(-h * 0.75f, -h), Vector2.zero, lw, c);
            g.Line(Vector2.zero, new Vector2(h * 0.75f, -h), lw, c);
            g.Line(new Vector2(0f, -h * 1.6f), new Vector2(0f, -h * 3.2f), lw, c);
            // lead marks to the sides: short ticks, a longer one every fourth
            if (p.leadTickSpacing > 0f)
            {
                float tick = U(p.tickLength);
                for (int i = 1; i <= p.leadTickCount; i++)
                {
                    float x = U(i * p.leadTickSpacing);
                    float len = (i % 4 == 0 ? 1.8f : 1f) * tick;
                    g.Line(new Vector2(x, -h * 0.5f), new Vector2(x, -h * 0.5f - len), lw, c);
                    g.Line(new Vector2(-x, -h * 0.5f), new Vector2(-x, -h * 0.5f - len), lw, c);
                }
                float end = U(p.leadTickCount * p.leadTickSpacing);
                g.Line(new Vector2(U(p.leadTickSpacing) * 0.6f, -h * 0.5f), new Vector2(end, -h * 0.5f), lw, c);
                g.Line(new Vector2(-U(p.leadTickSpacing) * 0.6f, -h * 0.5f), new Vector2(-end, -h * 0.5f), lw, c);
            }

            // stadiametric rangefinder: a baseline and a curve whose height is the target height at each range
            if (p.stadiaRangefinder && p.stadiaRanges != null && p.stadiaRanges.Length > 1)
            {
                Vector2 o = new Vector2(U(p.stadiaOffset.x), U(p.stadiaOffset.y));
                float x0 = o.x - U(p.stadiaWidth), x1 = o.x;
                g.Line(new Vector2(x0, o.y), new Vector2(x1, o.y), lw, c);
                Vector2 prev = default;
                for (int i = 0; i < p.stadiaRanges.Length; i++)
                {
                    float rng = Mathf.Max(10f, p.stadiaRanges[i]);
                    float x = Mathf.Lerp(x1, x0, i / (float)(p.stadiaRanges.Length - 1));
                    float hh = Mathf.Atan(p.stadiaTargetHeight / rng) / unitRad;    // angle the target subtends
                    var pt = new Vector2(x, o.y + U(hh));
                    if (i > 0) g.Line(prev, pt, lw, c);
                    g.Line(new Vector2(x, o.y), new Vector2(x, o.y + U(0.4f)), lw, c);
                    if (i % 2 == 0) g.Text((rng / 100f).ToString("0"), new Vector2(x, o.y - 6f), p.textSize * 0.75f, c, new Vector2(0f, 1f));
                    prev = pt;
                }
            }

            // ballistic range scales: one column per round, marks where the gun must be raised for each range;
            // the horizontal index is the range the fire control uses (laser or hand-set)
            var ammo = rig.ammo;
            if (p.ballisticScales && ammo != null && ammo.Length > 0)
            {
                Vector2 o = new Vector2(U(p.scaleOffset.x), U(p.scaleOffset.y));
                float spacing = U(p.scaleSpacing);
                float tick = U(0.6f);
                float colTop = o.y;
                for (int a = 0; a < ammo.Length; a++)
                {
                    var am = ammo[a];
                    float x = o.x + a * spacing;
                    float maxEl = am.Superelevation(am.maxRange) / unitRad;
                    float scale = U(p.scaleHeight) / Mathf.Max(0.01f, maxEl);     // every column gets the same height
                    float bottom = colTop;
                    float top = colTop + maxEl * scale;
                    var colour = a == rig.ammoIndex ? c : new Color(c.r, c.g, c.b, c.a * 0.55f);
                    g.Line(new Vector2(x, bottom), new Vector2(x, top), lw, colour);
                    for (float rr = p.scaleStep; rr <= am.maxRange + 1f; rr += p.scaleStep)
                    {
                        float y = colTop + am.Superelevation(rr) / unitRad * scale;
                        bool major = p.scaleLabelStep > 0f && Mathf.Abs(Mathf.Repeat(rr + 0.5f, p.scaleLabelStep) - 0.5f) < 1f;
                        g.Line(new Vector2(x, y), new Vector2(x + (major ? tick * 1.8f : tick), y), lw, colour);
                        if (major) g.Text((rr / 100f).ToString("0"), new Vector2(x + tick * 2.2f, y), p.textSize * 0.75f, colour, new Vector2(-1f, 0f));
                    }
                    g.Text(am.code, new Vector2(x, top + 8f), p.textSize * 0.8f, colour, new Vector2(0f, -1f));
                    if (a == rig.ammoIndex)
                    {
                        float rr = Mathf.Clamp(rig.FireControlRange, 0f, am.maxRange);
                        float y = colTop + am.Superelevation(rr) / unitRad * scale;
                        g.Line(new Vector2(x - tick * 2f, y), new Vector2(x + tick * 3f, y), lw * 1.5f, c);
                    }
                }
            }
        }
    }
}
