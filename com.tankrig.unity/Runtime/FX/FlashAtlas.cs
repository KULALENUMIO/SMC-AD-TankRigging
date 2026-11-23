// Flipbooks for sprite particles (GunFlashFX.PType.Sprite): two texture arrays with one flipbook per slice
//   A: r coverage / thickness, g temperature (0 cold .. 1 white hot), b detail (albedo variation)
//   B: rg normal (tangent space of the card, 0.5 = flat)
// and, per slice, the grid and the look (how the slice is lit). Default builds a small procedural set (a billowing
// smoke puff and a fireball, 4x4 frames each) so sprites work before real flipbooks are baked from the simulation.
using UnityEngine;

namespace TankRig.FX
{
    public class FlashAtlas
    {
        public const int MaxSlices = 16;
        public Texture2DArray A, B;
        /// <summary>Optional, any size: rg motion of each frame to the next (card uv, 0.5 = none, +-FlowRange). With it the
        /// shader slides the two frames it blends along the motion (flow-blended flipbook) instead of cross-fading.</summary>
        public Texture2DArray C;
        /// <summary>The motion texture's range: 0 / 1 = this much of the card back / forward.</summary>
        public const float FlowRange = 0.25f;
        /// <summary>Frames per side of the baked flipbooks.</summary>
        public const int BakedFrames = 8;
        public readonly Vector4[] grid = new Vector4[MaxSlices];     // frames x, frames y, frame count, -
        public readonly Vector4[] light = new Vector4[MaxSlices];    // normal softness (wrap), translucency, sphere power, max opacity
        public readonly Vector4[] scatter = new Vector4[MaxSlices];  // HG g, sun self-shadow, rim strength, ambient self-shadow
        public readonly Vector4[] look = new Vector4[MaxSlices];     // direction-ambient min, thin fade, rim opacity, temperature power
        /// <summary>Baked slices (CPU only): shot time of the first / last frame (s), the frame curve's power
        /// (frame k at t0 + (t1 - t0) (k / (n-1))^pow), and the card's half width (m).</summary>
        public readonly Vector4[] timing = new Vector4[MaxSlices];
        /// <summary>Baked slices (CPU only): the card's centre from the muzzle along the bore and up (m), the muzzle's
        /// height above the ground in the bake (m), 1 = place it from the ground (else from the muzzle).</summary>
        public readonly Vector4[] place = new Vector4[MaxSlices];

        /// <summary>The (fractional) frame of a baked slice at shot time t.</summary>
        public float FrameAt(int slice, float t)
        {
            var tm = timing[slice]; float n = Mathf.Max(grid[slice].z, 1f);
            if (tm.y <= tm.x) return 0f;
            float u = Mathf.Clamp01((t - tm.x) / (tm.y - tm.x));
            return (n - 1f) * Mathf.Pow(u, 1f / Mathf.Max(tm.z, 0.1f));
        }

        /// <summary>Slices of the default set: the fireball, the muzzle cloud, the ground dust (baked from the simulation).</summary>
        public const int SliceFire = 0, SliceSmoke = 1, SliceDust = 2;

        static FlashAtlas def;
        /// <summary>The flipbooks baked from the simulation (Resources/TankRigFX/BlastAtlas), else procedural stand-ins.</summary>
        public static FlashAtlas Default
        {
            get
            {
                if (def != null && def.A) return def;
                var baked = Resources.Load<FlashAtlasAsset>("TankRigFX/BlastAtlas");
                def = baked && baked.A ? baked.ToAtlas() : BuildDefault();
                return def;
            }
        }
        /// <summary>Forget the cached default (after a new bake).</summary>
        public static void ResetDefault() => def = null;

        /// <summary>The look of a slice (the defaults suit thick smoke).</summary>
        public void SetLook(int slice, float softness = 0.25f, float translucency = 0.4f, float spherePower = 0.8f, float maxOpacity = 0.95f,
                            float hgG = 0.55f, float selfShadow = 1.2f, float rim = 0.6f, float ambSelfShadow = 0.8f,
                            float dirAmbMin = 0.45f, float thinFade = 0.3f, float rimOpacity = 0.3f, float tempPower = 2.5f)
        {
            light[slice] = new Vector4(softness, translucency, spherePower, maxOpacity);
            scatter[slice] = new Vector4(hgG, selfShadow, rim, ambSelfShadow);
            look[slice] = new Vector4(dirAmbMin, thinFade, rimOpacity, tempPower);
        }

        static FlashAtlas BuildDefault()
        {
            const int F = 4, R = 128, N = F * R;   // 4 x 4 frames of 128 px
            var at = new FlashAtlas
            {
                A = new Texture2DArray(N, N, 3, TextureFormat.RGBA32, true, true) { name = "TankRigFlashAtlasA", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave },
                B = new Texture2DArray(N, N, 3, TextureFormat.RGBA32, true, true) { name = "TankRigFlashAtlasB", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave },
            };
            for (int slice = 0; slice < 3; slice++)
            {
                bool isFire = slice == SliceFire;
                var a = new Color32[N * N]; var b = new Color32[N * N];
                var h = new float[R * R];
                for (int fr = 0; fr < F * F; fr++)
                {
                    float life = fr / (F * F - 1f);
                    int fx = fr % F, fy = fr / F;
                    // a lumpy ball: several overlapping blobs that drift apart as it grows, eaten by noise
                    for (int y = 0; y < R; y++)
                        for (int x = 0; x < R; x++)
                        {
                            float u = (x + 0.5f) / R * 2f - 1f, v = (y + 0.5f) / R * 2f - 1f;
                            float d = 0f;
                            for (int k = 0; k < 5; k++)
                            {
                                float ang = k * 1.2566f + slice * 0.7f, spread = 0.18f + 0.22f * life;
                                float cx = Mathf.Cos(ang) * spread * (k == 0 ? 0f : 1f), cy = Mathf.Sin(ang) * spread * (k == 0 ? 0f : 1f) + (isFire ? 0f : 0.08f * life);
                                float rad = (k == 0 ? 0.55f : 0.38f) * (0.75f + 0.25f * life);
                                float q = 1f - ((u - cx) * (u - cx) + (v - cy) * (v - cy)) / (rad * rad);
                                d = Mathf.Max(d, q);
                            }
                            float n = 0f, amp = 0.5f, fq = 3f;
                            for (int o = 0; o < 4; o++) { n += amp * Mathf.PerlinNoise(u * fq + 13.1f * slice + 3f + life * 1.5f, v * fq + 7.7f + o * 11.3f); amp *= 0.5f; fq *= 2.1f; }
                            // noise only shapes the inside and the edge of the blobs (no stray bits), and the cell's border stays empty
                            float th = Mathf.Clamp01(d * 1.5f + (n - 0.5f) * 1.0f - 0.15f * life) * Mathf.Clamp01(d * 4f);
                            float edgeW = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 0.98f, Mathf.Max(Mathf.Abs(u), Mathf.Abs(v))));
                            th *= edgeW;
                            h[y * R + x] = th;
                        }
                    for (int y = 0; y < R; y++)
                        for (int x = 0; x < R; x++)
                        {
                            float th = h[y * R + x];
                            float hx = h[y * R + Mathf.Min(x + 1, R - 1)] - h[y * R + Mathf.Max(x - 1, 0)];
                            float hy = h[Mathf.Min(y + 1, R - 1) * R + x] - h[Mathf.Max(y - 1, 0) * R + x];
                            var nrm = new Vector3(-hx * 4f, -hy * 4f, 1f).normalized;
                            float temp = isFire ? Mathf.Clamp01((1f - life * 1.1f) * (0.4f + 0.8f * th)) : 0f;
                            float detail = Mathf.PerlinNoise(x * 0.09f + fr, y * 0.09f + slice * 5f);
                            int i = (fy * R + y) * N + fx * R + x;
                            a[i] = new Color32((byte)(th * 255), (byte)(temp * 255), (byte)(detail * 255), 255);
                            b[i] = new Color32((byte)((nrm.x * 0.5f + 0.5f) * 255), (byte)((nrm.y * 0.5f + 0.5f) * 255), 255, 255);
                        }
                }
                at.A.SetPixels32(a, slice); at.B.SetPixels32(b, slice);
                at.grid[slice] = new Vector4(F, F, F * F, 0f);
                at.timing[slice] = new Vector4(0f, 1f, 1f, 1f);
            }
            at.A.Apply(true, true); at.B.Apply(true, true);
            at.SetLook(SliceSmoke);
            at.SetLook(SliceDust);
            at.SetLook(SliceFire, softness: 0.5f, translucency: 0.2f, hgG: 0.3f, selfShadow: 0.6f, rim: 0.2f, tempPower: 2.0f);
            return at;
        }
    }
}
