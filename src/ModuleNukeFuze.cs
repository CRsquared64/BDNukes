using System;
using System.Reflection;
using System.Text;
using BDArmory.Weapons;
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

        PartModule nukeModule;
        MethodInfo nukeDetonate;
        float armTime;
        Vessel lastVessel;
        bool wasAboveAirburst;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            FindNuke();

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
                    range.stepIncrement = maxAirburstAltitude > 5000f ? 100f : 50f;
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
                return;
            }
            hasDetonated = true;
            if (fuseFailureRate > 0f && UnityEngine.Random.value < fuseFailureRate)
            {
                fuseFailed = true;
                return;
            }
            try
            {
                nukeDetonate.Invoke(nukeModule, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Nuke detonation failed: " + e);
            }
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

            if (airburstAltitude <= 0f || vessel.packed) return;

            double ground = vessel.terrainAltitude;
            if (vessel.mainBody.ocean && ground < 0d) ground = 0d;
            double agl = vessel.altitude - ground;

            if (agl > airburstAltitude + 150f) wasAboveAirburst = true;
            bool released = vessel != sourcevessel;
            if ((released || wasAboveAirburst) && Time.time > armTime && vessel.verticalSpeed < -1d && agl <= airburstAltitude)
            {
                Detonate();
            }
        }

        void OnCollisionEnter(Collision col)
        {
            if (!HighLogic.LoadedSceneIsFlight || !impactFuze || hasDetonated || !Armed) return;
            if (vessel == null || vessel.packed || Time.time < armTime) return;
            if (col.relativeVelocity.magnitude < impactFuzeSpeed) return;
            Part other = col.collider != null ? col.collider.GetComponentInParent<Part>() : null;
            if (other != null && other.vessel == vessel) return;
            Detonate();
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nuclear Fuze");
            sb.AppendLine($"Impact fuze: >{impactFuzeSpeed:0} m/s");
            sb.AppendLine("Proximity fuze (toggle in flight/editor)");
            sb.AppendLine("Airburst altitude fuze");
            sb.AppendLine("Detonate action group / button");
            if (detonateOnDestroyed) sb.AppendLine("Detonates if destroyed while armed");
            return sb.ToString();
        }
    }
}
