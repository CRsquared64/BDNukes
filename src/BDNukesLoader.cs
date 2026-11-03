using System;
using BDArmory.Extensions;
using BDArmory.FX;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>Applies the Harmony patches once at game start.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class BDNukesLoader : MonoBehaviour
    {
        void Awake()
        {
            try
            {
                new Harmony("BDNukes").PatchAll(typeof(BDNukesLoader).Assembly);
                Debug.Log("[BDNukes]: Harmony patches applied.");
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Failed to apply Harmony patches: " + e);
            }
        }
    }

    /// <summary>
    /// Every BDArmory nuclear detonation (missiles, warheads, nuclear shells, reactor cores) goes through
    /// NukeFX.CreateExplosion. BDArmory's damage model is kept as-is; we only add the cinematic effect
    /// and blank the stock visual/sound paths so the old emitters don't draw on top of it.
    /// A ModuleNukeCloudProfile on the detonating part shapes the cloud; a ModuleMIRVBus adds more warheads.
    /// </summary>
    [HarmonyPatch(typeof(NukeFX), nameof(NukeFX.CreateExplosion))]
    internal static class Patch_NukeFX_CreateExplosion
    {
        // set while a MIRV bus is adding its extra explosions, so those don't release buses of their own
        static bool inMirv;

        static void Prefix(Vector3 position, ExplosionSourceType explosionSourceType, string sourceVesselName,
            string sourceWeaponName, float delay, float blastRadius, float Yield, float thermalShock, bool emp,
            ref string blastSound, ref string flashModel, ref string shockModel,
            ref string blastModel, ref string plumeModel, ref string debrisModel,
            string ModelPath, string soundPath, Part nukePart, Vector3 sourceVelocity, bool bulletHitRegistered)
        {
            try
            {
                if (!HighLogic.LoadedSceneIsFlight) return;
                var settings = NukeSettings.Current;

                // MIRV first, while the model and sound paths are still the originals.
                if (!inMirv && settings.mirvBus && nukePart != null)
                {
                    var bus = nukePart.FindModuleImplementing<ModuleMIRVBus>();
                    if (bus != null && bus.warheads > 1)
                    {
                        inMirv = true;
                        try
                        {
                            SpawnMirv(bus, position, explosionSourceType, sourceVesselName, sourceWeaponName, delay, blastRadius, Yield,
                                thermalShock, emp, blastSound, flashModel, shockModel, blastModel, plumeModel, debrisModel,
                                ModelPath, soundPath, nukePart, sourceVelocity, bulletHitRegistered);
                        }
                        finally { inMirv = false; }
                    }
                }

                if (!settings.enabled) return;

                NukeCinematic.Spawn(position, Yield, delay, ModuleNukeCloudProfile.FromPart(nukePart));

                if (settings.replaceStockVisuals)
                {
                    blastSound = flashModel = shockModel = blastModel = plumeModel = debrisModel = "";
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Error spawning nuke cinematic: " + e);
            }
        }

        /// <summary>
        /// Adds (warheads - 1) BDArmory explosions across the footprint, each at the original height above the
        /// terrain (or sea), with delays staggered by the bus interval. Every other argument is passed through.
        /// </summary>
        static void SpawnMirv(ModuleMIRVBus bus, Vector3 position, ExplosionSourceType type, string vesselName, string weaponName,
            float delay, float blastRadius, float yield, float thermalShock, bool emp, string blastSound, string flashModel,
            string shockModel, string blastModel, string plumeModel, string debrisModel, string modelPath, string soundPath,
            Part nukePart, Vector3 sourceVelocity, bool bulletHitRegistered)
        {
            var body = FlightGlobals.currentMainBody;
            if (body == null) return;
            double lat = body.GetLatitude(position), lon = body.GetLongitude(position), alt = body.GetAltitude(position);
            double hAbove = alt - GroundAlt(body, lat, lon);
            double radius = body.Radius + Math.Max(0d, alt);
            double cosLat = Math.Max(0.01d, Math.Cos(lat * Math.PI / 180d));

            var offsets = bus.FootprintOffsets(bus.warheads - 1);
            float d = delay;
            for (int i = 0; i < offsets.Length; i++)
            {
                d += Mathf.Max(0f, bus.interval) * UnityEngine.Random.Range(0.5f, 1.5f);
                double lat2 = Math.Max(-89.9d, Math.Min(89.9d, lat + offsets[i].y / radius * 180d / Math.PI));
                double lon2 = lon + offsets[i].x / (radius * cosLat) * 180d / Math.PI;
                double alt2 = GroundAlt(body, lat2, lon2) + hAbove;
                Vector3 pos2 = body.GetWorldSurfacePosition(lat2, lon2, alt2);
                // the hit part belongs to the first warhead only
                NukeFX.CreateExplosion(pos2, type, vesselName, weaponName, d, blastRadius, yield, thermalShock, emp,
                    blastSound, flashModel, shockModel, blastModel, plumeModel, debrisModel, modelPath, soundPath,
                    nukePart, null, sourceVelocity, bulletHitRegistered);
            }
            Debug.Log($"[BDNukes]: MIRV bus released {offsets.Length + 1} warheads over a {bus.spread:0} m footprint.");
        }

        static double GroundAlt(CelestialBody body, double lat, double lon)
        {
            double g = body.pqsController != null ? body.TerrainAltitude(lat, lon) : 0d;
            if (body.ocean && g < 0d) g = 0d;
            return g;
        }
    }
}
