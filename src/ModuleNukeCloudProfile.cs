using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Shapes the mushroom cloud's character (colour, cap shape, stem, collars, skirt) rather than its size.
    /// Read from the detonating part in the NukeFX.CreateExplosion hook. Without this module, or with
    /// style = auto, everything is worked out from the environment at detonation (see NukeCloudProfile.Resolve).
    /// Any field left at -1 (or empty) comes from the style preset or the environment; set it to override.
    /// </summary>
    public class ModuleNukeCloudProfile : PartModule
    {
        [KSPField] public string style = "auto";      // auto | desert | oceanic | tower | tropicalMegaton | tsar | highAltitude
        [KSPField] public float dustiness = -1f;      // 0 white condensation .. 1 heavy brown/red dust
        [KSPField] public float capAspect = -1f;      // cap height/width: ~0.3 flat pancake .. ~1.0 tall cauliflower
        [KSPField] public float capSpread = -1f;      // multiplier on cap radius
        [KSPField] public float stemWidth = -1f;      // multiplier on stem radius
        [KSPField] public float stemCollars = -1f;    // condensation collars around the stem (integer)
        [KSPField] public float skirtScale = -1f;     // multiplier on the base dust skirt
        [KSPField] public float billowScale = -1f;    // puff size multiplier (smaller = finer cauliflower detail)
        [KSPField] public float glowScale = -1f;      // incandescence duration multiplier
        [KSPField] public string tint = "";           // optional "r,g,b" dust tint override

        /// <summary>
        /// Ballistic missiles only (MissileLauncher with homingType = agmballistic and thrust): cut the motor once the
        /// drag-free trajectory reaches the target, instead of BDArmory's slow throttle-back (see NukeBallisticCutoff).
        /// </summary>
        [KSPField] public bool ballisticCutoff = true;
        /// <summary>Cut when the predicted range is this multiple of the target range.</summary>
        [KSPField] public float ballisticCutoffMargin = 1f;
        /// <summary>
        /// Predict the coast with drag (the missile's own drag, measured in flight) rather than in vacuum, so the warhead
        /// comes down on the target instead of the drag-free point beyond it.
        /// </summary>
        [KSPField] public bool ballisticDrag = true;
        /// <summary>Scale on the measured drag in that prediction (above 1 cuts later, for a draggier re-entry).</summary>
        [KSPField] public float ballisticDragScale = 1f;

        // in-flight drag estimate and the last drag prediction (NukeBallisticCutoff); not saved
        [NonSerialized] internal double cutCdA;          // m^2: drag force / dynamic pressure, smoothed
        [NonSerialized] internal double cutDragLoss;     // rad: drag-free sweep minus the sweep with drag
        [NonSerialized] internal double cutTargetShift;  // rad: target angle at the drag flight time minus at the drag-free one
        [NonSerialized] internal bool cutHaveDrag;
        [NonSerialized] internal double cutStockCdA;     // m^2: the part of cutCdA measured from stock drag
        [NonSerialized] internal float cutNextSim;

        /// <summary>
        /// Scale on the smoke from BDArmory's exhaust effects (emission, particle size and life); 1 leaves them as
        /// they are. BDArmory's largeExhaust piles up into a huge cloud round a slow heavy missile leaving the pad.
        /// </summary>
        [KSPField] public float exhaustSmoke = 1f;

        /// <summary>Set once the motor has been cut; it never relights.</summary>
        [KSPField(isPersistant = true)] public bool ballisticCutoffDone;

        /// <summary>Snapshot of the requested profile, safe to keep after the part is destroyed.</summary>
        internal NukeCloudProfile ToProfile()
        {
            var p = new NukeCloudProfile
            {
                style = string.IsNullOrEmpty(style) ? "auto" : style.Trim(),
                dustiness = dustiness,
                capAspect = capAspect,
                capSpread = capSpread,
                stemWidth = stemWidth,
                stemCollars = stemCollars < 0f ? -1 : Mathf.RoundToInt(stemCollars),
                skirtScale = skirtScale,
                billowScale = billowScale,
                glowScale = glowScale,
            };
            if (NukeCloudProfile.TryParseColor(tint, out Color c)) { p.hasTint = true; p.tint = c; }
            return p.Latch();
        }

        internal static NukeCloudProfile FromPart(Part part)
        {
            if (part == null) return null;
            var m = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            return m != null ? m.ToProfile() : null;
        }

        public override string GetInfo() => "Cloud style: " + (string.IsNullOrEmpty(style) ? "auto" : style);
    }

    /// <summary>The environment around a detonation that the auto style reads.</summary>
    internal struct NukeCloudEnv
    {
        public CelestialBody body;
        public double lat, lon, alt;    // burst point; alt is ASL
        public float W;                 // yield (kt)
        public float R;                 // fireball radius (m)
        public float hb;                // burst height above ground or sea (m)
        public float groundFactor;      // 1 = surface burst, 0 = no ground interaction
        public bool overOcean;          // sea under the burst
        public float cloudTopASL;       // where the unconstrained cloud top would end up (m ASL)
        public float tropopauseASL;     // m ASL
    }

    /// <summary>
    /// The cloud's character. Requested values (-1 = unset) come from ModuleNukeCloudProfile; Resolve() fills
    /// every field from the environment, then the style preset, then explicit overrides, in that order.
    /// </summary>
    internal class NukeCloudProfile
    {
        // --- the cfg-facing parameters ---
        public string style = "auto";
        public float dustiness = -1f, capAspect = -1f, capSpread = -1f, stemWidth = -1f;
        public int stemCollars = -1;
        public float skirtScale = -1f, billowScale = -1f, glowScale = -1f;
        public bool hasTint;
        public Color tint = Color.white;

        // --- derived look parameters (always filled by Resolve) ---
        public float humidity;          // 0 dry .. 1 saturated: whiter cloud, more collars
        public bool water;              // ocean under the burst: white condensation and spray
        public float overhang;          // how far the cap's outer rim curls down past the stem
        public float rollSpeed = 1f;    // poloidal roll rate multiplier
        public float turbulence = 1f;   // boiling / lumpiness multiplier
        public float rimLight = 0.35f;  // backlit silver lining strength
        public float stack;             // 0..1 second, higher cap layer (Tsar Bomba)
        public float pancake;           // 0..1 how much the cap spreads along the tropopause
        public float stemTaper = 1f;    // flare at the stem base (0 = straight column)
        public float lowYield;          // 0..1 sub-0.1 kt look: squat dusty puff, short stem
        public float hiAltBias;         // highAltitude style: show the transitional look in denser air
        public string biome = "";
        public Color dustTint;          // resolved dust/smoke tint

        public bool Resolved { get; private set; }

        static readonly Color RedBrown = new Color(0.55f, 0.31f, 0.20f);
        static readonly Color GreyBrown = new Color(0.47f, 0.39f, 0.31f);
        static readonly Color Rocky = new Color(0.45f, 0.41f, 0.37f);
        static readonly Color Sandy = new Color(0.62f, 0.53f, 0.42f);
        static readonly Color WhiteGrey = new Color(0.76f, 0.76f, 0.79f);
        static readonly Color Spray = new Color(0.92f, 0.93f, 0.95f);

        public static bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            float[] v = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return false;
            c = new Color(Mathf.Clamp01(v[0]), Mathf.Clamp01(v[1]), Mathf.Clamp01(v[2]));
            return true;
        }

        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        /// <summary>Fills every parameter. Safe to call on a null request via ResolveOrAuto.</summary>
        public static NukeCloudProfile ResolveOrAuto(NukeCloudProfile request, NukeCloudEnv env)
        {
            var p = request ?? new NukeCloudProfile();
            if (!p.Resolved) p.Resolve(env);
            return p;
        }

        public void Resolve(NukeCloudEnv env)
        {
            var s = NukeSettings.Current;

            // ---------------- 1. environment (style = auto) ----------------
            string b = "";
            try
            {
                if (env.body != null) b = ScienceUtil.GetExperimentBiome(env.body, env.lat, env.lon) ?? "";
            }
            catch (Exception) { b = ""; }
            biome = b;
            string bl = b.ToLowerInvariant();

            Color landTint = Rocky;
            float landDust = 0.6f, dryness = 0.4f;
            if (Has(bl, "desert", "badland", "mesa", "dune", "canyon")) { landTint = RedBrown; landDust = 0.92f; dryness = 0.85f; }
            else if (Has(bl, "ice", "tundra", "pole", "polar", "snow", "glacier", "shelf", "frozen")) { landTint = WhiteGrey; landDust = 0.35f; dryness = 0.55f; }
            else if (Has(bl, "grass", "plain", "savanna", "steppe", "meadow", "lowland", "forest", "jungle")) { landTint = GreyBrown; landDust = 0.65f; dryness = 0.3f; }
            else if (Has(bl, "shore", "beach", "coast")) { landTint = Sandy; landDust = 0.55f; dryness = 0.2f; }
            if (env.body != null && env.body.name == "Duna") { landTint = RedBrown; landDust = 0.95f; dryness = 1f; }

            water = env.overOcean || Has(bl, "water", "ocean", "sea");
            float gf = Mathf.Clamp01(env.groundFactor);
            // dust only when the fireball reaches the ground; water lifts spray, not dust
            float dust = water ? 0f : landDust * gf;

            // humid air near sea level condenses into collars and whitens the cloud
            float depth = (float)NukeScale.AirDepth(env.body);
            float lowAir = Mathf.Clamp01(1f - (float)env.alt / (0.08f * depth));
            humidity = lowAir * (water ? 1f : 0.85f * (1f - dryness));

            lowYield = env.W < s.lowYieldThreshold ? Mathf.Clamp01(Mathf.Log10(s.lowYieldThreshold / Mathf.Max(env.W, 1e-4f)) / 1.5f) : 0f;

            // tropopause: once the cloud top would pass it the cap stalls and spreads into a pancake
            float pk = 0f;
            if (env.tropopauseASL > 0f && env.cloudTopASL > env.tropopauseASL)
                pk = SS(0f, 1f, (env.cloudTopASL - env.tropopauseASL) / (0.6f * env.tropopauseASL));
            pancake = pk;

            dustiness = Mathf.Clamp01(dust + 0.25f * lowYield * gf * (water ? 0f : 1f));
            capAspect = Mathf.Lerp(Mathf.Lerp(0.6f, 0.85f, dust), 0.95f, lowYield);
            capSpread = (1f + 0.25f * humidity * (1f - dust)) * Mathf.Lerp(1f, 0.8f, lowYield);
            overhang = (0.15f + 0.4f * humidity * (1f - dust)) * (1f - lowYield);
            stemWidth = Mathf.Lerp(1f, 0.78f, dust) * Mathf.Lerp(1f, 1.7f, lowYield);
            stemCollars = lowYield > 0.01f ? 0 : Mathf.Clamp(Mathf.RoundToInt(humidity * 4.2f * (1f - 0.6f * pk)), 0, 5);
            skirtScale = (water ? 0.9f : 0.6f + 1.3f * dust) * Mathf.Lerp(1f, 1.2f, lowYield);
            billowScale = (1f + 0.25f * dust - 0.12f * humidity) * Mathf.Lerp(1f, 1.2f, lowYield);
            glowScale = 1f + 0.8f * pk;
            dustTint = water ? Spray : landTint;
            rollSpeed = 1f;
            turbulence = 1f + 0.4f * dust;
            rimLight = 0.35f;
            stack = SS(5000f, 30000f, env.W) * 0.8f;
            stemTaper = Mathf.Lerp(1f, 0.5f, pk);
            hiAltBias = 0f;

            // ---------------- 2. style preset ----------------
            switch ((style ?? "auto").ToLowerInvariant())
            {
                case "desert": // Nevada tower shot: red-brown lumpy tall cap, thin dark stem, huge ground-hugging skirt
                    dustiness = 0.92f; capAspect = 1.1f; capSpread = 0.8f; stemWidth = 0.65f; stemCollars = 0;
                    skirtScale = 2.4f; billowScale = 1.3f; glowScale = 0.85f; dustTint = RedBrown;
                    rollSpeed = 0.8f; turbulence = 1.6f; overhang = 0.05f; stemTaper = 0.8f; water = false;
                    break;
                case "oceanic": // humid tropical tower/barge shot: brilliant white wide cauliflower, collared white stem
                    dustiness = 0f; capAspect = 0.72f; capSpread = 1.3f; stemWidth = 1f; stemCollars = 4;
                    skirtScale = 0.9f; billowScale = 0.85f; glowScale = 1f; dustTint = Spray;
                    rollSpeed = 1.1f; turbulence = 0.9f; overhang = 0.5f; humidity = Mathf.Max(humidity, 0.9f); stemTaper = 0.7f;
                    break;
                case "tower": // generic tower shot over dry ground
                    dustiness = 0.55f; capAspect = 0.85f; capSpread = 1f; stemWidth = 0.8f; stemCollars = 1;
                    skirtScale = 1.5f; billowScale = 1.05f; glowScale = 1f; dustTint = GreyBrown;
                    rollSpeed = 1f; turbulence = 1.2f; overhang = 0.2f;
                    break;
                case "tropicalmegaton": // Pacific megaton airburst: huge flat white pancake on the tropopause, long glow
                    dustiness = 0.04f; capAspect = 0.3f; capSpread = 1.5f; stemWidth = 0.55f; stemCollars = 2;
                    skirtScale = 1.1f; billowScale = 0.9f; glowScale = 2.6f; dustTint = Spray;
                    rollSpeed = 0.8f; turbulence = 0.8f; overhang = 0.3f; pancake = Mathf.Max(pancake, 0.95f);
                    humidity = Mathf.Max(humidity, 0.8f); stemTaper = 0.4f;
                    break;
                case "tsar": // Tsar Bomba: very thick column, stacked cap with a second layer, strong backlit rim
                    dustiness = 0.12f; capAspect = 0.65f; capSpread = 1.25f; stemWidth = 1.9f; stemCollars = 2;
                    skirtScale = 1f; billowScale = 1.15f; glowScale = 1.6f; dustTint = WhiteGrey;
                    rollSpeed = 0.7f; turbulence = 1.1f; overhang = 0.3f; rimLight = 1.5f; stack = 1f;
                    pancake = Mathf.Clamp(pancake, 0.25f, 0.45f); stemTaper = 0.3f;
                    break;
                case "highaltitude": // thin-air burst: glow-heavy, no collars, little dust; transitional look comes in earlier
                    dustiness = 0f; capAspect = 0.5f; capSpread = 1.2f; stemWidth = 0.5f; stemCollars = 0;
                    skirtScale = 0.3f; billowScale = 1f; glowScale = 1.8f; dustTint = WhiteGrey;
                    rollSpeed = 1.3f; turbulence = 0.8f; overhang = 0.1f; hiAltBias = 1f;
                    break;
            }

            // ---------------- 3. explicit overrides ----------------
            var req = this;
            if (req.reqDustiness >= 0f) dustiness = req.reqDustiness;
            if (req.reqCapAspect >= 0f) capAspect = req.reqCapAspect;
            if (req.reqCapSpread >= 0f) capSpread = req.reqCapSpread;
            if (req.reqStemWidth >= 0f) stemWidth = req.reqStemWidth;
            if (req.reqStemCollars >= 0) stemCollars = req.reqStemCollars;
            if (req.reqSkirtScale >= 0f) skirtScale = req.reqSkirtScale;
            if (req.reqBillowScale >= 0f) billowScale = req.reqBillowScale;
            if (req.reqGlowScale >= 0f) glowScale = req.reqGlowScale;
            if (hasTint) dustTint = tint;

            dustiness = Mathf.Clamp01(dustiness);
            capAspect = Mathf.Clamp(capAspect, 0.15f, 1.8f);
            capSpread = Mathf.Clamp(capSpread, 0.3f, 4f);
            stemWidth = Mathf.Clamp(stemWidth, 0.2f, 4f);
            stemCollars = Mathf.Clamp(stemCollars, 0, 6);
            if (!s.stemCollars) stemCollars = 0;
            skirtScale = Mathf.Clamp(skirtScale, 0f, 5f);
            billowScale = Mathf.Clamp(billowScale, 0.4f, 2.5f);
            glowScale = Mathf.Clamp(glowScale, 0.2f, 5f);
            pancake = Mathf.Clamp01(pancake * s.pancakeStrength);
            Resolved = true;
        }

        // The cfg request is kept separately so Resolve can overwrite the public fields with resolved values.
        float reqDustiness = -1f, reqCapAspect = -1f, reqCapSpread = -1f, reqStemWidth = -1f, reqSkirtScale = -1f, reqBillowScale = -1f, reqGlowScale = -1f;
        int reqStemCollars = -1;

        /// <summary>Latches the requested (-1 = unset) values before Resolve overwrites the fields.</summary>
        public NukeCloudProfile Latch()
        {
            reqDustiness = dustiness; reqCapAspect = capAspect; reqCapSpread = capSpread; reqStemWidth = stemWidth;
            reqStemCollars = stemCollars; reqSkirtScale = skirtScale; reqBillowScale = billowScale; reqGlowScale = glowScale;
            return this;
        }

        static bool Has(string s, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++) if (s.Contains(keys[i])) return true;
            return false;
        }
    }
}
