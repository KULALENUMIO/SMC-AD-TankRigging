using System.Collections.Generic;
using UnityEngine;

namespace TankRig
{
    /// <summary>
    /// One side's track: the wheels of the track model plus a chain of link instances laid along the belt
    /// that wraps the wheels (2D convex hull of the wheel circles, in the plane perpendicular to the axle).
    /// The links move along the belt and the wheels spin according to <see cref="speed"/> (world m/s, + = tank forward).
    ///
    /// Torsion-bar suspension: every road wheel hangs on an arm whose pivot is behind the wheel. The wheel's
    /// position in the model is its static position on the ground; without load it drops a little (and swings
    /// slightly backwards on the arc), when pushed it rises. The belt is as long as it is with every road wheel
    /// fully dropped, so at rest the top run sags a little and the track is tight when the wheels hang free.
    ///
    /// Links are generated (never saved), so the same component works in edit mode as a live preview.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10)]
    [AddComponentMenu("SMC-AD/Tank Rigging/Tank Track")]
    public class TankTrack : MonoBehaviour
    {
        public enum WheelRole { Road, Return, End }

        [System.Serializable]
        public class Wheel
        {
            public Transform transform;
            public WheelRole role;
            [Tooltip("転輪の半径（履帯ローカル単位）")] public float radius = 0.3f;
            public Quaternion restRotation = Quaternion.identity;
            [Tooltip("地面での静止位置（親のローカル）")] public Vector3 restPosition;
            public bool restCaptured;
            [Tooltip("転輪の幅（車軸方向、ローカル単位）。0 ならメッシュから求める")] public float width;
        }

        public List<Wheel> wheels = new List<Wheel>();
        [Tooltip("履帯リンクのモデル（1枚分）")] public GameObject linkPrefab;
        [Tooltip("リンクの間隔（ピン間の距離）")] public float pitch = 0.25f;
        [Tooltip("転輪の表面からリンクのピン軸までの距離")] public float pitchLineOffset = 0.03125f;
        [Tooltip("リンクのピン軸から接地面までの距離")] public float linkGroundDepth = 0.094f;
        [Tooltip("リンクの向きの補正（度）")] public Vector3 linkEulerOffset;
        [Tooltip("戦車の前方向（このオブジェクトのローカル）")] public Vector3 forwardLocal = Vector3.right;
        [Tooltip("上方向（ローカル）")] public Vector3 upLocal = Vector3.up;
        [Tooltip("車軸の方向（ローカル）")] public Vector3 axleLocal = Vector3.forward;
        [Tooltip("エディタでもリンクを表示する")] public bool previewInEditor = true;

        [Header("サスペンション（トーションバー）※長さはこのオブジェクトのローカル単位")]
        public bool suspension = true;
        [Tooltip("荷重がないときに転輪が下がる量")] public float droop = 0.08f;
        [Tooltip("押されたときに転輪が上がる量")] public float bump = 0.28f;
        [Tooltip("アームの長さ（支点は転輪の後ろ側）")] public float armLength = 0.45f;
        [Tooltip("静止時のアームの角度（支点から見て水平より下、度）")] public float armAngle = 12f;
        [Tooltip("転輪が地面を追う速さ")] public float suspensionResponse = 18f;
        [Tooltip("履帯の上側のたわみの上限")] public float maxSag = 0.12f;
        public LayerMask groundMask = ~0;

        /// <summary>Track surface speed in world units per second (+ = tank moves forward).</summary>
        [System.NonSerialized] public float speed;

        const string ContainerName = "__TrackLinks";

        [Tooltip("下側の履帯を地面（石・段差）に沿わせる（再生中のみ）")] public bool conformToGround = true;
        [Tooltip("カメラからこの距離（m）より遠いと、下側の履帯の地形追従を省く（負荷軽減）。0 = 常に行う")] public float conformMaxDistance = 80f;
        [Tooltip("接地を調べる幅方向の本数（1 = 中央だけ、3 = 両端と中央）")] [Range(1, 5)] public int widthSamples = 3;

        /// <summary>Set by a physics-driven <see cref="TankController"/>: it probes the ground with <see cref="Probe"/> and the track only renders.</summary>
        [System.NonSerialized] public bool externalSuspension;
        /// <summary>Colliders under this transform are ignored by the ground rays (the tank itself).</summary>
        [System.NonSerialized] public Transform ignoreRoot;

        /// <summary>Ground contact of one road wheel, in world units.</summary>
        public struct Contact
        {
            public Vector3 point, normal;
            /// <summary>How far the wheel is pushed up from fully dropped (can exceed <see cref="maxTravel"/>: bump stop).</summary>
            public float travel;
            /// <summary>Travel at the static position (= droop).</summary>
            public float restTravel;
            public float maxTravel;
        }

        struct Arm
        {
            public bool active;
            public Vector2 rest, pivot;
            public float depth, aMin, aMax, angle, target, prevTarget;
        }

        Transform container;
        readonly List<Transform> links = new List<Transform>();
        readonly List<Vector2> path = new List<Vector2>();
        readonly List<float> cumulative = new List<float>();
        readonly List<Vector2> samples = new List<Vector2>();
        readonly List<Vector3> circles = new List<Vector3>(); // wheel circles of the pitch line: x, y = centre, z = radius
        Arm[] arms = new Arm[0];
        float length, depth, offset, beltLength, previewTravel;
        float[] wheelAngles;
        bool dirty;

        /// <summary>Current belt path length in local units.</summary>
        public float PathLength => length;
        /// <summary>Belt length (tight with every road wheel fully dropped), local units.</summary>
        public float BeltLength => beltLength;
        /// <summary>Largest sag of the top run right now, local units.</summary>
        public float CurrentSag { get; private set; }
        public int LinkCount => links.Count;
        /// <summary>The belt's pitch line in the track's frame (x along forwardLocal, y along upLocal), local units. Rebuilt as the suspension moves; read-only.</summary>
        public IReadOnlyList<Vector2> PathPoints => path;
        /// <summary>Distance along the pitch line at each PathPoints entry (same count), local units.</summary>
        public IReadOnlyList<float> PathDistances => cumulative;
        /// <summary>Where the links sit along axleLocal, local units.</summary>
        public float LinkDepth => depth;
        /// <summary>How far the links have run round the belt (local units, wraps at PathLength).</summary>
        public float BeltTravel => offset;

        void OnEnable() => Rebuild();

        void OnDisable()
        {
            ClearLinks();
            if (!Application.isPlaying) ResetWheelsToRest();
        }

        void OnValidate() => dirty = true;

        /// <summary>Edit-mode preview of the suspension: -1 = fully compressed, 0 = static, +1 = fully dropped.</summary>
        public void SetPreview(float travel)
        {
            previewTravel = Mathf.Clamp(travel, -1f, 1f);
            if (!Application.isPlaying) Rebuild();
        }

        public void Rebuild()
        {
            CaptureRest();
            SetupArms();
            beltLength = ComputeBeltLength();
            if (!Application.isPlaying)
            {
                for (int i = 0; i < arms.Length; i++)
                {
                    var a = arms[i];
                    if (!a.active) continue;
                    float rest = Rest(a);
                    a.angle = previewTravel >= 0f ? Mathf.Lerp(rest, a.aMax, previewTravel) : Mathf.Lerp(rest, a.aMin, -previewTravel);
                    arms[i] = a;
                }
            }
            ApplyWheelPositions();
            BuildPath();
            ClearLinks();
            if (linkPrefab != null && path.Count > 2 && (Application.isPlaying || previewInEditor))
                CreateLinks();
            Layout();
        }

        void Update()
        {
            if (!Application.isPlaying)
            {
                if (dirty) { dirty = false; Rebuild(); }
                return;
            }
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            if (suspension)
            {
                if (!externalSuspension)
                {
                    var m = transform.localToWorldMatrix;
                    for (int i = 0; i < arms.Length; i++)
                        if (arms[i].active) Probe(i, m, out _);
                }
                if (externalSuspension)
                {
                    // the physics probes at fixed steps and the hull is drawn interpolated between the last two steps:
                    // draw the wheels the same way, so they sit exactly on the ground the hull is drawn over (no lag)
                    float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
                    for (int i = 0; i < arms.Length; i++)
                    {
                        if (!arms[i].active) continue;
                        var a = arms[i];
                        a.angle = Mathf.Lerp(a.prevTarget, a.target, alpha);
                        arms[i] = a;
                    }
                }
                else
                {
                    float k = 1f - Mathf.Exp(-suspensionResponse * dt);
                    for (int i = 0; i < arms.Length; i++)
                    {
                        if (!arms[i].active) continue;
                        var a = arms[i];
                        a.angle = Mathf.Lerp(a.angle, a.target, k);
                        arms[i] = a;
                    }
                }
                ApplyWheelPositions();
            }
            BuildPath();
            float scale = Mathf.Abs(transform.lossyScale.y);
            if (scale < 1e-5f || length <= 0f) return;
            float d = speed * dt / scale;
            // The belt is CCW in (forward, up): along +s the bottom run moves forward.
            // When the tank drives forward the bottom run must move backward relative to the hull.
            offset = Mathf.Repeat(offset - d, length);
            RotateWheels(d);
            Layout();
        }

        // ------------------------------------------------------------------ suspension

        void CaptureRest()
        {
            foreach (var w in wheels)
            {
                if (w == null || w.transform == null || w.restCaptured) continue;
                w.restPosition = w.transform.localPosition;
                w.restCaptured = true;
            }
        }

        void ResetWheelsToRest()
        {
            foreach (var w in wheels)
                if (w != null && w.transform != null && w.restCaptured && w.transform.localPosition != w.restPosition)
                    w.transform.localPosition = w.restPosition;
        }

        float ArmLen => Mathf.Max(0.01f, armLength);
        float Rest(Arm a) => armAngle * Mathf.Deg2Rad;
        Vector2 ArmPos(Arm a, float angle) => a.pivot + new Vector2(ArmLen * Mathf.Cos(angle), -ArmLen * Mathf.Sin(angle));

        Vector3 RestLocal(Wheel w) =>
            w.transform.parent == transform ? w.restPosition : transform.InverseTransformPoint(w.transform.parent.TransformPoint(w.restPosition));

        void SetupArms()
        {
            var old = arms;
            arms = new Arm[wheels.Count];
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float L = ArmLen, a0 = armAngle * Mathf.Deg2Rad, s0 = Mathf.Sin(a0);
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                if (w == null || w.transform == null) continue;
                Vector3 r = RestLocal(w);
                var arm = new Arm
                {
                    rest = new Vector2(Vector3.Dot(r, F), Vector3.Dot(r, U)),
                    depth = Vector3.Dot(r, A),
                    active = suspension && w.role == WheelRole.Road && w.transform.parent == transform,
                    aMax = Mathf.Asin(Mathf.Clamp(s0 + Mathf.Max(0f, droop) / L, -0.99f, 0.99f)),
                    aMin = Mathf.Asin(Mathf.Clamp(s0 - Mathf.Max(0f, bump) / L, -0.99f, 0.99f)),
                    angle = a0,
                    target = a0,
                };
                // pivot behind the wheel: dropping swings the wheel down and slightly backwards
                arm.pivot = arm.rest + new Vector2(-L * Mathf.Cos(a0), L * s0);
                if (Application.isPlaying && i < old.Length)
                {
                    arm.angle = Mathf.Clamp(old[i].angle, arm.aMin, arm.aMax);
                    arm.target = Mathf.Clamp(old[i].target, arm.aMin, arm.aMax);
                }
                arms[i] = arm;
            }
        }

        public int ArmCount => arms.Length;
        public bool IsSuspended(int i) => i >= 0 && i < arms.Length && arms[i].active;

        /// <summary>
        /// Casts the ground ray of road wheel <paramref name="i"/> with the given track-to-world matrix (the physics passes the
        /// rigidbody's exact pose), sets the wheel's target position for rendering and returns the contact.
        /// No hit = the wheel hangs fully dropped.
        /// </summary>
        public bool Probe(int i, Matrix4x4 trackToWorld, out Contact contact)
        {
            contact = default;
            if (!IsSuspended(i)) return false;
            var a = arms[i];
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float scaleU = trackToWorld.MultiplyVector(U).magnitude;
            if (scaleU < 1e-5f) return false;
            float L = ArmLen;
            float c = wheels[i].radius + pitchLineOffset + linkGroundDepth;
            Vector2 top = ArmPos(a, a.aMin) + new Vector2(0f, 0.25f);
            Vector2 bottom = ArmPos(a, a.aMax);
            float reach = top.y - bottom.y + c + 0.1f;
            contact.restTravel = (ArmPos(a, Rest(a)).y - bottom.y) * scaleU;
            contact.maxTravel = (ArmPos(a, a.aMin).y - bottom.y) * scaleU;
            a.prevTarget = a.target;
            a.target = a.aMax; // no load: hang down
            bool grounded = false;
            if (CircleOnGround(trackToWorld, top.x, top.y, a.depth, c, WheelWidth(i), reach, out float centerY, out Vector3 point, out Vector3 normal))
            {
                a.target = Mathf.Asin(Mathf.Clamp((a.pivot.y - centerY) / L, Mathf.Sin(a.aMin), Mathf.Sin(a.aMax)));
                contact.travel = (centerY - bottom.y) * scaleU;
                contact.point = point;
                contact.normal = normal;
                grounded = true;
            }
            arms[i] = a;
            return grounded;
        }

        static readonly float[] circleOffsets = { -0.85f, -0.5f, 0f, 0.5f, 0.85f };

        /// <summary>Wheel width along the axle (local units): set by the builder, else from the mesh.</summary>
        float WheelWidth(int i)
        {
            var w = wheels[i];
            if (w.width > 0f) return w.width;
            var mf = w.transform ? w.transform.GetComponent<MeshFilter>() : null;
            w.width = mf && mf.sharedMesh ? mf.sharedMesh.bounds.size.z * Mathf.Abs(w.transform.localScale.z) : w.radius;
            return w.width;
        }

        float TrackWidth()
        {
            float wmax = 0f;
            for (int i = 0; i < wheels.Count; i++)
                if (wheels[i] != null && wheels[i].transform) wmax = Mathf.Max(wmax, WheelWidth(i));
            return wmax;
        }

        /// <summary>Lateral sample positions across a width (fractions of the half width): 1 = centre, 3 = edges and centre.</summary>
        float LateralOffset(int j, float width)
        {
            int n = Mathf.Max(1, widthSamples);
            if (n == 1) return 0f;
            return Mathf.Lerp(-0.4f, 0.4f, j / (float)(n - 1)) * width;
        }

        /// <summary>
        /// Where a wheel (circle of radius <paramref name="r"/>, in the track plane) has to be so it does not dig into the ground:
        /// vertical rays along the track (5 positions over the circle) and across the track (<see cref="widthSamples"/>);
        /// the highest "ground + circle" wins. A wheel meeting a step therefore rides up its edge as it rolls on, and a rock
        /// under the edge of the track lifts it too.
        /// </summary>
        bool CircleOnGround(Matrix4x4 m, float x, float topY, float depthA, float r, float width, float reach, out float centerY, out Vector3 point, out Vector3 normal)
        {
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float scaleU = m.MultiplyVector(U).magnitude;
            Vector3 down = m.MultiplyVector(-U).normalized;
            centerY = float.MinValue;
            point = normal = Vector3.zero;
            bool found = false;
            int lateral = Mathf.Max(1, widthSamples);
            foreach (float k in circleOffsets)
            {
                float dx = k * r;
                float lift = Mathf.Sqrt(r * r - dx * dx);
                for (int j = 0; j < lateral; j++)
                {
                    Vector3 origin = m.MultiplyPoint3x4(F * (x + dx) + U * topY + A * (depthA + LateralOffset(j, width)));
                    if (!TankPhysics.Raycast(origin, down, reach * scaleU, groundMask, ignoreRoot, out RaycastHit hit)) continue;
                    float need = topY - hit.distance / scaleU + lift;
                    if (need <= centerY) continue;
                    centerY = need;
                    point = hit.point;
                    normal = hit.normal;
                    found = true;
                }
            }
            return found;
        }

        public bool IsEndWheel(int i) => i >= 0 && i < wheels.Count && wheels[i] != null && wheels[i].transform && wheels[i].role == WheelRole.End;

        /// <summary>
        /// Sprocket / idler: fixed, but the belt wrapped round it touches steps and logs first. Returns how far an obstacle
        /// pushes into its circle (world units) and from where, so the physics can push back softly (no rigid edge to bounce off).
        /// </summary>
        public bool ProbeEnd(int i, Matrix4x4 trackToWorld, out Vector3 point, out Vector3 pushDir, out float penetration)
        {
            point = pushDir = Vector3.zero;
            penetration = 0f;
            if (!IsEndWheel(i) || i >= arms.Length) return false;
            var a = arms[i];
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float scaleU = trackToWorld.MultiplyVector(U).magnitude;
            if (scaleU < 1e-5f) return false;
            float r = wheels[i].radius + pitchLineOffset + linkGroundDepth;
            float topY = a.rest.y + r + 0.3f;
            if (!CircleOnGround(trackToWorld, a.rest.x, topY, a.depth, r, WheelWidth(i), 2f * r + 0.3f, out float need, out point, out _)) return false;
            if (need <= a.rest.y) return false;
            penetration = (need - a.rest.y) * scaleU;
            Vector3 centre = trackToWorld.MultiplyPoint3x4(F * a.rest.x + U * a.rest.y + A * a.depth);
            pushDir = (centre - point).normalized;
            return true;
        }

        void ApplyWheelPositions()
        {
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized;
            for (int i = 0; i < wheels.Count && i < arms.Length; i++)
            {
                var w = wheels[i];
                if (w == null || w.transform == null || !w.restCaptured) continue;
                Vector3 p = w.restPosition;
                if (arms[i].active)
                {
                    Vector2 d = ArmPos(arms[i], arms[i].angle) - arms[i].rest;
                    p += F * d.x + U * d.y;
                }
                if ((w.transform.localPosition - p).sqrMagnitude > 1e-12f) w.transform.localPosition = p;
            }
        }

        // ------------------------------------------------------------------ belt path

        void SampleWheels(bool allDropped)
        {
            samples.Clear();
            circles.Clear();
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float depthSum = 0f;
            int n = 0;
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                if (w == null || w.transform == null) continue;
                Vector2 c2;
                Vector3 c = transform.InverseTransformPoint(w.transform.position);
                if (allDropped && i < arms.Length && arms[i].active) c2 = ArmPos(arms[i], arms[i].aMax);
                else c2 = new Vector2(Vector3.Dot(c, F), Vector3.Dot(c, U));
                depthSum += Vector3.Dot(c, A);
                n++;
                float r = w.radius + pitchLineOffset;
                circles.Add(new Vector3(c2.x, c2.y, r));
                const int steps = 32;
                for (int k = 0; k < steps; k++)
                {
                    float ang = k * Mathf.PI * 2f / steps;
                    samples.Add(c2 + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r);
                }
            }
            if (n > 0) depth = depthSum / n;
        }

        static float PolyLength(List<Vector2> p)
        {
            float l = 0f;
            for (int i = 0; i < p.Count; i++) l += Vector2.Distance(p[i], p[(i + 1) % p.Count]);
            return l;
        }

        float ComputeBeltLength()
        {
            SampleWheels(suspension);
            return samples.Count > 2 ? PolyLength(ConvexHull(samples)) : 0f;
        }

        void BuildPath()
        {
            path.Clear();
            cumulative.Clear();
            length = 0f;
            CurrentSag = 0f;
            SampleWheels(false);
            if (samples.Count < 3) return;
            var hull = ConvexHull(samples);
            if (conformToGround && Application.isPlaying) hull = ConformToGround(hull);
            float slack = suspension ? Mathf.Max(0f, beltLength - PolyLength(hull)) : 0f;
            if (slack > 1e-5f) AddSag(hull, slack);
            else path.AddRange(hull);
            for (int i = 0; i < path.Count; i++)
            {
                cumulative.Add(length);
                length += Vector2.Distance(path[i], path[(i + 1) % path.Count]);
            }
        }

        /// <summary>
        /// Lets the free spans of the top run hang down so the belt uses up its slack (parabolic sag).
        /// A straight top edge of the hull is split where it rests on a wheel (e.g. return rollers in line with the idler),
        /// so the belt sags between the supports instead of through them.
        /// </summary>
        void AddSag(List<Vector2> hull, float slack)
        {
            int n = hull.Count;
            float threshold = Mathf.Max(0.2f, pitch);
            var spans = new List<(int edge, Vector2 a, Vector2 b, float len, Vector2 outward)>();
            for (int i = 0; i < n; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % n];
                if (!IsTopSpan(a, b, threshold, out float d, out Vector2 outward)) continue;
                Vector2 dir = (b - a) / d;
                var cuts = new List<float> { 0f, 1f };
                foreach (var c in circles)
                {
                    var center = new Vector2(c.x, c.y);
                    if (Mathf.Abs(Vector2.Dot(center - a, outward) + c.z) > 0.01f) continue; // not touching this edge
                    float t = Vector2.Dot(center + outward * c.z - a, dir) / d;
                    if (t > 0.01f && t < 0.99f) cuts.Add(t);
                }
                cuts.Sort();
                for (int k = 0; k + 1 < cuts.Count; k++)
                {
                    float len = (cuts[k + 1] - cuts[k]) * d;
                    if (len > 0.15f) spans.Add((i, a + dir * (cuts[k] * d), a + dir * (cuts[k + 1] * d), len, outward));
                }
            }
            float total = 0f;
            foreach (var s in spans) total += s.len;
            if (total <= 0f) { path.AddRange(hull); return; }
            int si = 0;
            for (int i = 0; i < n; i++)
            {
                path.Add(hull[i]);
                for (; si < spans.Count && spans[si].edge == i; si++)
                {
                    var s = spans[si];
                    float share = slack * s.len / total;
                    float h = Mathf.Min(maxSag, Mathf.Sqrt(3f * s.len * share / 8f)); // parabola: extra length ≈ 8h²/(3d)
                    CurrentSag = Mathf.Max(CurrentSag, h);
                    path.Add(s.a);
                    const int steps = 10;
                    for (int k = 1; k < steps; k++)
                    {
                        float t = k / (float)steps;
                        path.Add(Vector2.Lerp(s.a, s.b, t) - s.outward * (4f * h * t * (1f - t)));
                    }
                    path.Add(s.b);
                }
            }
            // drop duplicate consecutive points (span ends that coincide with hull corners)
            for (int i = path.Count - 1; i > 0; i--)
                if ((path[i] - path[i - 1]).sqrMagnitude < 1e-10f) path.RemoveAt(i);
        }

        /// <summary>
        /// Lower run: where the ground between the road wheels is higher than the straight belt (a rock, a step, a crest),
        /// lift the belt onto it. The belt never goes below the line between the wheels (it is pulled tight there).
        /// One short ray per link pitch, rendering only.
        /// </summary>
        List<Vector2> ConformToGround(List<Vector2> hull)
        {
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float scaleU = Mathf.Abs(transform.lossyScale.y);
            if (scaleU < 1e-5f) return hull;
            if (conformMaxDistance > 0f)
            {
                var cam = Camera.main;
                if (cam && (cam.transform.position - transform.position).sqrMagnitude > conformMaxDistance * conformMaxDistance) return hull;
            }
            Vector3 down = transform.TransformDirection(-U).normalized;
            float step = Mathf.Max(0.1f, pitch);
            float width = TrackWidth();
            int lateral = Mathf.Max(1, widthSamples);
            const float above = 0.8f, maxLift = 0.6f;
            var result = new List<Vector2>(hull.Count + 48);
            int n = hull.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % n];
                result.Add(a);
                Vector2 v = b - a;
                float d = v.magnitude;
                if (d < step * 1.5f) continue;
                Vector2 outward = new Vector2(v.y, -v.x) / d;
                if (outward.y > -0.3f) continue; // only the lower run and the slanted ends
                int steps = Mathf.CeilToInt(d / step);
                for (int k = 1; k < steps; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, k / (float)steps);
                    float reach = (above + linkGroundDepth + 0.05f) * scaleU;
                    float needed = float.MinValue;
                    for (int j = 0; j < lateral; j++)
                    {
                        Vector3 origin = transform.TransformPoint(F * p.x + U * (p.y + above) + A * (depth + LateralOffset(j, width)));
                        if (TankPhysics.Raycast(origin, down, reach, groundMask, ignoreRoot, out RaycastHit hit))
                            needed = Mathf.Max(needed, p.y + above - hit.distance / scaleU + linkGroundDepth);
                    }
                    if (needed > p.y) p.y = Mathf.Min(needed, p.y + maxLift);
                    result.Add(p);
                }
            }
            return result;
        }

        static bool IsTopSpan(Vector2 a, Vector2 b, float threshold, out float d, out Vector2 outward)
        {
            Vector2 v = b - a;
            d = v.magnitude;
            outward = d > 1e-6f ? new Vector2(v.y, -v.x) / d : Vector2.zero; // right-hand normal of a CCW loop
            return d > threshold && outward.y > 0.5f;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>Andrew's monotone chain; returns the hull counter-clockwise.</summary>
        static List<Vector2> ConvexHull(List<Vector2> input)
        {
            var p = new List<Vector2>(input);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new List<Vector2>();
            foreach (var pt in p)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], pt) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(pt);
            }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0f) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            if (h.Count > 0) h.RemoveAt(h.Count - 1);
            return h;
        }

        Vector2 Evaluate(float s)
        {
            s = Mathf.Repeat(s, length);
            int lo = 0, hi = cumulative.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (cumulative[mid] <= s) lo = mid; else hi = mid - 1;
            }
            int next = (lo + 1) % path.Count;
            float segEnd = lo + 1 < cumulative.Count ? cumulative[lo + 1] : length;
            float segLen = segEnd - cumulative[lo];
            float t = segLen > 1e-6f ? (s - cumulative[lo]) / segLen : 0f;
            return Vector2.Lerp(path[lo], path[next], t);
        }

        // ------------------------------------------------------------------ links

        void CreateLinks()
        {
            var go = new GameObject(ContainerName);
            if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable | HideFlags.HideInHierarchy;
            container = go.transform;
            container.SetParent(transform, false);
            int count = Mathf.Max(4, Mathf.RoundToInt(Mathf.Max(beltLength, length) / Mathf.Max(0.01f, pitch)));
            for (int i = 0; i < count; i++)
            {
                var link = Instantiate(linkPrefab, container, false);
                link.name = "Link_" + i;
                if (!Application.isPlaying) link.hideFlags = go.hideFlags;
                links.Add(link.transform);
            }
        }

        void ClearLinks()
        {
            links.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var c = transform.GetChild(i);
                if (c.name != ContainerName) continue;
                if (Application.isPlaying) Destroy(c.gameObject);
                else DestroyImmediate(c.gameObject);
            }
            container = null;
        }

        void Layout()
        {
            if (links.Count == 0 || length <= 0f) return;
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            var extra = Quaternion.Euler(linkEulerOffset);
            float spacing = length / links.Count;
            for (int i = 0; i < links.Count; i++)
            {
                float s = offset + i * spacing;
                // place each link between its two pins so it looks right around the wheels
                Vector2 a = Evaluate(s - spacing * 0.5f), b = Evaluate(s + spacing * 0.5f);
                Vector2 c = (a + b) * 0.5f;
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-12f) d = Vector2.right;
                d.Normalize();
                var outward = new Vector2(d.y, -d.x);
                Vector3 pos = F * c.x + U * c.y + A * depth;
                Vector3 n3 = F * outward.x + U * outward.y;
                links[i].localPosition = pos;
                // link model: +Y = wheel side (inside of the loop), Z = width (axle)
                links[i].localRotation = Quaternion.LookRotation(A, -n3) * extra;
            }
        }

        // ------------------------------------------------------------------ wheels

        void RotateWheels(float distanceLocal)
        {
            // rolling forward: angular velocity = v / r about (up x forward)
            Vector3 axis = Vector3.Cross(upLocal.normalized, forwardLocal.normalized);
            if (wheelAngles == null || wheelAngles.Length != wheels.Count) wheelAngles = new float[wheels.Count];
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                if (w == null || w.transform == null || w.radius <= 1e-4f) continue;
                // return rollers touch the top run, which moves the other way
                float sign = w.role == WheelRole.Return ? -1f : 1f;
                wheelAngles[i] = Mathf.Repeat(wheelAngles[i] + sign * distanceLocal / w.radius * Mathf.Rad2Deg, 360f);
                Vector3 axisParent = w.transform.parent == transform
                    ? axis
                    : w.transform.parent.InverseTransformDirection(transform.TransformDirection(axis));
                w.transform.localRotation = Quaternion.AngleAxis(wheelAngles[i], axisParent) * w.restRotation;
            }
        }

        // ------------------------------------------------------------------ queries for the builder / gizmos

        /// <summary>Torsion arm of wheel <paramref name="i"/> in world space: pivot, the wheel centre at rest, fully dropped and fully compressed.</summary>
        public bool TryGetArm(int i, out Vector3 pivot, out Vector3 rest, out Vector3 dropped, out Vector3 compressed, out Vector3 axle)
        {
            pivot = rest = dropped = compressed = axle = Vector3.zero;
            if (i < 0 || i >= arms.Length || !arms[i].active) return false;
            var a = arms[i];
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            Vector3 W(Vector2 v) => transform.TransformPoint(F * v.x + U * v.y + A * a.depth);
            pivot = W(a.pivot);
            rest = W(ArmPos(a, Rest(a)));
            dropped = W(ArmPos(a, a.aMax));
            compressed = W(ArmPos(a, a.aMin));
            axle = transform.TransformDirection(A).normalized;
            return true;
        }

        /// <summary>Height (in <paramref name="space"/>'s local Y) of the track's ground contact (bottom of the links).</summary>
        public float GetBottomY(Transform space)
        {
            if (path.Count == 0) BuildPath();
            if (path.Count == 0) return 0f;
            Vector3 F = forwardLocal.normalized, U = upLocal.normalized, A = axleLocal.normalized;
            float best = float.MaxValue;
            foreach (var p in path)
            {
                Vector3 local = F * p.x + U * (p.y - linkGroundDepth) + A * depth;
                float y = space.InverseTransformPoint(transform.TransformPoint(local)).y;
                if (y < best) best = y;
            }
            return best;
        }

        /// <summary>Half of the track's length along the forward axis, in world units.</summary>
        public float GetHalfLengthWorld()
        {
            if (path.Count == 0) BuildPath();
            if (path.Count == 0) return 1f;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in path) { min = Mathf.Min(min, p.x); max = Mathf.Max(max, p.x); }
            return (max - min) * 0.5f * Mathf.Abs(transform.lossyScale.y);
        }

        void OnDrawGizmosSelected()
        {
            for (int i = 0; i < arms.Length; i++)
            {
                if (!TryGetArm(i, out var p, out var r, out var d, out var c, out _)) continue;
                Gizmos.color = new Color(1f, 0.8f, 0.2f);
                Gizmos.DrawLine(p, r);
                Gizmos.DrawSphere(p, 0.04f * Mathf.Abs(transform.lossyScale.y));
                Gizmos.color = new Color(0.3f, 1f, 0.5f);
                Gizmos.DrawLine(c, d);
            }
        }
    }
}
