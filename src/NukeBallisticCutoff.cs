using System;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Thrust termination for ballistic missiles.
    ///
    /// BDArmory's agmballistic guidance (BallisticGuidance) only eases the throttle back by 0.001 per physics frame
    /// once its flat-Earth free-fall estimate says the target is reachable, and pushes it back up (to atmDensity * 10,
    /// i.e. full throttle in dense air) on the way down. With realistic propellant (Minuteman III ~6.3 km/s, R-36M2
    /// ~7.4 km/s of delta-v against the ~1.5-2 km/s a 200 km shot needs on Kerbin) the missiles overshoot and then
    /// power-dive onto the target.
    ///
    /// After BDArmory's ballistic guidance has run each frame, this predicts the drag-free impact point from the
    /// current state vector: a Kepler conic around the body (inertial velocity, the body's real gravitational
    /// parameter) cut at the target's radius, with the target carried round by the planet's rotation over the
    /// time of flight. Once the predicted downrange angle reaches the target's (times ballisticCutoffMargin), the
    /// motor is cut for good (Throttle 0, which BDArmory's boost/cruise routines also use to stop the exhaust and
    /// the fuel burn). The missile then coasts and BDArmory's own guidance and fuzes take it from there.
    ///
    /// Only for MissileLaunchers that have thrust and a ModuleNukeCloudProfile with ballisticCutoff = true
    /// (every BDNukes part; other mods' missiles are untouched).
    /// </summary>
    [HarmonyPatch(typeof(MissileLauncher), "AGMBallisticGuidance")]
    internal static class Patch_MissileLauncher_AGMBallisticGuidance
    {
        static bool loggedError;

        static void Postfix(MissileLauncher __instance)
        {
            try
            {
                NukeBallisticCutoff.Apply(__instance);
            }
            catch (Exception e)
            {
                if (loggedError) return;
                loggedError = true;
                Debug.LogError("[BDNukes]: Ballistic cutoff failed: " + e);
            }
        }
    }

    internal static class NukeBallisticCutoff
    {
        internal enum Outcome { Short, Impact, Beyond }

        internal static void Apply(MissileLauncher ml)
        {
            var part = ml.part;
            if (part == null) return;
            var prof = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof == null || !prof.ballisticCutoff) return;
            if (prof.ballisticCutoffDone)
            {
                ml.Throttle = 0f;
                return;
            }
            if (ml.thrust <= 0f && ml.cruiseThrust <= 0f) return; // glide bombs: nothing to cut
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return;
            if (!ml.TargetAcquired) return;
            Vessel v = ml.vessel;
            if (v == null || v.packed || v.mainBody == null) return;
            CelestialBody body = v.mainBody;
            Vector3 targetW = ml.TargetPosition;
            if (targetW == Vector3.zero) return;

            Vector3d r = v.CoMD - body.position;
            Vector3d vel = v.obt_velocity;
            Vector3d rt = (Vector3d)targetW - body.position;
            double targetRadius = rt.magnitude;
            if (targetRadius < 1d) return;

            Outcome o = Predict(r, vel, body.gravParameter, targetRadius, out double sweep, out double tof);
            if (o == Outcome.Short) return;

            double targetAngle = 0d, rangeDeg = 0d;
            if (o == Outcome.Impact)
            {
                Vector3d rtAtImpact = RotateWithBody(body, rt, tof);
                Vector3d rHat = r.normalized;
                // only once the missile is actually heading downrange towards the target
                Vector3d vHoriz = vel - rHat * Vector3d.Dot(vel, rHat);
                Vector3d tHoriz = rtAtImpact - rHat * Vector3d.Dot(rtAtImpact, rHat);
                if (Vector3d.Dot(vHoriz, tHoriz) <= 0d) return;
                targetAngle = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(rHat, rtAtImpact.normalized))));
                if (sweep < targetAngle * Math.Max(0.5f, prof.ballisticCutoffMargin)) return;
                rangeDeg = sweep * 180d / Math.PI;
            }

            prof.ballisticCutoffDone = true;
            ml.Throttle = 0f;
            Debug.Log(o == Outcome.Impact
                ? $"[BDNukes]: {ml.shortName}: motor cut, ballistic range {rangeDeg * Math.PI / 180d * targetRadius / 1000d:0.0} km for a target {targetAngle * targetRadius / 1000d:0.0} km downrange (flight time {tof:0} s)."
                : $"[BDNukes]: {ml.shortName}: motor cut, trajectory no longer comes back down to the target.");
        }

        /// <summary>
        /// Drag-free (Kepler) prediction of where the trajectory from (r, v) next comes down through radius rImpact.
        /// Short: it never climbs to rImpact, or is already below it on the way down. Beyond: it never comes back
        /// down to rImpact (orbit or escape). Impact: sweep is the angle travelled round the body to the impact
        /// point (rad) and tof the time to get there (s).
        /// </summary>
        internal static Outcome Predict(Vector3d r, Vector3d v, double mu, double rImpact, out double sweep, out double tof)
        {
            sweep = tof = 0d;
            double rm = r.magnitude;
            if (rm < 1d || mu <= 0d) return Outcome.Short;
            double v2 = v.sqrMagnitude, rv = Vector3d.Dot(r, v);
            double h2 = rm * rm * v2 - rv * rv;
            if (h2 < 1e-6) return Outcome.Short; // straight up or down: no downrange travel
            double p = h2 / mu;
            Vector3d ev = ((v2 - mu / rm) * r - rv * v) / mu;
            double e = ev.magnitude;

            if (e < 1e-9) return rm > rImpact ? Outcome.Beyond : Outcome.Short; // circular
            double cosImpact = (p / rImpact - 1d) / e;
            if (cosImpact >= 1d) return Outcome.Beyond;  // periapsis above the target: never comes down
            if (cosImpact <= -1d) return Outcome.Short;  // apoapsis below the target: never gets up there
            if (e >= 1d && rv >= 0d) return Outcome.Beyond; // escaping

            double nuImpact = 2d * Math.PI - Math.Acos(cosImpact); // the descending crossing
            double nu0 = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(ev, r) / (e * rm))));
            if (rv < 0d) nu0 = 2d * Math.PI - nu0;
            if (nu0 > nuImpact) return Outcome.Short; // already below the target radius on the way down
            sweep = nuImpact - nu0;

            if (e < 1d)
            {
                double a = p / (1d - e * e);
                double n = Math.Sqrt(mu / (a * a * a));
                tof = (MeanAnomalyElliptic(nuImpact, e) - MeanAnomalyElliptic(nu0, e)) / n;
            }
            else
            {
                double a = p / (e * e - 1d);
                double n = Math.Sqrt(mu / (a * a * a));
                tof = (MeanAnomalyHyperbolic(nuImpact - 2d * Math.PI, e) - MeanAnomalyHyperbolic(nu0 - 2d * Math.PI, e)) / n;
            }
            if (double.IsNaN(tof) || tof < 0d) tof = 0d;
            return Outcome.Impact;
        }

        /// <summary>Mean anomaly for a true anomaly in [0, 2pi] on an ellipse; increases monotonically over the orbit.</summary>
        static double MeanAnomalyElliptic(double nu, double e)
        {
            double E = 2d * Math.Atan2(Math.Sqrt(1d - e) * Math.Sin(nu / 2d), Math.Sqrt(1d + e) * Math.Cos(nu / 2d));
            return E - e * Math.Sin(E);
        }

        /// <summary>Hyperbolic mean anomaly for a true anomaly between the asymptotes (-nuInf, nuInf).</summary>
        static double MeanAnomalyHyperbolic(double nu, double e)
        {
            double x = Math.Sqrt((e - 1d) / (e + 1d)) * Math.Tan(nu / 2d);
            x = Math.Max(-0.999999999d, Math.Min(0.999999999d, x));
            double F = Math.Log((1d + x) / (1d - x)); // 2 atanh(x)
            return e * Math.Sinh(F) - F;
        }

        /// <summary>Where a surface point (relative to the body centre) will be after `seconds` of the body's rotation.</summary>
        static Vector3d RotateWithBody(CelestialBody body, Vector3d rel, double seconds)
        {
            if (!body.rotates || body.rotationPeriod <= 0d || seconds <= 0d) return rel;
            Vector3 axis = ((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized;
            float deg = (float)(360d * (seconds / body.rotationPeriod) % 360d);
            // turn the way the surface actually moves (the frame's handedness decides the sign)
            Vector3 surf = (Vector3)body.getRFrmVel(body.position + rel);
            Vector3 probe = Quaternion.AngleAxis(1f, axis) * (Vector3)rel - (Vector3)rel;
            if (Vector3.Dot(probe, surf) < 0f) deg = -deg;
            return (Vector3d)(Quaternion.AngleAxis(deg, axis) * (Vector3)rel);
        }
    }
}
