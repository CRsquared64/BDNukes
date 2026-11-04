using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Thermal scorch on craft: the flash chars the side of loaded vessels that faces the fireball.
    /// Purely cosmetic and not persisted; it lasts until the part is unloaded.
    /// </summary>
    /// <remarks>
    /// Uses the <c>_BurnColor</c> property of KSP's part shaders (a multiplier on the albedo, white by default).
    /// Stock code never sets it except through ModuleColorChanger (heat shield char), so the scorch is written
    /// as a floor on darkness: each frame the darker of the stock value and the scorch wins.
    /// </remarks>
    public static class NukeScorch
    {
        internal static readonly int BurnColorID = Shader.PropertyToID("_BurnColor");

        const int PartMask = 1 << 0;      // part colliders
        const int SceneryMask = 1 << 15;  // terrain and buildings

        /// <summary>Scorches every loaded vessel in sight of a burst at <paramref name="burst"/>.</summary>
        public static void Flash(Vector3 burst, float W, float R, float delay)
        {
            var vessels = FlightGlobals.VesselsLoaded;
            if (vessels == null) return;

            // Fluence falls off roughly as 1/d^2; no scorch beyond rMax, full char inside rFull.
            float rMax = 1200f * Mathf.Sqrt(Mathf.Max(W, 0.001f));
            float rFull = 0.3f * rMax;
            float kFull = (rMax / rFull) * (rMax / rFull) - 1f;
            float ramp = Mathf.Clamp(0.25f * Mathf.Pow(W, 0.3f), 0.2f, 4f); // thermal pulse length
            float start = Time.time + delay;
            var hits = new RaycastHit[32];

            for (int v = 0; v < vessels.Count; v++)
            {
                var vessel = vessels[v];
                if (vessel == null || vessel.parts == null) continue;
                if ((vessel.CoMD - (Vector3d)burst).sqrMagnitude > (double)rMax * rMax * 1.2) continue;

                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    var part = vessel.parts[i];
                    if (part == null) continue;
                    var rends = BurnRenderers(part);
                    if (rends.Count == 0) continue;

                    Vector3 c = Centre(rends);
                    Vector3 toBurst = burst - c;
                    float d = toBurst.magnitude;
                    if (d >= rMax || d < 1f) continue;
                    float fluence = Mathf.Clamp01(((rMax / d) * (rMax / d) - 1f) / kFull);
                    if (fluence < 0.02f) continue;

                    fluence *= Exposure(part, c, toBurst / d, d, R, hits);
                    if (fluence < 0.02f) continue;

                    var comp = part.gameObject.GetComponent<NukeScorchPart>();
                    if (comp == null) comp = part.gameObject.AddComponent<NukeScorchPart>();
                    comp.Add(part, rends, fluence, start, ramp);
                }
            }
        }

        /// <summary>
        /// 0 behind terrain, reduced when other parts stand between this one and the burst (the far side of
        /// the vessel stays clean), 1 in the open.
        /// </summary>
        static float Exposure(Part part, Vector3 c, Vector3 dir, float d, float R, RaycastHit[] hits)
        {
            int n = Physics.RaycastNonAlloc(c, dir, hits, d, PartMask | SceneryMask, QueryTriggerInteraction.Ignore);
            float k = 1f;
            for (int i = 0; i < n; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;
                if (col.gameObject.layer == 15)
                {
                    // the fireball sits on/in the ground for surface bursts: ignore the last stretch near it
                    if (d - hits[i].distance > 0.5f * R) return 0f;
                    continue;
                }
                var other = col.GetComponentInParent<Part>();
                if (other != null && other != part) k = Mathf.Min(k, 0.25f);
            }
            return k;
        }

        static readonly List<Renderer> scratch = new List<Renderer>();

        static List<Renderer> BurnRenderers(Part part)
        {
            var list = new List<Renderer>();
            scratch.Clear();
            var all = part.FindModelRenderersCached();
            if (all != null) scratch.AddRange(all);
            for (int i = 0; i < scratch.Count; i++)
            {
                var r = scratch[i];
                if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                var mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null && mats[m].HasProperty(BurnColorID)) { list.Add(r); break; }
                }
            }
            return list;
        }

        static Vector3 Centre(List<Renderer> rends)
        {
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Count; i++) b.Encapsulate(rends[i].bounds);
            return b.center;
        }
    }

    /// <summary>
    /// Keeps a part's scorch applied: re-written in LateUpdate after stock code has set its property blocks,
    /// taking the darker of the stock _BurnColor (heat shield char) and the scorch.
    /// </summary>
    public class NukeScorchPart : MonoBehaviour
    {
        static readonly Color Char = new Color(0.2f, 0.17f, 0.15f, 1f);
        const float MaxDarkness = 0.85f;

        Part part;
        Renderer[] rends;
        Color[] written;    // what this component last left in each renderer's block
        Color[] stockCol;   // the stock value it was darkened from
        MaterialPropertyBlock block;
        float prev;     // scorch already reached before the newest flash
        float target;   // scorch after the newest flash
        float start, ramp;

        internal void Add(Part p, List<Renderer> r, float fluence, float startTime, float rampTime)
        {
            part = p;
            if (rends == null || rends.Length != r.Count)
            {
                rends = r.ToArray();
                written = new Color[rends.Length];
                stockCol = new Color[rends.Length];
            }
            if (block == null) block = new MaterialPropertyBlock();
            prev = Current();
            target = 1f - (1f - target) * (1f - fluence); // several flashes add up
            start = startTime;
            ramp = rampTime;
        }

        float Current() => Mathf.Lerp(prev, target, ramp > 0f ? Mathf.Clamp01((Time.time - start) / ramp) : 1f);

        void LateUpdate()
        {
            if (rends == null || part == null) return;
            float k = Current();
            if (k <= 0f) return;
            Color scorch = Color.Lerp(Color.white, Char, k * MaxDarkness);
            int id = NukeScorch.BurnColorID;

            // share the value with the part's own block, so stock writes of part.mpb carry it too
            if (part.mpb != null) part.mpb.SetColor(id, scorch);

            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null) continue;
                r.GetPropertyBlock(block);
                Color cur = block.GetColor(id);

                // whatever the stock code wants: its own block value, or the material's
                Color stock;
                bool unset = cur.r <= 0f && cur.g <= 0f && cur.b <= 0f && cur.a <= 0f;
                if (cur == written[i] && !unset) stock = stockCol[i];
                else if (!unset && cur != scorch) stock = cur;  // part.mpb carrying our own value is not stock
                else stock = r.sharedMaterial != null ? r.sharedMaterial.GetColor(id) : Color.white;
                stockCol[i] = stock;

                Color c = new Color(Mathf.Min(stock.r, scorch.r), Mathf.Min(stock.g, scorch.g),
                    Mathf.Min(stock.b, scorch.b), Mathf.Max(stock.a, scorch.a));
                written[i] = c;
                if (c == cur) continue;
                block.SetColor(id, c);
                r.SetPropertyBlock(block);
            }
        }
    }
}
