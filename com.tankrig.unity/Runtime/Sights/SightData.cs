using UnityEngine;

namespace TankRig.Sights
{
    /// <summary>Angle unit used by a reticle's scales and readouts.</summary>
    public enum AngleUnit
    {
        [InspectorName("NATO ミル（1 周 = 6400）")] NatoMil,
        [InspectorName("ソ連・ロシアの千分（1 周 = 6000）")] Thousandth,
        [InspectorName("ミリラジアン（1 周 ≒ 6283）")] Milliradian,
        [InspectorName("度（1 周 = 360）")] Degree,
    }

    /// <summary>How the sight's line of sight moves.</summary>
    public enum LineOfSight
    {
        [InspectorName("独立安定化（照準線は車体の揺れに関係なく止まる。砲が追いかける）")] Stabilized,
        [InspectorName("砲と連動（照準線は砲の向きそのもの。砲が遅れると視界も遅れる）")] GunLinked,
    }

    public enum ReticleStyle
    {
        [InspectorName("西側（十字 + ミル目盛り）")] Western,
        [InspectorName("ソ連・ロシア（山形 + 距離尺 + 測距曲線）")] Soviet,
    }

    public enum LaserReturn
    {
        [InspectorName("最初の反射")] First,
        [InspectorName("最後の反射")] Last,
    }

    /// <summary>One step of an optical sight: magnification and the true field of view seen through it.</summary>
    [System.Serializable]
    public class ZoomStep
    {
        [Tooltip("倍率（表示用）")] public float magnification = 3f;
        [Tooltip("実視界（度）。接眼レンズの円の直径に見える範囲")] public float fieldOfView = 18f;

        public ZoomStep() { }
        public ZoomStep(float magnification, float fieldOfView) { this.magnification = magnification; this.fieldOfView = fieldOfView; }
    }

    /// <summary>
    /// A round the fire-control system knows. Used for the ammunition readout and the ballistic range scales.
    /// The plugin's shot is still a straight line, so the drop only changes what the reticle draws.
    /// </summary>
    [System.Serializable]
    public class AmmoType
    {
        [Tooltip("照準器に出す名前（例: APFSDS）")] public string name = "APFSDS";
        [Tooltip("距離尺などに出す短い記号（例: БР, S）")] public string code = "S";
        [Tooltip("砲口初速（m/s）")] public float muzzleVelocity = 1650f;
        [Tooltip("空気抵抗: 1 km 飛ぶごとに失う速さの割合（0.05 = 5 %）")] [Range(0f, 0.9f)] public float velocityLossPerKm = 0.05f;
        [Tooltip("距離尺の最大距離（m）")] public float maxRange = 4000f;

        public AmmoType() { }
        public AmmoType(string name, string code, float v0, float lossPerKm, float maxRange)
        {
            this.name = name; this.code = code; muzzleVelocity = v0; velocityLossPerKm = lossPerKm; this.maxRange = maxRange;
        }

        /// <summary>Time of flight (s) to <paramref name="range"/> m, with the speed falling off exponentially.</summary>
        public float TimeOfFlight(float range)
        {
            float v0 = Mathf.Max(1f, muzzleVelocity);
            float k = -Mathf.Log(Mathf.Clamp(1f - velocityLossPerKm, 0.05f, 1f)) / 1000f;
            if (k < 1e-7f) return range / v0;
            return (Mathf.Exp(k * range) - 1f) / (k * v0);
        }

        /// <summary>Gun superelevation (radians) needed to hit at <paramref name="range"/> m on flat ground.</summary>
        public float Superelevation(float range)
        {
            if (range <= 1f) return 0f;
            float t = TimeOfFlight(range);
            return Mathf.Atan(0.5f * 9.81f * t * t / range);
        }

        public static AmmoType[] DefaultWestern() => new[]
        {
            new AmmoType("APFSDS", "S", 1650f, 0.05f, 4000f),
            new AmmoType("HEAT", "H", 1140f, 0.12f, 3000f),
            new AmmoType("HE", "HE", 900f, 0.15f, 4000f),
            new AmmoType("MG", "MG", 830f, 0.45f, 1500f),
        };

        public static AmmoType[] DefaultSoviet() => new[]
        {
            new AmmoType("APFSDS", "БР", 1700f, 0.05f, 4000f),
            new AmmoType("HEAT", "КУМ", 905f, 0.12f, 4000f),
            new AmmoType("HE-FRAG", "ОФ", 850f, 0.15f, 5000f),
            new AmmoType("PKT", "ПКТ", 825f, 0.45f, 1800f),
        };
    }

    /// <summary>Laser rangefinder settings.</summary>
    [System.Serializable]
    public class LaserRangefinder
    {
        [Tooltip("レーザー測距儀を使う")] public bool enabled = true;
        [Tooltip("測れる最短距離（m）")] public float minRange = 200f;
        [Tooltip("測れる最大距離（m）")] public float maxRange = 9990f;
        [Tooltip("表示の刻み（m）")] public float resolution = 10f;
        [Tooltip("反射が複数あるとき、どれを採るか（初期値は最後）")] public LaserReturn useReturn = LaserReturn.Last;
        [Tooltip("次に測れるまでの時間（秒）")] public float cycleTime = 1.3f;
        [Tooltip("表示の桁数")] [Range(3, 5)] public int digits = 4;
    }

    public static class SightMath
    {
        public static float PerCircle(AngleUnit u) => u switch
        {
            AngleUnit.NatoMil => 6400f,
            AngleUnit.Thousandth => 6000f,
            AngleUnit.Milliradian => 2000f * Mathf.PI,
            _ => 360f,
        };

        /// <summary>Radians per unit.</summary>
        public static float Rad(AngleUnit u) => 2f * Mathf.PI / PerCircle(u);

        public static string Short(AngleUnit u) => u switch
        {
            AngleUnit.NatoMil => "MIL",
            AngleUnit.Thousandth => "т.д.",
            AngleUnit.Milliradian => "MRAD",
            _ => "°",
        };

        /// <summary>Range text with a fixed number of digits ("----" when there is none).</summary>
        public static string RangeText(float range, int digits)
        {
            if (range <= 0f) return new string('-', digits);
            int max = (int)Mathf.Pow(10, digits) - 1;
            return Mathf.Min(max, Mathf.RoundToInt(range)).ToString(new string('0', digits));
        }
    }
}
