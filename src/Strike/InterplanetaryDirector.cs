using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BDArmory.Extensions;
using BDArmory.FX;
using UnityEngine;

namespace BDNukes
{
    /// <summary>One re-entry vehicle released by a Starlance bus.</summary>
    internal class InterplanetaryWarhead
    {
        public Guid id = Guid.Empty;         // the spawned RV vessel (Empty: simulated only)
        public double lat, lon;              // aim point on the target body
        public double burstUT;               // when the Kepler arc reaches the burst height over the aim point
        public string state = "flying";      // flying, done, lost
        public double dieAt = -1d;           // UT to remove the RV vessel after a loaded detonation
        // the planned arc's elements round the target body: inc, ecc, sma, LAN, argPe, mEp, epoch (null: none)
        public double[] plan;

        public Orbit PlanOrbit(CelestialBody body)
        {
            if (plan == null || plan.Length != 7 || body == null) return null;
            return new Orbit(plan[0], plan[1], plan[2], plan[3], plan[4], plan[5], plan[6], body);
        }

        public void SetPlan(Orbit o)
        {
            plan = o == null ? null : new[] { o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch, o.epoch };
        }

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("RV");
            n.AddValue("id", id.ToString());
            n.AddValue("lat", F(lat)); n.AddValue("lon", F(lon));
            n.AddValue("burstUT", F(burstUT));
            n.AddValue("state", state);
            n.AddValue("dieAt", F(dieAt));
            if (plan != null) n.AddValue("plan", string.Join(",", Array.ConvertAll(plan, F)));
            return n;
        }

        public static InterplanetaryWarhead Load(ConfigNode n)
        {
            var w = new InterplanetaryWarhead();
            string s = n.GetValue("id");
            if (!string.IsNullOrEmpty(s)) { try { w.id = new Guid(s); } catch { w.id = Guid.Empty; } }
            n.TryGetValue("lat", ref w.lat); n.TryGetValue("lon", ref w.lon);
            n.TryGetValue("burstUT", ref w.burstUT);
            w.state = n.GetValue("state") ?? "flying";
            n.TryGetValue("dieAt", ref w.dieAt);
            string ps = n.GetValue("plan");
            if (!string.IsNullOrEmpty(ps))
            {
                string[] parts = ps.Split(',');
                var el = new double[parts.Length];
                bool ok = parts.Length == 7;
                for (int i = 0; ok && i < parts.Length; i++)
                    ok = double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out el[i]);
                if (ok) w.plan = el;
            }
            return w;
        }
    }

    /// <summary>
    /// The persistent record of one Starlance strike from the moment its light drive starts charging. Everything
    /// that has to survive time warp, vessel switching and scene changes is in here, as UT timestamps and
    /// frame-free coordinates (see InterplanetaryMath), so the strike runs to completion whether or not the missile
    /// is loaded.
    /// </summary>
    internal class InterplanetaryFlight
    {
        public Guid vesselId;
        public string vesselName = "Starlance";
        // Charging -> Transit -> Arrived -> Released -> Done (or Failed)
        public string state = "Charging";
        public double chargeEndUT, departUT, arrivalUT, releaseUT;
        public bool waitingForWindow;

        public string targetBody; public double tLat, tLon, tHeight; public string tName = "target";
        public string departBody; public double dLat, dLon, dAlt;
        public bool useWaypoint; public double wLat, wLon, wAlt;   // body-fixed on the target body at arrivalUT
        public double arrivalAlt;                                  // m above the target body's datum

        public double speed = 1.8e7;          // m/s
        public int warheads = 10;
        public double spreadDeg = 30d;
        public float yield = 5000f, thermalRadius = 11100f, fluence = 5000f, burstHeight = 3000f;
        public double deorbitTime = 600d, releaseDelay = 20d;
        public string rvPart = "bdnukesStarlanceRV";
        public bool autoFollow = true;
        public bool followed;
        public readonly List<InterplanetaryWarhead> rvs = new List<InterplanetaryWarhead>();

        public CelestialBody Target => FlightGlobals.GetBodyByName(targetBody);
        public CelestialBody Depart => FlightGlobals.GetBodyByName(departBody);

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("STARLANCE");
            n.AddValue("vesselId", vesselId.ToString());
            n.AddValue("vesselName", vesselName);
            n.AddValue("state", state);
            n.AddValue("chargeEndUT", F(chargeEndUT)); n.AddValue("departUT", F(departUT));
            n.AddValue("arrivalUT", F(arrivalUT)); n.AddValue("releaseUT", F(releaseUT));
            n.AddValue("targetBody", targetBody ?? "");
            n.AddValue("tLat", F(tLat)); n.AddValue("tLon", F(tLon)); n.AddValue("tHeight", F(tHeight));
            n.AddValue("tName", tName ?? "");
            n.AddValue("departBody", departBody ?? "");
            n.AddValue("dLat", F(dLat)); n.AddValue("dLon", F(dLon)); n.AddValue("dAlt", F(dAlt));
            n.AddValue("useWaypoint", useWaypoint);
            n.AddValue("wLat", F(wLat)); n.AddValue("wLon", F(wLon)); n.AddValue("wAlt", F(wAlt));
            n.AddValue("arrivalAlt", F(arrivalAlt));
            n.AddValue("speed", F(speed));
            n.AddValue("warheads", warheads);
            n.AddValue("spreadDeg", F(spreadDeg));
            n.AddValue("yield", F(yield)); n.AddValue("thermalRadius", F(thermalRadius));
            n.AddValue("fluence", F(fluence)); n.AddValue("burstHeight", F(burstHeight));
            n.AddValue("deorbitTime", F(deorbitTime)); n.AddValue("releaseDelay", F(releaseDelay));
            n.AddValue("rvPart", rvPart);
            n.AddValue("autoFollow", autoFollow);
            n.AddValue("followed", followed);
            n.AddValue("waitingForWindow", waitingForWindow);
            foreach (var w in rvs) n.AddNode(w.Save());
            return n;
        }

        public static InterplanetaryFlight Load(ConfigNode n)
        {
            var f = new InterplanetaryFlight();
            try { f.vesselId = new Guid(n.GetValue("vesselId")); } catch { return null; }
            f.vesselName = n.GetValue("vesselName") ?? f.vesselName;
            f.state = n.GetValue("state") ?? "Failed";
            n.TryGetValue("chargeEndUT", ref f.chargeEndUT); n.TryGetValue("departUT", ref f.departUT);
            n.TryGetValue("arrivalUT", ref f.arrivalUT); n.TryGetValue("releaseUT", ref f.releaseUT);
            f.targetBody = n.GetValue("targetBody");
            n.TryGetValue("tLat", ref f.tLat); n.TryGetValue("tLon", ref f.tLon); n.TryGetValue("tHeight", ref f.tHeight);
            f.tName = n.GetValue("tName") ?? "target";
            f.departBody = n.GetValue("departBody");
            n.TryGetValue("dLat", ref f.dLat); n.TryGetValue("dLon", ref f.dLon); n.TryGetValue("dAlt", ref f.dAlt);
            n.TryGetValue("useWaypoint", ref f.useWaypoint);
            n.TryGetValue("wLat", ref f.wLat); n.TryGetValue("wLon", ref f.wLon); n.TryGetValue("wAlt", ref f.wAlt);
            n.TryGetValue("arrivalAlt", ref f.arrivalAlt);
            n.TryGetValue("speed", ref f.speed);
            n.TryGetValue("warheads", ref f.warheads);
            n.TryGetValue("spreadDeg", ref f.spreadDeg);
            n.TryGetValue("yield", ref f.yield); n.TryGetValue("thermalRadius", ref f.thermalRadius);
            n.TryGetValue("fluence", ref f.fluence); n.TryGetValue("burstHeight", ref f.burstHeight);
            n.TryGetValue("deorbitTime", ref f.deorbitTime); n.TryGetValue("releaseDelay", ref f.releaseDelay);
            f.rvPart = n.GetValue("rvPart") ?? f.rvPart;
            n.TryGetValue("autoFollow", ref f.autoFollow);
            n.TryGetValue("followed", ref f.followed);
            n.TryGetValue("waitingForWindow", ref f.waitingForWindow);
            foreach (var rn in n.GetNodes("RV")) f.rvs.Add(InterplanetaryWarhead.Load(rn));
            return f;
        }
    }

    /// <summary>
    /// Runs Starlance strikes after the ascent: the light-drive charge, the near-light transit, the arrival in a high
    /// orbit of the target body, the release of the re-entry vehicles and their detonations.
    ///
    /// Why a scenario module and not the part module: the missile is very often not loaded (the player stays with the
    /// launch site, switches vessels, goes to the tracking station) and an unloaded vessel's PartModules don't run.
    /// The scenario runs in the flight scene, the tracking station and the space centre, saves its records in the
    /// game, and moves the missile through its Vessel's orbit (loaded or not) or, failing that, its ProtoVessel's
    /// orbit snapshot. Every step is keyed to universal time, so warping through it, or coming back to the game
    /// hours later, gives the same result.
    ///
    /// The transit is scripted, not flown: the missile is put on rails and, every frame, placed on a straight
    /// line (in the Sun's frame) from where it left its parking orbit to its arrival point above the target, at
    /// `speed`, as an orbit around whichever body's sphere of influence it is in. At 18,000 km/s the Sun's pull bends
    /// that line by well under 0.01%, so a straight line is what a hyperbola would look like anyway. If the line
    /// would cross the target planet (target point on its far side), it goes via a waypoint beside the planet. The
    /// departure waits (still "charging") for the parking orbit to bring the missile round to the side facing the
    /// target, so the line never starts through the departure planet. Bodies passed on the way (including the Sun)
    /// are not avoided.
    ///
    /// Warheads: each RV is a real vessel (part bdnukesStarlanceRV) spawned on its own Kepler arc from the bus to
    /// its aim point, so the map shows them and the camera can follow one. Their detonation is decided here, not by
    /// a fuze: a loaded RV goes off at the burst height above the ground (or on contact, or late), through BDArmory's
    /// NukeFX.CreateExplosion with the RV part (so damage, BDNukes' cinematic, craters and so on all happen as for
    /// any warhead); an RV still on rails when its arc reaches the burst height (the player is elsewhere) goes off
    /// at its aim point: with the full explosion if the active vessel is near enough on that body to see it, else as
    /// a "remote" detonation that leaves its crater and a persistent cloud (rebuilt when you visit) but does no
    /// BDArmory damage (BDArmory only damages loaded vessels anyway). An RV that vanishes early (shot down) is lost;
    /// one that vanishes in its last minutes (KSP deletes unloaded vessels deep in an atmosphere) still detonates.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.TRACKSTATION, GameScenes.SPACECENTER)]
    public class InterplanetaryDirector : ScenarioModule
    {
        internal static InterplanetaryDirector Instance;
        internal readonly List<InterplanetaryFlight> flights = new List<InterplanetaryFlight>();

        /// <summary>Range from the active vessel within which an RV on rails still gets a full explosion.</summary>
        const double VisibleRange = 2.5e6;

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnLoad(ConfigNode node)
        {
            flights.Clear();
            foreach (var n in node.GetNodes("STARLANCE"))
            {
                try
                {
                    var f = InterplanetaryFlight.Load(n);
                    if (f != null && f.state != "Done" && f.state != "Failed") flights.Add(f);
                }
                catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't load a Starlance record: " + e.Message); }
            }
        }

        public override void OnSave(ConfigNode node)
        {
            foreach (var f in flights)
                if (f.state != "Done" && f.state != "Failed") node.AddNode(f.Save());
        }

        // ---------------------------------------------------------------------------------------------------
        // Interface for ModuleNukeInterplanetary and the planner
        // ---------------------------------------------------------------------------------------------------

        internal static InterplanetaryFlight Find(Guid vesselId)
        {
            if (Instance == null) return null;
            foreach (var f in Instance.flights) if (f.vesselId == vesselId) return f;
            return null;
        }

        internal static bool Begin(InterplanetaryFlight f)
        {
            if (Instance == null)
            {
                Debug.LogError("[BDNukes]: Starlance: no director in this scene; the strike can't start.");
                return false;
            }
            Instance.flights.RemoveAll(x => x.vesselId == f.vesselId);
            Instance.flights.Add(f);
            Debug.Log($"[BDNukes]: Starlance {f.vesselName}: light drive charging for {f.tName} on {f.targetBody}.");
            return true;
        }

        internal static Vessel Warhead(Guid busId, int i)
        {
            var f = Find(busId);
            if (f == null || i < 0 || i >= f.rvs.Count || f.rvs[i].id == Guid.Empty) return null;
            return FlightGlobals.FindVessel(f.rvs[i].id);
        }

        // ---------------------------------------------------------------------------------------------------
        // The state machine
        // ---------------------------------------------------------------------------------------------------

        void Update()
        {
            if (Planetarium.fetch == null || FlightGlobals.Bodies == null || flights.Count == 0) return;
            if (HighLogic.LoadedSceneIsFlight && !FlightGlobals.ready) return;
            double now = Planetarium.GetUniversalTime();
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                var f = flights[i];
                try { Tick(f, now); }
                catch (Exception e)
                {
                    Debug.LogError($"[BDNukes]: Starlance {f.vesselName} failed in state {f.state}: {e}");
                    f.state = "Failed";
                }
                if (f.state == "Done" || f.state == "Failed") flights.RemoveAt(i);
            }
        }

        void Tick(InterplanetaryFlight f, double now)
        {
            CelestialBody target = f.Target;
            if (target == null) { f.state = "Failed"; return; }
            switch (f.state)
            {
                case "Charging":
                    if (!VesselExists(f.vesselId)) { Fail(f, "the missile is gone"); return; }
                    if (now < f.chargeEndUT) return;
                    StartTransit(f, now);
                    return;

                case "Transit":
                    if (!VesselExists(f.vesselId)) { Fail(f, "the missile is gone"); return; }
                    if (now >= f.arrivalUT) Arrive(f, now);
                    else MoveAlongPath(f, now);
                    return;

                case "Arrived":
                    if (!VesselExists(f.vesselId)) { Fail(f, "the missile is gone"); return; }
                    if (now < f.releaseUT) return;
                    // spawning vessels and Lambert arcs need the bodies placed: not from the space centre
                    if (HighLogic.LoadedScene == GameScenes.SPACECENTER) return;
                    Release(f, now);
                    return;

                case "Released":
                    TrackWarheads(f, now);
                    return;
            }
        }

        static void Fail(InterplanetaryFlight f, string why)
        {
            Debug.LogWarning($"[BDNukes]: Starlance {f.vesselName} aborted ({f.state}): {why}.");
            if (HighLogic.LoadedSceneIsFlight) ScreenMessages.PostScreenMessage($"Starlance aborted: {why}", 5f, ScreenMessageStyle.UPPER_CENTER);
            f.state = "Failed";
        }

        // ---- transit ----

        void StartTransit(InterplanetaryFlight f, double now)
        {
            CelestialBody target = f.Target;
            if (!GetState(f.vesselId, now, out CelestialBody body, out Vector3d rZup, out Vector3d vZup)) { Fail(f, "the missile is gone"); return; }

            // wait for the parking orbit to face the target, so the line doesn't start through the planet
            Vector3d sunHere = InterplanetaryMath.SunPos(body, now) + rZup;
            Vector3d toTarget = InterplanetaryMath.SunPos(target, now) - sunHere;
            if (body != target && toTarget.magnitude > 1d)
            {
                double facing = Vector3d.Dot(rZup.normalized, toTarget.normalized);
                if (facing < 0.05d)
                {
                    if (!f.waitingForWindow)
                    {
                        f.waitingForWindow = true;
                        Message($"Starlance: light drive charged, waiting for the jump window over {body.bodyName}");
                    }
                    return;
                }
            }

            Vector3d worldRel = rZup.xzy;
            f.departBody = body.bodyName;
            f.dLat = body.GetLatitude(worldRel, true);
            f.dLon = body.GetLongitude(worldRel, true);
            f.dAlt = worldRel.magnitude - body.Radius;
            f.departUT = now;

            // arrival time: distance / speed, iterated as the target moves (and with the waypoint if needed)
            double T = Math.Max(1d, toTarget.magnitude / f.speed);
            f.useWaypoint = false;
            for (int it = 0; it < 6; it++)
            {
                f.arrivalUT = now + T;
                if (it == 3) PlanWaypoint(f, now);
                double L = PathLength(f, now);
                T = Math.Max(1d, L / f.speed);
            }
            f.arrivalUT = now + T;
            f.state = "Transit";
            f.waitingForWindow = false;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BDNukes]: Starlance {0}: jump from {1} to {2}, {3:F0} km/s, {4:F0} s{5}.",
                f.vesselName, f.departBody, f.targetBody, f.speed / 1000d, T, f.useWaypoint ? " (via a waypoint beside the target)" : ""));
            Message($"Starlance: JUMP. {NukeStrikeMath.Duration(T)} to {target.bodyName}");
            OnLoadedModule(f, m => m.OnJump());
            MoveAlongPath(f, now);
        }

        /// <summary>The path's corner points in the current frame (zup, Sun-relative).</summary>
        static List<Vector3d> Path(InterplanetaryFlight f, double now)
        {
            var pts = new List<Vector3d>(3);
            CelestialBody dep = f.Depart, tgt = f.Target;
            pts.Add(InterplanetaryMath.SunPos(dep, f.departUT) + InterplanetaryMath.SurfaceRelAt(dep, f.dLat, f.dLon, f.dAlt, f.departUT, now).xzy);
            Vector3d tc = InterplanetaryMath.SunPos(tgt, f.arrivalUT);
            if (f.useWaypoint) pts.Add(tc + InterplanetaryMath.SurfaceRelAt(tgt, f.wLat, f.wLon, f.wAlt, f.arrivalUT, now).xzy);
            pts.Add(tc + InterplanetaryMath.SurfaceRelAt(tgt, f.tLat, f.tLon, f.arrivalAlt, f.arrivalUT, now).xzy);
            return pts;
        }

        static double PathLength(InterplanetaryFlight f, double now)
        {
            var p = Path(f, now);
            double L = 0;
            for (int i = 1; i < p.Count; i++) L += (p[i] - p[i - 1]).magnitude;
            return L;
        }

        /// <summary>If the straight line would pass through the target planet, go via a point beside it.</summary>
        static void PlanWaypoint(InterplanetaryFlight f, double now)
        {
            f.useWaypoint = false;
            CelestialBody tgt = f.Target;
            var p = Path(f, now);
            Vector3d D = p[0], A = p[p.Count - 1];
            Vector3d C = InterplanetaryMath.SunPos(tgt, f.arrivalUT);
            if (InterplanetaryMath.SegmentDistance(D, A, C) > 1.3d * tgt.Radius + (tgt.atmosphere ? tgt.atmosphereDepth : 0d)) return;
            Vector3d u = (A - D).normalized;
            Vector3d aRel = A - C;
            Vector3d perp = aRel - u * Vector3d.Dot(aRel, u);
            if (perp.sqrMagnitude < 1d) perp = Vector3d.Cross(u, Math.Abs(u.z) < 0.9 ? new Vector3d(0, 0, 1) : new Vector3d(1, 0, 0));
            Vector3d wRel = perp.normalized * aRel.magnitude;
            InterplanetaryMath.BodyFixed(tgt, wRel.xzy, f.arrivalUT, now, out f.wLat, out f.wLon, out f.wAlt);
            f.useWaypoint = true;
        }

        void MoveAlongPath(InterplanetaryFlight f, double now)
        {
            var p = Path(f, now);
            double total = 0;
            var seg = new double[p.Count];
            for (int i = 1; i < p.Count; i++) { seg[i] = (p[i] - p[i - 1]).magnitude; total += seg[i]; }
            double T = Math.Max(1d, f.arrivalUT - f.departUT);
            double s = Math.Max(0d, Math.Min(1d, (now - f.departUT) / T)) * total;
            double v = total / T;
            Vector3d pos = p[p.Count - 1], dir = Vector3d.zero;
            for (int i = 1; i < p.Count; i++)
            {
                dir = seg[i] > 1e-6 ? (p[i] - p[i - 1]) / seg[i] : Vector3d.zero;
                if (s <= seg[i] || i == p.Count - 1) { pos = p[i - 1] + dir * Math.Min(s, seg[i]); break; }
                s -= seg[i];
            }
            CelestialBody soi = InterplanetaryMath.DominantBody(pos, now);
            if (soi == null) return;
            Vector3d rel = pos - InterplanetaryMath.SunPos(soi, now);
            Vector3d vel = dir * v - InterplanetaryMath.SunVel(soi, now);
            // never inside a body (start/end rounding, a moon on the way)
            double minR = soi.Radius + (soi.atmosphere ? soi.atmosphereDepth : 0d) + 5000d;
            if (rel.magnitude < minR) rel = rel.normalized * minR;
            // The position is set here every frame, so the orbit's speed only has to point the right way. At the real
            // drive speed (a large fraction of c on a real-scale system) the hyperbola's semi-major axis is about a
            // centimetre, KSP's propagation of it loses all precision, puts the craft under the ground and its terrain
            // check destroys it the moment it jumps. Ten times escape speed keeps the orbit well-conditioned.
            double cap = 10d * Math.Sqrt(2d * soi.gravParameter / rel.magnitude);
            if (vel.magnitude > cap) vel = vel.normalized * cap;
            SetOrbit(f.vesselId, soi, rel, vel, now, true);
        }

        void Arrive(InterplanetaryFlight f, double now)
        {
            CelestialBody tgt = f.Target;
            Vector3d rel = tgt.GetRelSurfacePosition(f.tLat, f.tLon, f.arrivalAlt);
            Vector3d east = tgt.getRFrmVel(tgt.position + rel);
            if (east.sqrMagnitude < 1e-6)
                east = Vector3d.Cross(NukeStrikeMath.Axis(tgt), rel);
            if (east.sqrMagnitude < 1e-6) east = Vector3d.Cross(rel, new Vector3d(1, 0, 0));
            east = InterplanetaryMath.Exclude(rel, east).normalized;
            Vector3d vel = east * InterplanetaryMath.Circular(tgt, rel.magnitude);
            SetOrbit(f.vesselId, tgt, rel.xzy, vel.xzy, now, false);
            f.state = "Arrived";
            f.releaseUT = now + Math.Max(1d, f.releaseDelay);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Starlance {0}: arrived in a {1:F0} km orbit of {2} over {3}.",
                f.vesselName, f.arrivalAlt / 1000d, tgt.bodyName, f.tName));
            Message($"Starlance: arrived at {tgt.bodyName}. Warhead release in {f.releaseDelay:0} s");
            OnLoadedModule(f, m => m.OnArrive());
        }

        // ---- release ----

        void Release(InterplanetaryFlight f, double now)
        {
            CelestialBody tgt = f.Target;
            if (!GetState(f.vesselId, now, out CelestialBody body, out Vector3d rZup, out Vector3d vZup) || body != tgt)
            {
                Fail(f, "the bus isn't in orbit of " + tgt.bodyName);
                return;
            }
            Vector3d r = rZup.xzy, v = vZup.xzy;   // world axes, this frame
            var aims = InterplanetaryMath.AimPattern(f.tLat, f.tLon, f.warheads, f.spreadDeg, UnityEngine.Random.Range(0f, 360f));

            // spawn on a ring round the bus, across its path, so they don't unpack inside each other
            Vector3d e1 = Vector3d.Cross(v, r);
            if (e1.sqrMagnitude < 1e-6) e1 = Vector3d.Cross(r, new Vector3d(1, 0, 0));
            e1 = e1.normalized;
            Vector3d e2 = Vector3d.Cross(v.normalized, e1).normalized;

            double half = Math.PI * Math.Sqrt(Math.Pow((r.magnitude + tgt.Radius) / 2d, 3d) / tgt.gravParameter);
            double tMax = Math.Max(120d, Math.Min(half * 1.3d, f.deorbitTime * 1.6d));
            double tMin = Math.Max(60d, Math.Min(f.deorbitTime * 0.5d, tMax * 0.5d));

            AvailablePart ap = PartLoader.getPartInfoByName(f.rvPart);
            if (ap == null) Debug.LogWarning($"[BDNukes]: Starlance: RV part {f.rvPart} not found; the warheads are simulated without vessels.");

            int spawned = 0;
            for (int i = 0; i < aims.Length; i++)
            {
                double ang = 2d * Math.PI * i / aims.Length;
                Vector3d ri = r + (e1 * Math.Cos(ang) + e2 * Math.Sin(ang)) * 40d;
                var st = new StrikeTarget { body = tgt, lat = aims[i].x, lon = aims[i].y, height = f.burstHeight, name = f.tName + " " + (i + 1) };
                bool ok = NukeStrikeMath.BestImpactArc(ri, v, tgt, st, tMin, tMax, out Vector3d dv, out double tof)
                       || NukeStrikeMath.BestImpactArc(ri, v, tgt, st, 30d, Math.Max(tMax, half * 2d), out dv, out tof);
                var w = new InterplanetaryWarhead { lat = aims[i].x, lon = aims[i].y };
                if (!ok)
                {
                    w.state = "lost";
                    Debug.LogWarning($"[BDNukes]: Starlance: no arc found for warhead {i + 1}; it's not released.");
                    f.rvs.Add(w);
                    continue;
                }
                w.burstUT = now + tof;
                if (ap != null)
                {
                    try
                    {
                        var o = new Orbit();
                        o.UpdateFromStateVectors(ri.xzy, (v + dv).xzy, tgt, now);
                        w.SetPlan(o);
                        w.id = SpawnRV(f, ap, o, i);
                        spawned++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[BDNukes]: Starlance: couldn't spawn RV {i + 1} ({e.Message}); simulating it.");
                        w.id = Guid.Empty;
                    }
                }
                f.rvs.Add(w);
            }
            f.state = "Released";
            Debug.Log($"[BDNukes]: Starlance {f.vesselName}: released {f.rvs.Count} warheads ({spawned} as vessels) over {f.tName}.");
            Message($"Starlance: {f.rvs.Count} warheads released over {f.tName}");
            OnLoadedModule(f, m => m.OnRelease());
        }

        static Guid SpawnRV(InterplanetaryFlight f, AvailablePart ap, Orbit o, int i)
        {
            uint fid = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
            ConfigNode partNode = ProtoVessel.CreatePartNode(ap.name, fid);
            ConfigNode disc = ProtoVessel.CreateDiscoveryNode(DiscoveryLevels.Owned, UntrackedObjectClass.A, double.PositiveInfinity, double.PositiveInfinity);
            ConfigNode vn = ProtoVessel.CreateVesselNode($"{f.vesselName} RV {i + 1}", VesselType.Probe, o, 0, new[] { partNode }, disc);
            ProtoVessel pv = HighLogic.CurrentGame.AddVessel(vn);
            return pv.vesselID;
        }

        // ---- warheads ----

        void TrackWarheads(InterplanetaryFlight f, double now)
        {
            CelestialBody tgt = f.Target;
            bool any = false;
            for (int i = 0; i < f.rvs.Count; i++)
            {
                var w = f.rvs[i];
                if (w.state == "done" && w.dieAt > 0d)
                {
                    if (now >= w.dieAt || Time.timeScale == 0f) { KillVessel(w.id); w.dieAt = -1d; }
                    else any = true;
                    continue;
                }
                if (w.state != "flying") continue;
                any = true;
                Vessel rv = w.id != Guid.Empty ? FlightGlobals.FindVessel(w.id) : null;
                bool exists = rv != null || (w.id != Guid.Empty && FindProto(w.id) != null);

                // the camera rides the first warhead down if the bus was being watched
                if (f.autoFollow && !f.followed && i == 0 && rv != null && rv.loaded && HighLogic.LoadedSceneIsFlight)
                {
                    f.followed = true;
                    var bus = FlightGlobals.FindVessel(f.vesselId);
                    if (bus != null && FlightGlobals.ActiveVessel == bus)
                    {
                        try { FlightGlobals.ForceSetActiveVessel(rv); }
                        catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance: couldn't follow the warhead: " + e.Message); }
                    }
                }

                if (rv != null) KeepOnPlan(w, rv, tgt, now);

                if (rv != null && rv.loaded && !rv.packed && HighLogic.LoadedSceneIsFlight)
                {
                    double ground = Ground(tgt, rv.latitude, rv.longitude);
                    double agl = rv.altitude - ground;
                    bool down = rv.verticalSpeed < 0d;
                    double step = Math.Max(0, -rv.verticalSpeed) * TimeWarp.fixedDeltaTime;
                    if ((down && agl <= f.burstHeight + step) || agl < 50d + step || rv.Landed || rv.Splashed || now > w.burstUT + 60d)
                    {
                        if (DetonateLoaded(f, w, rv))
                        {
                            w.state = "done";
                            w.dieAt = now + 1.5d;
                        }
                    }
                    continue;
                }

                if (!exists && w.id != Guid.Empty && now < w.burstUT - 300d)
                {
                    w.state = "lost";
                    Debug.Log($"[BDNukes]: Starlance: RV {i + 1} was destroyed before re-entry.");
                    continue;
                }
                if (now < w.burstUT) continue;
                DetonateAtAim(f, w, tgt);
                w.state = "done";
                KillVessel(w.id);
            }
            if (!any) f.state = "Done";
        }

        /// <summary>
        /// Puts an RV back on its planned arc if it has drifted off it (unpacking, Krakensbane, collisions with the
        /// other RVs). Only well before the burst and well above the air, so drag and the terminal fall are left alone.
        /// </summary>
        static void KeepOnPlan(InterplanetaryWarhead w, Vessel rv, CelestialBody tgt, double now)
        {
            if (w.plan == null || rv == null || rv.orbit == null || tgt == null) return;
            if (now > w.burstUT - 45d) return;
            if (rv.mainBody != tgt || rv.altitude < tgt.atmosphereDepth + 20000d) return;
            Orbit plan;
            try { plan = w.PlanOrbit(tgt); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance: bad planned orbit: " + e.Message); w.plan = null; return; }
            if (plan == null) return;
            Vector3d rp = plan.getRelativePositionAtUT(now), vp = plan.getOrbitalVelocityAtUT(now);
            Vector3d r = rv.orbit.getRelativePositionAtUT(now), v = rv.orbit.getOrbitalVelocityAtUT(now);
            if ((r - rp).magnitude <= 2000d && (v - vp).magnitude <= 5d) return;
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Starlance: {0} was {1:0} m / {2:0.0} m/s off its arc; put back on it.",
                rv.vesselName, (r - rp).magnitude, (v - vp).magnitude));
            SetOrbit(rv.id, tgt, rp, vp, now, true);
        }

        bool DetonateLoaded(InterplanetaryFlight f, InterplanetaryWarhead w, Vessel rv)
        {
            Part p = rv.rootPart;
            if (p == null) return false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (PartModule module in p.Modules)
            {
                if (module.moduleName != "BDModuleNuke") continue;
                var type = module.GetType();
                try
                {
                    type.GetField("Sourcevessel", flags)?.SetValue(module, f.vesselName);
                    type.GetField("yield", flags)?.SetValue(module, f.yield);
                    type.GetField("thermalRadius", flags)?.SetValue(module, f.thermalRadius);
                    type.GetField("fluence", flags)?.SetValue(module, f.fluence);
                    var method = type.GetMethod("Detonate", flags);
                    if (method == null) return false;
                    method.Invoke(module, null);
                    return type.GetField("hasDetonated", flags)?.GetValue(module) is bool done && done;
                }
                catch (Exception e) { Debug.LogError("[BDNukes]: Starlance BDModuleNuke detonation failed: " + e); return false; }
            }
            return false;
        }

        void DetonateAtAim(InterplanetaryFlight f, InterplanetaryWarhead w, CelestialBody tgt)
        {
            double ground = Ground(tgt, w.lat, w.lon);
            double alt = ground + f.burstHeight;
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.currentMainBody == tgt && FlightGlobals.ActiveVessel != null)
            {
                Vector3d world = tgt.GetWorldSurfacePosition(w.lat, w.lon, alt);
                if ((world - FlightGlobals.ActiveVessel.CoMD).magnitude < VisibleRange)
                {
                    Explode(f, world, null);
                    return;
                }
            }
            RemoteDetonation(f, tgt, w.lat, w.lon, ground, alt);
        }

        static void Explode(InterplanetaryFlight f, Vector3 pos, Part p)
        {
            try
            {
                NukeFX.CreateExplosion(pos, ExplosionSourceType.Missile, f.vesselName, "Starlance RV", 0f,
                    f.thermalRadius, f.yield, f.fluence, true,
                    "BDArmory/Models/explosion/nuke/nukeBoom", "BDArmory/Models/explosion/nuke/nukeFlash",
                    "BDArmory/Models/explosion/nuke/nukeShock", "BDArmory/Models/explosion/nuke/nukeBlast",
                    "BDArmory/Models/explosion/nuke/nukePlume", "BDArmory/Models/explosion/nuke/nukeScatter",
                    "", "", p, null, Vector3.zero, true);
            }
            catch (Exception e) { Debug.LogError("[BDNukes]: Starlance warhead detonation failed: " + e); }
        }

        /// <summary>
        /// A detonation nobody is near: the crater and a persistent cloud record (rebuilt at its age when a vessel
        /// comes to this body), and the system-wide alarm if we're in flight. No BDArmory damage (nothing loaded).
        /// </summary>
        static void RemoteDetonation(InterplanetaryFlight f, CelestialBody body, double lat, double lon, double ground, double alt)
        {
            var s = NukeSettings.Current;
            float W = f.yield;
            float R = 70f * Mathf.Pow(W, 0.4f) * s.sizeScale;
            float hb = (float)(alt - ground);
            bool overOcean = body.ocean && body.pqsController != null && body.TerrainAltitude(lat, lon, true) < 0d;
            double now = Planetarium.GetUniversalTime();
            try
            {
                if (!overOcean)
                {
                    var crater = NukeCraters.Plan(body, lat, lon, W, R, hb);
                    if (crater != null) NukeCraters.Add(crater, 0.15f);
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance remote crater failed: " + e.Message); }
            try
            {
                if (s.enabled && s.persistentClouds)
                {
                    float yts = Mathf.Max(1f, Mathf.Pow(W / 20f, 0.12f));
                    NukePersistence.Add(new NukeRecord
                    {
                        body = body.bodyName, lat = lat, lon = lon, alt = alt,
                        ut = now, endUT = now + s.cloudLifetime * yts,
                        W = W, bulb = UnityEngine.Random.Range(0.35f, 0.85f), windHeading = UnityEngine.Random.Range(0f, 360f),
                        windSeed = UnityEngine.Random.Range(0, 100000),
                        hasGround = true, groundAlt = ground, overOcean = overOcean,
                    });
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance remote cloud record failed: " + e.Message); }
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready)
            {
                try
                {
                    Vector3 pos = body.GetWorldSurfacePosition(lat, lon, alt);
                    NukeRadiationWave.OnDetonation(body, pos, W, 0f);
                    NukeAlert.OnDetonation(body, pos, W, 0f);
                }
                catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance remote alert failed: " + e.Message); }
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Starlance: {0:0} kt detonation on {1} at {2:F2},{3:F2} (remote: no vessel near).",
                W, body.bodyName, lat, lon));
        }

        static double Ground(CelestialBody b, double lat, double lon)
        {
            double g = b.pqsController != null ? b.TerrainAltitude(lat, lon, true) : 0d;
            if (b.ocean && g < 0d) g = 0d;
            return g;
        }

        // ---------------------------------------------------------------------------------------------------
        // Vessels, loaded or not
        // ---------------------------------------------------------------------------------------------------

        static ProtoVessel FindProto(Guid id)
        {
            var fs = HighLogic.CurrentGame?.flightState;
            if (fs == null) return null;
            foreach (var pv in fs.protoVessels) if (pv != null && pv.vesselID == id) return pv;
            return null;
        }

        static bool VesselExists(Guid id) => FlightGlobals.FindVessel(id) != null || FindProto(id) != null;

        /// <summary>The vessel's orbital state now (orbit frame), from its Vessel or its ProtoVessel's snapshot.</summary>
        static bool GetState(Guid id, double now, out CelestialBody body, out Vector3d r, out Vector3d v)
        {
            body = null; r = v = Vector3d.zero;
            Orbit o = null;
            Vessel vs = FlightGlobals.FindVessel(id);
            if (vs != null && vs.orbit != null) o = vs.orbit;
            else
            {
                var pv = FindProto(id);
                if (pv?.orbitSnapShot != null) o = pv.orbitSnapShot.Load();
            }
            if (o == null || o.referenceBody == null) return false;
            body = o.referenceBody;
            r = o.getRelativePositionAtUT(now);
            v = o.getOrbitalVelocityAtUT(now);
            return true;
        }

        /// <summary>
        /// Puts a vessel on the orbit through (r, v) (orbit frame) around body at UT. A loaded vessel is packed
        /// first (and, while `hold`, kept packed: the light drive can't be flown in physics); an unloaded one is
        /// moved through its orbit driver, or its ProtoVessel's snapshot outside the flight scene.
        /// </summary>
        static void SetOrbit(Guid id, CelestialBody body, Vector3d r, Vector3d v, double now, bool hold)
        {
            Vessel vs = FlightGlobals.FindVessel(id);
            if (vs != null && vs.orbitDriver != null)
            {
                if (vs.loaded)
                {
                    if (hold) OrbitPhysicsManager.HoldVesselUnpack(2);
                    if (!vs.packed) vs.GoOnRails();
                }
                CelestialBody from = vs.orbitDriver.orbit != null ? vs.orbitDriver.orbit.referenceBody : vs.mainBody;
                vs.orbitDriver.orbit.UpdateFromStateVectors(r, v, body, now);
                vs.orbitDriver.updateFromParameters();
                if (from != body && from != null)
                {
                    try { GameEvents.onVesselSOIChanged.Fire(new GameEvents.HostedFromToAction<Vessel, CelestialBody>(vs, from, body)); }
                    catch (Exception e) { Debug.LogWarning("[BDNukes]: SOI change event failed: " + e.Message); }
                }
                return;
            }
            var pv = FindProto(id);
            if (pv != null)
            {
                var o = new Orbit();
                o.UpdateFromStateVectors(r, v, body, now);
                pv.orbitSnapShot = new OrbitSnapshot(o);
            }
        }

        static void KillVessel(Guid id)
        {
            if (id == Guid.Empty) return;
            try
            {
                Vessel vs = FlightGlobals.FindVessel(id);
                if (vs != null) { vs.Die(); return; }
                var pv = FindProto(id);
                if (pv != null) HighLogic.CurrentGame.flightState.protoVessels.Remove(pv);
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't remove a spent RV: " + e.Message); }
        }

        static void OnLoadedModule(InterplanetaryFlight f, Action<ModuleNukeInterplanetary> a)
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            Vessel vs = FlightGlobals.FindVessel(f.vesselId);
            if (vs == null || !vs.loaded) return;
            foreach (var p in vs.parts)
            {
                var m = p.FindModuleImplementing<ModuleNukeInterplanetary>();
                if (m == null) continue;
                try { a(m); } catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance effect failed: " + e.Message); }
            }
        }

        static void Message(string s)
        {
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                ScreenMessages.PostScreenMessage(s, 5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
