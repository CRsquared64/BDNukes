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
    ///
    /// BDNukes ballistic missiles that BDArmory fires itself (the AI, or a GPS or locked target) at more than
    /// AutoMinRange get the same guidance, aimed at BDArmory's target and following it if it moves.
    /// </summary>
    internal static class NukeStrikeGuidance
    {
        /// <summary>Planned missiles without a BDNukes profile, whose motor this has cut.</summary>
        static readonly HashSet<MissileBase> cut = new HashSet<MissileBase>();
        internal static void Clear() { cut.Clear(); lastAim.Clear(); nextLog.Clear(); autoPlans.Clear(); }
        // BDArmory-fired shots this guidance flies (null: looked at, and left to BDArmory)
        static readonly Dictionary<MissileBase, NukeStrike.Plan> autoPlans = new Dictionary<MissileBase, NukeStrike.Plan>();
        const double AutoMinRange = 30000d;    // m: closer than this, BDArmory's flat-ground guidance is fine
        // the aim Aim() last gave each planned missile (null: left to BDArmory), and when to log its flight next
        static readonly Dictionary<MissileBase, Vector3?> lastAim = new Dictionary<MissileBase, Vector3?>();
        static readonly Dictionary<MissileBase, float> nextLog = new Dictionary<MissileBase, float>();
        const float LogInterval = 4f;
        const float SteerRateDPS = 6f;         // how fast the steering assist turns the nose
        const float SteerAoADense = 15f;       // how far off the airflow the nose may be held in dense air (deg)
        const double SteerThinQ = 20000d;      // Pa: below this, the allowed angle opens out towards 90 deg
        const double HandBackRange = 80000d;   // m from the target: BDArmory's own terminal guidance from here

        internal static bool Planned(MissileBase m, out NukeStrike.Plan plan)
        {
            plan = null;
            if (!(m is MissileLauncher ml) || m.vessel == null) return false;
            if (NukeStrike.TryGetPlan(m, out plan))
                return plan.target.Valid && m.vessel.mainBody == plan.target.body;
            return AutoPlan(ml, out plan);
        }

        /// <summary>
        /// A plan for a BDNukes ballistic missile BDArmory fired itself, decided once when it is first seen under power
        /// with a target: far enough off for the round planet to matter. The target follows BDArmory's each call.
        /// </summary>
        static bool AutoPlan(MissileLauncher ml, out NukeStrike.Plan plan)
        {
            if (autoPlans.TryGetValue(ml, out plan) && plan == null) return false;
            Vessel v = ml.vessel;
            CelestialBody body = v.mainBody;
            if (body == null) return false;
            Vector3d tp = ml.TargetPosition;
            bool haveTarget = ml.TargetAcquired && ml.TargetPosition != Vector3.zero;
            if (plan == null)
            {
                var st = ml.MissileState;
                if ((st != MissileBase.MissileStates.Boost && st != MissileBase.MissileStates.Cruise) || !haveTarget) return false;
                var prof = ml.part != null ? ml.part.FindModuleImplementing<ModuleNukeCloudProfile>() : null;
                double range = NukeStrikeMath.CentralAngle(v.latitude, v.longitude, body.GetLatitude(tp), body.GetLongitude(tp)) * body.Radius;
                bool ok = ml.GuidanceMode == MissileBase.GuidanceModes.AGMBallistic && (ml.thrust > 0f || ml.cruiseThrust > 0f)
                          && prof != null && prof.ballisticCutoff && range > AutoMinRange;
                if (!ok) { autoPlans[ml] = null; return false; }
                plan = new NukeStrike.Plan { weapon = ml.shortName, launchUT = Planetarium.GetUniversalTime() };
                autoPlans[ml] = plan;
                Debug.Log($"[BDNukes]: {ml.shortName}: round-planet guidance for a BDArmory shot {range / 1000d:0} km away.");
            }
            if (haveTarget)
            {
                var t = new StrikeTarget { body = body, lat = body.GetLatitude(tp), lon = body.GetLongitude(tp), name = "BDArmory target" };
                t.height = body.GetAltitude(tp) - t.GroundAltitude;
                plan.target = t;
            }
            return plan.target.Valid && plan.target.body == body;
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
            try { Telemetry(ml, plan); } catch (Exception e) { Debug.LogWarning("[BDNukes]: strike telemetry failed: " + e.Message); }
            // the cut-off and BDArmory's terminal guidance both read TargetPosition: keep it on the planned point
            ml.TargetAcquired = true;
            ml.TargetPosition = plan.target.World;
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return;
            if (CutoffDone(ml)) { ml.Throttle = 0f; return; }
            ml.Throttle = 1f;
            try { Steer(ml); } catch (Exception e) { Debug.LogWarning("[BDNukes]: strike steering failed: " + e.Message); }
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

        /// <summary>
        /// A line in the log every couple of seconds while a planned missile is burning: what the guidance asked for
        /// and what the missile is doing, so a shot that goes wrong in game can be read back afterwards.
        /// </summary>
        static void Telemetry(MissileLauncher ml, NukeStrike.Plan plan)
        {
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return;
            if (CutoffDone(ml)) return;   // the cut itself is logged; the coast isn't
            if (nextLog.TryGetValue(ml, out float at) && Time.time < at) return;
            nextLog[ml] = Time.time + LogInterval;
            Vessel v = ml.vessel;
            CelestialBody body = v.mainBody;
            Vector3d pos = v.CoMD, up = (pos - body.position).normalized;
            Vector3d rt = plan.target.Rel;
            double Pitch(Vector3d d) => d.sqrMagnitude < 1e-6 ? double.NaN : 90d - Vector3d.Angle(up, d);
            Vector3 nose = ml.GetForwardTransform();
            lastAim.TryGetValue(ml, out Vector3? aim);
            string aimS = aim.HasValue ? $"{Pitch((Vector3d)aim.Value - pos):0.0}" : "BDA";
            double aimErr = aim.HasValue ? Vector3.Angle(nose, aim.Value - (Vector3)pos) : double.NaN;
            var o = NukeBallisticCutoff.Predict(pos - body.position, v.obt_velocity, body.gravParameter, rt.magnitude, out double sweep, out double tof);
            Vector3d rtF = NukeStrikeMath.RotateWithBody(body, rt, tof);
            Vector3d vel = v.obt_velocity;
            Vector3d vH = vel - up * Vector3d.Dot(vel, up), tH = rtF - up * Vector3d.Dot(rtF, up);
            double head = vH.sqrMagnitude > 1 && tH.sqrMagnitude > 1 ? Vector3d.Dot(vH.normalized, tH.normalized) : double.NaN;
            double tgtDeg = Vector3d.Angle(pos - body.position, rtF);
            Vector3d nPlane = Vector3d.Cross(pos - body.position, rtF);
            double across = nPlane.sqrMagnitude > 1 ? Vector3d.Dot(vel, nPlane.normalized) : double.NaN;   // what the coast carries sideways
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[BDNukes]: strike telemetry {0}: t {1:0.0} s {2} alt {3:0.0} km srf {4:0} m/s pitch vel {5:0.0} nose {6:0.0} aim {7} (nose off aim {8:0.0} deg) " +
                "| {9} sweep {10:0.00} of {11:0.00} deg, heading {12:+0.00;-0.00}, across {16:+0;-0} m/s | throttle {13:0.00} packed {14} cut {15}",
                ml.shortName, ml.TimeIndex, state, v.altitude / 1000d, v.srfSpeed, Pitch(v.srf_velocity), Pitch((Vector3d)nose), aimS, aimErr,
                o, sweep * 180d / Math.PI, tgtDeg, head, ml.Throttle, v.packed, CutoffDone(ml), across));
        }

        /// <summary>
        /// Steering assist for a planned shot under power. BDArmory steers a missile only within maxAoA of its
        /// airflow and with a weak, slowly ramping torque, which can't pitch a heavy ICBM over: it climbed straight
        /// up until it was too fast to turn and went into space. This turns the nose towards the guidance aim at a
        /// limited rate, held within SteerAoADense of the airflow in dense air and freer as the air thins.
        /// </summary>
        static void Steer(MissileLauncher ml)
        {
            if (!lastAim.TryGetValue(ml, out Vector3? aim) || !aim.HasValue) return;
            Vessel v = ml.vessel;
            Rigidbody rb = ml.part != null ? ml.part.Rigidbody : null;
            if (v == null || v.packed || rb == null || ml.TimeIndex < 1f) return;
            Vector3 fwd = ml.GetForwardTransform();
            Vector3 want = (aim.Value - v.CoM).normalized;
            Vector3 sv = v.srf_velocity;
            if (sv.sqrMagnitude > 400f)
            {
                double q = 0.5d * v.atmDensity * sv.sqrMagnitude;
                float aoa = Mathf.Clamp(SteerAoADense + (90f - SteerAoADense) * (float)(1d - q / SteerThinQ), SteerAoADense, 90f);
                Vector3 svn = sv.normalized;
                if (NukeBallisticCutoff.TrimNormal(ml, out Vector3 n))
                {
                    // the limit goes on the pitch in the plane; the sideways trim is added on top. Clamped as one, a
                    // big pitch-over ate the whole allowance and the sideways correction came late and overshot.
                    float side = Vector3.Dot(want, n);
                    Vector3 inPlane = want - n * side;
                    if (inPlane.sqrMagnitude > 1e-6f)
                    {
                        inPlane.Normalize();
                        float offIn = Vector3.Angle(svn, inPlane);
                        if (offIn > aoa) inPlane = Vector3.Slerp(svn, inPlane, aoa / offIn).normalized;
                        want = (inPlane * Mathf.Sqrt(Mathf.Max(0f, 1f - side * side)) + n * side).normalized;
                    }
                }
                else
                {
                    float off = Vector3.Angle(svn, want);
                    if (off > aoa) want = Vector3.Slerp(svn, want, aoa / off).normalized;
                }
            }
            float angle = Vector3.Angle(fwd, want);
            Vector3 cur = rb.angularVelocity;
            Vector3 roll = fwd * Vector3.Dot(cur, fwd);
            Vector3 wanted = Vector3.zero;
            if (NukeBallisticCutoff.TrimNormal(ml, out Vector3 pn))
            {
                // the pitch in the plane and the sideways trim each get their own turn rate: under one shared rate
                // the small sideways correction waited behind the big pitch-over, came seconds late and the sideways
                // speed swung back and forth (+-50 m/s, period ~12 s) right up to the motor cut
                float sideF = Mathf.Asin(Mathf.Clamp(Vector3.Dot(fwd, pn), -1f, 1f)) * Mathf.Rad2Deg;
                float sideW = Mathf.Asin(Mathf.Clamp(Vector3.Dot(want, pn), -1f, 1f)) * Mathf.Rad2Deg;
                Vector3 fwdIn = fwd - pn * Vector3.Dot(fwd, pn), wantIn = want - pn * Vector3.Dot(want, pn);
                if (fwdIn.sqrMagnitude > 1e-6f && wantIn.sqrMagnitude > 1e-6f)
                {
                    // turning about Cross(a, b) takes a towards b (as below)
                    Vector3 pitchAxis = Vector3.Cross(fwdIn, wantIn);
                    float pr = pitchAxis.sqrMagnitude > 1e-12f ? Mathf.Min(SteerRateDPS, Vector3.Angle(fwdIn, wantIn) * 1.5f) : 0f;
                    if (pr > 0f) pitchAxis.Normalize();
                    // sideways: about the axis in the plane across the nose, its sign from which way the nose must go
                    Vector3 sideAxis = Vector3.Cross(pn, fwd).normalized;   // turning about it tips the nose towards +pn
                    float sErr = sideW - sideF;
                    float sr = Mathf.Sign(sErr) * Mathf.Min(SteerRateDPS, Mathf.Abs(sErr) * 1.5f);
                    wanted = (pitchAxis * pr - sideAxis * sr) * Mathf.Deg2Rad;
                }
            }
            else if (angle > 0.05f)
            {
                Vector3 axis = Vector3.Cross(fwd, want).normalized;
                wanted = axis * Mathf.Min(SteerRateDPS, angle * 1.5f) * Mathf.Deg2Rad;
            }
            rb.angularVelocity = Vector3.Lerp(cur, wanted + roll, 0.25f);
        }

        /// <summary>The aim point for a planned shot (world), or null to leave BDArmory's.</summary>
        internal static Vector3? Aim(MissileLauncher ml)
        {
            var a = AimFor(ml);
            if (ml != null && Planned(ml, out _)) lastAim[ml] = a;
            return a;
        }

        static Vector3? AimFor(MissileLauncher ml)
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
            // pitch over by height or by speed, whichever is further on: a fast missile can't turn far any more
            double kAlt = Math.Max(0d, Math.Min(1d, (agl - 1200d) / climb));
            double kSpeed = Math.Max(0d, Math.Min(1d, (v.srfSpeed - 80d) / 1000d));
            double k = ml.TimeIndex < 4f ? 0d : Math.Max(kAlt, kSpeed);
            k = k * k * (3d - 2d * k);
            double gamma = 88d * Math.PI / 180d * (1d - k) + gammaOpt * k;
            Vector3d d = h * Math.Cos(gamma) + up * Math.Sin(gamma);
            if (speed > 300d && k > 0.3d)
            {
                // steer the velocity, not just the nose, onto the line (also takes out the planet's spin)
                Vector3d err = d - vel / speed;
                d = (d + err * Math.Min(1.5d, 0.5d + k)).normalized;
                // the velocity correction can swing the aim under the horizon while the missile is still steep
                double el = Vector3d.Dot(d, up);
                if (el < 0.02d) d = (d - up * el + up * 0.02d).normalized;
            }
            // the speed across the plane through the missile and the target is cancelled by tipping the thrust sideways
            // directly: the velocity correction above leaves ~150 m/s of the launch site's spin at the cut, which put
            // a Trident's warheads 300 km beside a target 8700 km away
            d = NukeBallisticCutoff.SidewaysTrim(ml, v, body, rtF, d);
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
