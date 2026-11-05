using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Keeps other explosion mods (Volumetric Explosions in particular, which swaps the game's FXMonger explosion
    /// prefabs for its own) away from nuclear detonations. BDNukes draws its own fireball, so when a warhead part dies
    /// the stock/replacement part explosion is skipped, and so is any part explosion inside a fresh nuclear fireball,
    /// where a small fuel-tank blast would only show up as a puff drawn over the nuke's own effect.
    /// Only active when such a mod is installed; without one the game's own behaviour is left exactly as it was.
    /// </summary>
    [HarmonyPatch]
    internal static class NukeVEGuard
    {
        static bool? present;

        static bool OtherFXPresent
        {
            get
            {
                if (present == null)
                    present = AssemblyLoader.loadedAssemblies.Any(a => a.name == "VolumetricExplosions");
                return present.Value;
            }
        }

        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(FXMonger).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "Explode");

        static bool Prefix(object[] __args)
        {
            try
            {
                if (!OtherFXPresent || __args == null) return true;
                Part part = null;
                Vector3d? pos = null;
                foreach (object a in __args)
                {
                    if (a is Part p) part = p;
                    else if (a is Vector3d v) pos = v;
                    else if (a is Vector3 v3) pos = v3;
                }
                if (part != null && IsNukePart(part)) return false;
                if (pos == null && part != null && part.transform != null) pos = part.transform.position;
                if (pos != null && InsideFreshFireball(pos.Value)) return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: explosion guard: " + e.Message);
            }
            return true;
        }

        internal static bool IsNukePart(Part p)
        {
            if (p.partInfo != null && p.partInfo.name.StartsWith("bdnukes", StringComparison.OrdinalIgnoreCase)) return true;
            for (int m = 0; m < p.Modules.Count; m++)
                if (p.Modules[m] != null && p.Modules[m].moduleName == "BDModuleNuke") return true;
            return false;
        }

        static bool InsideFreshFireball(Vector3d world)
        {
            if (Planetarium.fetch == null || FlightGlobals.Bodies == null) return false;
            double now = Planetarium.GetUniversalTime();
            var recs = NukeAlert.Records;
            for (int i = recs.Count - 1; i >= 0; i--)
            {
                var r = recs[i];
                double age = now - r.ut;
                if (age < -1d || age > 15d) continue;
                CelestialBody b = FlightGlobals.GetBodyByName(r.body);
                if (b == null) continue;
                Vector3d at = r.space && !r.NearGround ? r.WorldPos(b) : b.GetWorldSurfacePosition(r.lat, r.lon, r.alt);
                // a little beyond the fireball's full size (about 70 m * kt^0.4)
                double reach = 1.5d * 70d * Math.Pow(Math.Max(r.W, 0.001f), 0.4d) + 20d;
                if ((at - world).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }
    }
}
