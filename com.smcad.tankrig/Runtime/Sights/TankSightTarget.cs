using System.Collections.Generic;
using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>Marks an object for the helmet sight's target markers and minimap (other tanks are found automatically).</summary>
    [AddComponentMenu("Tank Rig/Sights/Sight Target")]
    public class TankSightTarget : MonoBehaviour
    {
        public enum Kind { [InspectorName("不明")] Unknown, [InspectorName("敵")] Hostile, [InspectorName("味方")] Friendly }
        public Kind kind = Kind.Unknown;
        [Tooltip("マーカーに添える名前（なくても可）")] public string label;

        static readonly List<TankSightTarget> all = new List<TankSightTarget>();
        public static IReadOnlyList<TankSightTarget> All => all;
        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);
    }
}
