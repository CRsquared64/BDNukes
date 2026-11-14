using System;
using System.Reflection;
using BDArmory.Utils;
using UnityEngine;

namespace BDNukes
{
    public class ModuleNukeStrikeVehicle : PartModule, IStrikeWeapon, IPartMassModifier
    {
        [KSPField] public float thrust = 8f;
        [KSPField] public float specificImpulse = 340f;
        [KSPField] public float propellantMass = 0.25f;
        [KSPField] public float stageMass = 0.04f;
        [KSPField] public float clearanceTime = 2f;
        /// <summary>Speed of the rod as a fraction of the mod's light speed setting (NukeSettings.waveSpeed).</summary>
        [KSPField] public float lightFraction = 1f;
        /// <summary>Seconds the streak of the strike stays drawn after impact.</summary>
        [KSPField] public float streakTime = 1.5f;
        [KSPField] public float attitudeGain = 4f;
        [KSPField] public float attitudeDamping = 3f;
        [KSPField] public float maxAngularAcceleration = 2f;
        [KSPField] public string stageTransform = "burnStage";
        [KSPField] public string motorSound = "BDArmory/Sounds/rocketLoop";
        [KSPField(isPersistant = true)] public float fuel = -1f;
        [KSPField(isPersistant = true, guiActive = true, guiName = "Flight phase")] public string phase = "Idle";
        [KSPField(isPersistant = true)] public int targetBody = -1;
        [KSPField(isPersistant = true)] public double targetLatitude, targetLongitude, burstHeight, launchUT, arrivalUT;
        [KSPField(isPersistant = true)] public bool stageDropped;
        [KSPField(isPersistant = true)] public string sourceId = "";
        [KSPField(guiActive = true, guiName = "Propellant")] public string fuelStatus;
        StrikeTarget target;
        Vessel launcher;
        float retryAt;
        AudioSource motor;
        PartModule nuke;
        MethodInfo detonate;
        FieldInfo detonated;

        Part IStrikeWeapon.StrikePart => part;
        string IStrikeWeapon.StrikeDescription => "Orbital strike rod: straight line at light-drive speed, same body or a direct moon. " + phase;
        double RodSpeed => Math.Max(1000d, Mathf.Clamp(lightFraction, 0.001f, 1f) * NukeScale.WaveSpeed);

        /// <summary>
        /// A straight line from the vehicle to the aim point: it must not pass through the planet it orbits, nor
        /// through the body the target is on (before the aim point).
        /// </summary>
        static bool LineClear(Vessel v, StrikeTarget t, out string why)
        {
            Vector3d from = v.CoMD, to = t.World;
            if (!Crosses(from, to, v.mainBody.position, v.mainBody.Radius - 50d, t.body == v.mainBody)) { }
            else { why = "No clear line to the target: the planet is in the way."; return false; }
            if (t.body != v.mainBody && Crosses(from, to, t.body.position, t.body.Radius, true))
            { why = "No clear line to the target: the target is on the far side of its body."; return false; }
            why = null;
            return true;
        }

        /// <summary>True if the segment enters the sphere before it ends (the end may sit on the sphere's surface).</summary>
        static bool Crosses(Vector3d a, Vector3d b, Vector3d centre, double radius, bool endOnSurface)
        {
            Vector3d d = b - a, f = a - centre;
            double aa = Vector3d.Dot(d, d);
            if (aa < 1d) return false;
            double bb = 2d * Vector3d.Dot(f, d), cc = Vector3d.Dot(f, f) - radius * radius;
            double disc = bb * bb - 4d * aa * cc;
            if (disc <= 0d) return false;
            double t1 = (-bb - Math.Sqrt(disc)) / (2d * aa);
            return t1 > 1e-6 && t1 < (endOnSurface ? 1d - 1e-5 : 1d);
        }

        bool IStrikeWeapon.CanReach(StrikeTarget t, out string why, out double flightTime)
        {
            flightTime = double.NaN;
            if (phase != "Idle") { why = "Already launched."; return false; }
            if (vessel == null || vessel.LandedOrSplashed || vessel.altitude <= vessel.mainBody.atmosphereDepth)
            { why = "Launch from space."; return false; }
            if (t.body != vessel.mainBody && (t.body?.orbit == null || t.body.orbit.referenceBody != vessel.mainBody))
            { why = "Only the current body and its direct moons are in reach."; return false; }
            if (!LineClear(vessel, t, out why)) return false;
            double distance = (t.World - vessel.CoMD).magnitude;
            flightTime = clearanceTime + distance / RodSpeed;
            why = $"Straight line, {NukeStrikeMath.Distance(distance)} at {RodSpeed / 1000d:0} km/s: {flightTime:0.0} s including the {clearanceTime:0} s separation.";
            return true;
        }

        Vessel IStrikeWeapon.Launch(StrikeTarget t, out string status)
        {
            if (!((IStrikeWeapon)this).CanReach(t, out status, out var duration)) return null;
            launcher = vessel; sourceId = vessel.id.ToString();
            target = t; targetBody = t.body.flightGlobalsIndex; targetLatitude = t.lat; targetLongitude = t.lon; burstHeight = t.height;
            launchUT = Planetarium.GetUniversalTime(); arrivalUT = launchUT + duration;
            phase = "Separating";
            TimeWarp.SetRate(0, true);
            part.decouple(1f);
            status = "Separating.";
            return part.vessel;
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (fuel < 0) fuel = propellantMass;
            if (!HighLogic.LoadedSceneIsFlight) return;
            launcher = FlightGlobals.Vessels.Find(v => v.id.ToString() == sourceId);
            target = new StrikeTarget { body = FlightGlobals.Bodies.Find(b => b.flightGlobalsIndex == targetBody),
                lat = targetLatitude, lon = targetLongitude, height = burstHeight, name = "Orbital target" };
            foreach (PartModule module in part.Modules)
                if (module.moduleName == "BDModuleNuke")
                {
                    nuke = module;
                    detonate = module.GetType().GetMethod("Detonate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    detonated = module.GetType().GetField("hasDetonated", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
            motor = gameObject.AddComponent<AudioSource>();
            motor.clip = GameDatabase.Instance.GetAudioClip(motorSound);
            motor.loop = true; motor.spatialBlend = 1; motor.minDistance = 8; motor.maxDistance = 2000;
            if (stageDropped) HideStage();
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => Math.Max(0, fuel < 0 ? propellantMass : fuel) + (stageDropped ? 0 : stageMass);
        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.CONSTANTLY;

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.packed || part.rb == null) return;
            fuelStatus = $"{Math.Max(0, fuel) * 1000:0.0} kg";
            SetThrottle(0);
            if (phase == "Idle" || phase == "Detonated" || phase == "Aborted" || !target.Valid) return;
            double now = Planetarium.GetUniversalTime();
            NukeStrike.KeepLoaded(vessel);
            if (phase == "Separating")
            {
                if (vessel == launcher)
                {
                    if (now - launchUT > 10) phase = "Aborted";
                    return;
                }
                if (now - launchUT < clearanceTime) return;
                vessel.vesselType = VesselType.Probe;
                phase = "Rod";
                DropStage();
                streak = RodStreak.Begin(vessel.mainBody, vessel.CoMD - vessel.mainBody.position);
                if (motor != null) motor.PlayOneShot(GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/launch"));
            }
            if (phase == "Rod") RodStep();
        }

        RodStreak streak;
        bool arrived;

        /// <summary>
        /// The rod: it is carried in a straight line to the aim point at light-drive speed, a step per physics frame
        /// (at the default setting that is the whole way in a frame or two from orbit). Real flight can't be simulated
        /// at that speed: the air alone would destroy it, so it is moved directly and detonates on arrival.
        /// </summary>
        void RodStep()
        {
            TimeWarp.SetRate(0, true);
            if (arrived) { Fire(); return; }   // the detonation did not take on the first try: keep at it
            Vector3d here = vessel.CoMD, aim = target.World;
            Vector3d to = aim - here;
            double distance = to.magnitude;
            double step = RodSpeed * TimeWarp.fixedDeltaTime;
            Point(to);
            SetThrottle(1f);
            if (distance <= step || distance < 2d)
            {
                MoveTo(aim);
                if (streak != null) streak.End(target.body, aim - target.body.position, streakTime);
                streak = null;
                SetThrottle(0f);
                arrived = true;
                Fire();
                return;
            }
            MoveTo(here + to / distance * step);
            if (streak != null) streak.Follow(target.body, vessel.mainBody, vessel.CoMD - vessel.mainBody.position);
        }

        void MoveTo(Vector3d world)
        {
            vessel.SetPosition(world);
            if (part.rb != null) { part.rb.velocity = Vector3.zero; part.rb.angularVelocity = Vector3.zero; }
        }

        void Point(Vector3d direction)
        {
            if (!StrikeTransfer.Finite(direction) || direction.sqrMagnitude < 1e-8) return;
            var nose = part.transform.forward;
            var wanted = ((Vector3)direction).normalized;
            var axis = Vector3.Cross(nose, wanted);
            if (axis.sqrMagnitude < 1e-8f && Vector3.Dot(nose, wanted) < 0) axis = part.transform.up;
            var error = axis.normalized * Mathf.Acos(Mathf.Clamp(Vector3.Dot(nose, wanted), -1, 1));
            part.rb.AddTorque(Vector3.ClampMagnitude(error * attitudeGain - part.rb.angularVelocity * attitudeDamping,
                maxAngularAcceleration), ForceMode.Acceleration);
        }

        void SetThrottle(float value)
        {
            part.SendMessage("SetPlumeThrottle", value, SendMessageOptions.DontRequireReceiver);
            if (motor == null) return;
            motor.volume = value * GameSettings.SHIP_VOLUME;
            if (value > 0 && !motor.isPlaying) motor.Play();
        }

        void HideStage()
        {
            var stage = part.FindModelTransform(stageTransform);
            if (stage != null) stage.gameObject.SetActive(false);
        }

        void DropStage()
        {
            if (stageDropped) return;
            stageDropped = true;
            var stage = part.FindModelTransform(stageTransform);
            if (stage == null) return;
            stage.SetParent(null, true);
            stage.gameObject.AddComponent<DecoupledBooster>().DecoupleBooster(part.rb.velocity, 3f);
        }

        void Fire()
        {
            if (phase == "Detonated" || nuke == null || detonate == null || Time.time < retryAt) return;
            retryAt = Time.time + 0.5f;
            try
            {
                nuke.GetType().GetField("Sourcevessel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(nuke, launcher != null ? launcher.vesselName : "Orbital strike");
                detonate.Invoke(nuke, null);
                if (detonated == null || detonated.GetValue(nuke) is bool done && done) phase = "Detonated";
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Orbital detonation failed: " + e.Message); }
        }

        void OnCollisionEnter(Collision collision)
        {
            if (phase == "Idle" || phase == "Separating" || phase == "Aborted" || !HighLogic.LoadedSceneIsFlight) return;
            var other = collision.collider != null ? collision.collider.GetComponentInParent<Part>() : null;
            if (other != null && (other.vessel == vessel || other.vessel == launcher)) return;
            Fire();
        }

        void OnDestroy() { if (motor != null) Destroy(motor); }
    }

    /// <summary>
    /// The line of the strike, drawn from where the rod left to where it is (and then to the aim point) and fading
    /// out after impact. Both ends are kept relative to a body's centre, so the line stays put as the world moves.
    /// </summary>
    internal class RodStreak : MonoBehaviour
    {
        LineRenderer line;
        CelestialBody fromBody, toBody;
        Vector3d fromRel, toRel;
        float endedAt = -1f, life = 1.5f;
        double length;

        public static RodStreak Begin(CelestialBody body, Vector3d rel)
        {
            var go = new GameObject("BDNukes rod streak");
            var rs = go.AddComponent<RodStreak>();
            rs.fromBody = rs.toBody = body; rs.fromRel = rs.toRel = rel;
            rs.line = go.AddComponent<LineRenderer>();
            Shader sh = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Sprites/Default");
            rs.line.material = new Material(sh);
            if (rs.line.material.HasProperty("_TintColor")) rs.line.material.SetColor("_TintColor", new Color(0.7f, 0.85f, 1f, 1f));
            rs.line.positionCount = 2;
            rs.line.useWorldSpace = true;
            rs.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rs.line.receiveShadows = false;
            return rs;
        }

        /// <summary>The rod is here (relative to the body it is flying over).</summary>
        public void Follow(CelestialBody target, CelestialBody over, Vector3d rel) { toBody = over; toRel = rel; }

        /// <summary>The rod hit: the line ends at the aim point and fades over `seconds`.</summary>
        public void End(CelestialBody body, Vector3d rel, float seconds)
        {
            toBody = body; toRel = rel; life = Mathf.Max(0.2f, seconds); endedAt = Time.time;
        }

        void LateUpdate()
        {
            if (line == null || fromBody == null || toBody == null) { Destroy(gameObject); return; }
            Vector3d a = fromBody.position + fromRel, b = toBody.position + toRel;
            length = (b - a).magnitude;
            float k = 1f;
            if (endedAt >= 0f)
            {
                k = 1f - (Time.time - endedAt) / life;
                if (k <= 0f) { Destroy(gameObject); return; }
            }
            line.SetPosition(0, (Vector3)a);
            line.SetPosition(1, (Vector3)b);
            // a thin bright thread from far away, thicker with length so it still reads from orbit
            float w = (float)Math.Min(60d, Math.Max(1.5d, length * 0.0004d)) * k;
            line.startWidth = w; line.endWidth = w * 0.15f;
            var c = new Color(0.8f, 0.9f, 1f, k);
            line.startColor = c; line.endColor = new Color(1f, 0.75f, 0.45f, k);
        }

        void OnDestroy() { if (line != null && line.material != null) Destroy(line.material); }
    }
}
