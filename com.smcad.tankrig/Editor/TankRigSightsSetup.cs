using TankRig.Sights;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Adds the sight rig and the three sights (optical, display, helmet) to a tank camera.</summary>
    public static class TankRigSightsSetup
    {
        [MenuItem("Tools/SMC-AD/戦車（Tank Rigging）/照準器を付ける（光学・ディスプレイ・ゴーグル）", priority = 60)]
        static void AddToSceneCamera()
        {
            var tc = Object.FindFirstObjectByType<TankCamera>();
            if (!tc || !tc.target)
            {
                EditorUtility.DisplayDialog("照準器", "戦車のカメラ（Tank Camera）が見つかりません。先に「戦車を完成させる」を押してください。", "OK");
                return;
            }
            AddTo(tc.GetComponent<Camera>(), tc.target);
            Selection.activeGameObject = tc.gameObject;
            Debug.Log($"[TankRig] {tc.name} に照準器を付けました（V で切り替え）。数値は各照準器の「数値」にプロファイルを入れて変えられます。", tc);
        }

        /// <summary>Adds what is missing; existing sights and their settings are kept.</summary>
        public static TankSightRig AddTo(Camera cam, TankController tank) => AddTo(cam, tank, null, AimSightKind.Display, false);

        /// <summary>Adds what is missing, and sets where the aiming camera sits (both the optical and the display sight)
        /// and which of them Shift aims with.</summary>
        public static TankSightRig AddTo(Camera cam, TankController tank, Transform sightPoint, AimSightKind kind) => AddTo(cam, tank, sightPoint, kind, true);

        static TankSightRig AddTo(Camera cam, TankController tank, Transform sightPoint, AimSightKind kind, bool setAim)
        {
            if (!cam) return null;
            var go = cam.gameObject;
            if (!go.TryGetComponent<TankSightRig>(out var rig)) rig = Undo.AddComponent<TankSightRig>(go);
            Undo.RecordObject(rig, "照準器");
            rig.tank = tank;
            rig.thirdPerson = go.GetComponent<TankCamera>();
            if (!go.GetComponent<OpticalSight>()) Undo.AddComponent<OpticalSight>(go);
            if (!go.GetComponent<DisplaySight>()) Undo.AddComponent<DisplaySight>(go);
            if (!go.GetComponent<HelmetSight>()) Undo.AddComponent<HelmetSight>(go);
            rig.sights = new TankSight[] { go.GetComponent<OpticalSight>(), go.GetComponent<DisplaySight>(), go.GetComponent<HelmetSight>() };
            if (setAim)
            {
                rig.aimSight = kind;
                foreach (TankSight sg in new TankSight[] { go.GetComponent<OpticalSight>(), go.GetComponent<DisplaySight>() })
                {
                    Undo.RecordObject(sg, "照準器");
                    if (sightPoint) sg.eyePoint = sightPoint;   // none: keep what the sight has (near the gun mount by default)
                    EditorUtility.SetDirty(sg);
                }
            }
            EditorUtility.SetDirty(rig);
            return rig;
        }
    }
}
