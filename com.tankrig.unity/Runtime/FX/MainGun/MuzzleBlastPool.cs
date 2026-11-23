// Several shots of one gun at once: a few copies (slots) of a MuzzleBlastSim, each with its own grids, so the next shot
// does not wipe the smoke of the last one. Fire() uses a slot that has finished, else the oldest. Every live slot is
// drawn by the passes (far shots first). Memory and simulation cost grow with the number of shots alive at once.
using System.Collections.Generic;
using UnityEngine;

namespace TankRig.FX
{
    public class MuzzleBlastPool : MonoBehaviour
    {
        [Tooltip("the configured shot (muzzle, tank, solvers, settings); the other slots are copies of it")]
        public MuzzleBlastSim template;
        [Tooltip("shots that can be alive at once (each holds its own grids: ~160 MB at the real-time preset)")]
        [Min(1)] public int slots = 2;

        readonly List<MuzzleBlastSim> pool = new List<MuzzleBlastSim>();
        public IReadOnlyList<MuzzleBlastSim> Slots { get { Build(); return pool; } }

        void Build()
        {
            if (pool.Count > 0 || !template) return;
            pool.Add(template);
            for (int i = 1; i < slots; i++)
            {
                var go = Instantiate(template.gameObject, template.transform.parent);
                go.name = template.gameObject.name + " #" + (i + 1);
                var sim = go.GetComponent<MuzzleBlastSim>();
                sim.p = JsonUtility.FromJson<MuzzleBlastParams>(JsonUtility.ToJson(template.p));   // own copies of the settings
                sim.fx = JsonUtility.FromJson<MuzzleFxControls>(JsonUtility.ToJson(template.fx));
                // each shot lights the scene with its own flash (a shared light would be driven by every slot)
                if (template.flashLight && !template.flashLight.transform.IsChildOf(template.transform))
                {
                    var l = Instantiate(template.flashLight.gameObject, template.flashLight.transform.parent);
                    l.name = template.flashLight.gameObject.name + " #" + (i + 1);
                    sim.flashLight = l.GetComponent<Light>();
                }
                // the template may carry settings changed in code (e.g. the real-time preset) that a copy keeps anyway:
                // resolutions and grid switches are plain fields, copied by Instantiate
                pool.Add(sim);
            }
        }

        void Awake() => Build();

        /// <summary>Fire a shot from a free slot (or the oldest). Returns the slot used.</summary>
        public MuzzleBlastSim Fire()
        {
            Build();
            MuzzleBlastSim pick = null;
            foreach (var s in pool) if (!s.Active) { pick = s; break; }
            if (pick == null)
            {
                pick = pool[0];
                foreach (var s in pool) if (s.Time > pick.Time) pick = s;   // oldest shot alive
            }
            pick.Fire();
            return pick;
        }
    }
}
