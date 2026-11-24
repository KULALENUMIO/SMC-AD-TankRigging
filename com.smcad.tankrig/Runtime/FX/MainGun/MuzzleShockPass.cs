// HDRP custom pass: the muzzle blast wave as a refraction ripple. Runs after the smoke pass (same injection point):
// copies the camera colour (smoke included) and redraws it displaced across the thin expanding shell.
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    [System.Serializable]
    public class MuzzleShockPass : CustomPass
    {
        public Material material;
        RTHandle copy;
        static readonly int SrcId = Shader.PropertyToID("_MB_Src");

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            copy = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "MB_ShockCopy");
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (material == null) return;
            foreach (var sim in MuzzleBlastSim.Live)
            {
                if (!sim || !sim.ShockVisible) continue;
                CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, copy);
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                sim.Props.SetTexture(SrcId, copy);
                CoreUtils.DrawFullScreen(ctx.cmd, material, sim.Props, shaderPassId: 0);
            }
        }

        protected override void Cleanup() => copy?.Release();
    }
}
