// Plays a baked fireball simulation (Blender Mantaflow, unity/Sim) inside the volumetric cloud. Shared by the
// plugin and the Unity test harness. UnityEngine only.
using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>Per-variant description of a bake, written by the importer (Unity axes, lengths in fireball radii).</summary>
    [Serializable]
    public class NukeSimMeta
    {
        public string variant;
        public int resX, resY, resZ;
        public float[] boundsMin, boundsMax;   // domain, in R
        public float[] times;                  // sim seconds per frame
        public float[] top;                    // plume top above the domain floor, in R
        public float[] radius;                 // plume horizontal radius, in R
        public float densityScale, flameScale, tempScale;
    }

    public class NukeSimData
    {
        public NukeSimMeta meta;
        public Texture[] frames;
        public bool Valid
        {
            get
            {
                if (meta == null || frames == null || frames.Length < 2 || meta.times == null || meta.times.Length != frames.Length) return false;
                if (meta.resX <= 0 || meta.resY <= 0 || meta.resZ <= 0) return false;
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i] == null) return false;
                    // times must climb, or the frame search and blend divide by nothing
                    if (i > 0 && !(meta.times[i] > meta.times[i - 1])) return false;
                }
                return meta.times[frames.Length - 1] > 0f;
            }
        }
    }

    public static class NukeSimPlayer
    {
        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        /// <summary>
        /// Sets the sim part of the state for time t (s) of a detonation whose fireball stage lasts simDuration (s).
        /// The sim box is stretched each frame so the baked plume's top and width match the cap the procedural
        /// cloud has at that moment (capTopAGL above the ground, capRadius across): the hand-over from the sim to
        /// the procedural cloud then lines up instead of jumping.
        /// </summary>
        /// <param name="floorY">local height of the bake's origin: the ground for a ground burst, the burst point for an air burst</param>
        /// <param name="capTopAboveFloor">how high above floorY the procedural cap's top is right now</param>
        /// <summary>
        /// Surface bursts up to this yield (kt) play the full mushroom bake (fireball, ground fire, the column rolling up
        /// into its head) through the cloud's rise; bigger ones spread into a wide pancake the bake can't fit, and play
        /// only the fireball stage.
        /// </summary>
        public const float MushroomMaxYield = 150f;

        /// <summary>How long (s) the mushroom bake plays: most of the climb.</summary>
        public static float MushroomDuration(float riseTime) => Mathf.Max(12f, 0.5f * riseTime);

        /// <summary>How much of the mushroom bake to play: past this its thermal has diffused into haze.</summary>
        public const float MushroomPlayFraction = 0.55f;

        public static void Fill(NukeSimData d, ref NukeVolumeState v, float t, float simDuration, float floorY,
            Vector3 capCenter, float capTopAboveFloor, float capRadius, float fade, float fire, float playFraction = 1f, bool keepAspect = false)
        {
            v.simMix = 0f;
            if (d == null || !d.Valid || simDuration <= 0f) return;
            var m = d.meta;
            int last = m.times.Length - 1;
            // the bake covers the fireball stage (burning, rolling up); the procedural cap carries on from there
            // playFraction: only the part of the bake that still holds its shape (later frames diffuse into haze)
            float st = Mathf.Min(Mathf.Max(0f, t) / simDuration, 1f) * m.times[last] * Mathf.Clamp01(playFraction);
            int i = 0;
            while (i < last - 1 && m.times[i + 1] <= st) i++;
            float b = Mathf.Clamp01((st - m.times[i]) / Mathf.Max(1e-4f, m.times[i + 1] - m.times[i]));
            // plume top above the bake's origin (the domain floor sits below it for an air burst)
            float top = Mathf.Max(0.2f, Mathf.Lerp(m.top[i], m.top[i + 1], b) + m.boundsMin[1]);
            float rad = Mathf.Max(0.2f, Mathf.Lerp(m.radius[i], m.radius[i + 1], b));
            float sy = Mathf.Max(1f, capTopAboveFloor) / top;
            // keep the baked proportions within reason: stretching a plume too far turns it into a pencil
            float sxz = Mathf.Clamp(Mathf.Max(1f, capRadius) / rad, 0.8f * sy, 1.35f * sy);
            // the whole-mushroom bake already has the column's own proportions: stretching it out to a wide cap
            // turns the column into a fat egg
            if (keepAspect) sxz = sy;
            var bmin = new Vector3(m.boundsMin[0], m.boundsMin[1], m.boundsMin[2]);
            var bmax = new Vector3(m.boundsMax[0], m.boundsMax[1], m.boundsMax[2]);
            v.simOrigin = new Vector3(capCenter.x + bmin.x * sxz, floorY + bmin.y * sy, capCenter.z + bmin.z * sxz);
            v.simSize = new Vector3((bmax.x - bmin.x) * sxz, (bmax.y - bmin.y) * sy, (bmax.z - bmin.z) * sxz);
            v.simA = d.frames[i];
            v.simB = d.frames[i + 1];
            v.simBlend = b;
            // the sim is the fireball for most of its stage, then hands over to the procedural cap
            v.simMix = (1f - SS(0.55f * simDuration, simDuration, t)) * fade;
            v.simFire = fire;
            v.simDensity = 1.4f;
        }
    }
}
