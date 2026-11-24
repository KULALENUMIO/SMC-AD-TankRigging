using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Editor
{
    /// <summary>HDRP helpers for the generated effects (the plugin supports HDRP only).</summary>
    public static class HDRPSetup
    {
        /// <summary>HDRP keywords / passes / render queue of a material. Before HDRP has rendered once (batch mode,
        /// first import) Shader.globalRenderPipeline is empty and HDRP's validation would silently do nothing.</summary>
        public static void Validate(Material m)
        {
            if (m == null) return;
            if (string.IsNullOrEmpty(Shader.globalRenderPipeline) && GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)
                Shader.globalRenderPipeline = "HDRenderPipeline";
            if (!HDMaterial.ValidateMaterial(m))
                Debug.LogWarning("[TankRig] HDRP のマテリアル設定ができませんでした（HDRP が有効か確認してください）: " + m.name, m);
        }

        /// <summary>The package's particle shader. Shader.Find only sees shaders that are already loaded, so a package
        /// Shader Graph nothing has used yet (fresh project, batch mode) is loaded from its file.</summary>
        public static Shader ParticleShader()
        {
            var s = Shader.Find(TankFxFactory.ParticleShader);
            if (s == null) s = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Packages/com.smcad.tankrig/Runtime/Shaders/Particle Unlit.shadergraph");
            return s;
        }

        /// <summary>A light with its HDRP data, intensity in physical units (point / spot: lumen, directional: lux).</summary>
        public static Light AddLight(GameObject go, LightType type)
        {
            var l = go.AddComponent<Light>();
            l.type = type;
            if (!go.TryGetComponent<HDAdditionalLightData>(out _)) go.AddComponent<HDAdditionalLightData>();
            l.lightUnit = type == LightType.Directional ? LightUnit.Lux : LightUnit.Lumen;
            return l;
        }
    }
}
