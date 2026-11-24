using TankRig.FX;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Editor
{
    /// <summary>Adds the realistic effects (TankFx: gun flashes, exhaust, dust, track spill) to a rigged tank, and the shared pass to the scene.</summary>
    public static class TankRigFxSetup
    {
        const string FlashMaterialPath = TankFxFactory.Folder + "/TankRig_FlashParticles.mat";

        [MenuItem("Tools/SMC-AD/戦車（Tank Rigging）/リアルなエフェクトを付ける（選択中の戦車）", priority = 61)]
        static void AddToSelection()
        {
            var c = Selection.activeGameObject ? Selection.activeGameObject.GetComponentInParent<TankController>() : null;
            if (!c) c = Object.FindFirstObjectByType<TankController>();
            if (!c)
            {
                EditorUtility.DisplayDialog("リアルなエフェクト", "戦車（Tank Controller）が見つかりません。先に「戦車を完成させる」を押してください。", "OK");
                return;
            }
            var fx = AddTo(c);
            Selection.activeGameObject = fx.gameObject;
            Debug.Log($"[TankRig] {c.name} にリアルなエフェクト（機銃・副砲の発砲、排気、土煙、巻き込み）を付けました。Tank FX のチェックで個別にオン / オフできます。", fx);
        }

        /// <summary>The shared pass material (Hidden/TankRig/Flash), made once in the generated folder.</summary>
        public static Material FlashMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(FlashMaterialPath);
            if (m) return m;
            var sh = Shader.Find("Hidden/TankRig/Flash");
            if (!sh) { Debug.LogWarning("[TankRig] Hidden/TankRig/Flash が見つかりません"); return null; }
            System.IO.Directory.CreateDirectory(TankFxFactory.Folder);
            m = new Material(sh) { name = "TankRig_FlashParticles" };
            AssetDatabase.CreateAsset(m, FlashMaterialPath);
            return m;
        }

        /// <summary>Adds what is missing (settings already there are kept) and makes sure the scene draws the particles.</summary>
        public static TankFx AddTo(TankController c)
        {
            var go = c.gameObject;
            if (!go.TryGetComponent<TankFx>(out var fx))
            {
                fx = Undo.AddComponent<TankFx>(go);
                fx.controller = c;
            }
            Undo.RecordObject(fx, "リアルなエフェクト");
            fx.controller = c;
            fx.flashMaterial = FlashMaterial();
            fx.AddParts();
            foreach (var comp in go.GetComponents<MonoBehaviour>())
                if (comp is TankExhaustFX || comp is TankDustFX || comp is TankTrackSpillFX) Undo.RegisterCreatedObjectUndo(comp, "リアルなエフェクト");
            EnsurePassInScene(fx.flashMaterial);
            fx.Apply();
            EditorUtility.SetDirty(fx);
            return fx;
        }

        /// <summary>A saved global custom pass volume with the flash pass (kept with the scene, so builds draw it too).</summary>
        public static void EnsurePassInScene(Material material)
        {
            foreach (var v in Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None))
                foreach (var p in v.customPasses)
                    if (p is GunFlashPass gp)
                    {
                        if (!gp.material && material) { Undo.RecordObject(v, "リアルなエフェクト"); gp.material = material; EditorUtility.SetDirty(v); }
                        return;
                    }
            var pass = TankFx.EnsurePass(material);
            var vol = Object.FindObjectsByType<CustomPassVolume>(FindObjectsSortMode.None);
            foreach (var v in vol) if (v.customPasses.Contains(pass)) { Undo.RegisterCreatedObjectUndo(v.gameObject, "リアルなエフェクト"); EditorUtility.SetDirty(v); }
        }
    }
}
