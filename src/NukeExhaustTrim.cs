using System;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Thins the smoke of BDArmory's exhaust prefabs on BDNukes missiles (ModuleNukeCloudProfile.exhaustSmoke).
    /// MissileLauncher.OnStart attaches the prefabs under the exhaust transforms; afterwards this scales their
    /// world-space (smoke trail) emitters: fewer, smaller, shorter-lived particles. The flame emitters, which move
    /// with the missile, are left alone.
    /// </summary>
    [HarmonyPatch(typeof(MissileLauncher), "OnStart")]
    internal static class Patch_MissileLauncher_ExhaustSmoke
    {
        static void Postfix(MissileLauncher __instance)
        {
            try
            {
                if (!HighLogic.LoadedSceneIsFlight || __instance == null || __instance.part == null) return;
                var prof = __instance.part.FindModuleImplementing<ModuleNukeCloudProfile>();
                if (prof == null) return;
                float k = Mathf.Clamp(prof.exhaustSmoke, 0f, 4f);
                if (Mathf.Abs(k - 1f) < 1e-3f) return;
                int n = 0;
                foreach (var e in __instance.part.GetComponentsInChildren<KSPParticleEmitter>(true))
                {
                    if (e == null || !e.useWorldSpace) continue;
                    e.minEmission = Mathf.RoundToInt(e.minEmission * k);
                    e.maxEmission = Mathf.Max(1, Mathf.RoundToInt(e.maxEmission * k));
                    float s = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(k));   // sizes shrink less than the count
                    e.minSize *= s;
                    e.maxSize *= s;
                    e.sizeGrow *= s;
                    e.minEnergy *= s;
                    e.maxEnergy *= s;
                    n++;
                }
                if (n > 0) Debug.Log($"[BDNukes]: {__instance.shortName}: exhaust smoke scaled by {k:0.##} on {n} emitters.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Couldn't thin the exhaust smoke: " + e.Message);
            }
        }
    }
}
