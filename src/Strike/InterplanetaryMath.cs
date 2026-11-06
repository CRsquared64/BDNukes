using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Geometry for the Starlance interplanetary missile (ModuleNukeInterplanetary / InterplanetaryDirector).
    ///
    /// Frames. KSP has two sets of axes: "world" (Unity, what CelestialBody.position and vessel.CoMD use) and the
    /// orbit frame ("zup", what Orbit.getRelativePositionAtUT and Orbit.UpdateFromStateVectors use); at any one
    /// moment they differ by a y/z swap (Vector3d.xzy). Both turn with the planet while the active vessel is low over
    /// it (the rotating frame), so a vector is only meaningful in the frame update it was computed in. Everything
    /// here is therefore recomputed each tick from frame-free quantities: universal times, body names and body-fixed
    /// latitude / longitude / altitude. Future surface points come from NukeStrikeMath.RotateWithBody, whose spin
    /// direction is taken from CelestialBody.angularVelocity (a constant, so right in either frame); future body
    /// positions from the bodies' orbits (Orbit maths always answers in the current frame).
    /// </summary>
    internal static class InterplanetaryMath
    {
        public const double G0 = 9.80665;

        public static CelestialBody Sun => Planetarium.fetch != null ? Planetarium.fetch.Sun : (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null);

        /// <summary>Position of a body relative to the Sun at ut, orbit frame (zup).</summary>
        public static Vector3d SunPos(CelestialBody b, double ut)
        {
            Vector3d p = Vector3d.zero;
            int guard = 0;
            while (b != null && b.orbit != null && b.referenceBody != null && b.referenceBody != b && guard++ < 16)
            {
                p += b.orbit.getRelativePositionAtUT(ut);
                b = b.referenceBody;
            }
            return p;
        }

        /// <summary>Velocity of a body relative to the Sun at ut, orbit frame (zup).</summary>
        public static Vector3d SunVel(CelestialBody b, double ut)
        {
            Vector3d v = Vector3d.zero;
            int guard = 0;
            while (b != null && b.orbit != null && b.referenceBody != null && b.referenceBody != b && guard++ < 16)
            {
                v += b.orbit.getOrbitalVelocityAtUT(ut);
                b = b.referenceBody;
            }
            return v;
        }

        /// <summary>The innermost body whose sphere of influence holds a Sun-relative point (zup) at ut.</summary>
        public static CelestialBody DominantBody(Vector3d sunRel, double ut)
        {
            CelestialBody cur = Sun;
            if (cur == null || FlightGlobals.Bodies == null) return cur;
            for (int depth = 0; depth < 8; depth++)
            {
                CelestialBody next = null;
                foreach (var c in FlightGlobals.Bodies)
                {
                    if (c == null || c == cur || c.referenceBody != cur || c.orbit == null) continue;
                    if ((sunRel - SunPos(c, ut)).magnitude < c.sphereOfInfluence) { next = c; break; }
                }
                if (next == null) break;
                cur = next;
            }
            return cur;
        }

        /// <summary>
        /// A body-fixed point (lat, lon, alt) relative to the body's centre at universal time ut, in the current
        /// frame's world axes (ut may be past or future).
        /// </summary>
        public static Vector3d SurfaceRelAt(CelestialBody b, double lat, double lon, double alt, double ut, double now)
        {
            Vector3d rel = b.GetRelSurfacePosition(lat, lon, alt);
            return NukeStrikeMath.RotateWithBody(b, rel, ut - now);
        }

        /// <summary>
        /// The body-fixed coordinates of a point that is at worldRel (relative to the centre, world axes) at ut.
        /// </summary>
        public static void BodyFixed(CelestialBody b, Vector3d worldRelAtUt, double ut, double now, out double lat, out double lon, out double alt)
        {
            Vector3d relNow = NukeStrikeMath.RotateWithBody(b, worldRelAtUt, now - ut);
            lat = b.GetLatitude(relNow, true);
            lon = b.GetLongitude(relNow, true);
            alt = relNow.magnitude - b.Radius;
        }

        /// <summary>The point `dist` radians of arc from (lat, lon) along the initial bearing `brg` (radians from north).</summary>
        public static void Destination(double lat, double lon, double brg, double dist, out double lat2, out double lon2)
        {
            double p1 = lat * Math.PI / 180d, l1 = lon * Math.PI / 180d;
            double sp2 = Math.Sin(p1) * Math.Cos(dist) + Math.Cos(p1) * Math.Sin(dist) * Math.Cos(brg);
            sp2 = Math.Max(-1d, Math.Min(1d, sp2));
            double p2 = Math.Asin(sp2);
            double l2 = l1 + Math.Atan2(Math.Sin(brg) * Math.Sin(dist) * Math.Cos(p1), Math.Cos(dist) - Math.Sin(p1) * sp2);
            lat2 = p2 * 180d / Math.PI;
            lon2 = ((l2 * 180d / Math.PI + 540d) % 360d) - 180d;
        }

        /// <summary>
        /// Aim points for `count` warheads: the centre, then a ring of the rest at `radiusDeg` of arc, evenly spread
        /// in bearing (beyond 10 warheads, a second inner ring at half the radius takes every other one).
        /// </summary>
        public static Vector2d[] AimPattern(double lat, double lon, int count, double radiusDeg, double rotationDeg)
        {
            count = Math.Max(1, count);
            var pts = new Vector2d[count];
            pts[0] = new Vector2d(lat, lon);
            int ring = count - 1;
            if (ring <= 0) return pts;
            bool two = ring > 9;
            int outer = two ? (ring + 1) / 2 : ring, inner = ring - outer;
            int k = 1;
            for (int i = 0; i < outer; i++, k++)
            {
                double brg = (rotationDeg + 360d * i / outer) * Math.PI / 180d;
                Destination(lat, lon, brg, radiusDeg * Math.PI / 180d, out double a, out double b);
                pts[k] = new Vector2d(a, b);
            }
            for (int i = 0; i < inner; i++, k++)
            {
                double brg = (rotationDeg + 180d / Math.Max(1, inner) + 360d * i / inner) * Math.PI / 180d;
                Destination(lat, lon, brg, 0.5d * radiusDeg * Math.PI / 180d, out double a, out double b);
                pts[k] = new Vector2d(a, b);
            }
            return pts;
        }

        /// <summary>Parking orbit altitude for a body: clear of the air (and of the rotating frame), or of the mountains.</summary>
        public static double ParkingAltitude(CelestialBody b, double requested)
        {
            if (b == null) return requested;
            double alt;
            if (b.atmosphere) alt = Math.Max(b.atmosphereDepth + Math.Max(10000d, 0.3d * b.atmosphereDepth), b.atmosphereDepth + 30000d);
            else alt = Math.Max(20000d, 0.06d * b.Radius);
            // above the inverse-rotation altitude, so the light drive starts from a non-rotating frame
            alt = Math.Max(alt, b.inverseRotThresholdAltitude + 2000d);
            if (requested > 0d) alt = Math.Max(alt, requested);
            if (b.sphereOfInfluence > 0d && !double.IsInfinity(b.sphereOfInfluence))
                alt = Math.Min(alt, 0.5d * b.sphereOfInfluence - b.Radius);
            return alt;
        }

        /// <summary>Circular orbit speed at radius r.</summary>
        public static double Circular(CelestialBody b, double r) => Math.Sqrt(b.gravParameter / Math.Max(1d, r));

        /// <summary>Delta-v (rough) to reach a parking orbit from the surface: orbital speed plus gravity and drag losses.</summary>
        public static double AscentDeltaV(CelestialBody b, double parkAlt)
        {
            double r = b.Radius + parkAlt;
            double v = Circular(b, r) - (b.rotates && b.rotationPeriod > 0 ? 2d * Math.PI * b.Radius / b.rotationPeriod * 0.8d : 0d);
            double g = b.gravParameter / (b.Radius * b.Radius);
            double gravityLoss = 0.11d * Math.Sqrt(g * b.Radius) + 40d * g;   // ~ 700 m/s on Kerbin
            double drag = b.atmosphere ? 250d * Math.Min(4d, Math.Sqrt(Math.Max(0d, b.atmospherePressureSeaLevel) / 101.325d)) : 0d;
            return Math.Max(0d, v) + gravityLoss + drag;
        }

        /// <summary>Min distance from point c to the segment a-b.</summary>
        public static double SegmentDistance(Vector3d a, Vector3d b, Vector3d c)
        {
            Vector3d ab = b - a;
            double l2 = ab.sqrMagnitude;
            if (l2 < 1e-9) return (c - a).magnitude;
            double t = Math.Max(0d, Math.Min(1d, Vector3d.Dot(c - a, ab) / l2));
            return (a + ab * t - c).magnitude;
        }

        public static Vector3d Lerp(Vector3d a, Vector3d b, double t) => a + (b - a) * t;

        /// <summary>v with its component along n removed.</summary>
        public static Vector3d Exclude(Vector3d n, Vector3d v)
        {
            double n2 = n.sqrMagnitude;
            return n2 < 1e-18 ? v : v - n * (Vector3d.Dot(v, n) / n2);
        }

        /// <summary>Angle between two vectors in degrees.</summary>
        public static double AngleDeg(Vector3d a, Vector3d b)
        {
            double d = a.magnitude * b.magnitude;
            if (d < 1e-18) return 0d;
            return Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(a, b) / d))) * 180d / Math.PI;
        }
    }
}
