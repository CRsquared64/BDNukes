using System;
using System.Collections.Generic;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Round-planet guidance for ballistic missiles sent by the strike planner.
    ///
    /// BDArmory's agmballistic guidance aims as if the ground were flat: fine for the 200 km it was made for, but at
    /// a thousand km the line to the target runs through the planet, and its miss check (target behind the nose)
    /// gives up on the shot. For planned shots this replaces the aim during the burn with a pitch program on the
    /// sphere: straight up off the pad, then over onto the minimum-energy flight path angle (45° less a quarter of the
    /// angle round the planet to the target), heading for where the target will be when the warhead arrives, the
    /// thrust nudging the real (inertial) velocity onto that line. The motor runs flat out until the Kepler
    /// prediction says the warhead comes down on the target (NukeBallisticCutoff; missiles without a BDNukes profile
    /// get the same cut-off here). On the long coast the nose stays on the airflow; BDArmory's guidance and fuzes take
    /// over for the last few tens of km.
    /// </summary>
    internal static class NukeStrikeGuidance
    {
        /// <summary>Planned missiles without a BDNukes profile, whose motor this has cut.</summary>
        static readonly HashSet<MissileBase> cut = new HashSet<MissileBase>();
        internal static void Clear() { cut.Clear(); }
        const double HandBackRange = 80000d;   // m from the target: BDArmory's own terminal guidance from here

        internal static bool Planned(MissileBase m, out NukeStrike.Plan plan)
        {
            plan = null;
            return m is MissileLauncher && NukeStrike.TryGetPlan(m, out plan) && plan.target.Valid
                && m.vessel != null && m.vessel.mainBody == plan.target.body;
        }

        static bool CutoffDone(MissileLauncher ml)
        {
            var prof = ml.part != null ? ml.part.FindModuleImplementing<ModuleNukeCloudProfile>() : null;
            if (prof != null && prof.ballisticCutoff) return prof.ballisticCutoffDone;
            return cut.Contains(ml);
        }

        /// <summary>Runs before BDNukes' own cut-off: full throttle on a planned shot until the cut.</summary>
        internal static void BeforeCutoff(MissileLauncher ml)
        {
            if (!Planned(ml, out var plan)) return;
            // the cut-off and BDArmory's terminal guidance both read TargetPosition: keep it on the planned point
            ml.TargetAcquired = true;
            ml.TargetPosition = plan.target.World;
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return;
            if (CutoffDone(ml)) { ml.Throttle = 0f; return; }
            ml.Throttle = 1f;
            var prof = ml.part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof != null && prof.ballisticCutoff) return;   // NukeBallisticCutoff does it next

            // no BDNukes profile: cut here the same way
            Vessel v = ml.vessel;
            CelestialBody body = v.mainBody;
            Vector3d r = v.CoMD - body.position, rt = plan.target.Rel;
            var o = NukeBallisticCutoff.Predict(r, v.obt_velocity, body.gravParameter, rt.magnitude, out double sweep, out double tof);
            if (o == NukeBallisticCutoff.Outcome.Short) return;
            if (o == NukeBallisticCutoff.Outcome.Impact)
            {
                Vector3d rtF = NukeStrikeMath.RotateWithBody(body, rt, tof);
                double ang = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(r.normalized, rtF.normalized))));
                if (sweep < ang) return;
            }
            cut.Add(ml);
            ml.Throttle = 0f;
            Debug.Log($"[BDNukes]: {ml.shortName}: motor cut for {plan.target.name}.");
        }

        /// <summary>The aim point for a planned shot (world), or null to leave BDArmory's.</summary>
        internal static Vector3? Aim(MissileLauncher ml)
        {
            if (!Planned(ml, out var plan)) return null;
            Vessel v = ml.vessel;
            if (v.packed) return null;
            CelestialBody body = v.mainBody;
            Vector3d pos = v.CoMD;
            Vector3d up = (pos - body.position).normalized;
            Vector3d rt = plan.target.Rel;
            double groundLeft = NukeStrikeMath.CentralAngle(v.latitude, v.longitude, plan.target.lat, plan.target.lon) * body.Radius;
            var state = ml.MissileState;
            bool burning = (state == MissileBase.MissileStates.Boost || state == MissileBase.MissileStates.Cruise) && !CutoffDone(ml);

            if (!burning)
            {
                if (groundLeft < HandBackRange || state == MissileBase.MissileStates.Idle || state == MissileBase.MissileStates.Drop) return null;
                Vector3d sv = v.srf_velocity;
                if (sv.sqrMagnitude < 100) return null;
                return (Vector3)(pos + sv.normalized * 50000d);   // nose into the airflow on the coast
            }

            Vector3d vel = v.obt_velocity;
            double speed = vel.magnitude;
            double tof;
            var o = NukeBallisticCutoff.Predict(pos - body.position, vel, body.gravParameter, rt.magnitude, out _, out double tofP);
            tof = o == NukeBallisticCutoff.Outcome.Impact ? tofP : groundLeft / 2500d + 200d;
            Vector3d rtF = NukeStrikeMath.RotateWithBody(body, rt, tof);
            double theta = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(up, rtF.normalized))));
            Vector3d h = rtF - up * Vector3d.Dot(rtF, up);
            if (h.sqrMagnitude < 1) return null;
            h = h.normalized;

            double gammaOpt = Math.Max(8d, Math.Min(45d, 45d - theta * 180d / Math.PI / 4d)) * Math.PI / 180d;
            double agl = v.altitude - Math.Max(0d, v.terrainAltitude);
            double climb = body.atmosphere ? Math.Max(15000d, 0.35d * body.atmosphereDepth) : 15000d;
            double k = ml.TimeIndex < 4f ? 0d : Math.Max(0d, Math.Min(1d, (agl - 1200d) / climb));
            k = k * k * (3d - 2d * k);
            double gamma = 88d * Math.PI / 180d * (1d - k) + gammaOpt * k;
            Vector3d d = h * Math.Cos(gamma) + up * Math.Sin(gamma);
            if (speed > 300d && k > 0.3d)
            {
                // steer the velocity, not just the nose, onto the line (also takes out the planet's spin)
                Vector3d err = d - vel / speed;
                d = (d + err * Math.Min(1.5d, 0.5d + k)).normalized;
            }
            return (Vector3)(pos + d * 50000d);
        }

        /// <summary>BDArmory's miss check stays off on a planned shot until it's close (the long arc looks like a miss).</summary>
        internal static bool SkipMissCheck(MissileLauncher ml)
        {
            if (!Planned(ml, out var plan)) return false;
            Vessel v = ml.vessel;
            double groundLeft = NukeStrikeMath.CentralAngle(v.latitude, v.longitude, plan.target.lat, plan.target.lon) * v.mainBody.Radius;
            return groundLeft > 30000d;
        }
    }

    [HarmonyPatch(typeof(MissileBase), "CalculateAGMBallisticGuidance")]
    internal static class Patch_MissileBase_BallisticAim
    {
        static bool loggedError;
        static void Postfix(MissileBase __instance, object[] __args, ref Vector3 __result)
        {
            try
            {
                var m = (__args != null && __args.Length > 0 ? __args[0] as MissileBase : null) ?? __instance;
                var aim = NukeStrikeGuidance.Aim(m as MissileLauncher);
                if (aim.HasValue) __result = aim.Value;
            }
            catch (Exception e)
            {
                if (loggedError) return;
                loggedError = true;
                Debug.LogError("[BDNukes]: Strike guidance failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), "AGMBallisticGuidance")]
    [HarmonyPriority(Priority.High)]
    internal static class Patch_MissileLauncher_StrikeThrottle
    {
        static bool loggedError;
        static void Postfix(MissileLauncher __instance)
        {
            try { NukeStrikeGuidance.BeforeCutoff(__instance); }
            catch (Exception e)
            {
                if (loggedError) return;
                loggedError = true;
                Debug.LogError("[BDNukes]: Strike throttle failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), "CheckMiss")]
    internal static class Patch_MissileLauncher_CheckMiss
    {
        static bool Prefix(MissileLauncher __instance)
        {
            try { return !NukeStrikeGuidance.SkipMissCheck(__instance); }
            catch { return true; }
        }
    }
}
