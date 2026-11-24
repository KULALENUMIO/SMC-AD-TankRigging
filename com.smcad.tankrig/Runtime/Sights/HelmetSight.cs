using System.Collections.Generic;
using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>
    /// 360° helmet sight ("see-through armour" through VR goggles): the view turns with the head (VR headset, or the
    /// mouse without one), the own tank is hidden where the outside cameras cover, and conformal symbology is drawn
    /// inside the goggles' field of view: heading tape, horizon, gun line, hull outline, target markers, minimap.
    /// G designates the target under the gaze; the gun then stays on it (slew to cue) until G releases it.
    /// </summary>
    [AddComponentMenu("Tank Rig/Sights/Helmet Sight (VR)")]
    public class HelmetSight : TankSight
    {
        [Tooltip("数値（なければ既定値: 40°×30°、全周）")] public HelmetSightProfile profile;

        HelmetSightProfile fallback;
        float headYaw, headPitch;        // relative to the hull (degrees)
        bool xr;
        Quaternion xrRot = Quaternion.identity;
        Vector3 xrPos, xrPosOrigin;
        bool xrOriginSet;
        bool locked;
        Vector3 lockPoint;
        Transform lockTarget;
        Vector3 lockLocal;
        Vector3 gazePoint;
        bool seeThrough;
        readonly List<Transform> targets = new List<Transform>();
        Vector3 autoHead;            // automatic head offset (turret space), found on Enter
        Bounds hullBox;              // hull outline in hull space (without the turret), found on Enter
        bool hullBoxValid;
        float targetScan;

        public HelmetSightProfile Profile => profile ? profile : fallback ? fallback : fallback = ScriptableObject.CreateInstance<HelmetSightProfile>();
        public override SightProfile BaseProfile => Profile;
        public override string HelpKeys => "G: 目標指定・解除";
        public bool Locked => locked;
        public bool XRActive => xr;
        public Vector3 LockPoint => lockTarget ? lockTarget.TransformPoint(lockLocal) : lockPoint;

        // the tank root: its axes are the vehicle's (the model's hull transform can have any axes)
        Transform Hull => rig && rig.tank ? rig.tank.transform : transform;

        Vector3 HeadPosition()
        {
            if (eyePoint) return eyePoint.position;
            var t = rig ? rig.tank : null;
            if (!t) return transform.position;
            Transform turret = t.turret ? t.turret : t.transform;
            Vector3 off = Profile.headOffset;
            if (off == Vector3.zero) off = autoHead;
            Vector3 p = turret.position + rig.TurretFrame(false) * off;
            if (xr) p += Hull.rotation * (xrPos - xrPosOrigin);
            return p;
        }

        void Measure()
        {
            var t = rig ? rig.tank : null;
            autoHead = new Vector3(0.35f, 1f, -0.4f);
            hullBoxValid = false;
            if (!t) return;
            Transform turret = t.turret ? t.turret : t.transform;
            // head: on top of the turret, a little behind its pivot
            var b = new Bounds(turret.position, Vector3.zero);
            foreach (var r in turret.GetComponentsInChildren<Renderer>())
                if (!(r is ParticleSystemRenderer)) b.Encapsulate(r.bounds);
            autoHead = new Vector3(0.35f, b.max.y - turret.position.y + 0.25f, -0.4f);
            // hull outline: the hull's meshes (not the turret's) in hull space
            Transform hull = Hull;
            foreach (var mf in hull.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh || (t.turret && mf.transform.IsChildOf(t.turret))) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var hp = hull.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!hullBoxValid) { hullBox = new Bounds(hp, Vector3.zero); hullBoxValid = true; } else hullBox.Encapsulate(hp);
                }
            }
        }

        public override void Enter(Camera cam, Vector3 aim)
        {
            Measure();
            ReadXR();
            xrOriginSet = false;
            Vector3 d = Quaternion.Inverse(Hull.rotation) * (aim - HeadPosition());
            headYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            headPitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            cam.nearClipPlane = Profile.nearClip;
            cam.fieldOfView = Profile.desktopFov;
            seeThrough = false;
            targetScan = 0f;
        }

        public override void Exit(Camera cam)
        {
            rig.SetOwnTankHidden(false);
            seeThrough = false;
        }

        void ReadXR()
        {
            xr = false;
#if TANKRIG_XR
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.CenterEye);
            if (dev.isValid && dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyeRotation, out Quaternion q))
            {
                xr = true;
                xrRot = q;
                dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyePosition, out xrPos);
                if (!xrOriginSet) { xrPosOrigin = xrPos; xrOriginSet = true; }
            }
#endif
        }

        Vector3 GazeDirection => rig.Camera.transform.forward;

        public override void Steer(TankInputState input, float dt)
        {
            var p = Profile;
            ReadXR();
            if (!xr)
            {
                float s = ScaledSensitivity(p, p.desktopFov);
                headYaw += input.look.x * s;
                headYaw = p.headYawLimit >= 179.9f ? Mathf.Repeat(headYaw + 180f, 360f) - 180f : Mathf.Clamp(headYaw, -p.headYawLimit, p.headYawLimit);
                headPitch = Mathf.Clamp(headPitch - input.look.y * s, -p.headPitchLimit.y, -p.headPitchLimit.x);
            }
            Vector3 head = HeadPosition();
            Vector3 gaze = Hull.rotation * (xr ? xrRot * Vector3.forward : Quaternion.Euler(headPitch, headYaw, 0f) * Vector3.forward);
            gazePoint = rig.AimAlong(head, gaze, p.aimRange);
            if (input.designate)
            {
                if (locked) { locked = false; lockTarget = null; }
                else if (TankPhysics.Raycast(head, gaze, p.aimRange, ~0, rig.tank ? rig.tank.transform : null, out var hit))
                {
                    locked = true;
                    lockPoint = hit.point;
                    // stick to a moving thing (another tank, a rigidbody)
                    lockTarget = hit.rigidbody ? hit.rigidbody.transform : hit.collider.GetComponentInParent<TankController>() ? hit.collider.GetComponentInParent<TankController>().transform : null;
                    if (lockTarget) lockLocal = lockTarget.InverseTransformPoint(hit.point);
                }
            }
        }

        public override void PlaceCamera(Camera cam, float dt)
        {
            var p = Profile;
            Quaternion local = xr ? xrRot : Quaternion.Euler(headPitch, headYaw, 0f);
            cam.transform.SetPositionAndRotation(HeadPosition(), Hull.rotation * local);
            if (!xr) cam.fieldOfView = p.desktopFov;
            // see through the hull only where the outside cameras look (elevation from the hull's plane)
            float el = 90f - Vector3.Angle(Hull.up, cam.transform.forward);
            bool want = p.seeThroughArmor && el >= p.coverageDown && el <= p.coverageUp;
            if (want != seeThrough) { seeThrough = want; rig.SetOwnTankHidden(want); }
        }

        public override void LookAt(Vector3 point)
        {
            Vector3 d = Quaternion.Inverse(Hull.rotation) * (point - HeadPosition());
            if (d.sqrMagnitude < 1e-4f) return;
            headYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            headPitch = -Mathf.Asin(Mathf.Clamp(d.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            gazePoint = point;
        }

        /// <summary>Designate a target point (the gun stays on it). <paramref name="follow"/>: a moving object to stick to.</summary>
        public void Designate(Vector3 point, Transform follow = null)
        {
            locked = true;
            lockPoint = point;
            lockTarget = follow;
            if (follow) lockLocal = follow.InverseTransformPoint(point);
        }

        public void ReleaseDesignation() { locked = false; lockTarget = null; }

        public override bool TryGetAim(out Vector3 point)
        {
            if (locked) { point = LockPoint; return true; }
            point = gazePoint;
            return Profile.gunFollowsHead;
        }

        public override Ray LaserRay
        {
            get
            {
                var t = rig.Camera.transform;
                return new Ray(t.position, t.forward);
            }
        }

        public override void SetupFrame(SightFrame f, Camera cam)
        {
            base.SetupFrame(f, cam);
            if (xr)
            {
                // XR: the overlay covers the goggles' field of view and is laid over each eye by direction
                var p = Profile;
                float tx = Mathf.Tan(p.displayFov.x * 0.5f * Mathf.Deg2Rad), ty = Mathf.Tan(p.displayFov.y * 0.5f * Mathf.Deg2Rad);
                f.height = 1620;
                f.width = Mathf.RoundToInt(1620 * tx / ty);
                f.tanHalf = new Vector2(tx, ty);
                f.angular = true;
            }
        }

        // ------------------------------------------------------------------ symbology

        void ScanTargets()
        {
            targets.Clear();
            foreach (var t in FindObjectsByType<TankController>(FindObjectsSortMode.None))
                if (t && (!rig.tank || t != rig.tank)) targets.Add(t.transform);
            foreach (var t in TankSightTarget.All)
                if (t && !targets.Contains(t.transform)) targets.Add(t.transform);
        }

        public override void Paint(SightPainter g, SightFrame f, Camera cam)
        {
            var p = Profile;
            float tanV = f.angular ? f.tanHalf.y : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float upt = SightPainter.Height * 0.5f / tanV;
            float bx = Mathf.Tan(p.displayFov.x * 0.5f * Mathf.Deg2Rad) * upt, by = Mathf.Tan(p.displayFov.y * 0.5f * Mathf.Deg2Rad) * upt;
            bx = Mathf.Min(bx, g.HalfWidth - 4f); by = Mathf.Min(by, SightPainter.Height * 0.5f - 4f);
            var box = new Rect(-bx, -by, bx * 2f, by * 2f);
            Color sc = p.symbolColor, ol = p.symbolOutline;
            float lw = p.lineWidth, ts = p.textSize;
            Vector3 camPos = cam.transform.position;
            if (!f.angular) g.Brackets(box, 40f, lw, new Color(sc.r, sc.g, sc.b, 0.5f));

            float heading = TankSightRig.Bearing(cam.transform.forward);
            float hullHeading = TankSightRig.Bearing(Hull.forward);
            float gunHeading = TankSightRig.Bearing(rig.GunDirection);

            // conformal: horizon, hull outline, gun line, targets
            if (p.showHorizon)
            {
                var l = Quaternion.Euler(0f, heading - 30f, 0f) * Vector3.forward;
                var r = Quaternion.Euler(0f, heading + 30f, 0f) * Vector3.forward;
                if (Project(cam, l, upt, out var a) && Project(cam, r, upt, out var b))
                {
                    var mid = (a + b) * 0.5f; var d = (b - a).normalized;
                    ClipLine(g, a, mid - d * 60f, box, lw, sc);
                    ClipLine(g, mid + d * 60f, b, box, lw, sc);
                }
            }
            if (p.showHullOutline && seeThrough) HullOutline(g, cam, upt, box, new Color(sc.r, sc.g, sc.b, 0.45f), lw);

            if (p.showGunLine && rig.tank)
            {
                float dist = Mathf.Max(50f, Vector3.Distance(rig.GunOrigin, rig.tank.AimPoint));
                Vector3 gp = rig.GunOrigin + rig.GunDirection * dist;
                Marker(g, cam, gp - camPos, upt, box, (g2, at) =>
                {
                    g2.Circle(at, 12f, lw, sc);
                    g2.Line(at + new Vector2(-20f, 0f), at + new Vector2(-12f, 0f), lw, sc);
                    g2.Line(at + new Vector2(12f, 0f), at + new Vector2(20f, 0f), lw, sc);
                    g2.Line(at + new Vector2(0f, 12f), at + new Vector2(0f, 20f), lw, sc);
                }, sc, lw);
            }

            targetScan -= Time.unscaledDeltaTime;
            if (targetScan <= 0f) { ScanTargets(); targetScan = 1f; }
            foreach (var t in targets)
            {
                if (!t) continue;
                Vector3 tp = t.position + Vector3.up * 1.2f;
                float dist = Vector3.Distance(camPos, tp);
                if (dist > p.markerRange) continue;
                var tag = t.GetComponent<TankSightTarget>();
                Color c = tag && tag.kind == TankSightTarget.Kind.Hostile ? p.threatColor : sc;
                string label = (tag && !string.IsNullOrEmpty(tag.label) ? tag.label + " " : "") + $"{dist:0}";
                Marker(g, cam, tp - camPos, upt, box, (g2, at) =>
                {
                    g2.Box(new Rect(at.x - 16f, at.y - 16f, 32f, 32f), lw, c);
                    g2.Text(label, at + new Vector2(20f, 16f), ts * 0.75f, c, new Vector2(-1f, 0f), ol);
                }, c, lw);
            }

            if (locked)
            {
                Vector3 lp = LockPoint;
                float dist = Vector3.Distance(camPos, lp);
                Marker(g, cam, lp - camPos, upt, box, (g2, at) =>
                {
                    var dm = new[] { at + new Vector2(0f, 22f), at + new Vector2(22f, 0f), at + new Vector2(0f, -22f), at + new Vector2(-22f, 0f) };
                    g2.Polyline(dm, true, lw * 1.5f, p.threatColor);
                    g2.Text($"LOCK {dist:0}", at + new Vector2(0f, -36f), ts * 0.8f, p.threatColor, new Vector2(0f, 0f), ol);
                }, p.threatColor, lw);
            }

            // head-fixed: centre cue, heading tape, readouts, minimap
            g.Line(new Vector2(-10f, 0f), new Vector2(-4f, 0f), lw, sc);
            g.Line(new Vector2(4f, 0f), new Vector2(10f, 0f), lw, sc);
            g.Line(new Vector2(0f, -10f), new Vector2(0f, -4f), lw, sc);

            HeadingTape(g, new Vector2(0f, box.yMax - 40f), bx * 0.62f, heading, hullHeading, gunHeading, p, sc, ol, lw, ts);

            float el = -cam.transform.eulerAngles.x;
            el = Mathf.DeltaAngle(0f, el);
            g.Text($"EL {el:+00;-00}", new Vector2(box.xMin + 16f, 0f), ts * 0.85f, sc, new Vector2(-1f, 0f), ol);
            g.Text($"TRT {Mathf.Repeat(rig.TurretAzimuth, 360f):000}", new Vector2(box.xMax - 16f, 0f), ts * 0.85f, sc, new Vector2(1f, 0f), ol);

            float ry = box.yMin + 24f;
            string rng = p.rangefinder.enabled ? "RNG " + SightMath.RangeText(rig.LaserRange, p.rangefinder.digits) + (rig.LaserMultiple ? " M" : "") : "";
            g.Text(rng, new Vector2(0f, ry), ts, sc, Vector2.zero, ol);
            if (rig.Ammo != null) g.Text(rig.Ammo.name, new Vector2(-bx * 0.35f, ry), ts * 0.85f, sc, Vector2.zero, ol);
            if (rig.tank) g.Text(rig.Ready ? "RDY" : $"LOAD {rig.tank.Reload01 * 100f:0}%", new Vector2(bx * 0.35f, ry), ts * 0.85f, rig.Ready ? sc : p.threatColor, Vector2.zero, ol);
            string mode = (p.seeThroughArmor ? (seeThrough ? "SEE-THRU" : "HULL") : "DIRECT") + (locked ? "  LOCK" : p.gunFollowsHead ? "  SLAVE" : "") + (xr ? "  HMD" : "");
            g.Text(mode, new Vector2(0f, ry + ts * 1.3f), ts * 0.75f, sc, Vector2.zero, ol);

            if (p.showMinimap) Minimap(g, new Vector2(box.xMax - p.minimapRadius - 20f, box.yMin + p.minimapRadius + 70f), p.minimapRadius, heading, hullHeading, gunHeading, camPos, p, sc, ol, lw, ts);
        }

        /// <summary>Draws a marker where a world direction appears; outside the goggles an arrow at the edge points to it.</summary>
        void Marker(SightPainter g, Camera cam, Vector3 dir, float upt, Rect box, System.Action<SightPainter, Vector2> draw, Color c, float lw)
        {
            bool front = Project(cam, dir, upt, out var at);
            var inner = new Rect(box.x + 30f, box.y + 30f, box.width - 60f, box.height - 60f);
            if (front && inner.Contains(at)) { draw(g, at); return; }
            // direction on the screen plane even when behind
            Vector3 l = cam.transform.InverseTransformDirection(dir);
            var d2 = new Vector2(l.x, l.y);
            if (d2.sqrMagnitude < 1e-8f) d2 = Vector2.down;
            d2.Normalize();
            float k = Mathf.Min(inner.width * 0.5f / Mathf.Max(1e-4f, Mathf.Abs(d2.x)), inner.height * 0.5f / Mathf.Max(1e-4f, Mathf.Abs(d2.y)));
            var e = inner.center + d2 * k;
            var n = new Vector2(-d2.y, d2.x);
            g.FillPoly(new[] { e + d2 * 14f, e + n * 8f, e - n * 8f }, c);
        }

        static void ClipLine(SightPainter g, Vector2 a, Vector2 b, Rect r, float w, Color c)
        {
            // Liang–Barsky
            float t0 = 0f, t1 = 1f;
            Vector2 d = b - a;
            float[] p = { -d.x, d.x, -d.y, d.y };
            float[] q = { a.x - r.xMin, r.xMax - a.x, a.y - r.yMin, r.yMax - a.y };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Abs(p[i]) < 1e-6f) { if (q[i] < 0f) return; continue; }
                float t = q[i] / p[i];
                if (p[i] < 0f) t0 = Mathf.Max(t0, t); else t1 = Mathf.Min(t1, t);
                if (t0 > t1) return;
            }
            g.Line(a + d * t0, a + d * t1, w, c);
        }

        static readonly int[] Edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };

        void HullOutline(SightPainter g, Camera cam, float upt, Rect box, Color c, float lw)
        {
            if (!rig.tank || !hullBoxValid) return;
            Transform hull = Hull;
            var b = hullBox;
            var pts = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                var hp = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                pts[i] = cam.transform.InverseTransformPoint(hull.TransformPoint(hp));
            }
            const float near = 0.05f;
            for (int e = 0; e < Edges.Length; e += 2)
            {
                Vector3 a = pts[Edges[e]], bb = pts[Edges[e + 1]];
                if (a.z < near && bb.z < near) continue;
                if (a.z < near) a = Vector3.Lerp(a, bb, (near - a.z) / (bb.z - a.z));
                if (bb.z < near) bb = Vector3.Lerp(bb, a, (near - bb.z) / (a.z - bb.z));
                ClipLine(g, new Vector2(a.x / a.z, a.y / a.z) * upt, new Vector2(bb.x / bb.z, bb.y / bb.z) * upt, box, lw, c);
            }
        }

        static float Delta(float bearing, float heading) => Mathf.DeltaAngle(heading, bearing);

        void HeadingTape(SightPainter g, Vector2 c, float halfW, float heading, float hull, float gun, HelmetSightProfile p, Color sc, Color ol, float lw, float ts)
        {
            float span = Mathf.Max(5f, p.headingSpan);
            float k = halfW / span;
            g.Line(new Vector2(c.x - halfW, c.y), new Vector2(c.x + halfW, c.y), lw, sc);
            float tick = Mathf.Max(1f, p.headingTick);
            float start = Mathf.Ceil((heading - span) / tick) * tick;
            for (float b = start; b <= heading + span + 0.01f; b += tick)
            {
                float x = c.x + (b - heading) * k;
                float bb = Mathf.Repeat(b, 360f);
                bool label = p.headingLabel > 0f && Mathf.Abs(Mathf.Repeat(bb + 0.5f, p.headingLabel) - 0.5f) < 0.6f;
                g.Line(new Vector2(x, c.y), new Vector2(x, c.y + (label ? 12f : 6f)), lw, sc);
                if (label)
                {
                    int ib = Mathf.RoundToInt(bb) % 360;
                    string s = ib == 0 ? "N" : ib == 90 ? "E" : ib == 180 ? "S" : ib == 270 ? "W"
                        : p.angleUnit == AngleUnit.Degree ? ib.ToString("000") : (ib * Mathf.Deg2Rad / SightMath.Rad(p.angleUnit)).ToString("0000");
                    g.Text(s, new Vector2(x, c.y + 26f), ts * 0.75f, sc, Vector2.zero, ol);
                }
            }
            // current heading box
            string hs = p.angleUnit == AngleUnit.Degree ? $"{Mathf.Repeat(heading, 360f):000}" : $"{Mathf.Repeat(heading, 360f) * Mathf.Deg2Rad / SightMath.Rad(p.angleUnit):0000}";
            float w = g.TextWidth(hs, ts) + 14f;
            var hb = new Rect(c.x - w * 0.5f, c.y - 34f, w, 24f);
            g.Fill(hb, new Color(0f, 0f, 0f, 0.35f));
            g.Box(hb, lw, sc);
            g.Text(hs, hb.center, ts, sc, Vector2.zero);
            g.FillPoly(new[] { new Vector2(c.x - 6f, c.y - 10f), new Vector2(c.x + 6f, c.y - 10f), new Vector2(c.x, c.y - 2f) }, sc);
            // hull and gun bearings on the tape (clamped to its ends)
            float hx = c.x + Mathf.Clamp(Delta(hull, heading), -span, span) * k;
            g.Text("H", new Vector2(hx, c.y - 6f), ts * 0.7f, sc, new Vector2(0f, 1f), ol);
            float gx = c.x + Mathf.Clamp(Delta(gun, heading), -span, span) * k;
            g.FillPoly(new[] { new Vector2(gx - 5f, c.y + 2f), new Vector2(gx + 5f, c.y + 2f), new Vector2(gx, c.y + 10f) }, sc);
        }

        void Minimap(SightPainter g, Vector2 c, float r, float heading, float hull, float gun, Vector3 camPos, HelmetSightProfile p, Color sc, Color ol, float lw, float ts)
        {
            g.Ring(c, 0f, r, new Color(0f, 0f, 0f, 0.25f), new Color(0f, 0f, 0f, 0.25f));
            float maxRange = 1f;
            if (p.rangeRings != null) foreach (var rr in p.rangeRings) maxRange = Mathf.Max(maxRange, rr);
            if (p.rangeRings != null)
                foreach (var rr in p.rangeRings)
                {
                    float rad = r * rr / maxRange;
                    g.Circle(c, rad, lw * 0.7f, new Color(sc.r, sc.g, sc.b, 0.4f));
                }
            g.Text($"{maxRange:0}", c + new Vector2(r * 0.72f, -r * 0.72f), ts * 0.6f, new Color(sc.r, sc.g, sc.b, 0.7f), new Vector2(-1f, 0f));
            // heading-up: a bearing b is drawn at angle (b - heading) from up
            Vector2 At(float bearing, float frac)
            {
                float a = Delta(bearing, heading) * Mathf.Deg2Rad;
                return c + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * (r * frac);
            }
            // goggles' field of view
            g.Line(c, At(heading - Profile.displayFov.x * 0.5f, 1f), lw * 0.7f, new Color(sc.r, sc.g, sc.b, 0.5f));
            g.Line(c, At(heading + Profile.displayFov.x * 0.5f, 1f), lw * 0.7f, new Color(sc.r, sc.g, sc.b, 0.5f));
            // own tank: hull and gun
            float hr = Delta(hull, heading) * Mathf.Deg2Rad;
            Vector2 R(Vector2 v) => c + new Vector2(v.x * Mathf.Cos(hr) + v.y * Mathf.Sin(hr), -v.x * Mathf.Sin(hr) + v.y * Mathf.Cos(hr));
            g.Polyline(new[] { R(new Vector2(-6f, -10f)), R(new Vector2(6f, -10f)), R(new Vector2(6f, 10f)), R(new Vector2(-6f, 10f)) }, true, lw, sc);
            g.Line(c, At(gun, 0.25f), lw * 1.5f, sc);
            foreach (var t in targets)
            {
                if (!t) continue;
                Vector3 d = t.position - camPos;
                float dist = new Vector2(d.x, d.z).magnitude;
                if (dist > maxRange) continue;
                var tag = t.GetComponent<TankSightTarget>();
                Color col = tag && tag.kind == TankSightTarget.Kind.Hostile ? p.threatColor : sc;
                var at = At(TankSightRig.Bearing(d), dist / maxRange);
                g.Ring(at, 0f, 4f, col, col);
            }
            if (locked)
            {
                Vector3 d = LockPoint - camPos;
                float dist = new Vector2(d.x, d.z).magnitude;
                var at = At(TankSightRig.Bearing(d), Mathf.Min(1f, dist / maxRange));
                g.Polyline(new[] { at + new Vector2(0f, 6f), at + new Vector2(6f, 0f), at + new Vector2(0f, -6f), at + new Vector2(-6f, 0f) }, true, lw, p.threatColor);
            }
        }
    }
}
