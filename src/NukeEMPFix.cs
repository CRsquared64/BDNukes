using System;
using System.Collections.Generic;
using BDArmory.Damage;
using BDArmory.FX;
using BDArmory.Settings;
using BDArmory.Utils;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Fixes BDArmory's nuclear EMP, which never affects anything in BDArmory 1.12.x.
    ///
    /// NukeFX.CalculateEMPEvent casts its shielding ray along Vector3.zero with zero length, so it never hits a
    /// part, the shielding term stays at +infinity, and the resulting damage is always negative.
    /// This replacement does what the original intends: for each loaded vessel inside the EMP radius, it casts
    /// a ray from the burst to each command module and sums the armour and hull shielding along that path.
    /// The least-shielded path wins, and the remaining damage goes to the vessel's ModuleDrainEC.
    ///
    /// Vessels inside the thermal radius take a hard EMP (bricked); beyond it the pulse is soft and they
    /// reboot once their electronics recover. The carrier of a megaton bomb is not permanently bricked
    /// 100 km away.
    /// </summary>
    [HarmonyPatch(typeof(NukeFX), "CalculateEMPEvent")]
    internal static class Patch_NukeFX_CalculateEMPEvent
    {
        static readonly AccessTools.FieldRef<NukeFX, float> EMPRadius = AccessTools.FieldRefAccess<NukeFX, float>("EMPRadius");
        static RaycastHit[] hits = new RaycastHit[100];
        static readonly HashSet<Part> counted = new HashSet<Part>();
        const int EMPLayerMask = 67108865; // same mask BDArmory uses for its EMP raycasts (parts + EVA)

        static bool Prefix(NukeFX __instance)
        {
            try
            {
                Apply(__instance);
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: EMP fix failed, falling back to BDArmory: " + e);
                return true;
            }
            return false;
        }

        static void Apply(NukeFX fx)
        {
            float radius = EMPRadius(fx);
            Vector3 origin = fx.Position;
            foreach (Vessel vessel in FlightGlobals.Vessels)
            {
                if (vessel == null || !vessel.loaded || vessel.packed || vessel.HoldPhysics) continue;
                if (VesselModuleRegistry.IgnoredVesselTypes.Contains(vessel.vesselType)) continue;

                float dist = Vector3.Distance(origin, vessel.CoM);
                if (dist > radius) continue;

                float damage = radius / Mathf.Max(dist, 1f) * 100f * BDArmorySettings.DMG_MULTIPLIER;

                // least-shielded path from the burst to any command module
                float shielding = float.PositiveInfinity;
                var commands = VesselModuleRegistry.GetModuleCommands(vessel);
                if (commands == null || commands.Count == 0) shielding = 0f;
                else
                {
                    foreach (var cmd in commands)
                    {
                        if (cmd == null || cmd.part == null) continue;
                        shielding = Mathf.Min(shielding, PathShielding(origin, cmd.part));
                    }
                    if (float.IsInfinity(shielding)) shielding = 0f;
                }

                damage -= shielding;
                if (damage <= 0f) continue;

                var drain = vessel.rootPart.FindModuleImplementing<ModuleDrainEC>();
                if (drain == null) drain = (ModuleDrainEC)vessel.rootPart.AddModule("ModuleDrainEC");
                drain.softEMP = dist > fx.thermalRadius;
                drain.incomingDamage = damage;
                if (BDArmorySettings.DEBUG_DAMAGE)
                    Debug.Log($"[BDNukes]: EMP from {fx.ReportingName} hits {vessel.vesselName} at {dist:0} m: {damage:0.0} ({(drain.softEMP ? "soft" : "hard")})");
            }
        }

        static float PathShielding(Vector3 origin, Part target)
        {
            Vector3 dir = target.transform.position - origin;
            float length = dir.magnitude;
            if (length < 0.01f) return 0f;
            var ray = new Ray(origin, dir / length);
            int n = Physics.RaycastNonAlloc(ray, hits, length, EMPLayerMask);
            if (n == hits.Length)
            {
                hits = Physics.RaycastAll(ray, length, EMPLayerMask);
                n = hits.Length;
            }

            // raycast hits come back unsorted, so a part with several colliders must be counted only once
            float total = 0f;
            counted.Clear();
            for (int i = 0; i < n; i++)
            {
                Part p = hits[i].collider != null ? hits[i].collider.GetComponentInParent<Part>() : null;
                if (p == null || p.vessel != target.vessel || !counted.Add(p)) continue;
                var hp = p.FindModuleImplementing<HitpointTracker>();
                if (hp == null) continue;
                if (hp.Diffusivity > 15f) total += hp.Armour;      // conductive armour acts as a Faraday shield
                if (hp.HullMassAdjust > 0f) total += p.mass * 4f;  // reinforced hull
            }
            counted.Clear();
            return total;
        }
    }
}
