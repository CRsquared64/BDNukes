using System;
using BDArmory.Guidances;
using BDArmory.Settings;
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
    /// time of flight. Once that drag-free range reaches the target, a second prediction flies the coast with the
    /// missile's own drag (measured in flight as drag force over dynamic pressure; KSP's drag cubes, which dwarf
    /// BDArmory's missile drag) through the atmosphere, a few times a second. Once the range with drag reaches the
    /// target's (times ballisticCutoffMargin), the motor is cut for good (Throttle 0, which BDArmory's boost/cruise routines also use to stop the exhaust and
    /// the fuel burn). The missile then coasts and BDArmory's own guidance and fuzes take it from there.
    ///
    /// Only for MissileLaunchers that have thrust and a ModuleNukeCloudProfile with ballisticCutoff = true
    /// (every BDNukes part; other mods' missiles are untouched).
    /// </summary>
    [HarmonyPatch(typeof(MissileLauncher), "AGMBallisticGuidance")]
    internal static class Patch_MissileLauncher_AGMBallisticGuidance
    {
        static bool loggedError;

        // BDArmory steers its boost at the target where it is now. Over a real-scale flight (half an hour) the planet
        // turns under the missile, which also carries the launch site's spin sideways: hundreds of km off to one side.
        // For its own steering it is handed the aim point that cancels that; the real target is put back after.
        static void Prefix(MissileLauncher __instance, out Vector3 __state)
        {
            __state = __instance.TargetPosition;
            try
            {
                // while climbing, ClimbAim steers at where the target will be and trims the sideways drift itself;
                // CrossRangeAim's offset aim point is only the fallback for the rest of the burn
                Vector3 aim;
                if (NukeBallisticCutoff.ClimbAim(__instance, NukeBallisticCutoff.TargetAtImpact(__instance), out aim)) __instance.TargetPosition = aim;
                else if (NukeBallisticCutoff.CrossRangeAim(__instance, out aim)) __instance.TargetPosition = aim;
            }
            catch (Exception) { __instance.TargetPosition = __state; }
        }

        static void Postfix(MissileLauncher __instance, Vector3 __state)
        {
            try
            {
                __instance.TargetPosition = __state;
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

        static readonly System.Reflection.FieldInfo burnedFuel = AccessTools.Field(typeof(MissileLauncher), "burnedFuelMass");

        /// <summary>
        /// BDArmory's boost (and cruise) only ends once its fuel is burnt, and with the throttle at 0 none ever is:
        /// the booster would never drop. Marks the stage's fuel spent so it ends now and the booster separates.
        /// </summary>
        static void EndBoost(MissileLauncher ml)
        {
            try
            {
                if (burnedFuel == null) return;
                float burned = (float)burnedFuel.GetValue(ml);
                float spent = ml.MissileState == MissileBase.MissileStates.Boost ? ml.boosterFuelMass : ml.boosterFuelMass + ml.cruiseFuelMass;
                if (burned < spent) burnedFuel.SetValue(ml, spent);
                // and its timer, so it ends this frame rather than at the stage's planned burnout
                if (ml.MissileState == MissileBase.MissileStates.Boost) ml.boostTime = 0f;
                else if (ml.MissileState == MissileBase.MissileStates.Cruise) ml.cruiseTime = 0f;
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't end the boost after the motor cut: " + e.Message); }
        }

        /// <summary>
        /// The point to hand BDArmory so its boost holds a proper climb. Its ballistic guidance aims BallisticAngle above
        /// the line of sight to the target; as the missile climbs and moves downrange that line tips below the horizon,
        /// so the climb flattens, and with an ICBM's delta-v (far more than a short shot needs, so the motor runs on
        /// against the drag low down) the missile flew a long, flat, depressed path through the air: the warheads never
        /// got above it to separate. Instead the boost holds a flight-path angle above the local horizon, towards the
        /// target: steep while the air is thick, easing to the minimum-energy angle for the range left
        /// (45 deg less a quarter of the arc to the target) by a third of the way up the atmosphere. The motor cut then
        /// ends the burn on that arc, which tops out well above the air on any but the shortest shots.
        /// </summary>
        internal static bool ClimbAim(MissileLauncher ml, Vector3 aimTarget, out Vector3 fake)
        {
            fake = Vector3.zero;
            var part = ml.part;
            if (part == null) return false;
            var prof = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof == null || !prof.ballisticCutoff || prof.ballisticCutoffDone) return false;
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return false;
            if (!ml.TargetAcquired || aimTarget == Vector3.zero) return false;
            Vessel v = ml.vessel;
            if (v == null || v.packed || v.mainBody == null || v.verticalSpeed <= 0d) return false;
            CelestialBody body = v.mainBody;
            Vector3d com = v.CoMD;
            Vector3d up = (com - body.position).normalized;
            Vector3d t = (Vector3d)aimTarget - body.position;
            Vector3d down = t - up * Vector3d.Dot(t, up);
            if (down.sqrMagnitude < 1d) return false;
            down = down.normalized;
            double arc = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(up, t.normalized))));
            double gamma = Math.Max(15d, 45d - arc * 180d / Math.PI / 4d);
            if (body.atmosphere && body.atmosphereDepth > 0d)
            {
                double f = Math.Max(0d, Math.Min(1d, v.altitude / (body.atmosphereDepth / 3d)));
                gamma += (80d - gamma) * (1d - f);
            }
            // BDArmory turns what it is given up by this much (MissileBase's BallisticGuidance): hand it a point that
            // much below the climb wanted, as far away as the real one
            double turn = Math.Max(0d, Math.Min(ml.maxOffBoresight * 0.9d, ml.BallisticAngle));
            double a = (gamma - turn) * Math.PI / 180d;
            Vector3d dir = down * Math.Cos(a) + up * Math.Sin(a);
            dir = SidewaysTrim(ml, v, body, t, dir);
            fake = (Vector3)(com + dir * Math.Max(1000d, ((Vector3d)aimTarget - com).magnitude));
            return true;
        }

        /// <summary>
        /// Tips the thrust sideways to null the missile's speed out of the plane through it and the target (where the
        /// target will be at impact). The launch site's spin starts the missile with up to a few hundred m/s across that
        /// plane; whatever is left at the cut carries through the coast. Turning the heading towards an offset aim point
        /// (CrossRangeAim) barely tilts the thrust while the climb is steep, so 160 m/s survived to the cut in game and
        /// the warheads came down 300 km beside the target. This sets the sideways share of the thrust directly: a
        /// 2 s response, plus an integral term for the plane turning steadily as the predicted flight time grows.
        /// </summary>
        internal static Vector3d SidewaysTrim(MissileLauncher ml, Vessel v, CelestialBody body, Vector3d rtImpact, Vector3d dir)
        {
            double thrust = ml.MissileState == MissileBase.MissileStates.Boost ? ml.thrust : ml.cruiseThrust;
            double accel = thrust * Math.Max(0f, ml.Throttle) / Math.Max(0.001d, v.totalMass);   // kN / t = m/s^2
            if (accel < 1d) return dir;
            Vector3d n = Vector3d.Cross(v.CoMD - body.position, rtImpact);
            if (n.sqrMagnitude < 1d) return dir;
            n = n.normalized;
            double cross = Vector3d.Dot(v.obt_velocity, n);
            const double tau = 2d, ti = 4d;
            double now = Planetarium.GetUniversalTime();
            var st = trim.GetOrCreateValue(ml);
            double dt = st.time > 0d ? Math.Max(0d, Math.Min(0.1d, now - st.time)) : 0d;
            st.time = now;
            // only a little sideways tilt in the thick air near the ground, where it would mean a big angle of attack
            double sMax = 0.5d;
            if (body.atmosphere && body.atmosphereDepth > 0d)
                sMax = 0.1d + 0.4d * Math.Max(0d, Math.Min(1d, v.altitude / (body.atmosphereDepth / 6d)));
            double sRaw = -(cross + st.integ / ti) / (tau * accel);
            // the integral only runs while the command isn't pinned at its limit: summed up while the nose couldn't
            // follow, it overshot the other way once it could (a Rome shot swung -185, +75, -73, +112 m/s)
            if (Math.Abs(sRaw) <= sMax || sRaw * cross > 0d)
            {
                double lim = 0.5d * tau * accel * ti;
                st.integ = Math.Max(-lim, Math.Min(lim, st.integ + cross * dt));
            }
            double s = Math.Max(-sMax, Math.Min(sMax, sRaw));
            st.normal = n;
            st.normalTime = Time.time;
            Vector3d inPlane = dir - n * Vector3d.Dot(dir, n);
            if (inPlane.sqrMagnitude < 1e-6) return dir;
            return inPlane.normalized * Math.Sqrt(1d - s * s) + n * s;
        }

        sealed class TrimState { public double integ, time; public Vector3d normal; public float normalTime; }

        /// <summary>The plane the sideways trim is holding the missile to, if it set one this frame or the last.</summary>
        internal static bool TrimNormal(MissileLauncher ml, out Vector3 n)
        {
            n = Vector3.zero;
            if (ml == null || !trim.TryGetValue(ml, out var st) || Time.time - st.normalTime > 0.1f) return false;
            n = (Vector3)st.normal;
            return n.sqrMagnitude > 0.5f;
        }
        // per missile, dropped with it
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MissileLauncher, TrimState> trim =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MissileLauncher, TrimState>();

        /// <summary>
        /// The point to steer the boost at so the coast comes down on the target, not beside it: the target carried
        /// round by the planet's rotation over the flight, pushed against the missile's sideways (inertial) drift out
        /// of the plane through it and the missile. Only while the motor burns, before the cut.
        /// </summary>
        /// <summary>The target carried round by the planet's rotation over the predicted flight time (as it is, if none).</summary>
        internal static Vector3 TargetAtImpact(MissileLauncher ml)
        {
            Vector3 tp = ml.TargetPosition;
            var v = ml.vessel;
            var prof = ml.part != null ? ml.part.FindModuleImplementing<ModuleNukeCloudProfile>() : null;
            if (tp == Vector3.zero || v == null || v.mainBody == null || prof == null) return tp;
            CelestialBody body = v.mainBody;
            Vector3d rt = (Vector3d)tp - body.position;
            double R = rt.magnitude;
            if (R < 1d) return tp;
            Vector3d r = v.CoMD - body.position;
            double angle = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(r.normalized, rt / R))));
            double tof = prof.cutTof > 0d ? prof.cutTof : angle * R / 5000d;
            return (Vector3)(body.position + RotateWithBody(body, rt, tof));
        }

        internal static bool CrossRangeAim(MissileLauncher ml, out Vector3 aim)
        {
            aim = Vector3.zero;
            var part = ml.part;
            if (part == null) return false;
            var prof = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof == null || !prof.ballisticCutoff || prof.ballisticCutoffDone) return false;
            var state = ml.MissileState;
            if (state != MissileBase.MissileStates.Boost && state != MissileBase.MissileStates.Cruise) return false;
            if (!ml.TargetAcquired || ml.TargetPosition == Vector3.zero) return false;
            Vessel v = ml.vessel;
            if (v == null || v.packed || v.mainBody == null) return false;
            CelestialBody body = v.mainBody;
            if (!body.rotates || body.rotationPeriod <= 0d) return false;
            Vector3d r = v.CoMD - body.position;
            Vector3d rt = (Vector3d)ml.TargetPosition - body.position;
            double R = rt.magnitude;
            if (R < 1d) return false;
            // too close for the planet to turn noticeably under it: leave BDArmory's steering alone
            double angle = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(r.normalized, rt / R))));
            if (angle * R < 50000d) return false;
            // the flight time: the drag-free prediction's once it has one, else a rough ~5 km/s average
            double tof = prof.cutTof > 0d ? prof.cutTof : angle * R / 5000d;
            Vector3d rtImpact = RotateWithBody(body, rt, tof);
            Vector3d n = Vector3d.Cross(r, rtImpact);
            if (n.sqrMagnitude < 1d) return false;
            n = n.normalized;
            // sideways speed out of the plane through the missile and where the target will be; it carries on
            // through the coast, so steer against it, the harder the longer the flight
            double cross = Vector3d.Dot(v.obt_velocity, n);
            double shift = -cross * tof;
            double maxShift = 0.35d * angle * R;
            if (shift > maxShift) shift = maxShift; else if (shift < -maxShift) shift = -maxShift;
            aim = (Vector3)(body.position + rtImpact + n * shift);
            return true;
        }

        internal static void Apply(MissileLauncher ml)
        {
            var part = ml.part;
            if (part == null) return;
            var prof = part.FindModuleImplementing<ModuleNukeCloudProfile>();
            if (prof == null || !prof.ballisticCutoff) return;
            if (prof.ballisticCutoffDone)
            {
                ml.Throttle = 0f;
                EndBoost(ml);   // a cut in the boost carries on into the cruise stage, which must end too
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

            MeasureDrag(ml, v, prof);
            Vector3d r = v.CoMD - body.position;
            Vector3d vel = v.obt_velocity;
            Vector3d rt = (Vector3d)targetW - body.position;
            double targetRadius = rt.magnitude;
            if (targetRadius < 1d) return;

            Outcome o = Predict(r, vel, body.gravParameter, targetRadius, out double sweep, out double tof);
            if (o == Outcome.Short) return;

            double targetAngle = 0d, rangeDeg = 0d;
            if (o == Outcome.Impact) prof.cutTof = tof;
            if (o == Outcome.Impact)
            {
                Vector3d rtAtImpact = RotateWithBody(body, rt, tof);
                Vector3d rHat = r.normalized;
                // only once the missile is actually heading downrange towards the target
                Vector3d vHoriz = vel - rHat * Vector3d.Dot(vel, rHat);
                Vector3d tHoriz = rtAtImpact - rHat * Vector3d.Dot(rtAtImpact, rHat);
                if (Vector3d.Dot(vHoriz, tHoriz) <= 0d) return;
                targetAngle = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(rHat, rtAtImpact.normalized))));
                double margin = Math.Max(0.5f, prof.ballisticCutoffMargin);
                // drag only shortens the flight: while the drag-free range is short, so is the real one
                if (sweep < targetAngle * margin) return;
                bool withDrag = prof.ballisticDrag && body.atmosphere && prof.cutCdA > 0d;
                if (withDrag)
                {
                    // the drag prediction is a few hundred steps: a few times a second, its offsets carried in between
                    if (!prof.cutHaveDrag || Time.time >= prof.cutNextSim)
                    {
                        prof.cutNextSim = Time.time + 0.2f;
                        if (CoastWithDrag(body, r, vel, v.totalMass, prof.cutCdA * Math.Max(0f, prof.ballisticDragScale), targetRadius,
                                out double dragSweep, out double dragTof))
                        {
                            double targetAtDrag = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(rHat, RotateWithBody(body, rt, dragTof).normalized))));
                            prof.cutDragLoss = sweep - dragSweep;
                            prof.cutTargetShift = targetAtDrag - targetAngle;
                            prof.cutHaveDrag = true;
                        }
                        else prof.cutHaveDrag = false;   // no impact within the steps allowed: not yet
                    }
                    if (!prof.cutHaveDrag) return;
                    if (sweep - prof.cutDragLoss < (targetAngle + prof.cutTargetShift) * margin) return;
                }
                rangeDeg = (withDrag ? sweep - prof.cutDragLoss : sweep) * 180d / Math.PI;
                if (withDrag) targetAngle += prof.cutTargetShift;
            }

            prof.ballisticCutoffDone = true;
            ml.Throttle = 0f;
            EndBoost(ml);
            Debug.Log(o == Outcome.Impact
                ? $"[BDNukes]: {ml.shortName}: motor cut, ballistic range {rangeDeg * Math.PI / 180d * targetRadius / 1000d:0.0} km for a target {targetAngle * targetRadius / 1000d:0.0} km downrange (flight time {tof:0} s"
                  + (prof.ballisticDrag && body.atmosphere && prof.cutCdA > 0d
                      ? $"; drag takes {prof.cutDragLoss * targetRadius / 1000d:0.0} km off the vacuum range, CdA {prof.cutCdA:0.000} m^2)."
                      : ", no drag allowed for).")
                : $"[BDNukes]: {ml.shortName}: motor cut, trajectory no longer comes back down to the target.");
        }

        /// <summary>
        /// The missile's drag area (drag force over dynamic pressure, m^2: kN / kPa). BDArmory flies its missiles with
        /// its own aero (currDragArea and its drag curve, nose-on), which stock's per-part drag doesn't see, so that is
        /// counted from the missile's own numbers, in vacuum too; any stock drag on the parts is measured on top,
        /// smoothed over a second or two so it follows the booster dropping off.
        /// </summary>
        static void MeasureDrag(MissileLauncher ml, Vessel v, ModuleNukeCloudProfile prof)
        {
            double bda = 0d;
            try
            {
                if (ml.aero && ml.currDragArea > 0f)
                    bda = 1000d * ml.currDragArea * BDArmorySettings.GLOBAL_DRAG_MULTIPLIER
                        * Math.Max(0f, MissileGuidance.DefaultDragCurve.Evaluate(0f));
            }
            catch { bda = 0d; }
            if (double.IsNaN(bda) || bda < 0d) bda = 0d;

            double q = v.dynamicPressurekPa;
            if (q >= 0.3d)
            {
                double f = 0d;
                for (int i = 0; i < v.parts.Count; i++) f += v.parts[i].dragScalar;
                if (f > 0d && !double.IsNaN(f))
                {
                    double cda = f / q;
                    prof.cutStockCdA = prof.cutStockCdA <= 0d ? cda : prof.cutStockCdA + (cda - prof.cutStockCdA) * Math.Min(1d, TimeWarp.fixedDeltaTime / 1.5d);
                }
            }
            prof.cutCdA = bda + prof.cutStockCdA;
        }

        /// <summary>
        /// Flies the unpowered coast from (r, v) (body-centred, inertial) with gravity and drag (cdA m^2 on massT
        /// tonnes, against the air turning with the body) until it comes down through rImpact. sweep is the angle
        /// from r to the impact point (rad), tof the time to it (s). False if it does not come down within an hour.
        /// </summary>
        internal static bool CoastWithDrag(CelestialBody body, Vector3d r, Vector3d v, double massT, double cdA, double rImpact,
            out double sweep, out double tof)
        {
            sweep = tof = 0d;
            if (massT <= 0d) return false;
            double mu = body.gravParameter, top = body.Radius + body.atmosphereDepth;
            Vector3d r0 = r;
            double t = 0d;
            Vector3d a = Accel(body, r, v, mu, massT, cdA, top);
            for (int i = 0; i < 40000 && t < 3600d; i++)
            {
                double rm = r.magnitude;
                // coarse steps in vacuum, fine ones in the air where the drag changes fast
                double dt = rm > top + 2000d ? 1d : 0.1d;
                Vector3d rPrev = r;
                // velocity Verlet (the drag for the new point taken at the old velocity)
                r += v * dt + a * (0.5d * dt * dt);
                Vector3d aNew = Accel(body, r, v, mu, massT, cdA, top);
                v += (a + aNew) * (0.5d * dt);
                a = aNew;
                t += dt;
                double rn = r.magnitude;
                if (rn <= rImpact && Vector3d.Dot(r, v) < 0d)
                {
                    double f = (rPrev.magnitude - rImpact) / Math.Max(1e-6d, rPrev.magnitude - rn);
                    Vector3d hit = rPrev + (r - rPrev) * f;
                    tof = t - dt * (1d - f);
                    sweep = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(r0.normalized, hit.normalized))));
                    return true;
                }
            }
            return false;
        }

        static Vector3d Accel(CelestialBody body, Vector3d r, Vector3d v, double mu, double massT, double cdA, double top)
        {
            double rm = r.magnitude;
            Vector3d a = r * (-mu / (rm * rm * rm));
            if (rm >= top || cdA <= 0d) return a;
            double alt = rm - body.Radius;
            double p = FlightGlobals.getStaticPressure(alt, body);
            if (p <= 0d) return a;
            double rho = FlightGlobals.getAtmDensity(p, FlightGlobals.getExternalTemperature(alt, body), body);
            Vector3d air = v - body.getRFrmVel(body.position + r);
            double s = air.magnitude;
            if (s < 1e-3d) return a;
            // kN / t = m/s^2: 0.5 rho s^2 (Pa) / 1000 * cdA (m^2) / mass (t)
            return a - air * (0.5d * rho * s * cdA / (1000d * massT));
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
        internal static Vector3d RotateWithBody(CelestialBody body, Vector3d rel, double seconds)
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
