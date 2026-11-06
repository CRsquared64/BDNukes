using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>A point to strike: a body, a surface position and the height above the ground to aim for.</summary>
    internal struct StrikeTarget
    {
        public CelestialBody body;
        public double lat, lon;
        public double height;   // m above the terrain (or sea) at the point
        public string name;

        public bool Valid => body != null;

        public double GroundAltitude
        {
            get
            {
                double g = body.pqsController != null ? body.TerrainAltitude(lat, lon, true) : 0d;
                if (body.ocean && g < 0d) g = 0d;
                return g;
            }
        }

        public double Altitude => GroundAltitude + height;
        public Vector3d World => body.GetWorldSurfacePosition(lat, lon, Altitude);
        /// <summary>The aim point relative to the body's centre, in world axes.</summary>
        public Vector3d Rel => World - body.position;

        public override string ToString() => $"{name} ({NukeStrikeMath.LatLon(lat, lon)}, {body?.bodyName})";
    }

    /// <summary>Targeting maths shared by every strike weapon: great circles, the planets turning, Lambert arcs.</summary>
    internal static class NukeStrikeMath
    {
        public static string LatLon(double lat, double lon)
        {
            lon = ((lon + 540d) % 360d) - 180d;
            return $"{Math.Abs(lat):0.00}°{(lat >= 0 ? "N" : "S")} {Math.Abs(lon):0.00}°{(lon >= 0 ? "E" : "W")}";
        }

        public static string Distance(double m) =>
            m >= 1e9 ? $"{m / 1e9:0.0} Gm" : m >= 1e6 ? $"{m / 1e6:0.0} Mm" : m >= 1e4 ? $"{m / 1e3:0} km" : m >= 1e3 ? $"{m / 1e3:0.0} km" : $"{m:0} m";

        public static string Duration(double s)
        {
            if (double.IsNaN(s) || s < 0) return "-";
            if (s < 120) return $"{s:0} s";
            if (s < 7200) return $"{Math.Floor(s / 60):0} min {s % 60:00} s";
            return $"{Math.Floor(s / 3600):0} h {Math.Floor(s % 3600 / 60):00} min";
        }

        /// <summary>Angle (rad) between two surface points.</summary>
        public static double CentralAngle(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180d, p2 = lat2 * Math.PI / 180d, dl = (lon2 - lon1) * Math.PI / 180d;
            double a = Math.Sin((p2 - p1) / 2) * Math.Sin((p2 - p1) / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0d, 1d - a)));
        }

        /// <summary>The body's spin axis in world space (towards the north pole).</summary>
        public static Vector3d Axis(CelestialBody body) => (body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position).normalized;

        /// <summary>Where a point fixed to the body's surface (relative to its centre) will be after `seconds`.</summary>
        public static Vector3d RotateWithBody(CelestialBody body, Vector3d rel, double seconds)
        {
            if (!body.rotates || body.rotationPeriod <= 0d || seconds == 0d) return rel;
            Vector3d axis = Axis(body);
            double ang = 2d * Math.PI * (seconds / body.rotationPeriod);
            // turn the way the surface actually moves (the frame's handedness decides the sign)
            Vector3d surf = body.getRFrmVel(body.position + rel);
            Vector3d probe = Vector3d.Cross(axis, rel);
            if (Vector3d.Dot(probe, surf) < 0d) ang = -ang;
            return Rotate(rel, axis, ang);
        }

        /// <summary>Rodrigues' rotation of v about the unit axis k by ang (rad), right-handed in the vectors' own frame.</summary>
        public static Vector3d Rotate(Vector3d v, Vector3d k, double ang)
        {
            double c = Math.Cos(ang), s = Math.Sin(ang);
            return v * c + Vector3d.Cross(k, v) * s + k * Vector3d.Dot(k, v) * (1d - c);
        }

        /// <summary>
        /// Where a moon (or any body on an orbit) will be relative to its parent at UT, in world axes (KSP's orbit
        /// frame swizzled: the planetarium frame is kept turned to match the world's, rotating frame or not).
        /// </summary>
        public static Vector3d BodyRelAt(CelestialBody b, double ut) => b.orbit != null ? b.orbit.getRelativePositionAtUT(ut).xzy : Vector3d.zero;

        // ---------------------------------------------------------------------------------------------------
        // Lambert's problem (universal variables, zero revolutions): the conic from r1 to r2 in time tof.
        // ---------------------------------------------------------------------------------------------------

        static double StumpffC(double z)
        {
            if (z > 1e-6) return (1d - Math.Cos(Math.Sqrt(z))) / z;
            if (z < -1e-6) return (Math.Cosh(Math.Sqrt(-z)) - 1d) / -z;
            return 0.5d - z / 24d;
        }

        static double StumpffS(double z)
        {
            if (z > 1e-6) { double s = Math.Sqrt(z); return (s - Math.Sin(s)) / (s * s * s); }
            if (z < -1e-6) { double s = Math.Sqrt(-z); return (Math.Sinh(s) - s) / (s * s * s); }
            return 1d / 6d - z / 120d;
        }

        /// <summary>
        /// Solves for the velocities at r1 (departure) and r2 (arrival) of the arc taking tof seconds. longWay picks
        /// the transfer angle above 180°; the sense of travel is set by `normal` (the transfer's angular momentum
        /// points along it), so prograde shots stay prograde.
        /// </summary>
        public static bool Lambert(Vector3d r1, Vector3d r2, double tof, double mu, Vector3d normal, out Vector3d v1, out Vector3d v2)
        {
            v1 = v2 = Vector3d.zero;
            double r1n = r1.magnitude, r2n = r2.magnitude;
            if (r1n < 1 || r2n < 1 || tof <= 0 || mu <= 0) return false;
            double cosd = Math.Max(-1d, Math.Min(1d, Vector3d.Dot(r1, r2) / (r1n * r2n)));
            double dth = Math.Acos(cosd);
            if (Vector3d.Dot(Vector3d.Cross(r1, r2), normal) < 0d) dth = 2d * Math.PI - dth;
            if (Math.Abs(1d - cosd) < 1e-12) return false;
            double A = Math.Sin(dth) * Math.Sqrt(r1n * r2n / (1d - cosd));
            if (Math.Abs(A) < 1e-9) return false;

            double sqmu = Math.Sqrt(mu);
            Func<double, double> Y = z => r1n + r2n + A * (z * StumpffS(z) - 1d) / Math.Sqrt(StumpffC(z));
            Func<double, double> T = z =>
            {
                double y = Y(z);
                if (y < 0) return double.NegativeInfinity;
                double c = StumpffC(z);
                return (Math.Pow(y / c, 1.5) * StumpffS(z) + A * Math.Sqrt(y)) / sqmu;
            };

            // the time of flight rises with z: bracket and bisect
            double lo = -4d * Math.PI * Math.PI, hi = 4d * Math.PI * Math.PI * 0.9999;
            while (T(lo) > tof && lo > -1e6) lo *= 2d;          // faster than a parabola: deep hyperbolas
            if (T(lo) > tof || T(hi) < tof) return false;
            double z0 = 0;
            for (int i = 0; i < 100; i++)
            {
                z0 = 0.5d * (lo + hi);
                double t = T(z0);
                if (double.IsNegativeInfinity(t) || t < tof) lo = z0; else hi = z0;
                if (hi - lo < 1e-12) break;
            }
            double yy = Y(z0);
            if (yy <= 0) return false;
            double f = 1d - yy / r1n, g = A * Math.Sqrt(yy / mu), gd = 1d - yy / r2n;
            if (Math.Abs(g) < 1e-9) return false;
            v1 = (r2 - f * r1) / g;
            v2 = (gd * r2 - r1) / g;
            return !(double.IsNaN(v1.sqrMagnitude) || double.IsNaN(v2.sqrMagnitude)
                || double.IsInfinity(v1.sqrMagnitude) || double.IsInfinity(v2.sqrMagnitude));
        }

        /// <summary>Lowest point of the conic through (r, v): it must stay above minR until the arrival to be flyable.</summary>
        public static double Periapsis(Vector3d r, Vector3d v, double mu)
        {
            Vector3d h = Vector3d.Cross(r, v);
            double rn = r.magnitude;
            Vector3d e = Vector3d.Cross(v, h) / mu - r / rn;
            double p = h.sqrMagnitude / mu;
            return p / (1d + e.magnitude);
        }

        /// <summary>
        /// Best arc from a vessel's state to a surface target on the same body: scans the flight time for the
        /// smallest burn whose arc comes down onto the target (descending, without dipping below the ground first).
        /// The target is carried round by the body's spin over the flight.
        /// </summary>
        public static bool BestImpactArc(Vector3d r, Vector3d v, CelestialBody body, StrikeTarget t, double tMin, double tMax,
            out Vector3d dv, out double tof)
        {
            dv = Vector3d.zero; tof = 0;
            double mu = body.gravParameter;
            Vector3d tgtNow = t.Rel;
            Vector3d normal = Vector3d.Cross(r, v);
            if (normal.sqrMagnitude < 1) normal = Vector3d.Cross(r, tgtNow);
            double best = double.MaxValue;
            const int N = 60;
            for (int pass = 0; pass < 2; pass++)
            {
                double a = pass == 0 ? tMin : Math.Max(tMin, tof * 0.8), b = pass == 0 ? tMax : Math.Min(tMax, tof * 1.2);
                if (pass == 1 && best == double.MaxValue) break;
                for (int i = 0; i <= N; i++)
                {
                    double tt = a + (b - a) * i / N;
                    Vector3d r2 = RotateWithBody(body, tgtNow, tt);
                    foreach (bool flip in new[] { false, true })
                    {
                        if (!Lambert(r, r2, tt, mu, flip ? -normal : normal, out var v1, out var v2)) continue;
                        if (Vector3d.Dot(v2, r2) >= 0) continue;                    // must come down onto it
                        // a zero-revolution arc that arrives descending hasn't been below the target's radius before
                        double cost = (v1 - v).magnitude + (flip ? 50 : 0);
                        if (cost < best) { best = cost; dv = v1 - v; tof = tt; }
                    }
                }
            }
            return best < double.MaxValue;
        }
    }
}
