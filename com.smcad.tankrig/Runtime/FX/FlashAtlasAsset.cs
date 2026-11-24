// Baked flipbooks for sprite particles, saved as one asset (this object + its two texture arrays as sub-assets).
// Made by Tools > SMC-AD > 戦車（Tank Rigging） > 主砲の煙をフリップブックに焼く (TankRig.Editor.FlashAtlasBaker) from the main-gun
// simulation itself; the package's own copy lives in Resources/TankRigFX/BlastAtlas and is FlashAtlas.Default.
using UnityEngine;

namespace TankRig.FX
{
    [PreferBinarySerialization]   // the texture arrays: tens of MB as text
    public class FlashAtlasAsset : ScriptableObject
    {
        public Texture2DArray A, B, C;
        public string[] sliceNames = new string[0];
        public Vector4[] grid = new Vector4[0], light = new Vector4[0], scatter = new Vector4[0], look = new Vector4[0];
        public Vector4[] timing = new Vector4[0], place = new Vector4[0];   // see FlashAtlas.timing / place
        public int[] top = new int[0];   // see FlashAtlas.top
        [TextArea] public string notes;

        public FlashAtlas ToAtlas()
        {
            var at = new FlashAtlas { A = A, B = B, C = C };
            for (int i = 0; i < FlashAtlas.MaxSlices; i++)
            {
                if (i < grid.Length) at.grid[i] = grid[i];
                if (i < light.Length) at.light[i] = light[i];
                if (i < scatter.Length) at.scatter[i] = scatter[i];
                if (i < look.Length) at.look[i] = look[i];
                if (i < timing.Length) at.timing[i] = timing[i];
                if (i < place.Length) at.place[i] = place[i];
                if (i < top.Length) at.top[i] = top[i];
            }
            return at;
        }
    }
}
