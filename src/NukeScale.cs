using System;
using System.Collections.Generic;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Compatibility with bigger planet systems (Real Solar System, Sol, rescales). BDNukes is tuned to Kerbin; on a
    /// home world at least planetScaleThreshold times Kerbin's radius (real scale is ~10.6x, quarter ~2.7x) a few
    /// things are scaled to match:
    ///   - the radiation front, and the light of distant bursts that travels with it, by the size of the system;
    ///   - weather heights that were fractions of Kerbin's 70 km atmosphere (the tropopause the cap pancakes against,
    ///     the humid low air, the haze scale height) by the air's real scale height instead;
    ///   - BDArmory's launch range for BDNukes ballistic missiles, by the planet's size.
    /// Physical sizes (fireballs, blast rings, craters) are in metres and need nothing. On Kerbin, or any system near
    /// its size, none of this applies and everything is exactly as before.
    /// </summary>
    internal static class NukeScale
    {
        const double KerbinRadius = 600000d;
        const double KerbinOrbit = 13599840256d;
        const double KerbinAirDepth = 70000d;
        const double KerbinScaleHeight = 5600d;   // m: Kerbin's air thins by e over about this much near the ground

        static CelestialBody home;
        static double planet = 1d, system = 1d;
        static readonly Dictionary<CelestialBody, double> airDepth = new Dictionary<CelestialBody, double>();

        static bool Ready()
        {
            CelestialBody h = FlightGlobals.fetch != null ? FlightGlobals.GetHomeBody() : null;
            if (h == null) return false;
            if (h != home)
            {
                home = h;
                airDepth.Clear();
                planet = h.Radius / KerbinRadius;
                system = h.orbit != null && h.referenceBody == Planetarium.fetch?.Sun ? h.orbit.semiMajorAxis / KerbinOrbit : planet;
                if (double.IsNaN(system) || system <= 0d) system = 1d;
                Debug.Log(string.Format("[BDNukes]: home world {0}: {1:0.00}x Kerbin's radius, orbit {2:0.00}x Kerbin's; bigger-planet scaling {3}.",
                    h.bodyName, planet, system, ActiveFor(planet) ? "on" : "off"));
            }
            return true;
        }

        static bool ActiveFor(double p)
        {
            var s = NukeSettings.Current;
            return s.planetScaleCompat && p >= Math.Max(1.01f, s.planetScaleThreshold);
        }

        /// <summary>The home world is big enough for the scaling to apply.</summary>
        public static bool Active => Ready() && ActiveFor(planet);

        /// <summary>Home radius over Kerbin's, or 1 when the scaling is off.</summary>
        public static double Planet => Active ? planet : 1d;

        /// <summary>Home orbit over Kerbin's (the size of the system), or 1 when the scaling is off.</summary>
        public static double SystemSize => Active ? Math.Max(1d, system) : 1d;

        const double KerbinOrbitSpeed = 2425.6d;   // m/s: sqrt(mu / R), the speed for a low circular orbit

        /// <summary>
        /// Home world's low-orbit speed over Kerbin's, or 1 when the scaling is off: what a missile's speed has to grow
        /// by to throw its warhead the same fraction of the way round the planet (~3.3 on real-scale Earth).
        /// </summary>
        public static double SpeedScale
        {
            get
            {
                if (!Active || home == null) return 1d;
                double v = Math.Sqrt(home.gravParameter / home.Radius) / KerbinOrbitSpeed;
                return double.IsNaN(v) || v < 1d ? 1d : v;
            }
        }

        /// <summary>The radiation front's (and BDNukes' light's) speed, m/s.</summary>
        public static double WaveSpeed => Math.Max(1e3d, NukeSettings.Current.waveSpeed * 1000d) * SystemSize;

        /// <summary>
        /// The depth of air the weather heights are fractions of. With the scaling off it is the body's own
        /// atmosphereDepth, as always; on a big world it is Kerbin's 70 km stretched by how slowly this air thins, so
        /// a tropopause comes out near 15 km on Earth, not at a sixth of RSS's 140 km atmosphere.
        /// </summary>
        public static double AirDepth(CelestialBody body)
        {
            if (body == null || !body.atmosphere) return KerbinAirDepth;
            if (!Active) return body.atmosphereDepth;
            if (airDepth.TryGetValue(body, out double d)) return d;
            d = body.atmosphereDepth;
            try
            {
                double p0 = body.GetPressure(0d), p1 = body.GetPressure(5000d);
                if (p0 > 0d && p1 > 0d && p1 < p0)
                {
                    double h = 5000d / Math.Log(p0 / p1);
                    d = Math.Min(body.atmosphereDepth, KerbinAirDepth * h / KerbinScaleHeight);
                }
            }
            catch { }
            airDepth[body] = d;
            return d;
        }

        /// <summary>
        /// A planet name in a BDNukes setting written for the stock system, matched against a body: Kerbin also
        /// stands for the home world and Jool for Jupiter, so stock lists keep working in a real solar system.
        /// </summary>
        public static bool NameMatches(string name, CelestialBody body)
        {
            if (body == null || string.IsNullOrEmpty(name)) return false;
            name = name.Trim();
            if (string.Equals(name, body.bodyName, StringComparison.OrdinalIgnoreCase)) return true;
            if (FlightGlobals.GetBodyByName(name) != null) return false;   // the named body exists: no stand-in
            if (string.Equals(name, "Kerbin", StringComparison.OrdinalIgnoreCase)) return body == FlightGlobals.GetHomeBody();
            if (string.Equals(name, "Jool", StringComparison.OrdinalIgnoreCase)) return string.Equals(body.bodyName, "Jupiter", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    /// <summary>
    /// On a big world, BDNukes ballistic missiles are scaled to it: their motors give SpeedScale times the speed (thrust
    /// and burn time each up by its square root, the fuel unchanged), so a missile reaches as far round the planet as
    /// it does round Kerbin, and BDArmory's launch range grows with the planet. On Kerbin nothing changes.
    /// </summary>
    [HarmonyPatch(typeof(MissileLauncher), "OnStart")]
    internal static class Patch_MissileLauncher_ScaleRange
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MissileLauncher, object> done =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MissileLauncher, object>();

        static void Postfix(MissileLauncher __instance)
        {
            try
            {
                if (!HighLogic.LoadedSceneIsFlight || __instance == null || __instance.part == null) return;
                if (__instance.GuidanceMode != MissileBase.GuidanceModes.AGMBallistic) return;
                if (__instance.thrust <= 0f && __instance.cruiseThrust <= 0f) return;
                if (__instance.part.FindModuleImplementing<ModuleNukeCloudProfile>() == null) return;
                if (done.TryGetValue(__instance, out _)) return;
                done.Add(__instance, null);
                double k = NukeScale.Planet;
                if (k <= 1d) return;

                // the launch range is saved with the craft: scale the part's own value, so a reload doesn't compound it
                var prefab = __instance.part.partInfo != null && __instance.part.partInfo.partPrefab != null
                    ? __instance.part.partInfo.partPrefab.FindModuleImplementing<MissileLauncher>() : null;
                float baseRange = prefab != null ? prefab.maxStaticLaunchRange : __instance.maxStaticLaunchRange;
                __instance.maxStaticLaunchRange = Mathf.Max(__instance.maxStaticLaunchRange, (float)(baseRange * k));

                float s = (float)Math.Sqrt(NukeScale.SpeedScale);
                if (s > 1.001f)
                {
                    __instance.thrust *= s;
                    __instance.cruiseThrust *= s;
                    __instance.boostTime *= s;
                    __instance.cruiseTime *= s;
                    Debug.Log($"[BDNukes]: {__instance.shortName}: motor scaled for {FlightGlobals.GetHomeBody()?.bodyName}: thrust and burn time x{s:0.00} ({s * s:0.00}x the speed).");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Couldn't scale a missile for this planet: " + e.Message);
            }
        }
    }

    /// <summary>
    /// BDArmory's nuclear explosion slows its centre by the air density relative to Kerbin's at sea level, reading
    /// Kerbin by name. In a system without a Kerbin (Real Solar System, Sol) older BDArmory builds throw on every
    /// physics frame of every nuclear explosion. Newer builds fall back to Kerbin's figure; do the same here.
    /// </summary>
    [HarmonyPatch(typeof(BDArmory.FX.NukeFX), "KerbinSeaLevelAtmDensity", MethodType.Getter)]
    internal static class Patch_NukeFX_KerbinDensity
    {
        const float KerbinSeaLevelDensity = 1.224977f;   // kg/m^3
        static float cached = -1f;

        static bool Prefix(ref float __result)
        {
            if (cached < 0f)
            {
                CelestialBody kerbin = FlightGlobals.fetch != null ? FlightGlobals.GetBodyByName("Kerbin") : null;
                if (FlightGlobals.fetch == null) { __result = KerbinSeaLevelDensity; return false; }
                cached = kerbin != null && kerbin.atmDensityASL > 0d ? (float)kerbin.atmDensityASL : KerbinSeaLevelDensity;
            }
            __result = cached;
            return false;
        }
    }
}
