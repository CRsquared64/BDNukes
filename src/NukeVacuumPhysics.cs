using System;
using System.Collections.Generic;
using BDArmory.FX;
using HarmonyLib;

namespace BDNukes
{
    /// <summary>
    /// Vacuum-burst physics fixes for BDArmory's NukeFX.
    ///
    /// In vacuum there is no air to carry a blast wave. Energy leaves as an instantaneous X-ray pulse
    /// (BDArmory's skin-heating term) plus an expanding shell of bomb-debris plasma that delivers the impulse.
    /// BDArmory still times every hit at its sea-level air-blast speed (422.75 m/s) and applies a negative-pressure
    /// "suck-back" pull, which can only happen when air rushes back in. These patches:
    ///   - reset the density BDArmory caches on pooled explosions (otherwise a space burst can inherit air from the last one),
    ///   - time vacuum hits by the debris shell speed (vacuumDebrisSpeed), in sync with the visual shell,
    ///   - drop the negative-pressure pull in vacuum.
    /// </summary>
    internal static class NukeVacuumPhysics
    {
        internal const float VacuumDensity = 0.05f; // same threshold BDArmory uses for its space visuals

        internal static readonly AccessTools.FieldRef<NukeFX, float> LastValidAtmDensity =
            AccessTools.FieldRefAccess<NukeFX, float>("lastValidAtmDensity");

        internal static bool IsVacuum(NukeFX fx) => LastValidAtmDensity(fx) < VacuumDensity;
    }

    [HarmonyPatch(typeof(NukeFX), "OnEnable")]
    internal static class Patch_NukeFX_OnEnable
    {
        static void Prefix(NukeFX __instance)
        {
            if (!NukeSettings.Current.vacuumPhysics) return;
            NukeVacuumPhysics.LastValidAtmDensity(__instance) = 0f;
        }
    }

    [HarmonyPatch(typeof(NukeFX), "ProcessingBlastSphere")]
    internal static class Patch_NukeFX_ProcessingBlastSphere
    {
        static void Postfix(NukeFX __instance, List<NukeHitEvent> __result)
        {
            if (__result == null || !NukeSettings.Current.vacuumPhysics || !NukeVacuumPhysics.IsVacuum(__instance)) return;
            float v = NukeSettings.Current.vacuumDebrisSpeed;
            for (int i = 0; i < __result.Count; i++)
            {
                if (__result[i] != null) __result[i].TimeToImpact = __result[i].Distance / v;
            }
        }
    }

    [HarmonyPatch(typeof(NukeFX), "ExecutePartBlastEvent")]
    internal static class Patch_NukeFX_ExecutePartBlastEvent
    {
        /// <summary>
        /// Drops the vacuum suck-back, and scales the impulse of nuclear hits only (nukeImpulseScale): BDArmory's
        /// global explosion-impulse modifier is lowered just for this hit and put back afterwards. Unscaled, a nuke
        /// can throw a light craft several km away at near-orbital speed (the camera following it off-screen); a real
        /// blast wave tumbles vehicles at tens of m/s.
        /// </summary>
        static bool Prefix(NukeFX __instance, NukeHitEvent eventToExecute, out float __state)
        {
            __state = float.NaN;
            if (eventToExecute != null && eventToExecute.IsNegativePressure && NukeSettings.Current.vacuumPhysics
                && NukeVacuumPhysics.IsVacuum(__instance))
                return false;
            float k = NukeSettings.Current.nukeImpulseScale;
            if (k < 0.999f)
            {
                __state = BDArmory.Settings.BDArmorySettings.EXP_IMP_MOD;
                BDArmory.Settings.BDArmorySettings.EXP_IMP_MOD = __state * k;
            }
            return true;
        }

        // A finalizer, not a postfix: it also runs when the original throws (a part destroyed mid-blast), so the
        // global modifier can never be left scaled down.
        static Exception Finalizer(Exception __exception, float __state)
        {
            if (!float.IsNaN(__state)) BDArmory.Settings.BDArmorySettings.EXP_IMP_MOD = __state;
            return __exception;
        }
    }
}
