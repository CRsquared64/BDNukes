using System;
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
    /// </summary>
    [HarmonyPatch(typeof(NukeFX), nameof(NukeFX.CreateExplosion))]
    internal static class Patch_NukeFX_CreateExplosion
    {
        static void Prefix(Vector3 position, float delay, float Yield,
            ref string blastSound, ref string flashModel, ref string shockModel,
            ref string blastModel, ref string plumeModel, ref string debrisModel)
        {
            try
            {
                if (!HighLogic.LoadedSceneIsFlight) return;
                var settings = NukeSettings.Current;
                if (!settings.enabled) return;

                NukeCinematic.Spawn(position, Yield, delay);

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
    }
}
