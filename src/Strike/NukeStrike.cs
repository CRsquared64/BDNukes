using System;
using System.Collections;
using System.Collections.Generic;
using BDArmory.Control;
using BDArmory.Targeting;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Strikes launched from the strike planner: the target each missile was sent to (read by the round-planet
    /// guidance), the vessel it came from (to go back to), and the camera that follows it in.
    ///
    /// A missile flying a few hundred km leaves the game's physics range around the active vessel and is put on
    /// rails, where nothing flies it. So a followed strike makes the missile the active vessel once it's clear of
    /// the launcher: the camera rides along, and its guidance keeps running all the way to the target.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeStrike : MonoBehaviour
    {
        internal static NukeStrike Instance;

        internal class Plan
        {
            public StrikeTarget target;
            public Vessel launcher;
            public string weapon;
            public bool follow;
            public double launchUT;
        }

        static readonly Dictionary<MissileBase, Plan> bdaPlans = new Dictionary<MissileBase, Plan>();
        internal static Plan LastPlan;

        Vessel followed;
        Plan followedPlan;
        GameObject pivot;
        float savedMaxDist = -1f;

        internal static bool TryGetPlan(MissileBase m, out Plan p)
        {
            p = null;
            return m != null && bdaPlans.TryGetValue(m, out p);
        }

        void Awake()
        {
            Instance = this;
            bdaPlans.Clear();
            NukeStrikeGuidance.Clear();
            LastPlan = null;
            GameEvents.onVesselChange.Add(OnVesselChange);
        }

        void OnVesselChange(Vessel vessel)
        {
            if (followed != null && vessel != followed) ClearPivot();
        }

        void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(OnVesselChange);
            ClearPivot();
            if (Instance == this) Instance = null;
            if (pivot != null) Destroy(pivot);
            bdaPlans.Clear();
        }

        // ----------------------------------------------------------------------------------------------------
        // BDArmory ICBMs
        // ----------------------------------------------------------------------------------------------------

        internal static bool IsIcbm(MissileLauncher ml) =>
            ml != null && ml.GuidanceMode == MissileBase.GuidanceModes.AGMBallistic && ml.TargetingMode == MissileBase.TargetingModes.Gps;

        /// <summary>Sends a BDArmory GPS ballistic missile to the target and fires it.</summary>
        internal static void LaunchBda(MissileLauncher ml, StrikeTarget t, bool follow)
        {
            var launcher = ml.vessel;
            var wm = launcher != null ? launcher.FindPartModuleImplementing<MissileFire>() : null;
            Vector3d coords = new Vector3d(t.lat, t.lon, t.GroundAltitude);
            if (wm != null)
            {
                // the missile may refresh its GPS point from the weapon manager in flight: give both the same one
                wm.designatedGPSInfo = new GPSTargetInfo(coords, t.name, null);
                ml.Team = wm.Team;
            }
            // a GPS point picked earlier in the missile's own menu (gpsSet) overrides targetGPSCoords in flight
            ml.gpsSet = false;
            ml.assignedGPSCoords = coords;
            ml.targetGPSCoords = coords;
            ml.TargetAcquired = true;
            ml.TargetPosition = t.World;
            var plan = new Plan { target = t, launcher = launcher, weapon = ml.shortName, follow = follow, launchUT = Planetarium.GetUniversalTime() };
            bdaPlans[ml] = plan;
            LastPlan = plan;
            ml.FireMissile();
            Debug.Log($"[BDNukes]: Strike planner: {ml.shortName} launched at {t}.");
            ScreenMessages.PostScreenMessage($"{ml.shortName} launched: {t.name}", 4f, ScreenMessageStyle.UPPER_CENTER);
            if (follow && Instance != null) Instance.StartCoroutine(Instance.FollowWhenClear(ml.part, launcher, plan));
        }

        /// <summary>Takes the camera (and the active vessel) to a launched weapon once it has separated from the launcher.</summary>
        internal IEnumerator FollowWhenClear(Part p, Vessel launcher, Plan plan)
        {
            float until = Time.time + 6f;
            while (Time.time < until)
            {
                if (p == null || p.vessel == null) yield break;
                if (p.vessel != launcher && p.vessel.loaded) break;
                yield return null;
            }
            if (p == null || p.vessel == null || p.vessel == launcher) yield break;
            yield return new WaitForSeconds(0.3f);
            if (p == null || p.vessel == null) yield break;
            Follow(p.vessel, plan);
        }

        internal void Follow(Vessel v, Plan plan)
        {
            ClearPivot();
            if (FlightGlobals.ActiveVessel != v)
            {
                try { FlightGlobals.ForceSetActiveVessel(v); }
                catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't follow the missile: " + e.Message); return; }
            }
            followed = v;
            followedPlan = plan;
            KeepLoaded(v);
        }

        /// <summary>Keeps a weapon in physics however far it goes from everything else.</summary>
        internal static void KeepLoaded(Vessel v)
        {
            if (v == null || v.vesselRanges == null) return;
            float r = 2.5e6f;
            var vr = v.vesselRanges;
            vr.flying = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            vr.subOrbital = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            vr.orbit = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            vr.escaping = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
        }

        /// <summary>Back to a vessel: directly if it's loaded, else through a save and reload, as the map's Switch To does.</summary>
        internal static void SwitchTo(Vessel v)
        {
            if (v == null) return;
            if (Instance != null) Instance.ClearPivot();
            if (v.loaded) { FlightGlobals.ForceSetActiveVessel(v); return; }
            int idx = FlightGlobals.Vessels.IndexOf(v);
            if (idx < 0) return;
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel("persistent", idx);
        }

        // ----------------------------------------------------------------------------------------------------
        // The camera over the target at the end
        // ----------------------------------------------------------------------------------------------------

        void Update()
        {
            if (followedPlan == null) return;
            var fc = FlightCamera.fetch;
            if (fc == null) return;
            if (pivot != null)
            {
                pivot.transform.position = followedPlan.target.World;
                return;
            }
            if (followed == null || !followed.loaded || followed.mainBody != followedPlan.target.body || MapView.MapIsEnabled) return;
            Vector3d to = followedPlan.target.World - followed.CoMD;
            double speed = Math.Max(followed.srfSpeed, 1d);
            double terminal = Math.Max(15000d, speed * 7d);
            if (to.magnitude > terminal) return;
            // close: put the camera off to the side of the target and let the warhead fly into the view
            float dist = (float)Math.Max(9000d, 0.5d * terminal);
            Watch(followedPlan.target, dist, 0.22f);
            ScreenMessages.PostScreenMessage("Terminal phase: watching the target", 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Points the flight camera at the target from `distance` (pitch in rad: 0 level, 1.2 looking well down).</summary>
        internal void Watch(StrikeTarget t, float distance, float pitch)
        {
            var fc = FlightCamera.fetch;
            if (fc == null) return;
            if (pivot != null) Destroy(pivot);
            pivot = new GameObject("BDNukes_StrikePivot");
            pivot.transform.position = t.World;
            if (followedPlan == null) followedPlan = new Plan { target = t };
            followedPlan.target = t;
            if (savedMaxDist < 0f) savedMaxDist = fc.maxDistance;
            fc.maxDistance = Mathf.Max(fc.maxDistance, distance * 1.5f);
            fc.SetTargetTransform(pivot.transform);
            fc.setModeImmediate(FlightCamera.Modes.FREE);
            fc.SetDistance(distance);
            fc.camPitch = Mathf.Clamp(pitch, fc.minPitch, fc.maxPitch);
        }

        internal void ClearPivot()
        {
            if (pivot != null && FlightCamera.fetch != null && FlightGlobals.ActiveVessel != null)
                FlightCamera.fetch.SetTargetTransform(FlightGlobals.ActiveVessel.transform);
            if (pivot != null) Destroy(pivot);
            pivot = null;
            followed = null;
            followedPlan = null;
            var fc = FlightCamera.fetch;
            if (fc != null && savedMaxDist > 0f) fc.maxDistance = savedMaxDist;
            savedMaxDist = -1f;
        }

        // ----------------------------------------------------------------------------------------------------
        // Range of a BDArmory ballistic missile
        // ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Burnout speed from the missile's motor: each phase burns its fuel at constant thrust, the boost phase's
        /// dry booster dropped after it (Tsiolkovsky with the effective exhaust speed F t / m_fuel), less the
        /// gravity and drag lost on the way up.
        /// </summary>
        internal static double BurnoutSpeed(MissileLauncher ml, CelestialBody body)
        {
            double m = ml.part.mass + ml.part.GetResourceMass();
            double dv = 0;
            if (ml.thrust > 0 && ml.boostTime > 0)
            {
                double mf = Math.Min(ml.boosterFuelMass, m * 0.95);
                dv += mf > 1e-4 ? ml.thrust * ml.boostTime / mf * Math.Log(m / (m - mf)) : ml.thrust * ml.boostTime / m;
                m -= mf;
                if (ml.decoupleBoosters) m = Math.Max(0.05, m - ml.boosterMass);
            }
            if (ml.cruiseThrust > 0 && ml.cruiseTime > 0)
            {
                double mf = Math.Min(ml.cruiseFuelMass, m * 0.95);
                dv += mf > 1e-4 ? ml.cruiseThrust * ml.cruiseTime / mf * Math.Log(m / (m - mf)) : ml.cruiseThrust * ml.cruiseTime / m;
            }
            double g = body.gravParameter / (body.Radius * body.Radius);
            double loss = 0.35 * g * Math.Min(ml.boostTime + ml.cruiseTime, 200) + (body.atmosphere ? 350 : 0);
            return Math.Max(0, dv - loss);
        }

        /// <summary>
        /// Furthest the missile can throw its warhead round the body (m along the surface): the minimum-energy
        /// ballistic arc for its burnout speed. Infinity when that's orbital speed (it reaches anywhere).
        /// </summary>
        internal static double MaxRange(MissileLauncher ml, CelestialBody body)
        {
            double vb = BurnoutSpeed(ml, body);
            double nu = vb * vb * body.Radius / body.gravParameter;
            if (nu >= 1) return double.PositiveInfinity;
            double s = nu / (2 - nu);
            return 2 * Math.Asin(Math.Min(1, s)) * body.Radius;
        }
    }
}
