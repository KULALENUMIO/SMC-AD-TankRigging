using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Builder steps for the machine guns (any number) and the secondary turrets.</summary>
    public static partial class TankRigBuilder
    {
        // ------------------------------------------------------------------ old data

        /// <summary>Tanks set up before v0.7 had one machine gun point: it becomes the first entry of the list.</summary>
        public static void Migrate(TankRigSetup s)
        {
            if (!s) return;
            bool listsMissing = s.machineGuns == null || s.secondaryTurrets == null;
            if (!listsMissing && !s.machineGun) return;
            Undo.RecordObject(s, "機銃の一覧");
            s.machineGuns ??= new List<TankMachineGun>();
            s.secondaryTurrets ??= new List<TankSecondaryTurret>();
            if (s.machineGun && !s.machineGuns.Any(m => m != null && m.point == s.machineGun))
            {
                bool coax = s.machineGunFollowsGun && s.gun;
                s.machineGuns.Insert(0, new TankMachineGun
                {
                    name = coax ? "同軸機銃" : "機銃",
                    point = s.machineGun,
                    mount = coax ? MachineGunMount.Coaxial : MachineGunMount.Turret,
                });
            }
            s.machineGun = null;
            EditorUtility.SetDirty(s);
        }

        // ------------------------------------------------------------------ machine guns

        public static string MountLabel(MachineGunMount m) => m switch
        {
            MachineGunMount.Coaxial => "同軸",
            MachineGunMount.Turret => "砲塔",
            _ => "車体",
        };

        public static Transform MachineGunParent(TankRigSetup s, MachineGunMount m) => m switch
        {
            MachineGunMount.Coaxial => s.gun ? s.gun : s.turret ? s.turret : s.hull ? s.hull : s.transform,
            MachineGunMount.Turret => s.turret ? s.turret : s.hull ? s.hull : s.transform,
            _ => s.hull ? s.hull : s.transform,
        };

        static Quaternion MachineGunRotation(TankRigSetup s, MachineGunMount m) =>
            Quaternion.LookRotation(m == MachineGunMount.Coaxial && s.gun ? BarrelDirection(s) : s.transform.forward, s.transform.up);

        /// <summary>How many machine guns with the same mount come before entry <paramref name="i"/> (spreads them left / right).</summary>
        static int SameMountIndex(TankRigSetup s, int i)
        {
            int n = 0;
            for (int k = 0; k < i; k++) if (s.machineGuns[k] != null && s.machineGuns[k].mount == s.machineGuns[i].mount) n++;
            return n;
        }

        static Bounds WorldBounds(IEnumerable<Renderer> renderers, Vector3 fallback)
        {
            bool first = true;
            var b = new Bounds(fallback, Vector3.zero);
            foreach (var r in renderers)
            {
                if (!r) continue;
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        /// <summary>A plausible first place: beside the main gun (coaxial), on the turret roof, or in the hull's front plate.</summary>
        public static Vector3 DefaultMachineGunPosition(TankRigSetup s, MachineGunMount mount, int index)
        {
            var root = s.transform;
            float side = index % 2 == 0 ? 1f : -1f;
            float spread = 1f + index / 2;
            var lb = LocalBounds(root, HullRenderers(s));
            if (lb.size == Vector3.zero) lb = new Bounds(Vector3.up, new Vector3(2f, 1.5f, 5f));

            if (mount == MachineGunMount.Coaxial && s.gun)
            {
                Vector3 dir = BarrelDirection(s);
                float len = s.muzzle ? Vector3.Distance(s.muzzle.position, s.gun.position) : 2f;
                return s.gun.position + dir * (len * 0.12f) + root.right * (side * spread * Mathf.Max(0.25f, lb.extents.x * 0.18f));
            }
            if (mount != MachineGunMount.Hull && s.turret)
            {
                var filters = s.turret.GetComponents<MeshFilter>();
                var tb = WorldBounds(s.turret.GetComponents<Renderer>(), s.turret.position);
                Vector3 above = tb.center + root.right * (side * spread * tb.extents.x * 0.35f) + root.up * (tb.extents.y + 2f);
                return MeshRaycast.Raycast(new Ray(above, -root.up), filters, out var hit)
                    ? hit.point + hit.normal * 0.15f
                    : above - root.up * 2f;
            }
            // hull: front plate, beside the centre line
            var hullFilters = s.hull ? s.hull.GetComponents<MeshFilter>() : new MeshFilter[0];
            float x = lb.center.x + side * spread * lb.extents.x * 0.3f, y = lb.center.y + lb.size.y * 0.2f;
            var ray = new Ray(root.TransformPoint(new Vector3(x, y, lb.max.z + 2f)), -root.forward);
            return MeshRaycast.Raycast(ray, hullFilters, out var h)
                ? h.point + h.normal * 0.04f
                : root.TransformPoint(new Vector3(x, y, lb.max.z));
        }

        public static TankMachineGun AddMachineGun(TankRigSetup s, MachineGunMount mount)
        {
            Undo.RecordObject(s, "機銃の追加");
            s.machineGuns ??= new List<TankMachineGun>();
            if (mount == MachineGunMount.Coaxial && !s.gun) mount = MachineGunMount.Turret;
            if (mount == MachineGunMount.Turret && !s.turret) mount = MachineGunMount.Hull;
            string baseName = mount switch
            {
                MachineGunMount.Coaxial => "同軸機銃",
                MachineGunMount.Turret => "砲塔機銃",
                _ => "車体機銃",
            };
            string name = baseName;
            for (int n = 2; s.machineGuns.Any(m => m != null && m.name == name); n++) name = baseName + " " + n;
            var mg = new TankMachineGun { name = name, mount = mount, aimCone = mount == MachineGunMount.Hull ? 15f : 0f };
            s.machineGuns.Add(mg);
            int i = s.machineGuns.Count - 1;
            PlaceMachineGun(s, i, DefaultMachineGunPosition(s, mount, SameMountIndex(s, i)));
            return mg;
        }

        /// <summary>Creates (or moves) the machine gun's point at <paramref name="worldPos"/>, parented to its mount.</summary>
        public static void PlaceMachineGun(TankRigSetup s, int i, Vector3 worldPos, bool resetRotation = true)
        {
            var mg = s.machineGuns[i];
            var parent = MachineGunParent(s, mg.mount);
            if (!mg.point)
            {
                var go = new GameObject("MG_" + (i + 1));
                Undo.RegisterCreatedObjectUndo(go, "機銃の配置");
                Undo.SetTransformParent(go.transform, parent, "機銃の配置");
                Undo.RecordObject(s, "機銃の配置");
                mg.point = go.transform;
                resetRotation = true;
            }
            else
            {
                Undo.RecordObject(mg.point, "機銃の配置");
                if (mg.point.parent != parent) Undo.SetTransformParent(mg.point, parent, "機銃の配置");
            }
            mg.point.position = worldPos;
            if (resetRotation) mg.point.rotation = MachineGunRotation(s, mg.mount);
        }

        public static void SetMachineGunMount(TankRigSetup s, int i, MachineGunMount mount)
        {
            var mg = s.machineGuns[i];
            Undo.RecordObject(s, "機銃の取り付け");
            mg.mount = mount;
            if (mg.point)
            {
                var parent = MachineGunParent(s, mount);
                if (mg.point.parent != parent) Undo.SetTransformParent(mg.point, parent, "機銃の取り付け");
            }
        }

        /// <summary>Removes the entry and the point object the builder made for it (never a part of the model).</summary>
        public static void RemoveMachineGun(TankRigSetup s, int i)
        {
            var mg = s.machineGuns[i];
            Undo.RecordObject(s, "機銃の削除");
            if (mg != null && IsOwnPoint(mg.point)) Undo.DestroyObjectImmediate(mg.point.gameObject);
            s.machineGuns.RemoveAt(i);
        }

        static bool IsOwnPoint(Transform t) =>
            t && !t.GetComponent<Renderer>() && t.Cast<Transform>().All(c => c.name.StartsWith("FX_"));

        // ------------------------------------------------------------------ secondary turrets

        public static TankSecondaryTurret AddSecondaryTurret(TankRigSetup s)
        {
            Undo.RecordObject(s, "副砲塔の追加");
            s.secondaryTurrets ??= new List<TankSecondaryTurret>();
            string name = "副砲塔 1";
            for (int n = 2; s.secondaryTurrets.Any(t => t != null && t.name == name); n++) name = "副砲塔 " + n;
            var st = new TankSecondaryTurret { name = name };
            s.secondaryTurrets.Add(st);
            AutoPlaceSecondaryMuzzle(s, st);
            return st;
        }

        public static void RemoveSecondaryTurret(TankRigSetup s, int i)
        {
            var st = s.secondaryTurrets[i];
            Undo.RecordObject(s, "副砲塔の削除");
            if (st != null && IsOwnPoint(st.muzzle)) Undo.DestroyObjectImmediate(st.muzzle.gameObject);
            s.secondaryTurrets.RemoveAt(i);
        }

        /// <summary>
        /// What is wrong with the secondary turret's parts (each message names a part that will be left out when the tank is
        /// completed). Missing parts are fine: they only switch off traversing, elevating or recoiling.
        /// </summary>
        public static List<string> SecondaryProblems(TankRigSetup s, TankSecondaryTurret st)
        {
            var list = new List<string>();
            CheckSecondary(s, st, (part, msg) => list.Add(msg));
            return list;
        }

        static void CheckSecondary(TankRigSetup s, TankSecondaryTurret st, System.Action<string, string> problem)
        {
            var root = s.transform;
            var main = new[] { s.turret, s.gun, s.barrel };
            var others = s.secondaryTurrets.TakeWhile(o => o != st).Where(o => o != null)
                .SelectMany(o => new[] { o.turret, o.gun, o.barrel }).Where(t => t).ToList();
            foreach (var (part, label, key) in new[] { (st.turret, "砲塔", "turret"), (st.gun, "砲架", "gun"), (st.barrel, "砲身", "barrel") })
            {
                if (!part) continue;
                if (!part.IsChildOf(root)) problem(key, $"{label}（{part.name}）がこの戦車の中にありません。");
                else if (part == root || part == s.model || part == s.hull || (s.hull && s.hull.IsChildOf(part)))
                    problem(key, $"{label}に車体（またはその親）は指定できません。");
                else if (main.Contains(part)) problem(key, $"{label}（{part.name}）は主砲の部位と同じです。主砲とは別のオブジェクトを指定してください。");
                else if (main.Any(m => m && m.IsChildOf(part))) problem(key, $"{label}（{part.name}）が主砲の部位の親になっています（動かすと主砲まで回ります）。");
                else if (others.Contains(part)) problem(key, $"{label}（{part.name}）はほかの副砲塔でも使われています。");
            }
            if (st.gun && st.turret)
            {
                if (st.gun == st.turret) problem("gun", "砲塔と砲架に同じオブジェクトが指定されています。上下だけ・旋回だけなら、もう片方は空にしてください。");
                else if (st.turret.IsChildOf(st.gun)) problem("gun", "砲架が砲塔の親になっています。砲架は砲塔の子にしてください。");
            }
            if (st.barrel)
            {
                if (st.barrel == st.gun || st.barrel == st.turret) problem("barrel", "砲身に砲塔・砲架と同じオブジェクトが指定されています。後退させないなら砲身は空にしてください。");
                else if ((st.gun && st.gun.IsChildOf(st.barrel)) || (st.turret && st.turret.IsChildOf(st.barrel)))
                    problem("barrel", "砲身が砲塔・砲架の親になっています。砲身は砲架（または砲塔）の子にしてください。");
            }
        }

        /// <summary>The copy given to the controller: parts that cannot work are left out (the rest keeps working).</summary>
        static TankSecondaryTurret ValidCopy(TankRigSetup s, TankSecondaryTurret src)
        {
            var st = src.Clone();
            CheckSecondary(s, src, (key, msg) =>
            {
                Debug.LogWarning($"[TankRig] 副砲塔「{src.name}」: {msg} この部位を外して完成させました。", s);
                if (key == "turret") st.turret = null;
                else if (key == "gun") st.gun = null;
                else st.barrel = null;
            });
            return st;
        }

        /// <summary>Called after the parts were changed: rotation axes from SMC-AD joints (or the shape), then the muzzle.</summary>
        public static void RefreshSecondary(TankRigSetup s, TankSecondaryTurret st)
        {
            Undo.RecordObject(s, "副砲塔の部位");
            var root = s.transform;
            Vector3 up = root.up;
            if (st.turret)
            {
                Vector3 axisW = st.turret.TransformDirection(JointAxisLocal(st.turret, up));
                if (Vector3.Dot(axisW, up) < 0f) axisW = -axisW; // + = to the right, like the traverse limits say
                st.turretAxisLocal = st.turret.InverseTransformDirection(axisW).normalized;
            }
            if (st.gun)
            {
                Vector3 gunAxisW;
                if (HasJoint(st.gun)) gunAxisW = st.gun.TransformDirection(JointAxisLocal(st.gun, root.right));
                else
                {
                    gunAxisW = root.right;
                    Transform meshes = st.barrel ? st.barrel : st.gun;
                    if (FindBarrelTip(meshes, st.gun.position, up, out _, out var flat))
                    {
                        var a = Vector3.Cross(up, flat);
                        if (a.sqrMagnitude > 0.25f) gunAxisW = a.normalized;
                    }
                }
                st.gunAxisLocal = st.gun.InverseTransformDirection(gunAxisW).normalized;
            }
            AutoPlaceSecondaryMuzzle(s, st);
        }

        static Transform SecondaryMuzzleParent(TankRigSetup s, TankSecondaryTurret st) =>
            st.barrel ? st.barrel : st.gun ? st.gun : st.turret ? st.turret : s.hull ? s.hull : s.transform;

        /// <summary>Muzzle at the barrel tip (barrel / mount meshes), else the turret's front, else the hull's front.</summary>
        public static void AutoPlaceSecondaryMuzzle(TankRigSetup s, TankSecondaryTurret st)
        {
            var root = s.transform;
            Vector3 pos, dir = root.forward;
            Transform pivot = st.gun ? st.gun : st.turret ? st.turret : st.barrel;
            Transform meshes = st.barrel ? st.barrel : st.gun;
            Vector3 axisW = st.gun ? st.gun.TransformDirection(st.gunAxisLocal) : root.up;
            if (meshes && pivot && FindBarrelTip(meshes, pivot.position, axisW, out var tip, out var d))
            {
                pos = tip;
                dir = d;
            }
            else if (st.turret)
            {
                var b = WorldBounds(st.turret.GetComponents<Renderer>(), st.turret.position);
                pos = b.center + dir * Vector3.Dot(b.extents, new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z)));
            }
            else
            {
                var lb = LocalBounds(root, HullRenderers(s));
                if (lb.size == Vector3.zero) lb = new Bounds(Vector3.up, new Vector3(2f, 1.5f, 5f));
                pos = root.TransformPoint(new Vector3(lb.center.x, lb.center.y + lb.extents.y * 0.5f, lb.max.z + 0.05f));
            }
            PlaceSecondaryMuzzle(s, st, pos, dir);
        }

        /// <summary>Creates (or moves) the muzzle point. A null direction keeps the current one.</summary>
        public static void PlaceSecondaryMuzzle(TankRigSetup s, TankSecondaryTurret st, Vector3 worldPos, Vector3? dir)
        {
            var parent = SecondaryMuzzleParent(s, st);
            if (!st.muzzle)
            {
                var go = new GameObject("Muzzle_" + st.name.Replace(' ', '_'));
                Undo.RegisterCreatedObjectUndo(go, "副砲の砲口");
                Undo.SetTransformParent(go.transform, parent, "副砲の砲口");
                Undo.RecordObject(s, "副砲の砲口");
                st.muzzle = go.transform;
                dir ??= s.transform.forward;
            }
            else
            {
                Undo.RecordObject(st.muzzle, "副砲の砲口");
                if (st.muzzle.parent != parent) Undo.SetTransformParent(st.muzzle, parent, "副砲の砲口");
            }
            st.muzzle.position = worldPos;
            if (dir.HasValue && dir.Value.sqrMagnitude > 1e-6f) st.muzzle.rotation = Quaternion.LookRotation(dir.Value, s.transform.up);
        }

        // ------------------------------------------------------------------ finish

        static void CompleteWeapons(TankRigSetup s, TankController c)
        {
            Migrate(s);
            var mgs = new List<TankMachineGun>();
            for (int i = 0; i < s.machineGuns.Count; i++)
            {
                var src = s.machineGuns[i];
                if (src == null || !src.point) continue;
                var parent = MachineGunParent(s, src.mount);
                if (IsOwnPoint(src.point) && src.point.parent != parent) Undo.SetTransformParent(src.point, parent, "戦車の完成");
                var mg = src.Clone();
                mg.tracer = TankFxFactory.CreateTracer(mg.point);
                mg.flash = TankFxFactory.CreateSmallFlash(mg.point);
                mgs.Add(mg);
            }
            c.machineGuns = mgs.ToArray();

            var sts = new List<TankSecondaryTurret>();
            foreach (var src in s.secondaryTurrets)
            {
                if (src == null) continue;
                if (!src.muzzle) AutoPlaceSecondaryMuzzle(s, src);
                var st = ValidCopy(s, src);
                // the muzzle has to move with what is left (its parent may be a part that was left out)
                var parent = SecondaryMuzzleParent(s, st);
                if (IsOwnPoint(st.muzzle) && st.muzzle.parent != parent) Undo.SetTransformParent(st.muzzle, parent, "戦車の完成");
                Vector3 dir = st.muzzle.forward;
                if (st.turret && st.turret.parent) st.turretRestForward = st.turret.parent.InverseTransformDirection(dir);
                if (st.gun && st.gun.parent) st.gunRestForward = st.gun.parent.InverseTransformDirection(dir);
                if (st.barrel && st.barrel.parent) st.barrelForward = (st.gun ? st.gun : st.barrel.parent).InverseTransformDirection(dir);
                st.muzzleFlash = st.muzzleSmoke = st.tracer = st.mgFlash = null;
                st.muzzleLight = null;
                if (st.weapon == SecondaryWeapon.Cannon)
                {
                    st.muzzleFlash = TankFxFactory.CreateMuzzleFlash(st.muzzle);
                    st.muzzleSmoke = TankFxFactory.CreateMuzzleSmoke(st.muzzle);
                    st.muzzleLight = TankFxFactory.CreateFlashLight(st.muzzle);
                }
                else
                {
                    st.tracer = TankFxFactory.CreateTracer(st.muzzle);
                    st.mgFlash = TankFxFactory.CreateSmallFlash(st.muzzle);
                }
                sts.Add(st);
            }
            c.secondaryTurrets = sts.ToArray();
        }
    }
}
