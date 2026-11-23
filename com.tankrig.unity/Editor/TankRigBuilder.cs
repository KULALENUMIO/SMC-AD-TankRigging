using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>The steps behind the builder window: set up the rig root, place tracks and points, and finish (rig) the tank.</summary>
    public static partial class TankRigBuilder
    {
        public enum PointKind { Muzzle, Exhaust, Headlight, RearLight }

        public static readonly PointKind[] AllPoints =
            { PointKind.Muzzle, PointKind.Exhaust, PointKind.Headlight, PointKind.RearLight };

        // ------------------------------------------------------------------ SMC-AD (optional, by reflection)

        static Component FindComponent(Transform t, string typeName) =>
            t ? t.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == typeName) : null;

        static T Field<T>(Component c, string name, T fallback)
        {
            if (c == null) return fallback;
            var f = c.GetType().GetField(name);
            return f != null && f.GetValue(c) is T v ? v : fallback;
        }

        public static bool HasJoint(Transform t) => FindComponent(t, "SMJoint") != null;

        /// <summary>
        /// Hull = root SM body, turret = joint body on the hull, gun mount = joint body on the turret (falls back to the renderer
        /// hierarchy). Barrel (optional) = a mesh under the mount that is a joint body or is named like a barrel.
        /// </summary>
        public static void AutoDetect(GameObject model, out Transform hull, out Transform turret, out Transform gun, out Transform barrel)
        {
            hull = turret = gun = barrel = null;
            if (!model) return;
            var all = model.GetComponentsInChildren<Transform>(true);
            var bodies = all.Where(t => FindComponent(t, "SMBody") != null).ToList();
            if (bodies.Count > 0)
            {
                hull = bodies.FirstOrDefault(t => Field(FindComponent(t, "SMBody"), "isRoot", false)) ?? bodies[0];
                var h = hull;
                var joints = all.Where(t => FindComponent(t, "SMJoint") != null).ToList();
                turret = joints.FirstOrDefault(t => t.parent == h) ?? joints.FirstOrDefault(t => t != h && t.IsChildOf(h));
                var tu = turret;
                if (tu) gun = joints.FirstOrDefault(t => t.parent == tu) ?? joints.FirstOrDefault(t => t != tu && t.IsChildOf(tu));
            }
            else
            {
                hull = all.FirstOrDefault(t => t.GetComponent<Renderer>());
                var hh = hull;
                if (hh) turret = hh.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != hh && t.GetComponent<Renderer>());
                var tt = turret;
                if (tt) gun = tt.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != tt && t.GetComponent<Renderer>());
            }
            var g = gun;
            if (g) barrel = g.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != g && t.GetComponent<Renderer>()
                && (HasJoint(t) || LooksLikeBarrel(t.name)));
        }

        static bool LooksLikeBarrel(string n)
        {
            n = n.ToLowerInvariant();
            return n.Contains("barrel") || n.Contains("砲身");
        }

        /// <summary>Checks the barrel against the mount. Returns a message when it cannot be used (and it must be cleared).</summary>
        public static string BarrelProblem(Transform gun, Transform barrel)
        {
            if (!barrel) return null;
            if (!gun) return "砲身を使うには砲座が必要です。";
            if (barrel == gun) return "砲身と砲座に同じオブジェクトが指定されています。砲身は砲座とは別のオブジェクトにしてください（なければ空のままで構いません）。";
            if (gun.IsChildOf(barrel)) return "砲身が砲座の親になっています。砲身は砲座の子（または別のオブジェクト）にしてください。";
            return null;
        }

        static Vector3 JointAxisLocal(Transform t, Vector3 fallbackWorld)
        {
            var j = FindComponent(t, "SMJoint");
            var v = Field(j, "localAxis", Vector3.zero);
            if (v.sqrMagnitude > 0.5f) return v.normalized;
            return t.InverseTransformDirection(fallbackWorld).normalized;
        }

        // ------------------------------------------------------------------ geometry helpers

        public static bool FindBarrelTip(Transform gun, Vector3 axisW, out Vector3 tip, out Vector3 dir) =>
            FindBarrelTip(gun, gun ? gun.position : Vector3.zero, axisW, out tip, out dir);

        /// <summary>
        /// Centre of the barrel tip: the vertices of <paramref name="meshes"/> farthest from <paramref name="pivot"/> (the elevation
        /// pivot), measured in the plane perpendicular to the elevation axis.
        /// </summary>
        public static bool FindBarrelTip(Transform meshes, Vector3 pivot, Vector3 axisW, out Vector3 tip, out Vector3 dir)
        {
            tip = pivot;
            dir = Vector3.forward;
            if (!meshes) return false;
            var points = new List<Vector3>();
            foreach (var mf in meshes.GetComponents<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var m = mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) points.Add(m.MultiplyPoint3x4(v));
            }
            if (points.Count == 0) return false;
            float best = -1f;
            foreach (var w in points)
            {
                var d = Vector3.ProjectOnPlane(w - pivot, axisW);
                if (d.sqrMagnitude > best) { best = d.sqrMagnitude; dir = d.normalized; }
            }
            float far = Mathf.Sqrt(best);
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var w in points)
            {
                if (Vector3.Dot(w - pivot, dir) > far - 0.12f) { sum += w; n++; }
            }
            tip = n > 0 ? sum / n : pivot + dir * far;
            dir = Vector3.ProjectOnPlane(tip - pivot, axisW).normalized;
            return true;
        }

        /// <summary>Bounds of the renderers' meshes in <paramref name="space"/>'s local coordinates.</summary>
        public static Bounds LocalBounds(Transform space, IEnumerable<Renderer> renderers)
        {
            bool first = true;
            var b = new Bounds();
            foreach (var r in renderers)
            {
                var mf = r ? r.GetComponent<MeshFilter>() : null;
                if (!mf || !mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = space.InverseTransformPoint(r.transform.TransformPoint(c));
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        static Renderer[] HullRenderers(TankRigSetup s) => s.hull ? s.hull.GetComponents<Renderer>() : new Renderer[0];

        // ------------------------------------------------------------------ step 1: rig root

        public static TankRigSetup CreateSetup(GameObject model, Transform hull, Transform turret, Transform gun, Transform barrel, bool flipForward)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Vector3 up = Vector3.up;
            if (BarrelProblem(gun, barrel) != null) barrel = null;

            // gun elevation axis: SMC-AD joint, else horizontal and perpendicular to the barrel
            Transform pivotT = gun ? gun : turret ? turret : hull;
            Transform tipMeshes = barrel ? barrel : pivotT;
            Vector3 gunAxisW;
            if (gun && HasJoint(gun)) gunAxisW = gun.TransformDirection(JointAxisLocal(gun, Vector3.right));
            else
            {
                FindBarrelTip(tipMeshes, pivotT.position, up, out _, out var flatDir);
                gunAxisW = Vector3.Cross(up, flatDir).normalized;
                if (gunAxisW.sqrMagnitude < 0.5f) gunAxisW = Vector3.right;
            }
            FindBarrelTip(tipMeshes, pivotT.position, gunAxisW, out _, out var barrelDir);
            Vector3 fwd = Vector3.ProjectOnPlane(barrelDir, up).normalized;
            if (fwd.sqrMagnitude < 0.5f) fwd = Vector3.forward;
            if (flipForward) fwd = -fwd;

            var hullBounds = hull.GetComponent<Renderer>() ? hull.GetComponent<Renderer>().bounds : new Bounds(hull.position, Vector3.one);
            var root = new GameObject("Tank_" + model.name);
            Undo.RegisterCreatedObjectUndo(root, "戦車の組み立て");
            root.transform.SetPositionAndRotation(new Vector3(hullBounds.center.x, hullBounds.min.y, hullBounds.center.z), Quaternion.LookRotation(fwd, up));
            Undo.SetTransformParent(model.transform, root.transform, "戦車の組み立て");

            var s = Undo.AddComponent<TankRigSetup>(root);
            s.model = model.transform;
            s.hull = hull;
            s.turret = turret;
            s.gun = gun;
            s.barrel = barrel;
            s.turretAxisLocal = turret ? JointAxisLocal(turret, up) : Vector3.up;
            s.gunAxisLocal = gun ? gun.InverseTransformDirection(gunAxisW).normalized : Vector3.right;
            s.axesFromJoints = HasJoint(turret) && HasJoint(gun);
            AutoPlacePoints(s, true);
            if (gun) AddMachineGun(s, MachineGunMount.Coaxial);
            Undo.CollapseUndoOperations(group);
            return s;
        }

        /// <summary>
        /// The track transforms are the source of truth in edit mode (<see cref="TankRigSetup.SyncMirrors"/> reads them back),
        /// so they have to be part of every undo step that moves them.
        /// </summary>
        public static void RecordTrackTransforms(TankRigSetup s, string undoName)
        {
            if (s.trackLeft) Undo.RecordObject(s.trackLeft, undoName);
            if (s.trackRight) Undo.RecordObject(s.trackRight, undoName);
        }

        /// <summary>Turns the rig root around (front and back swapped), keeping the model where it is.</summary>
        public static void FlipForward(TankRigSetup s)
        {
            var root = s.transform;
            Undo.RecordObject(root, "前後の反転");
            Undo.RecordObject(s, "前後の反転");
            if (s.model) Undo.RecordObject(s.model, "前後の反転");
            RecordTrackTransforms(s, "前後の反転");
            Vector3 mp = s.model ? s.model.position : Vector3.zero;
            Quaternion mr = s.model ? s.model.rotation : Quaternion.identity;
            root.rotation = Quaternion.AngleAxis(180f, root.up) * root.rotation;
            if (s.model) s.model.SetPositionAndRotation(mp, mr);
            s.trackOffset.z = -s.trackOffset.z;
            s.ApplyTrackPose();
            AutoPlacePoints(s, false);
        }

        // ------------------------------------------------------------------ points

        public static string Label(PointKind k) => k switch
        {
            PointKind.Muzzle => "主砲の砲口",
            PointKind.Exhaust => "エンジン排気",
            PointKind.Headlight => "前照灯",
            _ => "後部灯",
        };

        static string ObjectName(PointKind k) => k switch
        {
            PointKind.Muzzle => "Muzzle",
            PointKind.Exhaust => "Exhaust_L",
            PointKind.Headlight => "Headlight_L",
            _ => "RearLight_L",
        };

        public static Transform GetPoint(TankRigSetup s, PointKind k) => k switch
        {
            PointKind.Muzzle => s.muzzle,
            PointKind.Exhaust => s.exhaust,
            PointKind.Headlight => s.headlight,
            _ => s.rearLight,
        };

        public static Transform GetMirror(TankRigSetup s, PointKind k) => k switch
        {
            PointKind.Exhaust => s.exhaustMirror,
            PointKind.Headlight => s.headlightMirror,
            PointKind.RearLight => s.rearLightMirror,
            _ => null,
        };

        public static bool CanMirror(PointKind k) => k == PointKind.Exhaust || k == PointKind.Headlight || k == PointKind.RearLight;

        public static bool IsMirrored(TankRigSetup s, PointKind k) => k switch
        {
            PointKind.Exhaust => s.mirrorExhaust,
            PointKind.Headlight => s.mirrorHeadlight,
            PointKind.RearLight => s.mirrorRearLight,
            _ => false,
        };

        public static void SetMirrored(TankRigSetup s, PointKind k, bool on)
        {
            Undo.RecordObject(s, "左右対称");
            switch (k)
            {
                case PointKind.Exhaust: s.mirrorExhaust = on; break;
                case PointKind.Headlight: s.mirrorHeadlight = on; break;
                case PointKind.RearLight: s.mirrorRearLight = on; break;
            }
            EnsureMirror(s, k);
        }

        static void SetPoint(TankRigSetup s, PointKind k, Transform t)
        {
            switch (k)
            {
                case PointKind.Muzzle: s.muzzle = t; break;
                case PointKind.Exhaust: s.exhaust = t; break;
                case PointKind.Headlight: s.headlight = t; break;
                default: s.rearLight = t; break;
            }
        }

        static void SetMirror(TankRigSetup s, PointKind k, Transform t)
        {
            switch (k)
            {
                case PointKind.Exhaust: s.exhaustMirror = t; break;
                case PointKind.Headlight: s.headlightMirror = t; break;
                case PointKind.RearLight: s.rearLightMirror = t; break;
            }
        }

        public static Transform PointParent(TankRigSetup s, PointKind k)
        {
            switch (k)
            {
                case PointKind.Muzzle: return s.barrel ? s.barrel : s.gun ? s.gun : s.turret ? s.turret : s.hull; // moves with the recoil
                default: return s.hull ? s.hull : s.transform;
            }
        }

        public static Vector3 BarrelDirection(TankRigSetup s)
        {
            if (s.gun)
            {
                Vector3 axisW = s.gun.TransformDirection(s.gunAxisLocal);
                if (s.muzzle)
                {
                    var d = Vector3.ProjectOnPlane(s.muzzle.position - s.gun.position, axisW);
                    if (d.sqrMagnitude > 1e-4f) return d.normalized;
                }
                if (FindBarrelTip(s.barrel ? s.barrel : s.gun, s.gun.position, axisW, out _, out var dir)) return dir;
            }
            return s.transform.forward;
        }

        /// <summary>Sets (or clears) the barrel after the setup was made: checks it and moves the muzzle onto it.</summary>
        public static string SetBarrel(TankRigSetup s, Transform barrel)
        {
            string problem = BarrelProblem(s.gun, barrel);
            if (problem != null) return problem;
            Undo.RecordObject(s, "砲身の指定");
            s.barrel = barrel;
            if (s.muzzle)
            {
                var parent = PointParent(s, PointKind.Muzzle);
                if (s.muzzle.parent != parent) Undo.SetTransformParent(s.muzzle, parent, "砲身の指定");
            }
            return null;
        }

        static Quaternion DefaultRotation(TankRigSetup s, PointKind k)
        {
            var root = s.transform;
            switch (k)
            {
                case PointKind.Muzzle:
                    return Quaternion.LookRotation(BarrelDirection(s), root.up);
                case PointKind.Exhaust:
                    return Quaternion.LookRotation((root.up - root.forward * 0.35f).normalized, -root.forward);
                case PointKind.Headlight:
                    return Quaternion.LookRotation((root.forward - root.up * 0.08f).normalized, root.up);
                default:
                    return Quaternion.LookRotation(-root.forward, root.up);
            }
        }

        /// <summary>Creates (or moves) the point object at <paramref name="worldPos"/> with its default direction.</summary>
        public static Transform PlacePoint(TankRigSetup s, PointKind k, Vector3 worldPos)
        {
            var parent = PointParent(s, k);
            var t = GetPoint(s, k);
            if (!t)
            {
                var go = new GameObject(ObjectName(k));
                Undo.RegisterCreatedObjectUndo(go, "ポイントの配置");
                t = go.transform;
                Undo.SetTransformParent(t, parent, "ポイントの配置");
                Undo.RecordObject(s, "ポイントの配置");
                SetPoint(s, k, t);
            }
            else
            {
                Undo.RecordObject(t, "ポイントの配置");
                if (t.parent != parent) Undo.SetTransformParent(t, parent, "ポイントの配置");
            }
            t.position = worldPos;
            t.rotation = DefaultRotation(s, k);
            EnsureMirror(s, k);
            return t;
        }

        public static void EnsureMirror(TankRigSetup s, PointKind k)
        {
            if (!CanMirror(k)) return;
            var src = GetPoint(s, k);
            var mirror = GetMirror(s, k);
            bool want = IsMirrored(s, k) && src;
            if (want && !mirror)
            {
                var go = new GameObject(ObjectName(k).Replace("_L", "_R"));
                Undo.RegisterCreatedObjectUndo(go, "左右対称");
                Undo.SetTransformParent(go.transform, src.parent, "左右対称");
                Undo.RecordObject(s, "左右対称");
                SetMirror(s, k, go.transform);
            }
            else if (!want && mirror)
            {
                Undo.RecordObject(s, "左右対称");
                Undo.DestroyObjectImmediate(mirror.gameObject);
                SetMirror(s, k, null);
            }
            var m = GetMirror(s, k);
            if (m) Undo.RecordObject(m, "左右対称");
            s.SyncMirrors();
        }

        /// <summary>Puts every point at a plausible place on the model (the user then refines with the mouse).</summary>
        public static void AutoPlacePoints(TankRigSetup s, bool includeMuzzle)
        {
            var root = s.transform;
            var hullFilters = s.hull ? s.hull.GetComponents<MeshFilter>() : new MeshFilter[0];
            var lb = LocalBounds(root, HullRenderers(s));
            if (lb.size == Vector3.zero) lb = new Bounds(Vector3.up, new Vector3(2f, 1.5f, 5f));

            if (s.gun)
            {
                Vector3 axisW = s.gun.TransformDirection(s.gunAxisLocal);
                if (includeMuzzle && FindBarrelTip(s.barrel ? s.barrel : s.gun, s.gun.position, axisW, out var tip, out _))
                    PlacePoint(s, PointKind.Muzzle, tip);
            }
            // the machine guns that are already there move with the new front (coaxial ones along the barrel)
            for (int i = 0; s.machineGuns != null && i < s.machineGuns.Count; i++)
                if (s.machineGuns[i] != null && s.machineGuns[i].point)
                    PlaceMachineGun(s, i, DefaultMachineGunPosition(s, s.machineGuns[i].mount, SameMountIndex(s, i)));

            Vector3 OnHull(Vector3 localFrom, Vector3 localDir, Vector3 fallbackLocal)
            {
                var ray = new Ray(root.TransformPoint(localFrom), root.TransformDirection(localDir));
                return MeshRaycast.Raycast(ray, hullFilters, out var hit)
                    ? hit.point + hit.normal * 0.04f
                    : root.TransformPoint(fallbackLocal);
            }

            float x = -lb.extents.x * 0.35f + lb.center.x;
            float ezo = lb.min.z + lb.size.z * 0.12f;
            PlacePoint(s, PointKind.Exhaust, OnHull(new Vector3(x, lb.max.y + 2f, ezo), Vector3.down, new Vector3(x, lb.max.y, ezo)));
            float ly = lb.center.y + lb.size.y * 0.15f;
            PlacePoint(s, PointKind.Headlight, OnHull(new Vector3(x, ly, lb.max.z + 2f), Vector3.back, new Vector3(x, ly, lb.max.z)));
            PlacePoint(s, PointKind.RearLight, OnHull(new Vector3(x, ly, lb.min.z - 2f), Vector3.forward, new Vector3(x, ly, lb.min.z)));
        }

        // ------------------------------------------------------------------ step 2: tracks

        static bool IsWheelName(string n, out TankTrack.WheelRole role)
        {
            n = n.ToLowerInvariant();
            role = TankTrack.WheelRole.Road;
            if (n.Contains("return")) { role = TankTrack.WheelRole.Return; return true; }
            if (n.Contains("endwheel") || n.Contains("sprocket") || n.Contains("idler")) { role = TankTrack.WheelRole.End; return true; }
            return n.Contains("wheel") || n.Contains("roller");
        }

        public static void PlaceTracks(TankRigSetup s, bool autoFit)
        {
            if (!s.trackAsset || !s.linkAsset) return;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(s, "履帯の配置");
            if (s.trackLeft) Undo.DestroyObjectImmediate(s.trackLeft.gameObject);
            if (s.trackRight) Undo.DestroyObjectImmediate(s.trackRight.gameObject);

            var left = (GameObject)PrefabUtility.InstantiatePrefab(s.trackAsset, s.transform);
            var right = (GameObject)PrefabUtility.InstantiatePrefab(s.trackAsset, s.transform);
            Undo.RegisterCreatedObjectUndo(left, "履帯の配置");
            Undo.RegisterCreatedObjectUndo(right, "履帯の配置");
            left.name = "Track_L";
            right.name = "Track_R";
            s.trackLeft = left.transform;
            s.trackRight = right.transform;

            // which side of the track model is the outside: wheels sit outside the side plate (frame)
            float wheelZ = 0f, frameZ = 0f;
            int wn = 0, fn = 0;
            foreach (Transform c in left.transform)
            {
                var mf = c.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh) continue;
                float z = c.localPosition.z + mf.sharedMesh.bounds.center.z * c.localScale.z;
                if (IsWheelName(c.name, out _)) { wheelZ += z; wn++; }
                else { frameZ += z; fn++; }
            }
            s.trackOuterIsPositiveZ = fn == 0 || wn == 0 || wheelZ / wn >= frameZ / fn;

            s.linkGroundDepth = TankRigAssets.LinkGroundDepth(s.linkAsset, s.linkGroundDepth);

            SetupTrack(left, s);
            SetupTrack(right, s);
            if (autoFit) AutoFitTracks(s);
            s.ApplyTrackPose();
            left.GetComponent<TankTrack>().Rebuild();
            right.GetComponent<TankTrack>().Rebuild();
            Undo.CollapseUndoOperations(group);
        }

        static void SetupTrack(GameObject go, TankRigSetup s)
        {
            if (!go.TryGetComponent<TankTrack>(out var tt)) tt = Undo.AddComponent<TankTrack>(go);
            tt.wheels.Clear();
            foreach (Transform c in go.transform)
            {
                var mf = c.GetComponent<MeshFilter>();
                if (!mf || !mf.sharedMesh || !IsWheelName(c.name, out var role)) continue;
                var b = mf.sharedMesh.bounds;
                tt.wheels.Add(new TankTrack.Wheel
                {
                    transform = c,
                    role = role,
                    radius = Mathf.Max(b.extents.x, b.extents.y) * Mathf.Abs(c.localScale.x),
                    restRotation = c.localRotation,
                    restPosition = c.localPosition,
                    restCaptured = true,
                    width = b.size.z * Mathf.Abs(c.localScale.z),
                });
            }
            tt.linkPrefab = s.linkAsset;
            tt.forwardLocal = Vector3.right;
            tt.upLocal = Vector3.up;
            tt.axleLocal = Vector3.forward;
            CopySettings(s, tt);
        }

        /// <summary>Link and suspension settings of the window → a track (metres are converted to the track's local units).</summary>
        static void CopySettings(TankRigSetup s, TankTrack tt)
        {
            float toLocal = 1f / Mathf.Max(0.01f, s.trackScale);
            tt.pitch = s.linkPitch;
            tt.pitchLineOffset = s.linkPitchLineOffset;
            tt.linkGroundDepth = s.linkGroundDepth;
            tt.suspension = s.suspension;
            tt.droop = s.suspensionDroop * toLocal;
            tt.bump = s.suspensionBump * toLocal;
            tt.armLength = s.armLength * toLocal;
            tt.armAngle = s.armAngle;
            tt.maxSag = s.maxSag * toLocal;
        }

        /// <summary>Applies link / suspension settings to both tracks and rebuilds them (optionally with a suspension preview).</summary>
        public static void ApplyTrackSettings(TankRigSetup s, float preview = 0f)
        {
            foreach (var t in new[] { s.trackLeft, s.trackRight })
            {
                var tt = t ? t.GetComponent<TankTrack>() : null;
                if (!tt) continue;
                Undo.RecordObject(tt, "履帯の設定");
                CopySettings(s, tt);
                tt.SetPreview(preview);
                tt.Rebuild();
            }
        }

        /// <summary>First guess: as long as the hull, outer face flush with the hull side, upper half beside the hull.</summary>
        public static void AutoFitTracks(TankRigSetup s)
        {
            var tt = s.trackLeft ? s.trackLeft.GetComponent<TankTrack>() : null;
            if (!tt || tt.wheels.Count == 0) return;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, width = 0.3f;
            foreach (var w in tt.wheels)
            {
                var p = w.transform.localPosition;
                float r = w.radius + s.linkPitchLineOffset + s.linkGroundDepth;
                minX = Mathf.Min(minX, p.x - r); maxX = Mathf.Max(maxX, p.x + r);
                minY = Mathf.Min(minY, p.y - r); maxY = Mathf.Max(maxY, p.y + r);
                var mf = w.transform.GetComponent<MeshFilter>();
                if (mf && mf.sharedMesh) width = Mathf.Max(width, mf.sharedMesh.bounds.size.z);
            }
            float length = maxX - minX, height = maxY - minY;
            var hb = LocalBounds(s.transform, HullRenderers(s));
            if (hb.size == Vector3.zero) return;
            Undo.RecordObject(s, "履帯の自動フィット");
            float scale = Mathf.Clamp(hb.size.z * 0.92f / Mathf.Max(0.01f, length), 0.3f, 4f);
            s.trackScale = scale;
            s.trackOffset = new Vector3(
                Mathf.Max(0.1f, hb.max.x - hb.center.x - width * scale * 0.5f),
                hb.min.y + height * scale * 0.55f - maxY * scale,
                hb.center.z);
        }

        public static void RefreshTracks(TankRigSetup s) => ApplyTrackSettings(s);

        /// <summary>Swaps the track link design on both tracks (placement and wheels stay as they are).</summary>
        public static void SetLinkDesign(TankRigSetup s, GameObject linkAsset)
        {
            if (!linkAsset) return;
            Undo.RecordObject(s, "履帯のデザイン");
            s.linkAsset = linkAsset;
            s.linkGroundDepth = TankRigAssets.LinkGroundDepth(linkAsset, s.linkGroundDepth);
            foreach (var t in new[] { s.trackLeft, s.trackRight })
            {
                var tt = t ? t.GetComponent<TankTrack>() : null;
                if (!tt) continue;
                Undo.RecordObject(tt, "履帯のデザイン");
                tt.linkPrefab = linkAsset;
            }
            ApplyTrackSettings(s);
        }

        // ------------------------------------------------------------------ step 5: finish

        public static TankController Complete(TankRigSetup s, bool createGround, bool setupCamera)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var root = s.transform;
            TankFxFactory.EnsureAssets();

            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t && t != root && t.name.StartsWith("FX_")).ToList())
                if (t) Undo.DestroyObjectImmediate(t.gameObject);

            if (!root.TryGetComponent<TankController>(out var c)) c = Undo.AddComponent<TankController>(root.gameObject);
            Undo.RecordObject(c, "戦車の完成");
            c.hull = s.hull;
            c.turret = s.turret;
            c.gun = s.gun;
            c.barrel = BarrelProblem(s.gun, s.barrel) == null ? s.barrel : null;
            c.turretAxisLocal = s.turretAxisLocal;
            c.gunAxisLocal = s.gunAxisLocal;
            Vector3 barrel = BarrelDirection(s);
            if (s.turret && s.turret.parent) c.turretRestForward = s.turret.parent.InverseTransformDirection(barrel);
            if (s.gun && s.gun.parent) c.gunRestForward = s.gun.parent.InverseTransformDirection(barrel);
            if (s.gun) c.barrelForward = s.gun.InverseTransformDirection(barrel);
            c.leftTrack = s.trackLeft ? s.trackLeft.GetComponent<TankTrack>() : null;
            c.rightTrack = s.trackRight ? s.trackRight.GetComponent<TankTrack>() : null;
            c.muzzle = s.muzzle;
            c.machineGun = null; // v0.7: machineGuns
            c.machineGunTracer = c.machineGunFlash = null;
            c.exhausts = new[] { s.exhaust, s.mirrorExhaust ? s.exhaustMirror : null }.Where(t => t).ToArray();
            c.headlights = new[] { s.headlight, s.mirrorHeadlight ? s.headlightMirror : null }.Where(t => t).ToArray();
            c.rearLights = new[] { s.rearLight, s.mirrorRearLight ? s.rearLightMirror : null }.Where(t => t).ToArray();

            c.maxForwardSpeed = s.maxSpeedKmh / 3.6f;
            c.maxReverseSpeed = s.reverseSpeedKmh / 3.6f;
            c.acceleration = s.acceleration;
            c.braking = s.braking;
            c.driveResponse = 6f * s.responsiveness;
            c.turnResponse = 10f * s.responsiveness;
            c.turnRate = s.turnRate;
            c.traverseSpeed = s.traverseSpeed;
            c.minElevation = s.minElevation;
            c.maxElevation = s.maxElevation;
            c.reloadTime = s.reloadTime;
            c.gunPower = s.gunPower;
            c.bodySway = s.suspension ? s.bodySway : 0f;
            c.maxBounce = s.suspension ? s.suspensionBump : 0f;

            // tracks at their static (on the ground) state
            ApplyTrackSettings(s, 0f);

            // ground contact and footprint from the tracks
            var hb = LocalBounds(root, HullRenderers(s));
            c.groundOffset = hb.min.y;
            c.halfTrackWidth = Mathf.Max(0.3f, hb.extents.x);
            c.halfTrackLength = Mathf.Max(0.5f, hb.extents.z * 0.8f);
            var tracks = new[] { c.leftTrack, c.rightTrack }.Where(t => t).ToList();
            if (tracks.Count > 0)
            {
                c.groundOffset = tracks.Min(t => t.GetBottomY(root));
                c.halfTrackWidth = Mathf.Max(0.3f, Mathf.Abs(s.trackOffset.x));
                c.halfTrackLength = Mathf.Max(0.5f, tracks[0].GetHalfLengthWorld() * 0.8f);
            }

            // effects
            c.exhaustSmoke = c.exhausts.Select(TankFxFactory.CreateExhaust).ToArray();
            c.muzzleFlash = c.muzzleSmoke = null;
            c.muzzleLight = null;
            if (s.muzzle)
            {
                c.muzzleFlash = TankFxFactory.CreateMuzzleFlash(s.muzzle);
                c.muzzleSmoke = TankFxFactory.CreateMuzzleSmoke(s.muzzle);
                c.muzzleLight = TankFxFactory.CreateFlashLight(s.muzzle);
            }
            CompleteWeapons(s, c);
            TankFxFactory.CreateImpact(root, out var dust, out var sparks);
            c.impactDust = dust;
            c.impactSparks = sparks;
            c.headlightLights = c.headlights.Select(TankFxFactory.CreateHeadlight).ToArray();
            c.rearLightLights = c.rearLights.Select(TankFxFactory.CreateRearLight).ToArray();

            if (s.realisticEffects) TankRigFxSetup.AddTo(c);
            else if (root.TryGetComponent<TankRig.FX.TankFx>(out var realFx)) { Undo.RecordObject(realFx, "戦車の完成"); realFx.enabled = false; }

            c.driveMode = s.physicsDrive ? TankController.DriveMode.Physics : TankController.DriveMode.Kinematic;
            SetupBody(s, c, hb);

            if (setupCamera) SetupCamera(c, hb);
            if (createGround) CreateTestGround(root, root.position.y + c.groundOffset);

            EditorUtility.SetDirty(c);
            Undo.CollapseUndoOperations(group);
            return c;
        }

        public const string ColliderPrefix = "COL_";

        /// <summary>
        /// Physics mode: a Rigidbody with a low centre of mass and simple colliders (hull box, turret box that turns with the turret,
        /// a capsule along each track whose rounded ends ride up steps). The colliders stay clear of the ground at the static ride
        /// height, so the torsion bars carry the tank; they only touch when a bar is fully compressed or on big obstacles.
        /// </summary>
        static void SetupBody(TankRigSetup s, TankController c, Bounds hb)
        {
            var root = s.transform;
            // tanks rigged before v0.6 did not remember their collision mesh asset: adopt the one in use
            if (!s.hullColliderMesh)
            {
                var old = root.Find(ColliderPrefix + "Hull");
                var oldMesh = old && old.TryGetComponent<MeshCollider>(out var omc) ? omc.sharedMesh : null;
                if (oldMesh && AssetDatabase.Contains(oldMesh)) { Undo.RecordObject(s, "戦車の完成"); s.hullColliderMesh = oldMesh; }
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t && t.name.StartsWith(ColliderPrefix)).ToList())
                if (t) Undo.DestroyObjectImmediate(t.gameObject);
            root.TryGetComponent<Rigidbody>(out var rb);
            if (!rb) rb = Undo.AddComponent<Rigidbody>(root.gameObject);
            Undo.RecordObject(rb, "物理演算");
            rb.mass = Mathf.Max(0.1f, s.massTons) * 1000f;
            rb.linearDamping = 0.05f;
            rb.angularDamping = 0.05f; // the torsion bars damp pitch / roll; a high value would brake turning
            // kinematic mode moves the transform itself; the (kinematic) body only carries the colliders so the tank can be hit
            rb.interpolation = s.physicsDrive ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            rb.collisionDetectionMode = s.physicsDrive ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.Discrete;
            rb.isKinematic = !s.physicsDrive;
            rb.useGravity = s.physicsDrive;
            float ground = c.groundOffset;
            rb.automaticCenterOfMass = false;
            // over the middle of the tracks (so the tank stands level and turns about its centre), low down
            float comZ = s.trackLeft ? s.trackOffset.z : hb.center.z;
            rb.centerOfMass = new Vector3(0f, ground + (hb.max.y - ground) * 0.38f, comZ);

            var mat = TankFxFactory.HullPhysicsMaterial();
            GameObject Make(string name, Transform parent)
            {
                var go = new GameObject(ColliderPrefix + name);
                Undo.RegisterCreatedObjectUndo(go, "物理演算");
                go.transform.SetParent(parent, false);
                return go;
            }

            // hull: convex shape from 4 height slices of the hull mesh (keeps the sloped glacis so steps are climbed, not rammed)
            var hullMesh = HullColliderMesh(s, root);
            if (hullMesh)
            {
                var mc = Make("Hull", root).AddComponent<MeshCollider>();
                mc.sharedMesh = hullMesh;
                mc.convex = true;
                mc.sharedMaterial = mat;
            }
            else
            {
                var hull = Make("Hull", root).AddComponent<BoxCollider>();
                hull.center = hb.center;
                hull.size = hb.size;
                hull.sharedMaterial = mat;
            }

            void TurretBox(Transform turret, string name)
            {
                if (!turret) return;
                var tb = LocalBounds(turret, turret.GetComponents<Renderer>());
                if (tb.size == Vector3.zero) return;
                var box = Make(name, turret).AddComponent<BoxCollider>();
                box.center = tb.center;
                box.size = tb.size;
                box.sharedMaterial = mat;
            }
            TurretBox(s.turret, "Turret");
            for (int i = 0; c.secondaryTurrets != null && i < c.secondaryTurrets.Length; i++)
                TurretBox(c.secondaryTurrets[i]?.turret, "SecondaryTurret_" + (i + 1));

            const float clearance = 0.15f; // above the ground at the static ride height ≈ the bump travel
            foreach (var (track, side) in new[] { (s.trackLeft, "Track_L"), (s.trackRight, "Track_R") })
            {
                if (!track) continue;
                var renderers = track.GetComponentsInChildren<Renderer>(true).Where(r => !r.name.StartsWith("Link_"));
                var tb = LocalBounds(root, renderers);
                if (tb.size == Vector3.zero) continue;

                // steps and logs at the sprocket / idler are handled softly by the physics (TankController: end wheel
                // contacts); this capsule only keeps walls and other tanks out of the track's side, off the ground
                float bottom = ground + clearance;
                float half = Mathf.Max(0.1f, (tb.max.y - bottom) * 0.5f);
                var cap = Make(side, root).AddComponent<CapsuleCollider>();
                cap.direction = 2; // along the tank
                cap.radius = Mathf.Min(half, tb.extents.x);
                cap.height = Mathf.Max(tb.size.z, cap.radius * 2f);
                cap.center = new Vector3(tb.center.x, bottom + half, tb.center.z);
                cap.sharedMaterial = mat;
            }
        }

        /// <summary>
        /// A small convex collision mesh for the hull (root space): the hull's vertices cut into height bands, each band's
        /// footprint rectangle at the band's bottom and top. Saved as an asset (readable, works in builds).
        /// </summary>
        static Mesh HullColliderMesh(TankRigSetup s, Transform root)
        {
            var pts = new List<Vector3>();
            foreach (var mf in s.hull ? s.hull.GetComponents<MeshFilter>() : new MeshFilter[0])
            {
                if (!mf.sharedMesh) continue;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (var v in mf.sharedMesh.vertices) pts.Add(m.MultiplyPoint3x4(v));
            }
            if (pts.Count < 8) return null;
            float minY = pts.Min(p => p.y), maxY = pts.Max(p => p.y);
            float[] cuts = { 0f, 0.15f, 0.4f, 0.7f, 1f };
            var corners = new List<Vector3>();
            for (int b = 0; b + 1 < cuts.Length; b++)
            {
                float y0 = Mathf.Lerp(minY, maxY, cuts[b]), y1 = Mathf.Lerp(minY, maxY, cuts[b + 1]);
                var band = pts.Where(p => p.y >= y0 - 1e-4f && p.y <= y1 + 1e-4f).ToList();
                if (band.Count == 0) continue;
                float x0 = band.Min(p => p.x), x1 = band.Max(p => p.x), z0 = band.Min(p => p.z), z1 = band.Max(p => p.z);
                foreach (float y in new[] { y0, y1 })
                {
                    corners.Add(new Vector3(x0, y, z0)); corners.Add(new Vector3(x1, y, z0));
                    corners.Add(new Vector3(x0, y, z1)); corners.Add(new Vector3(x1, y, z1));
                }
            }
            // a point cloud is enough: the convex MeshCollider builds the hull; give it a triangle list over the points
            var tris = new List<int>();
            for (int i = 0; i + 2 < corners.Count; i++) { tris.Add(0); tris.Add(i + 1); tris.Add(i + 2); }
            var mesh = new Mesh { name = root.name + "_HullCollider" };
            mesh.SetVertices(corners);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            // one asset per tank: reuse the one this setup made before, else a new file with a safe, unique name
            // (not when a duplicated tank still shares it)
            var existing = s.hullColliderMesh && AssetDatabase.Contains(s.hullColliderMesh)
                && !Object.FindObjectsByType<TankRigSetup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(o => o != s && o.hullColliderMesh == s.hullColliderMesh)
                ? s.hullColliderMesh : null;
            Undo.RecordObject(s, "戦車の完成");
            if (existing)
            {
                existing.Clear();
                existing.SetVertices(corners);
                existing.SetTriangles(tris, 0);
                existing.RecalculateBounds();
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(mesh);
                return existing;
            }
            System.IO.Directory.CreateDirectory(TankFxFactory.Folder);
            string safe = string.Concat(root.name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string path = AssetDatabase.GenerateUniqueAssetPath(TankFxFactory.Folder + "/" + safe + "_HullCollider.asset");
            AssetDatabase.CreateAsset(mesh, path);
            s.hullColliderMesh = mesh;
            return mesh;
        }

        static void SetupCamera(TankController c, Bounds hullBounds)
        {
            var cam = Camera.main;
            if (!cam)
            {
                var go = new GameObject("Tank Camera", typeof(Camera), typeof(AudioListener));
                Undo.RegisterCreatedObjectUndo(go, "戦車の完成");
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }
            if (!cam.TryGetComponent<TankCamera>(out var tc)) tc = Undo.AddComponent<TankCamera>(cam.gameObject);
            Undo.RecordObject(tc, "戦車の完成");
            // one player tank per camera: the tank that had this camera stops reading the keyboard / mouse
            // (it can still be driven through externalInput, e.g. by AI)
            if (tc.target && tc.target != c)
            {
                var prev = tc.target;
                Undo.RecordObject(prev, "戦車の完成");
                if (prev.aimCamera == tc) prev.aimCamera = null;
                prev.useExternalInput = true;
                Debug.Log($"[TankRig] カメラを {c.name} に切り替えたので、{prev.name} はプレイヤー操作から外しました（externalInput で操縦できます）。", prev);
            }
            tc.target = c;
            float size = Mathf.Max(hullBounds.size.z, hullBounds.size.x);
            tc.height = Mathf.Max(1.5f, hullBounds.max.y + 1f);
            tc.distance = Mathf.Clamp(size * 1.7f, 6f, 40f);
            tc.maxDistance = Mathf.Max(tc.distance * 2.5f, 20f);
            c.aimCamera = tc;
            TankRigSightsSetup.AddTo(cam, c);   // V: optical sight / display / helmet
            var root = c.transform;
            Undo.RecordObject(cam.transform, "戦車の完成");
            Vector3 pivot = root.position + Vector3.up * tc.height;
            cam.transform.position = pivot - root.forward * tc.distance + Vector3.up * tc.distance * 0.2f;
            cam.transform.rotation = Quaternion.LookRotation(pivot - cam.transform.position);
        }

        public const string GroundName = "TankRig_TestGround";

        static void CreateTestGround(Transform tank, float groundY)
        {
            var old = GameObject.Find(GroundName);
            if (old) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(GroundName);
            Undo.RegisterCreatedObjectUndo(root, "テスト用の地面");
            var mat = TankFxFactory.GroundMaterial();
            Vector3 c = tank.position;
            Vector3 f = Vector3.ProjectOnPlane(tank.forward, Vector3.up).normalized;
            Vector3 r = Vector3.Cross(Vector3.up, f);

            void Add(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Quaternion rot)
            {
                var go = GameObject.CreatePrimitive(type);
                go.name = name;
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = mat;
            }

            Add(PrimitiveType.Plane, "Ground", new Vector3(c.x, groundY, c.z), new Vector3(60f, 1f, 60f), Quaternion.identity);
            var face = Quaternion.LookRotation(f, Vector3.up);
            Add(PrimitiveType.Cube, "Ramp", new Vector3(c.x, groundY - 0.6f, c.z) + f * 35f, new Vector3(10f, 2f, 18f), face * Quaternion.Euler(-10f, 0f, 0f));
            Add(PrimitiveType.Cube, "Hill", new Vector3(c.x, groundY - 2.2f, c.z) + f * 40f + r * 30f, new Vector3(20f, 5f, 20f), face * Quaternion.Euler(0f, 45f, 8f));
            Add(PrimitiveType.Cube, "Target", new Vector3(c.x, groundY + 2.5f, c.z) + f * 90f - r * 20f, new Vector3(6f, 5f, 6f), face);
            Add(PrimitiveType.Cube, "Wall", new Vector3(c.x, groundY + 1.5f, c.z) - f * 40f, new Vector3(30f, 3f, 1f), face);

            // obstacles: rocks straight ahead, logs and steps on the left lane, a washboard on the right lane
            Vector3 g0 = new Vector3(c.x, groundY, c.z);
            for (int i = 0; i < 7; i++)
            {
                float size = 0.55f + 0.12f * (i % 3);
                Add(PrimitiveType.Cube, "Rock" + i, g0 + f * (14f + i * 1.7f) + r * ((i % 2 == 0 ? -1f : 1f) * (0.6f + 0.25f * (i % 4))) + Vector3.up * size * 0.15f,
                    Vector3.one * size, face * Quaternion.Euler(20f + i * 17f, i * 41f, 15f + i * 23f));
            }
            for (int i = 0; i < 3; i++)
                Add(PrimitiveType.Capsule, "Log" + i, g0 - r * 15f + f * (14f + i * 5f) + Vector3.up * 0.15f,
                    new Vector3(0.45f, 3.5f, 0.45f), face * Quaternion.Euler(0f, 0f, 90f));
            Add(PrimitiveType.Cube, "Step50", g0 - r * 15f + f * 36f + Vector3.up * 0.25f, new Vector3(8f, 0.5f, 6f), face);
            Add(PrimitiveType.Cube, "Step80", g0 - r * 15f + f * 50f + Vector3.up * 0.4f, new Vector3(8f, 0.8f, 6f), face);
            for (int i = 0; i < 12; i++)
                Add(PrimitiveType.Cylinder, "Bump" + i, g0 + r * 15f + f * (12f + i * 1.6f) - Vector3.up * 0.3f,
                    new Vector3(0.8f, 4f, 0.8f), face * Quaternion.Euler(0f, 0f, 90f));

            // slopes on the far left: a 20° climb straight ahead and a 15° side slope beside it
            void Slope(string name, Vector3 start, float pitch, float roll, float length, float width)
            {
                const float t = 2f; // thickness
                var rot = face * Quaternion.Euler(-pitch, 0f, roll);
                Vector3 fw = rot * Vector3.forward, upv = rot * Vector3.up;
                // the top face starts at the ground at "start" and rises along the slope
                Vector3 centre = start + fw * (length * 0.5f) - upv * (t * 0.5f);
                Add(PrimitiveType.Cube, name, centre, new Vector3(width, t, length), rot);
            }
            Slope("Slope20", g0 - r * 35f + f * 10f, 20f, 0f, 24f, 9f);
            Slope("SideSlope15", g0 - r * 52f - f * 10f + Vector3.up * 1.2f, 0f, 15f, 60f, 10f);
        }
    }
}
