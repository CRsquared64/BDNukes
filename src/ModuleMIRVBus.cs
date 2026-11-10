using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Multiple re-entry vehicles. In flight, on the way down near the top of the air, the bus releases its extra
    /// warheads (NukeMirvSalvo): they separate one by one and fan out to their own aim points across the footprint.
    /// When the part's BDModuleNuke detonates, the NukeFX.CreateExplosion hook bursts each released warhead where it
    /// is; any still aboard (a shot too short to release) burst scattered round the impact point as before. Each one
    /// is a full BDArmory explosion (damage included) with the part's yield.
    /// </summary>
    public class ModuleMIRVBus : PartModule
    {
        [KSPField] public int warheads = 10;       // number of RVs, including the one that detonates the bus
        [KSPField] public float spread = 6000f;    // m, radius of the impact footprint
        [KSPField] public float interval = 0.6f;   // s between successive detonations (+-50% random)

        [KSPField] public float releaseAltitude = -1f;   // m; below it on the way down the RVs go (-1: just above the air)

        bool released;
        double highest;

        /// <summary>The height to release at: just above the atmosphere (on airless bodies, 30 km).</summary>
        public double ReleaseAltitude(CelestialBody body)
        {
            if (releaseAltitude > 0f) return releaseAltitude * NukeScale.Planet;
            return body != null && body.atmosphere ? body.atmosphereDepth * 1.1d : 30000d;
        }

        public void FixedUpdate()
        {
            if (released || !HighLogic.LoadedSceneIsFlight || warheads <= 1 || !NukeSettings.Current.mirvBus) return;
            Vessel v = vessel;
            if (v == null || v.packed || v.mainBody == null || v.Landed || v.Splashed) return;
            var ml = part.FindModuleImplementing<MissileLauncher>();
            if (ml == null || !ml.HasFired) return;
            var st = ml.MissileState;
            if (st == MissileBase.MissileStates.Idle || st == MissileBase.MissileStates.Drop) return;
            if (v.altitude > highest) highest = v.altitude;
            if (v.verticalSpeed >= 0d) return;   // still climbing
            var prof = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof != null && prof.ballisticCutoff && !prof.ballisticCutoffDone) return;   // still under power
            double rel = ReleaseAltitude(v.mainBody);
            double agl = v.altitude - System.Math.Max(0d, v.terrainAltitude);
            // over the release height on the way down, or at the top of an arc too low to reach it
            bool go = v.altitude <= rel ? highest >= rel || highest - v.altitude > 200d : false;
            if (!go || agl < 12000d) return;
            released = true;
            try { NukeMirvSalvo.Release(this); }
            catch (System.Exception e) { Debug.LogWarning("[BDNukes]: MIRV release failed, the warheads will burst round the impact point: " + e.Message); }
        }

        public override string GetInfo() =>
            $"MIRV bus: {Mathf.Max(1, warheads)} warheads\nFootprint radius: {spread / 1000f:0.#} km";

        /// <summary>
        /// Surface offsets (metres east, metres north) for the extra warheads: a jittered sunflower spiral, so the
        /// footprint is evenly filled without the regular look of a grid.
        /// </summary>
        public Vector2[] FootprintOffsets(int count)
        {
            var pts = new Vector2[Mathf.Max(0, count)];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            float rot = Random.value * 2f * Mathf.PI;
            for (int i = 0; i < pts.Length; i++)
            {
                float r = spread * Mathf.Sqrt((i + 0.5f + Random.Range(-0.3f, 0.3f)) / pts.Length);
                float a = rot + i * golden + Random.Range(-0.25f, 0.25f);
                pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            }
            // shuffle so the delay order is not a spiral
            for (int i = pts.Length - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var tmp = pts[i]; pts[i] = pts[j]; pts[j] = tmp;
            }
            return pts;
        }
    }
}
