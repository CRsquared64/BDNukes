using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A steady wind for one detonation: the scene's heading plus a slow random veer, a ground speed and a
    /// stronger wind aloft (shear). The displacement of a parcel of air is integrated once into a table, so
    /// the cloud, the fallout plume and the smoke all drift along the same path. Positions are in the
    /// detonation's surface-locked local frame (x/z horizontal).
    /// </summary>
    internal class NukeWind
    {
        const float Step = 2f;          // s per table entry
        readonly Vector2[] table;       // unit-speed displacement at i * Step
        readonly Vector2[] dirs;
        public readonly float lowSpeed, hiSpeed;

        public NukeWind(float headingDeg, float speed, float shear, int seed, float maxTime)
        {
            lowSpeed = Mathf.Max(0f, speed);
            hiSpeed = lowSpeed * Mathf.Max(0.5f, shear);
            var rng = new System.Random(seed);
            float veerRate = ((float)rng.NextDouble() * 2f - 1f) * 0.08f;             // deg/s, slow backing or veering
            float veerAmp = 6f + 8f * (float)rng.NextDouble();                         // deg of wander
            float veerFreq = 2f * Mathf.PI / (180f + 240f * (float)rng.NextDouble());
            float veerPhase = (float)rng.NextDouble() * 2f * Mathf.PI;
            float h0 = headingDeg + ((float)rng.NextDouble() * 2f - 1f) * 10f;

            int n = Mathf.Clamp(Mathf.CeilToInt(maxTime / Step) + 2, 4, 20000);
            table = new Vector2[n];
            dirs = new Vector2[n];
            Vector2 p = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                float time = i * Step;
                float a = (h0 + veerRate * time + veerAmp * Mathf.Sin(veerFreq * time + veerPhase)) * Mathf.Deg2Rad;
                var d = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                dirs[i] = d;
                table[i] = p;
                p += d * Step;
            }
        }

        Vector2 Unit(float time)
        {
            if (time <= 0f) return Vector2.zero;
            float f = time / Step;
            int i = Mathf.FloorToInt(f);
            if (i >= table.Length - 1) return table[table.Length - 1] + dirs[dirs.Length - 1] * (time - (table.Length - 1) * Step);
            return Vector2.Lerp(table[i], table[i + 1], f - i);
        }

        static Vector3 V3(Vector2 v) => new Vector3(v.x, 0f, v.y);

        /// <summary>Drift of near-ground air since detonation (m).</summary>
        public Vector3 Low(float time) => V3(Unit(time) * lowSpeed);

        /// <summary>Drift of air at cloud-cap height since detonation (m).</summary>
        public Vector3 Hi(float time) => V3(Unit(time) * hiSpeed);

        /// <summary>Drift at a height fraction between the ground (0) and the cap (1).</summary>
        public Vector3 At(float time, float frac) => V3(Unit(time) * Mathf.Lerp(lowSpeed, hiSpeed, Mathf.Clamp01(frac)));

        /// <summary>Unit wind direction at a time.</summary>
        public Vector3 Dir(float time)
        {
            int i = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, time) / Step), 0, dirs.Length - 1);
            return V3(dirs[i]);
        }
    }
}
