// Draws a sight's symbology over the finished image (HDRP custom pass, After Post Process), so the reticle is not
// touched by exposure, bloom, depth of field, motion blur or TAA.
// Two steps: the symbology mesh (and the sensor image of a digital sight) is painted into an overlay texture, then a
// full-screen pass lays the overlay over the camera colour. The overlay covers a rectangle of view-space tangents
// (tanHalf), so with XR each eye maps the same directions: the symbology appears at infinity in both eyes.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TankRig.Sights
{
    /// <summary>What one camera shows this frame (filled by <see cref="TankSightRig"/>).</summary>
    public sealed class SightFrame
    {
        public Mesh mesh;
        public int width = 16, height = 9;          // overlay size in pixels
        public Vector2 tanHalf = Vector2.one;       // view-space tangents covered by the overlay (half extents)
        public bool angular;                        // map by view direction (XR, or a test of that path)
        public Texture sensor;                      // digital sight: the sensor image
        public Vector4 sensorParams, sensorParams2, sensorParams3, sensorSize;
        public Color sensorTint = Color.white;
        public bool visible;
    }

    [System.Serializable]
    public class SightPass : CustomPass
    {
        static readonly Dictionary<Camera, SightFrame> frames = new Dictionary<Camera, SightFrame>();

        public static SightFrame FrameFor(Camera cam)
        {
            if (!frames.TryGetValue(cam, out var f)) frames[cam] = f = new SightFrame { mesh = new Mesh { name = "Sight Symbology" } };
            return f;
        }

        public static void Release(Camera cam)
        {
            if (cam && frames.TryGetValue(cam, out var f))
            {
                if (f.mesh) CoreUtils.Destroy(f.mesh);
                frames.Remove(cam);
            }
        }

        static readonly int MatrixId = Shader.PropertyToID("_SightMatrix"), FontId = Shader.PropertyToID("_FontTex"),
            SensorId = Shader.PropertyToID("_SensorTex"), SP1 = Shader.PropertyToID("_SensorParams"),
            SP2 = Shader.PropertyToID("_SensorParams2"), SP3 = Shader.PropertyToID("_SensorParams3"),
            SSize = Shader.PropertyToID("_SensorSize"), STint = Shader.PropertyToID("_SensorTint"),
            OverlayId = Shader.PropertyToID("_SightOverlay"), TanId = Shader.PropertyToID("_SightTan");

        Material paint, composite;
        MaterialPropertyBlock paintProps, compositeProps;
        readonly Dictionary<Camera, RenderTexture> overlays = new Dictionary<Camera, RenderTexture>();

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            var ps = Resources.Load<Shader>("TankRigSights/SightPaint");
            var cs = Resources.Load<Shader>("TankRigSights/SightComposite");
            if (ps) paint = CoreUtils.CreateEngineMaterial(ps);
            if (cs) composite = CoreUtils.CreateEngineMaterial(cs);
            paintProps = new MaterialPropertyBlock();
            compositeProps = new MaterialPropertyBlock();
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (!paint || !composite) return;
            var cam = ctx.hdCamera.camera;
            if (!frames.TryGetValue(cam, out var f) || !f.visible || !f.mesh || f.mesh.vertexCount == 0) return;

            if (!overlays.TryGetValue(cam, out var rt) || !rt || rt.width != f.width || rt.height != f.height)
            {
                if (rt) { rt.Release(); CoreUtils.Destroy(rt); }
                rt = new RenderTexture(f.width, f.height, 0, RenderTextureFormat.ARGBHalf) { name = "Sight Overlay", hideFlags = HideFlags.HideAndDontSave };
                rt.Create();
                overlays[cam] = rt;
            }

            var cmd = ctx.cmd;
            float hw = SightPainter.Height * 0.5f * f.width / f.height, hh = SightPainter.Height * 0.5f;
            var ortho = Matrix4x4.Ortho(-hw, hw, -hh, hh, -1f, 1f);
            paintProps.Clear();
            paintProps.SetMatrix(MatrixId, GL.GetGPUProjectionMatrix(ortho, true));
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font && font.material && font.material.mainTexture) paintProps.SetTexture(FontId, font.material.mainTexture);
            paintProps.SetTexture(SensorId, f.sensor ? f.sensor : Texture2D.blackTexture);
            paintProps.SetVector(SP1, f.sensorParams);
            paintProps.SetVector(SP2, f.sensorParams2);
            paintProps.SetVector(SP3, f.sensorParams3);
            paintProps.SetVector(SSize, f.sensorSize);
            paintProps.SetColor(STint, f.sensorTint.linear);
            cmd.SetRenderTarget(rt);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.DrawMesh(f.mesh, Matrix4x4.identity, paint, 0, 0, paintProps);

            compositeProps.Clear();
            compositeProps.SetTexture(OverlayId, rt);
            compositeProps.SetVector(TanId, new Vector4(f.tanHalf.x, f.tanHalf.y, f.angular ? 1f : 0f, 0f));
            CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
            CoreUtils.DrawFullScreen(cmd, composite, compositeProps, 0);
        }

        protected override void Cleanup()
        {
            CoreUtils.Destroy(paint);
            CoreUtils.Destroy(composite);
            foreach (var rt in overlays.Values) if (rt) { rt.Release(); CoreUtils.Destroy(rt); }
            overlays.Clear();
        }
    }
}
