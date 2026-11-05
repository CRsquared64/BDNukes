using System;
using System.Reflection;
using System.Text;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Fuze for standalone nuclear warheads. BDModuleNuke has no trigger of its own (it only fires when a
    /// MissileLauncher or BDModularGuidance calls it), so a bare warhead could never go off.
    /// This module inherits BDArmory's warhead base for Arm/Disarm, IFF, proximity fuze, the Detonate
    /// button and action group, and adds impact and airburst-altitude fuzes. It fires the part's
    /// BDModuleNuke so BDArmory's own blast/thermal/EMP damage is used.
    /// </summary>
    public class ModuleNukeFuze : BDWarheadBase
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Impact Fuze")]
        [UI_Toggle(disabledText = "Off", enabledText = "On", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public bool impactFuze = true;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Airburst Altitude", guiUnits = "m")]
        [UI_FloatRange(minValue = 0f, maxValue = 5000f, stepIncrement = 50f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public float airburstAltitude = 0f;

        /// <summary>Minimum relative impact speed (m/s) that trips the impact fuze.</summary>
        [KSPField] public float impactFuzeSpeed = 12f;

        /// <summary>Detonate when the part is destroyed (crash, gunfire, overheat) while armed.</summary>
        [KSPField] public bool detonateOnDestroyed = true;

        /// <summary>Upper limit for the proximity fuze slider (BDArmory's default is only 100 m).</summary>
        [KSPField] public float maxProximityRange = 3000f;

        /// <summary>Upper limit for the airburst altitude slider.</summary>
        [KSPField] public float maxAirburstAltitude = 5000f;

        /// <summary>Seconds after flight start or release before the impact and airburst fuzes go live.</summary>
        [KSPField] public float armingDelay = 3f;

        /// <summary>
        /// Laydown fuze: once armed and released, touching down (landed, splashed, or a contact slower than
        /// laydownMaxSpeed) starts this countdown, then the warhead detonates. 0 = off.
        /// </summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Laydown Delay", guiUnits = "s")]
        [UI_FloatRange(minValue = 0f, maxValue = 120f, stepIncrement = 1f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public float laydownDelay = 0f;

        /// <summary>While laydown is active, contacts slower than this start the countdown and only faster ones trip the impact fuze.</summary>
        [KSPField] public float laydownMaxSpeed = 60f;

        [KSPField(guiActive = false, guiActiveEditor = false, guiName = "Laydown")]
        public string laydownStatus = "";

        /// <summary>
        /// Hydrostatic depth fuze (depth bombs): once released and armed, detonate when the warhead sinks below this
        /// depth (m below sea level), or when it comes to rest on a shallower sea floor. 0 = off. Only shown, and only
        /// active, on parts that set maxDepthFuze.
        /// </summary>
        [KSPField(isPersistant = true, guiActive = false, guiActiveEditor = false, guiName = "Depth Fuze", guiUnits = "m")]
        [UI_FloatRange(minValue = 0f, maxValue = 1000f, stepIncrement = 10f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public float depthFuze = 0f;

        /// <summary>Upper limit for the depth fuze slider; 0 (the default) means the part has no depth fuze.</summary>
        [KSPField] public float maxDepthFuze = 0f;

        /// <summary>Set once the weapon has left the vessel it started on (persists across save/load mid-fall).</summary>
        [KSPField(isPersistant = true)] public bool fuzeReleased;

        /// <summary>Remaining laydown countdown in seconds; negative while not counting down.</summary>
        [KSPField(isPersistant = true)] public float laydownRemaining = -1f;

        PartModule nukeModule;
        MethodInfo nukeDetonate;
        FieldInfo nukeHasDetonated;
        MissileBase missile;
        bool fuseRolled;
        float retryTime;
        float armTime;
        Vessel lastVessel;
        bool wasAboveAirburst;
        float bottomedTime;

        bool LaydownActive => laydownDelay > 0f;
        bool Released => fuzeReleased || (vessel != null && sourcevessel != null && vessel != sourcevessel);

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            FindNuke();
            missile = part.FindModuleImplementing<MissileBase>();

            foreach (var ctrl in new[] { Fields["detonationRange"].uiControlEditor, Fields["detonationRange"].uiControlFlight })
            {
                if (ctrl is UI_FloatRange range)
                {
                    range.maxValue = maxProximityRange;
                    range.stepIncrement = 10f;
                }
            }

            foreach (var ctrl in new[] { Fields["airburstAltitude"].uiControlEditor, Fields["airburstAltitude"].uiControlFlight })
            {
                if (ctrl is UI_FloatRange range)
                {
                    range.maxValue = maxAirburstAltitude;
                    range.stepIncrement = maxAirburstAltitude > 100000f ? 1000f : maxAirburstAltitude > 5000f ? 100f : 50f;
                }
            }

            bool hasDepthFuze = maxDepthFuze > 0f;
            Fields["depthFuze"].guiActive = Fields["depthFuze"].guiActiveEditor = hasDepthFuze;
            if (hasDepthFuze)
            {
                foreach (var ctrl in new[] { Fields["depthFuze"].uiControlEditor, Fields["depthFuze"].uiControlFlight })
                {
                    if (ctrl is UI_FloatRange range)
                    {
                        range.maxValue = maxDepthFuze;
                        range.stepIncrement = maxDepthFuze > 1000f ? 50f : 10f;
                    }
                }
            }

            if (!HighLogic.LoadedSceneIsFlight) return;

            // Swap the base class's destruction hook for one that respects detonateOnDestroyed.
            part.OnJustAboutToBeDestroyed = (Callback)Delegate.Remove(part.OnJustAboutToBeDestroyed, new Callback(DetonateIfPossible));
            part.OnJustAboutToBeDestroyed = (Callback)Delegate.Combine(part.OnJustAboutToBeDestroyed, new Callback(OnPartDestroyed));
            armTime = Time.time + armingDelay;
            lastVessel = vessel;
        }

        protected override void WarheadSpecificSetup() { }

        protected override void WarheadSpecificUISetup()
        {
            if (detonationRange < 0f) detonationRange = 500f;
        }

        public override void DetonateIfPossible()
        {
            if (hasDetonated || !Armed) return;
            FireNuke();
        }

        protected override void Detonate()
        {
            if (hasDetonated || !Armed) return;
            FireNuke();
        }

        void OnPartDestroyed()
        {
            if (detonateOnDestroyed) DetonateIfPossible();
        }

        void FindNuke()
        {
            if (nukeModule != null) return;
            foreach (PartModule pm in part.Modules)
            {
                if (pm != null && pm.moduleName == "BDModuleNuke")
                {
                    nukeModule = pm;
                    nukeDetonate = pm.GetType().GetMethod("Detonate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    nukeHasDetonated = pm.GetType().GetField("hasDetonated", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    return;
                }
            }
        }

        void FireNuke()
        {
            FindNuke();
            if (nukeModule == null || nukeDetonate == null)
            {
                Debug.LogWarning("[BDNukes]: " + part.partInfo?.title + " has ModuleNukeFuze but no BDModuleNuke to detonate.");
                hasDetonated = true; // nothing to fire: don't warn on every contact
                return;
            }
            if (Time.time < retryTime) return;
            // the fuze either works or it doesn't: roll once, not on every retry
            if (!fuseRolled)
            {
                fuseRolled = true;
                if (fuseFailureRate > 0f && UnityEngine.Random.value < fuseFailureRate)
                {
                    hasDetonated = true;
                    fuseFailed = true;
                    return;
                }
            }
            hasDetonated = true;
            try
            {
                nukeDetonate.Invoke(nukeModule, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Nuke detonation failed: " + e);
                return;
            }
            // BDModuleNuke refuses while its MissileLauncher is still Idle or in Drop. Stay live (and retry
            // a little later) instead of turning into a silent dud.
            if (nukeHasDetonated != null && nukeHasDetonated.GetValue(nukeModule) is bool done && !done)
            {
                hasDetonated = false;
                retryTime = Time.time + 0.5f;
            }
        }

        /// <summary>
        /// Still on the aircraft or rocket that carries it: a missile that hasn't been fired, or a warhead that hasn't
        /// left a vessel with a weapon manager or crew. The airburst fuze must not fire there just because the carrier
        /// came down through the burst height.
        /// </summary>
        bool OnCarrier()
        {
            if (Released) return false;
            if (missile != null) return !missile.HasFired;
            return vessel.GetCrewCount() > 0 || VesselModuleRegistry.GetMissileFire(vessel, true) != null;
        }

        public override void OnFixedUpdate()
        {
            base.OnFixedUpdate();
            if (!HighLogic.LoadedSceneIsFlight || hasDetonated || !Armed || vessel == null) return;

            if (vessel != lastVessel)
            {
                // Released from the carrier: give it a moment to clear before the fuzes go live.
                lastVessel = vessel;
                armTime = Time.time + armingDelay;
            }
            if (!fuzeReleased && sourcevessel != null && vessel != sourcevessel) fuzeReleased = true;

            UpdateLaydown();
            if (hasDetonated) return;

            UpdateDepthFuze();
            if (hasDetonated) return;

            if (airburstAltitude <= 0f || vessel.packed) return;

            double ground = vessel.terrainAltitude;
            if (vessel.mainBody.ocean && ground < 0d) ground = 0d;
            double agl = vessel.altitude - ground;

            if (agl > airburstAltitude + 150f) wasAboveAirburst = true;
            if ((Released || wasAboveAirburst) && Time.time > armTime && vessel.verticalSpeed < -1d && agl <= airburstAltitude && !OnCarrier())
            {
                Detonate();
            }
        }

        void UpdateDepthFuze()
        {
            if (maxDepthFuze <= 0f || depthFuze <= 0f || vessel.packed || vessel.mainBody == null || !vessel.mainBody.ocean) return;
            if (!Released || Time.time < armTime || OnCarrier()) return;
            double alt = FlightGlobals.getAltitudeAtPos(part.transform.position, vessel.mainBody);
            if (alt <= -depthFuze)
            {
                Detonate();
                return;
            }
            // Resting on a sea floor shallower than the set depth: fire there rather than never.
            if (alt < -2d && Math.Abs(vessel.verticalSpeed) < 0.3d)
            {
                bottomedTime += TimeWarp.fixedDeltaTime;
                if (bottomedTime > 3f) Detonate();
            }
            else bottomedTime = 0f;
        }

        void UpdateLaydown()
        {
            if (!LaydownActive)
            {
                laydownRemaining = -1f;
                return;
            }
            if (laydownRemaining < 0f)
            {
                if (!vessel.packed && Released && Time.time > armTime && vessel.LandedOrSplashed) StartLaydown();
                return;
            }
            laydownRemaining -= TimeWarp.fixedDeltaTime;
            if (laydownRemaining <= 0f)
            {
                laydownRemaining = 0f;
                Detonate();
            }
        }

        void StartLaydown()
        {
            if (laydownRemaining >= 0f) return;
            laydownRemaining = laydownDelay;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!HighLogic.LoadedSceneIsFlight) return;
            var field = Fields["laydownStatus"];
            field.guiActive = LaydownActive;
            if (!LaydownActive) return;
            if (hasDetonated) laydownStatus = fuseFailed ? "Dud" : "Detonated";
            else if (!Armed) laydownStatus = "Safe";
            else if (laydownRemaining >= 0f) laydownStatus = $"Detonation in {laydownRemaining:0.0} s";
            else if (!Released) laydownStatus = "Awaiting release";
            else if (Time.time < armTime) laydownStatus = $"Arming ({armTime - Time.time:0.0} s)";
            else laydownStatus = "Awaiting touchdown";
        }

        void OnCollisionEnter(Collision col)
        {
            if (!HighLogic.LoadedSceneIsFlight || hasDetonated || !Armed) return;
            if (vessel == null || vessel.packed || Time.time < armTime) return;
            Part other = col.collider != null ? col.collider.GetComponentInParent<Part>() : null;
            if (other != null && other.vessel == vessel) return;
            float speed = col.relativeVelocity.magnitude;

            if (LaydownActive && Released)
            {
                // Laydown: soft contacts start the countdown; only a hard hit trips the impact fuze.
                if (impactFuze && speed > Mathf.Max(laydownMaxSpeed, impactFuzeSpeed)) Detonate();
                else if (speed <= laydownMaxSpeed) StartLaydown();
                return;
            }

            if (!impactFuze || speed < impactFuzeSpeed) return;
            Detonate();
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nuclear Fuze");
            sb.AppendLine($"Impact fuze: >{impactFuzeSpeed:0} m/s");
            sb.AppendLine("Proximity fuze (toggle in flight/editor)");
            sb.AppendLine("Airburst altitude fuze");
            sb.AppendLine($"Laydown fuze: 0-120 s delay after touchdown (<{laydownMaxSpeed:0} m/s)");
            if (maxDepthFuze > 0f) sb.AppendLine($"Depth fuze: 0-{maxDepthFuze:0} m below sea level (or on the sea floor)");
            sb.AppendLine("Detonate action group / button");
            if (detonateOnDestroyed) sb.AppendLine("Detonates if destroyed while armed");
            return sb.ToString();
        }
    }
}
