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
        [KSPField(isPersistant = true)] public int corrections;
        [KSPField(isPersistant = true)] public string sourceId = "";
        [KSPField(guiActive = true, guiName = "Propellant")] public string fuelStatus;
        StrikeTarget target;
        Vessel launcher;
        Vector3d required;
        double nextSolve, previousDistance = double.MaxValue;
        float retryAt;
        AudioSource motor;
        PartModule nuke;
        MethodInfo detonate;
        FieldInfo detonated;
        double cachedAt = -100;
        StrikeTarget cachedTarget;
        Vector3d cachedDelta;
        double cachedTime;
        bool cachedResult;

        Part IStrikeWeapon.StrikePart => part;
        string IStrikeWeapon.StrikeDescription => "Orbital vehicle: same body or a direct moon. " + phase;
        double AvailableDelta => specificImpulse * 9.80665 * Math.Log((part.mass + part.GetResourceMass()) /
            Math.Max(0.001, part.mass + part.GetResourceMass() - Math.Max(0, fuel)));

        bool IStrikeWeapon.CanReach(StrikeTarget t, out string why, out double flightTime)
        {
            flightTime = double.NaN;
            if (phase != "Idle") { why = "Already launched."; return false; }
            if (vessel == null || vessel.LandedOrSplashed || vessel.altitude <= vessel.mainBody.atmosphereDepth)
            { why = "Launch from space."; return false; }
            if (t.body != vessel.mainBody && (t.body?.orbit == null || t.body.orbit.referenceBody != vessel.mainBody))
            { why = "Only the current body and its direct moons are in reach."; return false; }
            double now = Planetarium.GetUniversalTime();
            if (now - cachedAt > 5 || t.body != cachedTarget.body || t.lat != cachedTarget.lat || t.lon != cachedTarget.lon || t.height != cachedTarget.height)
            {
                cachedAt = now; cachedTarget = t;
                cachedResult = StrikeTransfer.Plan(vessel, t, out cachedDelta, out cachedTime);
            }
            if (!cachedResult) { why = "No impact transfer found for this launch position."; return false; }
            flightTime = cachedTime;
            double margin = AvailableDelta - cachedDelta.magnitude - 100;
            why = $"Delta-v margin: {margin:0} m/s (100 m/s correction reserve).";
            return margin >= 0;
        }

        Vessel IStrikeWeapon.Launch(StrikeTarget t, out string status)
        {
            cachedAt = -100;
            if (!((IStrikeWeapon)this).CanReach(t, out status, out var duration)) return null;
            launcher = vessel; sourceId = vessel.id.ToString();
            target = t; targetBody = t.body.flightGlobalsIndex; targetLatitude = t.lat; targetLongitude = t.lon; burstHeight = t.height;
            launchUT = Planetarium.GetUniversalTime(); arrivalUT = launchUT + duration;
            required = cachedDelta; phase = "Separating";
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
                phase = "Burn";
                if (motor != null) motor.PlayOneShot(GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/launch"));
            }
            double distance = (target.World - vessel.CoMD).magnitude;
            if (vessel.mainBody == target.body && distance < Math.Max(15000, vessel.srfSpeed * 15))
            { phase = "Terminal"; DropStage(); }
            if (phase == "Terminal")
            {
                var relative = target.World - vessel.CoMD;
                var relVelocity = target.body.getRFrmVel(target.World) - vessel.obt_velocity;
                double closing = -Vector3d.Dot(relVelocity, relative.normalized);
                var losRate = Vector3d.Cross(relative, relVelocity) / Math.Max(1, relative.sqrMagnitude);
                var acceleration = 3 * Math.Max(0, closing) * Vector3d.Cross(losRate, relative.normalized);
                if (acceleration.sqrMagnitude > 0.01 && fuel > 0) Burn(acceleration, 0.3f);
                else Point(vessel.srf_velocity);
                double agl = vessel.altitude - Math.Max(target.body.ocean ? 0 : double.MinValue, vessel.terrainAltitude);
                double step = Math.Max(0, -vessel.verticalSpeed) * TimeWarp.fixedDeltaTime;
                if (now - launchUT > clearanceTime + 1 && (agl <= burstHeight + step + 2 ||
                    distance < Math.Max(10, vessel.srfSpeed * TimeWarp.fixedDeltaTime) ||
                    distance > previousDistance && previousDistance < 500)) Fire();
                previousDistance = distance;
                return;
            }
            if (phase == "Coast")
            {
                Point(vessel.obt_velocity);
                double fraction = (now - launchUT) / Math.Max(1, arrivalUT - launchUT);
                if (corrections < 2 && fraction >= (corrections == 0 ? 0.4 : 0.75))
                { corrections++; phase = "Correction"; nextSolve = 0; }
                else return;
            }
            TimeWarp.SetRate(0, true);
            if (now >= nextSolve)
            {
                nextSolve = now + 0.2;
                if (!StrikeTransfer.Solve(vessel, target, Math.Max(1, arrivalUT - now), out required))
                { phase = "Coast"; return; }
            }
            if (required.magnitude < 2 || fuel <= 0)
            {
                phase = "Coast";
                if (corrections >= 2 || fuel <= 0) DropStage();
                return;
            }
            Burn(required, Mathf.Clamp01((float)(required.magnitude * Math.Max(0.001, part.mass) / Math.Max(0.01, thrust) / 0.5)));
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

        void Burn(Vector3d direction, float throttle)
        {
            Point(direction);
            if (fuel <= 0 || Vector3d.Dot(part.transform.forward, direction.normalized) < 0.98) return;
            float dt = TimeWarp.fixedDeltaTime;
            float consumed = Math.Min(fuel, thrust * throttle / (specificImpulse * 9.80665f) * dt);
            if (consumed <= 0) return;
            float actualThrust = consumed * specificImpulse * 9.80665f / dt;
            fuel -= consumed;
            part.rb.AddForce(part.transform.forward * actualThrust, ForceMode.Force);
            SetThrottle(actualThrust / thrust);
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
}
