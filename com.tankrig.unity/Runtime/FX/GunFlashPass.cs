// HDRP custom pass that draws every flash-particle source (gun flashes, exhaust, dust, track spill, sprites) with
// TankRigFlash.shader. Particles are sorted far to near (the smoke is alpha-blended; the glowing layers add up).
// Half resolution (default): the soft, big kinds go to a half-size target against a half-size checkerboard depth and
// are put over the scene with a depth-aware upsample (they keep off the tank's outline); the small sharp kinds
// (flash core, flame tongues, sparks, soil grains) follow at full resolution. About 4x less pixel work for smoke and dust.
// Particle shadows: the soft particles are drawn from the sun into a small Fourier opacity map (256 x 256, the box
// round them); the particles read it to shade each other, and the scene under them is darkened by it.
// Heat haze: hot particles (exhaust at the outlet, flame, fireball) wobble the scene behind them (quarter size).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.FX
{
    [System.Serializable]
    public class GunFlashPass : CustomPass
    {
        [Tooltip("Hidden/TankRig/Flash (made from the shader when empty)")] public Material material;
        [Tooltip("draw smoke and dust at half resolution and upsample them with the depth as a guide (sharp bits stay full resolution)")]
        public bool halfResolution = true;
        [Tooltip("smoke and dust cast shadows: on the ground and the tank, and on each other (Fourier opacity map)")]
        public bool particleShadows = true;
        [Tooltip("how dark the shadow on the scene gets under thick smoke (the sun's share of the light there)")] [Range(0f, 1f)] public float sceneShadow = 0.65f;
        [Tooltip("how much the particles shade each other")] [Range(0f, 1f)] public float selfShadow = 1f;
        [Tooltip("flipbooks with a motion texture slide their frames along it while blending (else a plain cross-fade)")] public bool flowBlending = true;
        [Tooltip("heat haze behind the exhaust and the flame")] public bool heatHaze = true;
        [Range(0f, 3f)] public float hazeStrength = 1f;
        /// <summary>Flipbooks for sprite particles (null = FlashAtlas.Default).</summary>
        [System.NonSerialized] public FlashAtlas atlas;

        ComputeBuffer buf;
        RTHandle half, halfDepth, fom0, fom1, fomDepth, haze, sceneCopy;
        const int FomSize = 256;
        static readonly RenderTargetIdentifier[] fomTargets = new RenderTargetIdentifier[2];
        readonly List<GunFlashFX.Particle> list = new List<GunFlashFX.Particle>();
        Vector3 camPos;
        System.Comparison<GunFlashFX.Particle> farToNear;
        static readonly int PartId = Shader.PropertyToID("_GF_Particles"), SunDirId = Shader.PropertyToID("_GF_SunDir"),
            SunLuxId = Shader.PropertyToID("_GF_SunLux"), AmbId = Shader.PropertyToID("_GF_Ambient"), ModeId = Shader.PropertyToID("_GF_Mode"),
            HalfId = Shader.PropertyToID("_GF_Half"), HalfDepthId = Shader.PropertyToID("_GF_HalfDepth"),
            AtlasAId = Shader.PropertyToID("_GF_AtlasA"), AtlasBId = Shader.PropertyToID("_GF_AtlasB"), GridId = Shader.PropertyToID("_GF_Grid"),
            LightId = Shader.PropertyToID("_GF_Light"), ScatterId = Shader.PropertyToID("_GF_Scatter"), LookId = Shader.PropertyToID("_GF_Look"),
            Fom0Id = Shader.PropertyToID("_GF_Fom0"), Fom1Id = Shader.PropertyToID("_GF_Fom1"), FomOId = Shader.PropertyToID("_GF_FomO"),
            FomRId = Shader.PropertyToID("_GF_FomR"), FomUId = Shader.PropertyToID("_GF_FomU"), FomFId = Shader.PropertyToID("_GF_FomF"),
            FomKId = Shader.PropertyToID("_GF_FomK"), HazeId = Shader.PropertyToID("_GF_Haze"), SceneCopyId = Shader.PropertyToID("_GF_SceneCopy"),
            HazeKId = Shader.PropertyToID("_GF_HazeK"), AtlasCId = Shader.PropertyToID("_GF_AtlasC"), FlowOnId = Shader.PropertyToID("_GF_Flow");

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            if (material == null) { var s = Shader.Find("Hidden/TankRig/Flash"); if (s) material = CoreUtils.CreateEngineMaterial(s); }
            farToNear = (a, b) => (b.pos - camPos).sqrMagnitude.CompareTo((a.pos - camPos).sqrMagnitude);
            half = RTHandles.Alloc(Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "GF_Half");
            halfDepth = RTHandles.Alloc(Vector2.one * 0.5f, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R32_SFloat, useDynamicScale: true, name: "GF_HalfDepth");
            fom0 = RTHandles.Alloc(FomSize, FomSize, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, filterMode: FilterMode.Bilinear, wrapMode: TextureWrapMode.Clamp, name: "GF_Fom0");
            fom1 = RTHandles.Alloc(FomSize, FomSize, colorFormat: GraphicsFormat.R16_SFloat, filterMode: FilterMode.Bilinear, wrapMode: TextureWrapMode.Clamp, name: "GF_Fom1");
            fomDepth = RTHandles.Alloc(FomSize, FomSize, depthBufferBits: DepthBits.Depth16, name: "GF_FomDepth");
            haze = RTHandles.Alloc(Vector2.one * 0.25f, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "GF_Haze");
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (material == null || GunFlashFX.Sources.Count == 0) return;
            list.Clear();
            IFlashParticles first = null;
            foreach (var e in GunFlashFX.Sources) if (e != null && !(e is Object o && !o)) { e.CopyVisible(list); if (first == null) first = e; }
            if (list.Count == 0) return;
            camPos = ctx.hdCamera.camera.transform.position;
            list.Sort(farToNear);   // no per-frame closure
            if (buf == null || buf.count < list.Count) { buf?.Release(); buf = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(list.Count, 64)), GunFlashFX.Stride); }
            buf.SetData(list);
            var cmd = ctx.cmd;
            cmd.SetGlobalBuffer(PartId, buf);
            cmd.SetGlobalVector(SunDirId, first.SunDir);
            cmd.SetGlobalVector(SunLuxId, first.SunLux);
            cmd.SetGlobalVector(AmbId, first.Ambient);
            var at = atlas ?? FlashAtlas.Default;
            cmd.SetGlobalTexture(AtlasAId, at.A);
            cmd.SetGlobalTexture(AtlasBId, at.B);
            cmd.SetGlobalTexture(AtlasCId, at.C ? at.C : at.B);
            cmd.SetGlobalFloat(FlowOnId, at.C && flowBlending ? FlashAtlas.FlowRange : 0f);
            cmd.SetGlobalVectorArray(GridId, at.grid);
            cmd.SetGlobalVectorArray(LightId, at.light);
            cmd.SetGlobalVectorArray(ScatterId, at.scatter);
            cmd.SetGlobalVectorArray(LookId, at.look);
            int verts = list.Count * 6;
            // particle shadows: the opacity map from the sun, then the scene under it darkened
            bool shadows = particleShadows && Shadows(cmd, first.SunDir, verts);
            if (shadows)
            {
                CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
                cmd.DrawProcedural(Matrix4x4.identity, material, 5, MeshTopology.Triangles, 3);
            }
            // heat haze: offsets from the hot particles, then the scene read through them
            if (heatHaze && hazeStrength > 0f && AnyHot())
            {
                CoreUtils.SetRenderTarget(cmd, haze, ClearFlag.Color, Color.clear);
                cmd.DrawProcedural(Matrix4x4.identity, material, 6, MeshTopology.Triangles, verts);
                var fmt = ctx.cameraColorBuffer.rt.graphicsFormat;   // a copy needs the same format
                if (sceneCopy == null || sceneCopy.rt.graphicsFormat != fmt)
                {
                    sceneCopy?.Release();
                    sceneCopy = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: fmt, useDynamicScale: true, name: "GF_SceneCopy");
                }
                cmd.CopyTexture(ctx.cameraColorBuffer, sceneCopy);
                cmd.SetGlobalTexture(HazeId, haze);
                cmd.SetGlobalTexture(SceneCopyId, sceneCopy);
                cmd.SetGlobalFloat(HazeKId, hazeStrength);
                CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
                cmd.DrawProcedural(Matrix4x4.identity, material, 7, MeshTopology.Triangles, 3);
            }
            if (!halfResolution)
            {
                cmd.SetGlobalFloat(ModeId, 0f);
                CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, verts);
                return;
            }
            // half-size checkerboard depth, then the soft kinds at half size (colour + transmittance)
            CoreUtils.SetRenderTarget(cmd, halfDepth, ClearFlag.None);
            cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3);
            cmd.SetGlobalTexture(HalfDepthId, halfDepth);
            CoreUtils.SetRenderTarget(cmd, half, ClearFlag.Color, new Color(0f, 0f, 0f, 1f));
            cmd.SetGlobalFloat(ModeId, 1f);
            cmd.DrawProcedural(Matrix4x4.identity, material, 2, MeshTopology.Triangles, verts);
            // over the scene, then the sharp kinds at full resolution
            cmd.SetGlobalTexture(HalfId, half);
            CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
            cmd.DrawProcedural(Matrix4x4.identity, material, 3, MeshTopology.Triangles, 3);
            cmd.SetGlobalFloat(ModeId, 2f);
            CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, verts);
        }

        static bool CastsShadow(float type) => type == 5f || type == 7f || type == 8f || type == 10f;

        bool AnyHot()
        {
            foreach (var p in list) if (p.type == 7f || p.type == 4f || p.type == 0f || (p.type == 10f && p.extra.z > 0f)) return true;
            return false;
        }

        /// <summary>The opacity map: a box round the shadow casters seen from the sun. False when nothing casts.</summary>
        bool Shadows(CommandBuffer cmd, Vector3 toSun, int verts)
        {
            Vector3 F = -toSun.normalized;   // the way the light travels
            if (F.sqrMagnitude < 0.5f) { cmd.SetGlobalVector(FomOId, Vector4.zero); return false; }
            Vector3 R = Vector3.Cross(F, Mathf.Abs(F.y) > 0.99f ? Vector3.forward : Vector3.up).normalized, U = Vector3.Cross(R, F).normalized;
            float r0 = float.MaxValue, r1 = float.MinValue, u0 = float.MaxValue, u1 = float.MinValue, f0 = float.MaxValue, f1 = float.MinValue;
            int n = 0;
            foreach (var p in list)
            {
                if (!CastsShadow(p.type) || p.intensity <= 0f) continue;
                float size = p.width * (1f + p.grow * p.age01);
                float a = Vector3.Dot(p.pos, R), b = Vector3.Dot(p.pos, U), c = Vector3.Dot(p.pos, F);
                r0 = Mathf.Min(r0, a - size); r1 = Mathf.Max(r1, a + size);
                u0 = Mathf.Min(u0, b - size); u1 = Mathf.Max(u1, b + size);
                f0 = Mathf.Min(f0, c - size); f1 = Mathf.Max(f1, c + size);
                n++;
            }
            if (n == 0) { cmd.SetGlobalVector(FomOId, Vector4.zero); return false; }
            // at most 160 m across (0.6 m texels) round the middle; a little margin in depth
            float w = Mathf.Min(r1 - r0, 160f) + 1f, h = Mathf.Min(u1 - u0, 160f) + 1f, d = (f1 - f0) + 2f;
            Vector3 centre = R * (r0 + r1) * 0.5f + U * (u0 + u1) * 0.5f + F * (f0 - 1f);
            cmd.SetGlobalVector(FomOId, new Vector4(centre.x, centre.y, centre.z, 1f));
            cmd.SetGlobalVector(FomRId, new Vector4(R.x, R.y, R.z, w));
            cmd.SetGlobalVector(FomUId, new Vector4(U.x, U.y, U.z, h));
            cmd.SetGlobalVector(FomFId, new Vector4(F.x, F.y, F.z, d));
            cmd.SetGlobalVector(FomKId, new Vector4(sceneShadow, selfShadow, 0.4f, 0f));
            fomTargets[0] = fom0; fomTargets[1] = fom1;
            CoreUtils.SetRenderTarget(cmd, fomTargets, fomDepth, ClearFlag.Color, Color.clear);
            cmd.DrawProcedural(Matrix4x4.identity, material, 4, MeshTopology.Triangles, verts);
            cmd.SetGlobalTexture(Fom0Id, fom0);
            cmd.SetGlobalTexture(Fom1Id, fom1);
            return true;
        }

        protected override void Cleanup()
        {
            buf?.Release(); buf = null;
            half?.Release(); halfDepth?.Release(); fom0?.Release(); fom1?.Release(); fomDepth?.Release(); haze?.Release(); sceneCopy?.Release();
            half = halfDepth = fom0 = fom1 = fomDepth = haze = sceneCopy = null;
        }
    }
}
