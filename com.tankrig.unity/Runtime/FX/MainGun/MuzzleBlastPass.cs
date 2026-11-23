// HDRP custom pass that draws the muzzle blast (MuzzleBlastRaymarch.shader) at full or half resolution.
// Half resolution marches a half-size target (each texel takes the top-left pixel of its 2x2 block), then a
// depth-guided upsample composites it over the camera colour, so the smoke keeps to the tank's silhouette.
// Temporal reuse (half resolution only): the march jitter changes every frame and each camera keeps a history that
// is reprojected to where the gas was last frame and clamped to this frame's neighbourhood, which removes the grain
// of the long steps without smearing the fast early blast.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    [System.Serializable]
    public class MuzzleBlastPass : CustomPass
    {
        public Material material;   // Hidden/TankRig/BlastRaymarch (made from Resources when empty)
        [Tooltip("march at half resolution (about 4x cheaper) and upsample with the depth buffer as a guide")]
        public bool halfResolution = true;
        [Tooltip("half resolution: blend with the previous frames (removes grain; needs consecutive frames of one camera)")]
        public bool temporal = true;
        [Tooltip("weight of the new frame in the temporal blend (lower = smoother, more lag)")]
        [Range(0.05f, 1f)] public float temporalWeight = 0.2f;

        RTHandle half, halfDist, halfDepth;
        class CamState { public RTHandle a, b; public Matrix4x4 prevVP; public int frame; public bool valid; }
        readonly Dictionary<Camera, CamState> cams = new Dictionary<Camera, CamState>();
        readonly List<MuzzleBlastSim> shots = new List<MuzzleBlastSim>();

        static readonly int HalfId = Shader.PropertyToID("_MB_Half"), HalfDistId = Shader.PropertyToID("_MB_HalfDist"),
            HistId = Shader.PropertyToID("_MB_Hist"), PrevVPId = Shader.PropertyToID("_MB_PrevVP"),
            TParamsId = Shader.PropertyToID("_MB_TParams"), FrameId = Shader.PropertyToID("_MB_Frame");

        static RTHandle Half(GraphicsFormat f, string name) => RTHandles.Alloc(Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension,
            colorFormat: f, useDynamicScale: true, name: name);

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            half = Half(GraphicsFormat.R16G16B16A16_SFloat, "MB_Half");
            halfDist = Half(GraphicsFormat.R32_SFloat, "MB_HalfDist");   // blended by the shader output's alpha (coverage)
            halfDepth = RTHandles.Alloc(Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension,
                depthBufferBits: DepthBits.Depth16, useDynamicScale: true, name: "MB_HalfDepth");
        }

        /// <summary>Forget the history (call after a cut or a jump in time).</summary>
        public void ResetHistory() { foreach (var s in cams.Values) s.valid = false; }

        protected override void Execute(CustomPassContext ctx)
        {
            if (material == null) return;
            var cam = ctx.hdCamera.camera;
            // every shot with something to draw, far ones first (each composites over the ones behind it)
            shots.Clear();
            foreach (var sim in MuzzleBlastSim.Live) if (sim && sim.Drawable) shots.Add(sim);
            Vector3 cp = cam.transform.position;
            shots.Sort((a, b) => (b.AnchorPos - cp).sqrMagnitude.CompareTo((a.AnchorPos - cp).sqrMagnitude));
            if (!cams.TryGetValue(cam, out var st))
            {
                st = new CamState { a = Half(GraphicsFormat.R16G16B16A16_SFloat, "MB_HistA"), b = Half(GraphicsFormat.R16G16B16A16_SFloat, "MB_HistB") };
                cams[cam] = st;
            }
            st.frame++;
            ctx.cmd.SetGlobalFloat(FrameId, temporal && halfResolution ? st.frame % 64 : 0f);
            if (shots.Count == 0) { st.valid = false; return; }
            if (!halfResolution)
            {
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                foreach (var sim in shots) CoreUtils.DrawFullScreen(ctx.cmd, material, sim.Props, shaderPassId: 0);
                st.valid = false;
                return;
            }
            // march: colour + transmittance, and the mean distance of the gas along the ray (for reprojection)
            CoreUtils.SetRenderTarget(ctx.cmd, new RenderTargetIdentifier[] { half, halfDist }, halfDepth, ClearFlag.None);
            ctx.cmd.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 1f));   // nothing drawn yet: no light, fully transparent
            foreach (var sim in shots) CoreUtils.DrawFullScreen(ctx.cmd, material, sim.Props, shaderPassId: 1);
            RTHandle result = half;
            // absolute-world view-projection without TAA jitter (D3D-style z, no y flip: the shader maps NDC to pixels)
            var vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            if (temporal)
            {
                ctx.cmd.SetGlobalTexture(HalfId, half);
                ctx.cmd.SetGlobalTexture(HalfDistId, halfDist);
                ctx.cmd.SetGlobalTexture(HistId, st.a);
                ctx.cmd.SetGlobalMatrix(PrevVPId, st.prevVP);
                ctx.cmd.SetGlobalVector(TParamsId, new Vector4(st.valid ? 1f : 0f, temporalWeight, 0f, 0f));
                CoreUtils.SetRenderTarget(ctx.cmd, st.b, ClearFlag.None);
                CoreUtils.DrawFullScreen(ctx.cmd, material, shaderPassId: 3);
                (st.a, st.b) = (st.b, st.a);
                result = st.a;
                st.valid = true;
            }
            st.prevVP = vp;
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
            ctx.cmd.SetGlobalTexture(HalfId, result);
            CoreUtils.DrawFullScreen(ctx.cmd, material, shaderPassId: 2);
        }

        protected override void Cleanup()
        {
            half?.Release(); halfDist?.Release(); halfDepth?.Release();
            foreach (var s in cams.Values) { s.a?.Release(); s.b?.Release(); }
            cams.Clear();
        }
    }
}
