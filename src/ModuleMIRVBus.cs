using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Multiple re-entry vehicles. When the part's BDModuleNuke detonates, the NukeFX.CreateExplosion hook
    /// adds (warheads - 1) more BDArmory nuclear explosions scattered across the impact footprint, each at the
    /// same height above the terrain as the original, with staggered delays. Each one is a full BDArmory
    /// explosion (damage included) with the part's yield.
    /// </summary>
    public class ModuleMIRVBus : PartModule
    {
        [KSPField] public int warheads = 10;       // number of RVs, including the one that detonates the bus
        [KSPField] public float spread = 6000f;    // m, radius of the impact footprint
        [KSPField] public float interval = 0.6f;   // s between successive detonations (+-50% random)

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
