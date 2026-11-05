using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Every burst lights the ground with point lights of its own (the fireball, the firestorm, the ground fires), each
    /// set by its effect as if it were alone. Several bursts close together stacked them: six 100 kt bursts put six
    /// fireballs' light on the same ground and washed it out to yellow-white, and each fireball light cast its own soft
    /// shadow cube. Run after the effects have set their lights (NukeFXManager's LateUpdate), this scales every light so
    /// the light where several overlap grows only slowly past the brightest of them, and keeps shadows on the strongest.
    /// </summary>
    internal static class NukeLightBudget
    {
        class Entry
        {
            public Light light;
            public float desired;    // what the effect asked for
            public float applied;    // what this budget left on the light (to tell a fresh value from our own)
            public LightShadows shadows;
        }

        static readonly List<Entry> entries = new List<Entry>();
        /// <summary>How fast the combined light grows past the brightest light (0: never, 1: the plain sum).</summary>
        const float Growth = 0.35f;
        const int ShadowLights = 1;

        public static void Register(Light light)
        {
            if (light == null) return;
            foreach (var e in entries) if (e.light == light) return;
            entries.Add(new Entry { light = light, desired = light.intensity, applied = -1f, shadows = light.shadows });
        }

        static float Reach(Entry e, Vector3 p)
        {
            float r = Mathf.Max(1f, e.light.range);
            float d = Vector3.Distance(e.light.transform.position, p) / r;
            if (d >= 1f) return 0f;
            float w = 1f - d * d;
            return w * w;
        }

        static readonly List<Entry> live = new List<Entry>();

        public static void Apply()
        {
            live.Clear();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.light == null) { entries.RemoveAt(i); continue; }
                // an effect that didn't set its light this frame leaves our scaled value: keep what it asked for last
                if (Mathf.Abs(e.light.intensity - e.applied) > 1e-5f) e.desired = e.light.intensity;
                if (e.light.enabled && e.light.gameObject.activeInHierarchy && e.desired > 1e-4f) live.Add(e);
                else e.applied = e.light.intensity;
            }
            if (live.Count == 0) return;
            if (live.Count > 1) live.Sort((a, b) => (b.desired * b.light.range).CompareTo(a.desired * a.light.range));
            for (int i = 0; i < live.Count; i++)
            {
                var e = live[i];
                Vector3 p = e.light.transform.position;
                float sum = 0f, peak = 0f;
                foreach (var o in live)
                {
                    float c = o.desired * Reach(o, p);
                    sum += c;
                    if (c > peak) peak = c;
                }
                float k = 1f;
                if (sum > peak * 1.01f && peak > 0f) k = peak * Mathf.Pow(sum / peak, Growth) / sum;
                e.applied = e.desired * k;
                e.light.intensity = e.applied;
                e.light.shadows = i < ShadowLights ? e.shadows : LightShadows.None;
            }
        }
    }
}
