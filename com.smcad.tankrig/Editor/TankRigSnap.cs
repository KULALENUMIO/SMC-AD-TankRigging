using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TankRig.Editor
{
    /// <summary>
    /// Snapping to the Scrap Mechanic block grid. The grid runs along the model's axes (SM creations are built on it), and its
    /// origin is taken from the model itself: block corners are the most common vertex positions, so a model that was moved or
    /// re-centred on import still snaps to its own blocks.
    /// </summary>
    public static partial class TankRigBuilder
    {
        static readonly Dictionary<int, (float size, Vector3 phase)> gridCache = new Dictionary<int, (float, Vector3)>();

        public static float BlockSize(TankRigSetup s) => s && s.blockSize > 1e-4f ? s.blockSize : 0.25f;

        /// <summary>The grid's frame: the model's position and rotation, without its scale.</summary>
        public static Matrix4x4 GridFrame(TankRigSetup s)
        {
            Transform space = s.model ? s.model : s.transform;
            return Matrix4x4.TRS(space.position, space.rotation, Vector3.one);
        }

        /// <summary>Where the block corners are in the grid frame (modulo one block), per axis.</summary>
        public static Vector3 GridPhase(TankRigSetup s)
        {
            float block = BlockSize(s);
            int key = s.GetInstanceID();
            if (gridCache.TryGetValue(key, out var cached) && Mathf.Approximately(cached.size, block)) return cached.phase;

            var counts = new[] { new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, int>() };
            var sums = new[] { new Dictionary<int, double>(), new Dictionary<int, double>(), new Dictionary<int, double>() };
            Transform space = s.model ? s.model : s.transform;
            var toGrid = GridFrame(s).inverse;
            int budget = 300000;
            int steps = Mathf.Max(1, Mathf.RoundToInt(block * 1000f)); // 1 mm bins
            foreach (var mf in space.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || mf.name.StartsWith("Link_") || mf.GetComponentInParent<TankTrack>()) continue;
                var m = toGrid * mf.transform.localToWorldMatrix;
                var verts = mf.sharedMesh.vertices;
                int stride = Mathf.Max(1, verts.Length / Mathf.Max(1, budget / 4));
                for (int i = 0; i < verts.Length && budget > 0; i += stride, budget--)
                {
                    Vector3 p = m.MultiplyPoint3x4(verts[i]);
                    for (int a = 0; a < 3; a++)
                    {
                        float frac = Mathf.Repeat(p[a], block);
                        int bin = Mathf.RoundToInt(frac * 1000f) % steps;
                        counts[a].TryGetValue(bin, out int c);
                        counts[a][bin] = c + 1;
                        // exact value, unwrapped next to the bin (so 0.2499 and 0.0001 average to 0, not 0.125)
                        sums[a].TryGetValue(bin, out double sum);
                        sums[a][bin] = sum + (frac * 1000f - bin > steps * 0.5f ? frac - block : frac);
                    }
                }
            }
            var phase = Vector3.zero;
            for (int a = 0; a < 3; a++)
            {
                int best = 0, bestCount = -1;
                foreach (var kv in counts[a])
                    if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
                // the mean of the exact positions in that 1 mm bin (rounding to the bin would leave up to 0.5 mm off)
                phase[a] = bestCount > 0 ? Mathf.Repeat((float)(sums[a][best] / bestCount), block) : 0f;
            }
            gridCache[key] = (block, phase);
            return phase;
        }

        /// <summary>Forget the cached grid origin (after the model changed).</summary>
        public static void ResetGrid(TankRigSetup s)
        {
            if (s) gridCache.Remove(s.GetInstanceID());
        }

        /// <summary>The nearest grid point (every <paramref name="step"/> metres, aligned with the blocks).</summary>
        public static Vector3 Snap(TankRigSetup s, Vector3 world, float step)
        {
            if (step <= 0f) return world;
            var frame = GridFrame(s);
            Vector3 l = frame.inverse.MultiplyPoint3x4(world);
            Vector3 ph = GridPhase(s);
            for (int a = 0; a < 3; a++) l[a] = Mathf.Round((l[a] - ph[a]) / step) * step + ph[a];
            return frame.MultiplyPoint3x4(l);
        }

        /// <summary>Puts the left track's origin on the grid (the right one follows mirrored).</summary>
        public static void SnapTrack(TankRigSetup s, float step)
        {
            if (!s.trackLeft || step <= 0f) return;
            SetTrackPosition(s, Snap(s, s.trackLeft.position, step));
        }

        /// <summary>Moves the left track's origin to <paramref name="world"/> (the right one follows mirrored).</summary>
        public static void SetTrackPosition(TankRigSetup s, Vector3 world)
        {
            Undo.RecordObject(s, "履帯の移動");
            RecordTrackTransforms(s, "履帯の移動");
            var l = s.transform.InverseTransformPoint(world);
            s.trackOffset = new Vector3(Mathf.Max(0.02f, -l.x), l.y, l.z);
            s.ApplyTrackPose();
        }

        /// <summary>
        /// Moves the left track by whole steps in the tank's directions: x = outwards (both tracks apart), y = up, z = forwards.
        /// </summary>
        public static void NudgeTrack(TankRigSetup s, Vector3 steps, float step)
        {
            if (!s.trackLeft) return;
            var root = s.transform;
            Vector3 delta = -root.right * (steps.x * step) + root.up * (steps.y * step) + root.forward * (steps.z * step);
            Vector3 target = s.trackLeft.position + delta;
            SetTrackPosition(s, Snap(s, target, step));
        }

        /// <summary>The track position in blocks from the tank's centre line (x = outwards), for labels.</summary>
        public static Vector3 TrackOffsetBlocks(TankRigSetup s) => s.trackOffset / BlockSize(s);
    }
}
