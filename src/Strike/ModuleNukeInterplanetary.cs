using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// "Starlance": a fictional interplanetary strike missile. One part, three stages and a light-drive bus:
    ///
    ///  1. Ascent (this module, in physics): a gravity-turn autopilot flies the rocket from the pad to a parking
    ///     orbit (atmosphere height + ~30 km, ~100 km on Kerbin), staging as each stage burns out. Each stage has its
    ///     own thrust, vacuum / sea-level Isp, burn time and dry mass (KSPFields); its propellant (thrust / (Isp g0)
    ///     x burn time) is an internal budget carried as part mass (IPartMassModifier), not a KSP resource. Thrust is a
    ///     rigidbody force along the nose, attitude a PD controller on the rigidbody's angular acceleration (like a very
    ///     strong reaction wheel; the part has no command module, so no FlightCtrlState or SAS is involved). The pitch
    ///     program is altitude-based, with the angle of attack held small at high dynamic pressure and the throttle
    ///     limited above maxQ and maxAccel. Once the apoapsis reaches the parking altitude the motor cuts (topping the
    ///     apoapsis up against drag while still in air), coasts, and circularises at apoapsis, the burn starting half
    ///     its estimated length early. Spent stages drop away visibly: each stage is a "detach" slice of the
    ///     procedural model with its own exhaust transforms, unparented and flown off as debris
    ///     (InterplanetaryStageDebris); the collider is shortened to what's left. Launched from orbit, the ascent is
    ///     skipped.
    ///  2. Light drive (InterplanetaryDirector, persistent, works unloaded): the last stage is dropped, the drive
    ///     charges for chargeTime and waits for the parking orbit to face the target, then the bus crosses to the
    ///     target body at lightFraction x the mod's light speed (NukeSettings.waveSpeed), arriving in a circular orbit
    ///     of arrivalRadii body radii directly above the target point.
    ///  3. Release: `warheads` re-entry vehicles on Kepler arcs to a centre + ring pattern of aim points within
    ///     spreadAngle degrees of arc of the target point, each detonating at burstHeight above its aim point.
    ///
    /// Targeting needs a designator: some other vessel with a probe core or an antenna already orbiting the target
    /// body. Driven by the strike planner through IStrikeWeapon; FollowWarhead(i) gives the planner an RV to follow.
    /// Plumes are ModuleNukePlume modules on the stage exhaust transforms, driven by part.SendMessage("SetPlumeThrottle")
    /// with only the burning stage's exhaust transforms active.
    /// </summary>
    public class ModuleNukeInterplanetary : PartModule, IStrikeWeapon, IStrikeWarheadCarrier, IStrikeBodyFilter, IPartMassModifier
    {
        bool IStrikeBodyFilter.CanTargetBody(CelestialBody body) => body != InterplanetaryMath.Sun && (body.hasSolidSurface || body.pqsController != null);
        // ---- stages (up to 3; stage 1 burns first) ----
        [KSPField] public int stageCount = 3;
        [KSPField] public float stage1Thrust = 4500f, stage1IspVac = 300f, stage1IspSL = 270f, stage1BurnTime = 80f, stage1DryMass = 14f;
        [KSPField] public string stage1Transform = "stage1", stage1Exhaust = "stage1Exhaust";
        [KSPField] public float stage2Thrust = 1200f, stage2IspVac = 335f, stage2IspSL = 250f, stage2BurnTime = 160f, stage2DryMass = 6f;
        [KSPField] public string stage2Transform = "stage2", stage2Exhaust = "stage2Exhaust";
        [KSPField] public float stage3Thrust = 400f, stage3IspVac = 350f, stage3IspSL = 200f, stage3BurnTime = 170f, stage3DryMass = 3f;
        [KSPField] public string stage3Transform = "stage3", stage3Exhaust = "stage3Exhaust";
        [KSPField] public string driveExhaust = "driveExhaust";
        [KSPField] public float stagingDelay = 1.2f;        // s from burnout to the next stage's ignition

        // ---- ascent ----
        [KSPField] public float parkingAltitude = 0f;       // m; 0 = automatic (atmosphere + 30 km, ~100 km on Kerbin)
        [KSPField] public float turnStartAltitude = 800f;   // m above the ground where the pitch-over starts
        [KSPField] public float turnShape = 0.45f;          // exponent of the pitch program (smaller = flatter sooner)
        [KSPField] public float maxQ = 25f;                 // kPa
        [KSPField] public float maxAccel = 4f;              // g
        [KSPField] public float maxAngularAccel = 0.8f;     // rad/s^2 of the attitude controller

        // ---- light drive ----
        [KSPField] public float lightFraction = 0.9f;       // of NukeSettings.waveSpeed
        [KSPField] public float chargeTime = 12f;           // s
        [KSPField] public float arrivalRadii = 4f;          // arrival orbit radius in body radii

        // ---- warheads ----
        [KSPField] public int warheads = 10;
        [KSPField] public float spreadAngle = 30f;          // degrees of arc from the target point to the ring
        [KSPField] public float spreadRadius = 0f;         // m great-circle radius; 0 uses spreadAngle
        [KSPField] public float warheadYield = 5000f;       // kt
        [KSPField] public float warheadThermalRadius = 11100f;
        [KSPField] public float burstHeight = 3000f;        // m above the ground
        [KSPField] public float deorbitTime = 600f;         // s, preferred RV flight time from release
        [KSPField] public float releaseDelay = 20f;         // s after arrival
        [KSPField] public string rvPart = "bdnukesStarlanceRV";
        [KSPField] public bool autoFollowWarhead = true;    // if the bus is the active vessel, the camera goes with RV 1

        // ---- sounds (GameDatabase paths; missing clips are skipped) ----
        [KSPField] public string launchSound = "BDArmory/Sounds/launch";
        [KSPField] public string loopSound = "BDArmory/Sounds/rocketLoop";
        [KSPField] public string chargeSound = "BDNukes/Sounds/bdnukes_lightdrive_charge";
        [KSPField] public string jumpSound = "BDNukes/Sounds/bdnukes_lightdrive_jump";
        [KSPField] public string releaseSound = "BDNukes/Sounds/bdnukes_rv_release";

        // ---- persistent state ----
        [KSPField(isPersistant = true)] public string phase = "Idle";   // Idle, Ascent, Coast, Circularize, Drive, Failed
        [KSPField(isPersistant = true)] public int stageIndex = 0;      // the burning (or next) stage; earlier ones are gone
        [KSPField(isPersistant = true)] public float fuel1 = -1f, fuel2 = -1f, fuel3 = -1f;   // t left; -1 = full
        [KSPField(isPersistant = true)] public string tBody = "";
        [KSPField(isPersistant = true)] public double tLat, tLon, tHeight;
        [KSPField(isPersistant = true)] public string tName = "";

        [KSPField(guiActive = true, guiName = "Starlance")] public string status = "Ready";

        class Stage
        {
            public float thrust, ispVac, ispSL, burnTime, dry;
            public string transform, exhaust;
            public float FullFuel => ispVac > 0f ? thrust / (ispVac * (float)InterplanetaryMath.G0) * burnTime : 0f;
            public float MassFlow => ispVac > 0f ? thrust / (ispVac * (float)InterplanetaryMath.G0) : 0f;
        }

        Stage[] stages;
        float ignitionAt;
        float throttle;
        AudioSource loop, oneShot;
        AudioClip launchClip, loopClip, chargeClip, jumpClip, releaseClip;
        InterplanetaryDriveFx driveFx;
        bool exhaustsSet;
        int shownStage = -2;
        string lastDirectorState;

        // ===================================================================================================
        // Setup
        // ===================================================================================================

        Stage[] Stages
        {
            get
            {
                if (stages != null) return stages;
                var all = new[]
                {
                    new Stage { thrust = stage1Thrust, ispVac = stage1IspVac, ispSL = stage1IspSL, burnTime = stage1BurnTime, dry = stage1DryMass, transform = stage1Transform, exhaust = stage1Exhaust },
                    new Stage { thrust = stage2Thrust, ispVac = stage2IspVac, ispSL = stage2IspSL, burnTime = stage2BurnTime, dry = stage2DryMass, transform = stage2Transform, exhaust = stage2Exhaust },
                    new Stage { thrust = stage3Thrust, ispVac = stage3IspVac, ispSL = stage3IspSL, burnTime = stage3BurnTime, dry = stage3DryMass, transform = stage3Transform, exhaust = stage3Exhaust },
                };
                // on a big world (real scale) the stages need SpeedScale times the speed to make orbit: thrust and Isp
                // both up by it, so the propellant load (thrust / Isp x burn time) stays the same
                float kv = (float)NukeScale.SpeedScale;
                if (kv > 1.001f)
                    foreach (var st in all) { st.thrust *= kv; st.ispVac *= kv; st.ispSL *= kv; }
                int n = Mathf.Clamp(stageCount, 1, 3);
                stages = new Stage[n];
                Array.Copy(all, stages, n);
                return stages;
            }
        }

        float GetFuel(int i) => i == 0 ? fuel1 : i == 1 ? fuel2 : fuel3;
        void SetFuel(int i, float v) { if (i == 0) fuel1 = v; else if (i == 1) fuel2 = v; else fuel3 = v; }
        float Fuel(int i) { float f = GetFuel(i); return f < 0f ? Stages[i].FullFuel : f; }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            stages = null;
            if (!HighLogic.LoadedSceneIsFlight) return;
            for (int i = 0; i < Stages.Length; i++) if (GetFuel(i) < 0f) SetFuel(i, Stages[i].FullFuel);
            CacheTransforms();

            // stages dropped before this load: hide them (the model comes back whole from the prefab)
            int gone = phase == "Drive" ? Stages.Length : stageIndex;
            for (int i = 0; i < gone && i < Stages.Length; i++) HideStage(i);
            if (gone > 0) TrimCollider();
            part.UpdateMass();

            launchClip = Clip(launchSound); loopClip = Clip(loopSound);
            chargeClip = Clip(chargeSound); jumpClip = Clip(jumpSound); releaseClip = Clip(releaseSound);
            loop = MakeSource(true);
            oneShot = MakeSource(false);
            oneShot.volume = 1f;
            driveFx = new InterplanetaryDriveFx(part);
            if (IsFlying) KeepLoaded(vessel);
        }

        static AudioClip Clip(string path)
        {
            if (string.IsNullOrEmpty(path) || GameDatabase.Instance == null) return null;
            try { return GameDatabase.Instance.GetAudioClip(path); } catch { return null; }
        }

        AudioSource MakeSource(bool looping)
        {
            var a = part.gameObject.AddComponent<AudioSource>();
            a.loop = looping;
            a.playOnAwake = false;
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.minDistance = 30f;
            a.maxDistance = 20000f;
            a.dopplerLevel = 0f;
            a.volume = 0f;
            return a;
        }

        void OnDestroy()
        {
            driveFx?.Destroy();
            if (loop != null) Destroy(loop);
            if (oneShot != null) Destroy(oneShot);
        }

        bool IsFlying => phase == "Ascent" || phase == "Coast" || phase == "Circularize";

        // ===================================================================================================
        // Mass
        // ===================================================================================================

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit)
        {
            float m = 0f;
            var st = Stages;
            int first = HighLogic.LoadedSceneIsFlight ? (phase == "Drive" ? st.Length : stageIndex) : 0;
            for (int i = first; i < st.Length; i++)
                m += st[i].dry + (HighLogic.LoadedSceneIsFlight ? Mathf.Max(0f, Fuel(i)) : st[i].FullFuel);
            return m;
        }

        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.CONSTANTLY;

        double DeltaVLeft()
        {
            double m = part.mass + part.GetResourceMass();
            if (!HighLogic.LoadedSceneIsFlight) m = part.partInfo != null ? part.partInfo.partPrefab.mass + GetModuleMass(0f, ModifierStagingSituation.CURRENT) : m;
            double dv = 0;
            var st = Stages;
            for (int i = stageIndex; i < st.Length; i++)
            {
                double f = HighLogic.LoadedSceneIsFlight ? Math.Max(0f, Fuel(i)) : st[i].FullFuel;
                double ve = st[i].ispVac * InterplanetaryMath.G0;
                if (m - f > 0.01) dv += ve * Math.Log(m / (m - f));
                m -= f + st[i].dry;
            }
            return dv;
        }

        // ===================================================================================================
        // Ascent autopilot
        // ===================================================================================================

        Vector3 Nose()
        {
            var pm = part.FindModuleImplementing<ModuleProceduralModel>();
            return pm != null && !pm.AxisIsY ? part.transform.forward : part.transform.up;
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || !vessel.loaded) return;
            CelestialBody body = vessel.mainBody;
            if (body == null) return;

            if (vessel.packed)
            {
                throttle = 0f;
                // rails warp while it's climbing or burning would freeze the flight (KSP can pack the craft before the
                // autopilot gets a frame to refuse the warp): come back off rails, physics warp stays allowed
                if ((phase == "Ascent" || phase == "Circularize") && vessel == FlightGlobals.ActiveVessel
                    && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
                {
                    TimeWarp.SetRate(0, true);
                    ScreenMessages.PostScreenMessage("Starlance: climbing under power, only physics warp (Alt + .) works now", 4f, ScreenMessageStyle.UPPER_CENTER);
                }
                // rails warp in the coast: come out of it in time for the circularisation burn
                if (phase == "Coast" && vessel == FlightGlobals.ActiveVessel && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH)
                {
                    if (vessel.orbit.timeToAp < CircBurnTime(body) * 0.5 + 30d * TimeWarp.CurrentRate / 10d + 20d)
                        TimeWarp.SetRate(0, true);
                }
                return;
            }
            if (part.rb == null) return;

            if (IsFlying) FlyAscent(body);
            else
            {
                throttle = 0f;
                if (phase == "Drive")
                {
                    // keep the bus steady (prograde) while it charges, and after arrival
                    Vector3d v = vessel.obt_velocity;
                    if (v.sqrMagnitude > 1) Steer((Vector3)v.normalized);
                }
            }
        }

        double ParkAlt(CelestialBody b) => InterplanetaryMath.ParkingAltitude(b, parkingAltitude);

        double SafePeriapsis(CelestialBody b) => b.atmosphere ? b.atmosphereDepth + 5000d : Math.Max(5000d, 0.02d * b.Radius);

        float StageAccel()
        {
            var st = Stages;
            if (stageIndex >= st.Length) return 0f;
            float m = Mathf.Max(0.1f, part.mass + part.GetResourceMass());
            return st[stageIndex].thrust / m;
        }

        /// <summary>Delta-v and burn time to circularise at apoapsis.</summary>
        double CircDv(CelestialBody b)
        {
            var o = vessel.orbit;
            double ra = o.ApR, a = o.semiMajorAxis;
            if (ra <= 0 || a <= 0 || o.eccentricity >= 1) return 0;
            double vAp = Math.Sqrt(b.gravParameter * (2d / ra - 1d / a));
            return Math.Max(0d, InterplanetaryMath.Circular(b, ra) - vAp);
        }

        double CircBurnTime(CelestialBody b)
        {
            double acc = StageAccel();
            if (acc <= 0.01) acc = 0.01;
            return CircDv(b) / acc;
        }

        void FlyAscent(CelestialBody body)
        {
            // the ascent is flown in physics: rails warp would freeze it, but physics warp (up to 4x) is fine, as the
            // guidance steps with the physics frame. Dropping every warp here made a real-scale ascent unwatchably slow.
            // In the coast, rails warp is allowed too: the packed vessel comes out of it before the circularisation burn.
            if (phase != "Coast" && TimeWarp.CurrentRateIndex > 0 && TimeWarp.WarpMode == TimeWarp.Modes.HIGH) TimeWarp.SetRate(0, true);
            var st = Stages;
            double parkAlt = ParkAlt(body);
            Vector3d pos = vessel.CoMD;
            Vector3d up = (pos - body.position).normalized;
            Vector3d vObt = vessel.obt_velocity, vSrf = vessel.srf_velocity;
            double alt = vessel.altitude;
            double agl = alt - Math.Max(0d, vessel.terrainAltitude);
            double q = vessel.dynamicPressurekPa;
            var o = vessel.orbit;

            // east: the way the ground turns (any horizontal direction on a body that doesn't spin)
            Vector3d east = body.getRFrmVel(pos);
            if (east.sqrMagnitude < 1e-4) east = Vector3d.Cross(NukeStrikeMath.Axis(body), up);
            east = InterplanetaryMath.Exclude(up, east).normalized;

            // staging
            if (stageIndex < st.Length && Fuel(stageIndex) <= 0f)
            {
                if (stageIndex < st.Length - 1)
                {
                    DropStage(stageIndex);
                    stageIndex++;
                    ignitionAt = Time.time + stagingDelay;
                    Debug.Log($"[BDNukes]: Starlance: stage {stageIndex} burnout, stage {stageIndex + 1} ignition.");
                }
                else stageIndex = st.Length;   // all burnt
            }
            bool haveThrust = stageIndex < st.Length && Time.time >= ignitionAt;

            Vector3d dir = up;
            double thr = 0;
            Vector3d horiz = InterplanetaryMath.Exclude(up, vObt);
            if (horiz.sqrMagnitude < 1) horiz = east;
            horiz = horiz.normalized;

            switch (phase)
            {
                case "Ascent":
                {
                    double turnEnd = body.atmosphere ? 0.8d * body.atmosphereDepth : Math.Max(5000d, 0.3d * parkAlt);
                    double pitch = 90d;
                    if (agl > turnStartAltitude && vSrf.magnitude > 50d)
                    {
                        double frac = Math.Max(0d, Math.Min(1d, (alt - turnStartAltitude) / Math.Max(1000d, turnEnd - turnStartAltitude)));
                        pitch = 90d * (1d - Math.Pow(frac, Math.Max(0.1f, turnShape)));
                        // stay a little nose-up while the apoapsis is still low and we're near the ground
                        if (o.ApA < 0.6d * parkAlt) pitch = Math.Max(pitch, 8d);
                    }
                    double pr = pitch * Math.PI / 180d;
                    dir = (up * Math.Sin(pr) + east * Math.Cos(pr)).normalized;
                    // small angle of attack in thick, fast air
                    if (body.atmosphere && vSrf.magnitude > 60d && alt < body.atmosphereDepth)
                    {
                        double aoa = q > 0.2d * maxQ ? Lerp(10d, 2.5d, Math.Min(1d, q / maxQ)) : 15d;
                        dir = LimitAngle(vSrf.normalized, dir, aoa);
                    }
                    thr = 1d;
                    // ease off as the apoapsis approaches the parking orbit
                    double left = (parkAlt - o.ApA) / Math.Max(1000d, 0.05d * parkAlt);
                    thr *= Math.Max(0.05d, Math.Min(1d, left + 0.05d));
                    if (o.ApA >= parkAlt) { phase = "Coast"; thr = 0; Debug.Log("[BDNukes]: Starlance: apoapsis reached, coasting."); }
                    break;
                }
                case "Coast":
                {
                    bool inAir = body.atmosphere && alt < body.atmosphereDepth;
                    dir = inAir && vSrf.sqrMagnitude > 100 ? vSrf.normalized : horiz;
                    if (inAir && o.ApA < parkAlt * 0.998d)
                        thr = Math.Max(0d, Math.Min(1d, (parkAlt - o.ApA) / Math.Max(500d, 0.01d * parkAlt)));
                    double lead = CircBurnTime(body) * 0.5d + 1d;
                    bool pastAp = o.timeToAp > o.period * 0.5d || Vector3d.Dot(vObt, up) < 0d;
                    if (!inAir && (o.timeToAp <= lead || pastAp)) { phase = "Circularize"; Debug.Log("[BDNukes]: Starlance: circularising."); }
                    if (!inAir && o.PeA > SafePeriapsis(body) && CircDv(body) < 5d) { Park(); return; }
                    break;
                }
                case "Circularize":
                {
                    double r = (pos - body.position).magnitude;
                    double vC = InterplanetaryMath.Circular(body, r);
                    double vs = Vector3d.Dot(vObt, up);
                    double hs = InterplanetaryMath.Exclude(up, vObt).magnitude;
                    dir = (horiz + up * Math.Max(-0.5d, Math.Min(0.5d, -vs / 150d))).normalized;
                    double need = vC - hs;
                    double acc = Math.Max(0.05d, StageAccel());
                    thr = Math.Max(0.05d, Math.Min(1d, need / acc / 0.6d));
                    bool good = o.PeA > SafePeriapsis(body);
                    if ((good && (need < 1d || o.eccentricity < 0.002d)) || (good && o.PeA > 0.98d * parkAlt)) { Park(); return; }
                    if (!good && o.ApA > 3d * parkAlt) { Abort("overshot the parking orbit"); return; }
                    break;
                }
            }

            if (!haveThrust && stageIndex >= st.Length && thr > 0)
            {
                if (o.PeA > SafePeriapsis(body)) { Park(); return; }
                Abort("out of propellant before orbit");
                return;
            }

            // max-Q and acceleration limits
            if (q > maxQ) thr *= Math.Max(0.25d, 1d - (q - maxQ) / Math.Max(1d, 0.3d * maxQ));
            double a0 = StageAccel();
            if (a0 > 0) thr = Math.Min(thr, maxAccel * InterplanetaryMath.G0 / a0);

            Steer((Vector3)dir);
            float target = haveThrust ? (float)Math.Max(0d, Math.Min(1d, thr)) : 0f;
            // engines don't snap: ~0.3 s to full
            throttle = Mathf.MoveTowards(throttle, target, TimeWarp.fixedDeltaTime * 3f);
            if (haveThrust && throttle > 0f)
            {
                var s = st[stageIndex];
                double pAtm = Math.Max(0d, Math.Min(1d, vessel.staticPressurekPa / 101.325d));
                double isp = Lerp(s.ispVac, s.ispSL, pAtm);
                float dt = TimeWarp.fixedDeltaTime;
                float used = Mathf.Min(Fuel(stageIndex), s.MassFlow * throttle * dt);
                float F = (float)(used / dt * InterplanetaryMath.G0 * isp);
                SetFuel(stageIndex, Mathf.Max(0f, Fuel(stageIndex) - used));
                part.UpdateMass();
                part.rb.AddForce(Nose() * F, ForceMode.Force);   // kN on tonnes
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>dir, turned towards `axis` so it is at most maxDeg away from it.</summary>
        static Vector3d LimitAngle(Vector3d axis, Vector3d dir, double maxDeg)
        {
            double ang = InterplanetaryMath.AngleDeg(axis, dir);
            if (ang <= maxDeg || ang < 1e-6) return dir;
            return (Vector3d)Vector3.Slerp((Vector3)axis, (Vector3)dir, (float)(maxDeg / ang)).normalized;
        }

        /// <summary>PD attitude control straight on the rigidbody: angular acceleration, inertia ignored.</summary>
        void Steer(Vector3 desired)
        {
            if (part.rb == null || desired.sqrMagnitude < 1e-6f) return;
            Vector3 n = Nose();
            Vector3 axis = Vector3.Cross(n, desired);
            float sin = axis.magnitude;
            if (sin < 1e-6f && Vector3.Dot(n, desired) < 0f) { axis = part.transform.right; sin = 1f; }
            float ang = Mathf.Atan2(sin, Vector3.Dot(n, desired));
            Vector3 w = part.rb.angularVelocity;
            Vector3 wRoll = Vector3.Project(w, n);
            Vector3 wPitch = w - wRoll;
            Vector3 alpha = (sin > 1e-6f ? axis / sin * (ang * 1.2f) : Vector3.zero) - wPitch * 2.2f - wRoll * 1.5f;
            alpha = Vector3.ClampMagnitude(alpha, maxAngularAccel);
            part.rb.AddTorque(alpha, ForceMode.Acceleration);
        }

        void Abort(string why)
        {
            phase = "Failed";
            throttle = 0f;
            status = "Failed: " + why;
            Debug.LogWarning("[BDNukes]: Starlance aborted: " + why);
            ScreenMessages.PostScreenMessage("Starlance aborted: " + why, 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ===================================================================================================
        // Stages
        // ===================================================================================================

        // Looked up once, while everything is still active: a transform switched off later may not be found again.
        readonly Dictionary<string, List<Transform>> transformCache = new Dictionary<string, List<Transform>>();

        IEnumerable<Transform> FindTransforms(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            if (!transformCache.TryGetValue(name, out var list))
            {
                list = new List<Transform>();
                foreach (var t in part.FindModelTransforms(name)) if (t != null) list.Add(t);
                transformCache[name] = list;
            }
            foreach (var t in list) if (t != null) yield return t;
        }

        void CacheTransforms()
        {
            foreach (var s in Stages) { FindTransforms(s.transform).GetEnumerator().MoveNext(); FindTransforms(s.exhaust).GetEnumerator().MoveNext(); }
            FindTransforms(driveExhaust).GetEnumerator().MoveNext();
        }

        void DropStage(int i)
        {
            var s = Stages[i];
            Vector3 n = Nose();
            foreach (var t in FindTransforms(s.transform))
            {
                if (!t.IsChildOf(part.transform)) continue;
                // switch its nozzles off first (ModuleNukePlume also hides plumes that have left the part)
                foreach (var e in FindTransforms(s.exhaust)) if (e.IsChildOf(t)) e.gameObject.SetActive(false);
                t.SetParent(null, true);
                t.gameObject.AddComponent<BDArmory.Utils.DecoupledBooster>().DecoupleBooster(part.rb.velocity, 6f);
            }
            TrimCollider();
            part.UpdateMass();
            PlayOneShot(launchClip, 0.4f);
        }

        void HideStage(int i)
        {
            foreach (var t in FindTransforms(Stages[i].transform)) t.gameObject.SetActive(false);
        }

        /// <summary>Shrinks the capsule collider to the part of the body still attached.</summary>
        void TrimCollider()
        {
            var cap = part.collider as CapsuleCollider;
            if (cap == null) cap = part.FindModelComponent<CapsuleCollider>();
            if (cap == null) return;
            bool any = false;
            var b = new Bounds();
            Transform ct = cap.transform;
            foreach (var mf in part.FindModelComponents<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null || !mf.gameObject.activeInHierarchy || !mf.transform.IsChildOf(part.transform)) continue;
                if (mf.GetComponentInParent<InterplanetaryStageDebris>() != null) continue;
                if (mf.GetComponent<MeshRenderer>() == null) continue;
                if (mf.gameObject.name == "BDNukesPlume") continue;
                Bounds mb = mf.sharedMesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = c + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z);
                    Vector3 p = ct.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) return;
            int d = cap.direction;
            float len = d == 0 ? b.size.x : d == 1 ? b.size.y : b.size.z;
            Vector3 center = cap.center;
            if (d == 0) center.x = b.center.x; else if (d == 1) center.y = b.center.y; else center.z = b.center.z;
            cap.center = center;
            cap.height = Mathf.Max(len, cap.radius * 2f);
        }

        /// <summary>Only the burning stage's nozzles (or the drive's) are live, so SetPlumeThrottle lights just them.</summary>
        void ShowExhausts(int active)
        {
            if (shownStage == active && exhaustsSet) return;
            shownStage = active;
            exhaustsSet = true;
            var st = Stages;
            for (int i = 0; i < st.Length; i++)
                foreach (var t in FindTransforms(st[i].exhaust)) t.gameObject.SetActive(i == active);
            foreach (var t in FindTransforms(driveExhaust)) t.gameObject.SetActive(active == -1);
        }

        // ===================================================================================================
        // Light drive hand-off and effects
        // ===================================================================================================

        void Park()
        {
            throttle = 0f;
            Debug.Log($"[BDNukes]: Starlance: parking orbit {vessel.orbit.PeA / 1000d:0} x {vessel.orbit.ApA / 1000d:0} km.");
            BeginDrive();
        }

        void BeginDrive()
        {
            // the remaining stages fall away: the bus flies on alone
            var st = Stages;
            for (int i = 0; i < st.Length; i++)
                if (!vessel.packed) DropStage(i); else HideStage(i);
            stageIndex = st.Length;
            phase = "Drive";
            part.UpdateMass();
            TrimCollider();

            var body = FlightGlobals.GetBodyByName(tBody);
            var f = new InterplanetaryFlight
            {
                vesselId = vessel.id,
                vesselName = vessel.vesselName,
                state = "Charging",
                chargeEndUT = Planetarium.GetUniversalTime() + Math.Max(1f, chargeTime),
                targetBody = tBody, tLat = tLat, tLon = tLon, tHeight = tHeight, tName = string.IsNullOrEmpty(tName) ? "target" : tName,
                arrivalAlt = body != null ? ArrivalAlt(body) : 1e6,
                speed = LightSpeed,
                warheads = Math.Max(1, warheads),
                spreadDeg = spreadRadius > 0 && body != null ? Math.Min(89d, spreadRadius / body.Radius * 180d / Math.PI) : Mathf.Clamp(spreadAngle, 0f, 89f),
                yield = warheadYield, thermalRadius = warheadThermalRadius, fluence = warheadYield, burstHeight = burstHeight,
                deorbitTime = deorbitTime, releaseDelay = releaseDelay,
                rvPart = rvPart, autoFollow = autoFollowWarhead,
            };
            if (!InterplanetaryDirector.Begin(f)) { Abort("no light-drive director in this scene"); return; }
            status = "Light drive charging";
            PlayOneShot(chargeClip, 1f);
            ScreenMessages.PostScreenMessage($"Starlance: light drive charging ({chargeTime:0} s)", 4f, ScreenMessageStyle.UPPER_CENTER);
        }

        double LightSpeed => Math.Max(1000d, Mathf.Clamp(lightFraction, 0.001f, 1f) * NukeScale.WaveSpeed);

        double ArrivalAlt(CelestialBody b)
        {
            double r = Mathf.Clamp(arrivalRadii, 3f, 5f) * b.Radius;
            double min = b.Radius + (b.atmosphere ? b.atmosphereDepth : 0d) + 50000d;
            r = Math.Max(r, min);
            if (b.sphereOfInfluence > 0d && !double.IsInfinity(b.sphereOfInfluence)) r = Math.Min(r, 0.6d * b.sphereOfInfluence);
            return r - b.Radius;
        }

        internal void OnJump()
        {
            PlayOneShot(jumpClip, 1f);
            driveFx?.Flash(true);
        }

        internal void OnArrive()
        {
            PlayOneShot(jumpClip, 0.8f);
            driveFx?.Flash(false);
        }

        internal void OnRelease()
        {
            PlayOneShot(releaseClip, 1f);
        }

        void PlayOneShot(AudioClip c, float vol)
        {
            if (c == null || oneShot == null) return;
            oneShot.PlayOneShot(c, vol * GameSettings.SHIP_VOLUME);
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;
            var f = InterplanetaryDirector.Find(vessel.id);
            string ds = f?.state;

            // plumes: the burning stage, or the drive while it charges
            bool charging = ds == "Charging";
            float chargeK = 0f;
            if (charging) chargeK = Mathf.Clamp01(1f - (float)((f.chargeEndUT - Planetarium.GetUniversalTime()) / Math.Max(1f, chargeTime)));
            if (charging && f.waitingForWindow) chargeK = 1f;
            if (IsFlying) ShowExhausts(Time.time >= ignitionAt ? stageIndex : -2);
            else if (phase == "Drive") ShowExhausts(charging || ds == "Transit" ? -1 : -2);
            else ShowExhausts(-2);
            float plume = IsFlying ? throttle : charging ? 0.25f + 0.5f * chargeK : 0f;
            part.SendMessage("SetPlumeThrottle", plume, SendMessageOptions.DontRequireReceiver);

            if (loop != null)
            {
                float vol = IsFlying ? throttle * GameSettings.SHIP_VOLUME : 0f;
                if (vol > 0.01f && loopClip != null)
                {
                    if (!loop.isPlaying) { loop.clip = loopClip; loop.Play(); }
                    loop.volume = vol;
                    loop.pitch = 0.9f + 0.2f * throttle;
                }
                else if (loop.isPlaying) loop.Stop();
            }

            if (driveFx != null)
            {
                driveFx.charge = charging ? chargeK : 0f;
                Vector3 vdir = vessel.obt_velocity.sqrMagnitude > 1 ? (Vector3)vessel.obt_velocity.normalized : Vector3.zero;
                driveFx.Update(part.transform.position, vdir);
            }

            if (ds != lastDirectorState)
            {
                lastDirectorState = ds;
                if (ds == "Transit") status = "Light-drive transit";
                else if (ds == "Arrived") status = "Arrived, releasing";
                else if (ds == "Released") status = "Warheads released";
            }
            if (IsFlying) status = $"{phase} (stage {Mathf.Min(stageIndex + 1, Stages.Length)}, throttle {throttle:P0})";
        }

        // ===================================================================================================
        // IStrikeWeapon
        // ===================================================================================================

        public Part StrikePart => part;

        public string StrikeDescription =>
            $"Starlance interplanetary missile, {Math.Max(1, warheads)} x {warheadYield / 1000f:0.#} Mt (light drive {lightFraction:0.##}c)";

        /// <summary>The first vessel (not this one) orbiting `body` with a probe core or an antenna, or null.</summary>
        internal Vessel FindDesignator(CelestialBody body)
        {
            if (FlightGlobals.Vessels == null) return null;
            foreach (var v in FlightGlobals.Vessels)
            {
                if (v == null || v == vessel || v.mainBody != body || v.situation != Vessel.Situations.ORBITING) continue;
                switch (v.vesselType)
                {
                    case VesselType.Debris: case VesselType.SpaceObject: case VesselType.Flag: case VesselType.EVA: case VesselType.Unknown:
                        continue;
                }
                if (HasProbeOrAntenna(v)) return v;
            }
            return null;
        }

        static bool HasProbeOrAntenna(Vessel v)
        {
            if (v.loaded)
            {
                foreach (var p in v.parts)
                    foreach (PartModule m in p.Modules)
                        if (m is ModuleCommand || m is ModuleDataTransmitter) return true;
                return false;
            }
            if (v.protoVessel == null) return false;
            foreach (var pp in v.protoVessel.protoPartSnapshots)
                foreach (var pm in pp.modules)
                    if (pm.moduleName == "ModuleCommand" || pm.moduleName == "ModuleDataTransmitter") return true;
            return false;
        }

        bool InOrbit(CelestialBody body) =>
            vessel.situation == Vessel.Situations.ORBITING && vessel.orbit.PeA > SafePeriapsis(body);

        bool IStrikeWeapon.CanReach(StrikeTarget target, out string why, out double flightTime) => CanReach(target, out why, out flightTime);

        internal bool CanReach(StrikeTarget target, out string why, out double flightTime)
        {
            flightTime = double.NaN;
            if (vessel == null || part == null) { why = "no vessel"; return false; }
            if (!target.Valid) { why = "no target"; return false; }
            CelestialBody tb = target.body;
            if (tb == InterplanetaryMath.Sun || (tb.pqsController == null && !tb.hasSolidSurface)) { why = $"{tb.bodyName} has no surface to strike"; return false; }
            if (phase != "Idle") { why = phase == "Failed" ? "this Starlance has failed" : "already launched"; return false; }
            Vessel sat = FindDesignator(tb);
            if (sat == null) { why = $"no satellite in orbit of {tb.bodyName} to designate the target"; return false; }

            CelestialBody here = vessel.mainBody;
            double ascent = 0;
            if (!InOrbit(here))
            {
                if (vessel.situation == Vessel.Situations.ESCAPING || vessel.situation == Vessel.Situations.SUB_ORBITAL && vessel.altitude > (here.atmosphere ? here.atmosphereDepth : 1000d) * 2)
                { why = "launch from the ground or a parking orbit"; return false; }
                double need = InterplanetaryMath.AscentDeltaV(here, ParkAlt(here));
                double have = DeltaVLeft();
                if (have < need) { why = $"not enough propellant to reach orbit of {here.bodyName}: {have:0} of ~{need:0} m/s"; return false; }
                double burn = 0;
                foreach (var s in Stages) burn += s.burnTime;
                ascent = burn * Math.Min(1d, need / Math.Max(1d, have)) * 1.15d + 120d;
            }
            double dist = (tb.position - vessel.CoMD).magnitude;
            double transit = dist / LightSpeed;
            // on average a quarter of the parking orbit waiting for the side that faces the target
            double parkR = here.Radius + ParkAlt(here);
            double window = here != tb ? 0.25d * 2d * Math.PI * Math.Sqrt(parkR * parkR * parkR / here.gravParameter) : 0d;
            flightTime = ascent + chargeTime + window + transit + releaseDelay + deorbitTime;
            why = $"via {sat.vesselName}: {NukeStrikeMath.Distance(dist)} at {lightFraction:0.##}c ({LightSpeed / 1000d:0} km/s), " +
                  $"transit {NukeStrikeMath.Duration(transit)}; {Math.Max(1, warheads)} x {warheadYield / 1000f:0.#} Mt over {spreadAngle:0}°" +
                  (ascent > 0 ? $"; ascent dv margin {DeltaVLeft() - InterplanetaryMath.AscentDeltaV(here, ParkAlt(here)):0} m/s" : "; launching from orbit");
            return true;
        }

        Vessel IStrikeWeapon.Launch(StrikeTarget target, out string status) => Launch(target, out status);

        internal Vessel Launch(StrikeTarget target, out string statusLine)
        {
            if (!CanReach(target, out string why, out _)) { statusLine = "Starlance: " + why; return null; }
            if (InterplanetaryDirector.Instance == null) { statusLine = "Starlance director is not ready."; return null; }
            TimeWarp.SetRate(0, true);
            tBody = target.body.bodyName;
            tLat = target.lat; tLon = target.lon; tHeight = target.height;
            tName = string.IsNullOrEmpty(target.name) ? NukeStrikeMath.LatLon(target.lat, target.lon) : target.name;

            // free of the pad: clamps released, off anything it's attached to
            try
            {
                var clamps = new List<LaunchClamp>();
                foreach (var p in vessel.parts) { var lc = p.FindModuleImplementing<LaunchClamp>(); if (lc != null) clamps.Add(lc); }
                foreach (var lc in clamps) lc.Release();
                if (part.parent != null) part.decouple(0f);
                foreach (var c in new List<Part>(part.children)) c.decouple(0f);
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Starlance: couldn't free the missile: " + e.Message); }

            Vessel v = part.vessel;
            CelestialBody here = v.mainBody;
            v.vesselName = $"Starlance > {tName}";
            v.vesselType = VesselType.Probe;
            KeepLoaded(v);

            if (InOrbit(here))
            {
                Debug.Log($"[BDNukes]: Starlance launched from orbit of {here.bodyName} at {target}.");
                BeginDrive();
                statusLine = $"Starlance: already in orbit, light drive charging for {tName} on {tBody}";
            }
            else
            {
                phase = "Ascent";
                ignitionAt = Time.time + 0.3f;
                throttle = 0f;
                PlayOneShot(launchClip, 1f);
                Debug.Log($"[BDNukes]: Starlance launched from {here.bodyName} at {target}; parking orbit {ParkAlt(here) / 1000d:0} km.");
                statusLine = $"Starlance: ascent to a {ParkAlt(here) / 1000d:0} km parking orbit, then the light drive to {tBody}";
            }
            return v;
        }

        /// <summary>Keeps the missile in physics while it climbs out, however far the camera is (as NukeStrike does).</summary>
        static void KeepLoaded(Vessel v)
        {
            if (v == null || v.vesselRanges == null) return;
            float r = 2.5e6f;
            var s = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            v.vesselRanges.flying = s;
            v.vesselRanges.subOrbital = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            v.vesselRanges.orbit = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
            v.vesselRanges.escaping = new VesselRanges.Situation(r, r * 1.05f, r * 1.1f, r * 0.95f);
        }

        /// <summary>Re-entry vehicle i once released (a vessel the camera can follow), or null. Optional for the planner.</summary>
        public Vessel FollowWarhead(int i) => vessel != null ? InterplanetaryDirector.Warhead(vessel.id, i) : null;

        /// <summary>How many RVs have been released so far (0 before release).</summary>
        public int ReleasedWarheads => vessel != null ? (InterplanetaryDirector.Find(vessel.id)?.rvs.Count ?? 0) : 0;

        // ===================================================================================================
        // Editor info
        // ===================================================================================================

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Interplanetary strike missile (strike planner)");
            var st = Stages;
            for (int i = 0; i < st.Length; i++)
                sb.AppendLine($"Stage {i + 1}: {st[i].thrust:0} kN, Isp {st[i].ispSL:0}-{st[i].ispVac:0} s, {st[i].burnTime:0} s, {st[i].FullFuel:0.#} t propellant");
            sb.AppendLine($"Light drive: {lightFraction:0.##} x light speed setting");
            sb.AppendLine($"Warheads: {Math.Max(1, warheads)} x {warheadYield / 1000f:0.#} Mt within {spreadAngle:0}° of arc");
            sb.AppendLine("Needs a probe or antenna vessel orbiting the target body");
            return sb.ToString();
        }
    }
}
