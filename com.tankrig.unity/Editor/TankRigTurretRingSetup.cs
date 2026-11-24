using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>Adds the turret ring interior to the selected tank (or the first tank in the scene).</summary>
    public static class TankRigTurretRingSetup
    {
        [MenuItem("Tools/Tank Rig/ターレットリング内装を付ける", priority = 21)]
        static void Add()
        {
            var tank = Selection.activeGameObject ? Selection.activeGameObject.GetComponentInParent<TankController>() : null;
            if (!tank) tank = Object.FindFirstObjectByType<TankController>();
            if (!tank || !tank.turret)
            {
                EditorUtility.DisplayDialog("ターレットリング内装", "砲塔のある戦車（Tank Controller）が見つかりません。先に「戦車を完成させる」を押してください。", "OK");
                return;
            }
            if (!tank.TryGetComponent<TurretRingInterior>(out var ring)) ring = Undo.AddComponent<TurretRingInterior>(tank.gameObject);
            Undo.RecordObject(ring, "ターレットリング内装");
            ring.tank = tank;
            ring.Rebuild();
            Selection.activeGameObject = tank.gameObject;
            Debug.Log($"[TankRig] {tank.name} にターレットリング内装を付けました（普段は非表示。「表示」のチェックか Show(車体側, 砲塔側) で出ます）。", tank);
        }
    }
}
