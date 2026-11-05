using System;
using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// What a detonation does to the world around it beyond its own cloud: flattened forests (Parallax scatters),
    /// a persistent fallout footprint downwind, and a firestorm for big bursts over land. One call per new
    /// (not restored) detonation, from NukeCinematic.Setup.
    /// </summary>
    internal static class NukeWorldEffects
    {
        /// <param name="groundAlt">ASL of the ground under the burst (0 over the sea)</param>
        /// <param name="airBurst">the burst is in air (not underwater, not a vacuum look)</param>
        public static void Detonation(CelestialBody body, double lat, double lon, double groundAlt, float W, float R, float hb,
            bool overOcean, bool airBurst, float falloutK, float windHeading, NukeWind wind, double spawnTime, float delay)
        {
            var s = NukeSettings.Current;
            if (body == null) return;
            try
            {
                if (s.flattenTrees && airBurst && body.atmosphere && !overOcean)
                {
                    float kill = NukeScatterFlatten.KillRadius(W, hb, s.sizeScale);
                    var crater = NukeCraters.Plan(body, lat, lon, W, R, hb);   // the same size PlanCrater dug (null: none)
                    float shock = delay + 0.5f + 0.5f * kill / Mathf.Max(50f, s.speedOfSound);
                    NukeScatterFlatten.Add(body, lat, lon, kill, crater != null ? crater.radius : 0f, shock);
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Tree flattening failed: " + e.Message); }
            try
            {
                if (s.falloutFootprint && body.atmosphere && falloutK >= 0.15f)
                    NukeFootprint.Add(body, lat, lon, W, R, falloutK, windHeading, wind, delay);
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Fallout footprint failed: " + e.Message); }
            try
            {
                if (s.firestorm && airBurst && !overOcean && body.atmosphereContainsOxygen && W >= s.firestormMinYield)
                    NukeFirestorm.Spawn(body, lat, lon, groundAlt, W, R, hb, wind, spawnTime, delay);
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Firestorm failed: " + e.Message); }
        }
    }

    /// <summary>
    /// Keeps the flattened forests and the fallout footprints in the save, and runs their per-scene upkeep and the
    /// delayed tree-flattening (timed on the effects clock, so photo mode holds it back too).
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class NukeWorldScenario : ScenarioModule
    {
        static NukeWorldScenario instance;
        static readonly List<KeyValuePair<double, Action>> due = new List<KeyValuePair<double, Action>>();
        float nextTick;

        public override void OnAwake()
        {
            base.OnAwake();
            instance = this;
            due.Clear();
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            NukeFootprint.DropVisuals();
        }

        public override void OnLoad(ConfigNode node)
        {
            NukeScatterFlatten.Load(node);
            NukeFootprint.Load(node);
        }

        public override void OnSave(ConfigNode node)
        {
            NukeScatterFlatten.Save(node);
            NukeFootprint.Save(node);
        }

        /// <summary>Runs `a` after `delay` s of the effects clock (straight away if no scene is running).</summary>
        internal static void Schedule(float delay, Action a)
        {
            if (instance == null || delay <= 0f) { a(); return; }
            due.Add(new KeyValuePair<double, Action>(NukeFXManager.FxTime + delay, a));
        }

        void Update()
        {
            double now = NukeFXManager.FxTime;
            for (int i = due.Count - 1; i >= 0; i--)
            {
                if (now < due[i].Key) continue;
                var a = due[i].Value;
                due.RemoveAt(i);
                try { a(); }
                catch (Exception e) { Debug.LogWarning("[BDNukes]: World effect update failed: " + e.Message); }
            }
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            try { NukeScatterFlatten.Tick(); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Scatter mask upkeep failed: " + e.Message); }
            try { NukeFootprint.Tick(); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Fallout footprint upkeep failed: " + e.Message); }
        }
    }
}
