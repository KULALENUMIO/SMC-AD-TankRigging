using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Editor
{
    /// <summary>
    /// Brings tanks rigged with the URP versions (≤ 0.7) over to HDRP in the open scenes: effect materials on the
    /// package's particle Shader Graph, the ground on HDRP/Lit, and the effect lights / sun in physical units
    /// (URP intensities are unitless and far too dark in HDRP).
    /// </summary>
    public static class TankRigHdrpUpgrade
    {
        [MenuItem("Tools/TankRig/HDRP に合わせる（ライト・エフェクト）")]
        static void Menu()
        {
            string r = Upgrade();
            Debug.Log("[TankRig] " + r);
            EditorUtility.DisplayDialog("TankRig", r, "OK");
        }

        public static string Upgrade()
        {
            TankFxFactory.EnsureAssets();          // switches TankRig_Smoke / TankRig_Additive in place
            TankFxFactory.GroundMaterial();        // switches TankRig_Ground in place
            int lights = 0, tanks = 0, suns = 0;
            foreach (var c in Object.FindObjectsByType<TankController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Undo.RecordObject(c, "HDRP");
                if (c.muzzleLightIntensity < 1000f) { c.muzzleLightIntensity = 800000f; tanks++; }
                EditorUtility.SetDirty(c);
            }
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                float? lumen = l.name switch { "FX_Headlight" => 3000f, "FX_RearLight" => 60f, "FX_MuzzleLight" => 0f, _ => (float?)null };
                bool sun = l.type == LightType.Directional && l.intensity < 1000f;
                if (lumen == null && !sun && l.GetComponent<HDAdditionalLightData>() != null) continue;
                Undo.RecordObject(l, "HDRP");
                if (!l.TryGetComponent<HDAdditionalLightData>(out _)) Undo.AddComponent<HDAdditionalLightData>(l.gameObject);
                if (lumen.HasValue) { l.lightUnit = LightUnit.Lumen; l.intensity = lumen.Value; lights++; }
                else if (sun) { l.lightUnit = LightUnit.Lux; l.intensity = 100000f; suns++; }
                EditorUtility.SetDirty(l);
            }
            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (!cam.TryGetComponent<HDAdditionalCameraData>(out _)) Undo.AddComponent<HDAdditionalCameraData>(cam.gameObject);
            EditorSceneManager.MarkAllScenesDirty();
            return $"HDRP に合わせました: 戦車 {tanks}、エフェクトのライト {lights}、太陽 {suns}（100,000 lux）。" +
                   "空・霧・露出は HDRP の Volume で設定してください。";
        }
    }
}
