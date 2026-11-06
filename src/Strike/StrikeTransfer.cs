using System;
using UnityEngine;

namespace BDNukes
{
    // Orbit states here use KSP world axes, metres and seconds; only KSP celestial bodies supply gravity.
    internal static class StrikeTransfer
    {
        internal static bool Solve(Vessel vessel, StrikeTarget target, double seconds, out Vector3d delta)
        {
            delta = Vector3d.zero;
            var primary = vessel.mainBody;
            var r = vessel.CoMD - primary.position;
            var velocity = vessel.obt_velocity;
            double now = Planetarium.GetUniversalTime();
            var normal = Vector3d.Cross(r, velocity);
            if (target.body == primary)
            {
                if (!NukeStrikeMath.Lambert(r, NukeStrikeMath.RotateWithBody(primary, target.Rel, seconds),
                    seconds, primary.gravParameter, normal, out var departure, out var arrival)) return false;
                if (Vector3d.Dot(arrival, NukeStrikeMath.RotateWithBody(primary, target.Rel, seconds)) >= 0) return false;
                delta = departure - velocity;
                return Finite(delta);
            }
            if (target.body.orbit == null || target.body.orbit.referenceBody != primary) return false;
            var moon = target.body;
            var destination = NukeStrikeMath.BodyRelAt(moon, now + seconds)
                + NukeStrikeMath.RotateWithBody(moon, target.Rel, seconds);
            if (!NukeStrikeMath.Lambert(r, destination, seconds, primary.gravParameter, normal, out var guess, out _)) return false;
            // Shoot a patched-conic path, changing gravity at the moon's sphere of influence.
            for (int iteration = 0; iteration < 7; iteration++)
            {
                var end = Propagate(r, guess, primary, moon, now, seconds);
                var error = destination - end;
                if (error.magnitude < 100) { delta = guess - velocity; return Finite(delta); }
                const double epsilon = 0.5;
                var x = (Propagate(r, guess + Vector3d.right * epsilon, primary, moon, now, seconds) - end) / epsilon;
                var y = (Propagate(r, guess + Vector3d.up * epsilon, primary, moon, now, seconds) - end) / epsilon;
                var z = (Propagate(r, guess + Vector3d.forward * epsilon, primary, moon, now, seconds) - end) / epsilon;
                double det = Vector3d.Dot(x, Vector3d.Cross(y, z));
                if (Math.Abs(det) < 1e-9) return false;
                var change = new Vector3d(Vector3d.Dot(error, Vector3d.Cross(y, z)),
                    Vector3d.Dot(x, Vector3d.Cross(error, z)), Vector3d.Dot(x, Vector3d.Cross(y, error))) / det;
                if (!Finite(change)) return false;
                guess += change.normalized * Math.Min(change.magnitude, 1000);
            }
            return false;
        }

        static Vector3d Acceleration(Vector3d r, CelestialBody primary, CelestialBody moon, double ut)
        {
            var mr = NukeStrikeMath.BodyRelAt(moon, ut);
            var relative = r - mr;
            if (relative.magnitude < moon.sphereOfInfluence)
                return -relative.normalized * moon.gravParameter / Math.Max(1, relative.sqrMagnitude)
                    - mr.normalized * primary.gravParameter / mr.sqrMagnitude;
            return -r.normalized * primary.gravParameter / Math.Max(1, r.sqrMagnitude);
        }

        static Vector3d Propagate(Vector3d r, Vector3d v, CelestialBody primary, CelestialBody moon, double ut, double duration)
        {
            double elapsed = 0;
            for (int i = 0; i < 12000 && elapsed < duration; i++)
            {
                var relative = r - NukeStrikeMath.BodyRelAt(moon, ut + elapsed);
                double step = Math.Min(duration - elapsed, Math.Min(duration / 512,
                    Math.Max(0.02, relative.magnitude / Math.Max(1, v.magnitude) * 0.03)));
                var a1 = Acceleration(r, primary, moon, ut + elapsed);
                var a2 = Acceleration(r + v * (step / 2), primary, moon, ut + elapsed + step / 2);
                var a3 = Acceleration(r + (v + a1 * (step / 2)) * (step / 2), primary, moon, ut + elapsed + step / 2);
                var a4 = Acceleration(r + (v + a2 * (step / 2)) * step, primary, moon, ut + elapsed + step);
                r += (v + 2 * (v + a1 * (step / 2)) + 2 * (v + a2 * (step / 2)) + (v + a3 * step)) * (step / 6);
                v += (a1 + 2 * a2 + 2 * a3 + a4) * (step / 6);
                elapsed += step;
            }
            return elapsed >= duration ? r : new Vector3d(double.NaN, 0, 0);
        }

        internal static bool Finite(Vector3d v) => !double.IsNaN(v.sqrMagnitude) && !double.IsInfinity(v.sqrMagnitude);

        internal static bool Plan(Vessel vessel, StrikeTarget target, out Vector3d delta, out double time)
        {
            delta = Vector3d.zero; time = 0;
            if (target.body == vessel.mainBody)
                return NukeStrikeMath.BestImpactArc(vessel.CoMD - vessel.mainBody.position, vessel.obt_velocity,
                    vessel.mainBody, target, 30, Math.Min(21600, Math.Max(600, vessel.orbit.period)), out delta, out time);
            if (target.body.orbit == null || target.body.orbit.referenceBody != vessel.mainBody) return false;
            double a = ((vessel.CoMD - vessel.mainBody.position).magnitude + target.body.orbit.semiMajorAxis) / 2;
            double nominal = Math.PI * Math.Sqrt(a * a * a / vessel.mainBody.gravParameter);
            double best = double.MaxValue;
            for (int i = 0; i < 12; i++)
            {
                double t = nominal * (0.45 + i * 0.1);
                if (Solve(vessel, target, t, out var dv) && dv.magnitude < best)
                { best = dv.magnitude; delta = dv; time = t; }
            }
            return best < double.MaxValue;
        }
    }
}
