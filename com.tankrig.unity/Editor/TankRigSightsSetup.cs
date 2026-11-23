using TankRig.Sights;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Adds the sight rig and the three sights (optical, display, helmet) to a tank camera.</summary>
    public static class TankRigSightsSetup
    {
        [MenuItem("Tools/Tank Rig/照準器を付ける（光学・ディスプレイ・ゴーグル）", priority = 20)]
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
        public static TankSightRig AddTo(Camera cam, TankController tank)
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
            EditorUtility.SetDirty(rig);
            return rig;
        }
    }
}
