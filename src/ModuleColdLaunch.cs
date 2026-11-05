using System;
using BDArmory.Weapons.Missiles;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Cold launch for submarine-launched ballistic missiles. Fired while its nozzle is under water, the missile is
    /// ejected first (the gas generator / steam pop of a real SLBM tube): it is pushed straight up at ejectSpeed with
    /// the motor off, and the motor lights only once the nozzle is ignitionHeight above the surface.
    ///
    /// BDArmory reads MissileLauncher.dropTime once, when MissileLaunch() starts the missile's flight routine, and
    /// keeps the motor off and the steering idle for that long. So a Harmony prefix on MissileLaunch stretches
    /// dropTime to the time the ejection should take; this module then supplies the push during that time.
    /// Fired above water, or if anything is missing, it is an ordinary launch.
    /// </summary>
    public class ModuleColdLaunch : PartModule
    {
        [KSPField] public float ejectSpeed = 35f;       // m/s up while ejecting
        [KSPField] public float ignitionHeight = 10f;   // m: nozzle height above the water at ignition
        [KSPField] public float tailOffset = 5f;        // m from the part centre to the nozzle, along the missile axis
        [KSPField] public float maxEjectTime = 15f;     // s; the motor lights after this whatever happens
        [KSPField] public string axis = "Z";            // missile axis in part space (nose direction), as ModuleProceduralModel

        bool ejecting;
        float ejectEnd;

        Vector3 NoseDir
        {
            get
            {
                var t = part.transform;
                switch (axis.ToUpperInvariant())
                {
                    case "Y": return t.up;
                    case "X": return t.right;
                    default: return t.forward;
                }
            }
        }

        double NozzleAltitude()
        {
            var body = vessel != null ? vessel.mainBody : FlightGlobals.currentMainBody;
            Vector3 tail = part.transform.position - NoseDir * tailOffset;
            return FlightGlobals.getAltitudeAtPos(tail, body);
        }

        public override string GetInfo() =>
            $"Cold launch from under water: ejected at {ejectSpeed:0} m/s, motor lights {ignitionHeight:0} m above the surface.";

        /// <summary>Called just before BDArmory launches the missile. Returns the drop time to use, or -1 to leave it.</summary>
        internal float BeginLaunch()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.mainBody == null || !vessel.mainBody.ocean) return -1f;
            double nozzle = NozzleAltitude();
            if (nozzle >= 0d) return -1f;
            float t = Mathf.Min(maxEjectTime, (float)((ignitionHeight - nozzle) / Mathf.Max(1f, ejectSpeed)) + 0.5f);
            ejecting = true;
            ejectEnd = Time.time + t;
            ScreenMessages.PostScreenMessage("Cold launch: ejecting " + part.partInfo.title, 3f, ScreenMessageStyle.UPPER_CENTER);
            return t;
        }

        void FixedUpdate()
        {
            if (!ejecting || !HighLogic.LoadedSceneIsFlight) return;
            if (Time.time > ejectEnd || part.Rigidbody == null || vessel == null) { ejecting = false; return; }
            // until BDArmory has decoupled it, it's still part of the boat
            if (part.parent != null) return;
            if (NozzleAltitude() > ignitionHeight) { ejecting = false; return; }

            Vector3 up = (part.transform.position - vessel.mainBody.position).normalized;
            Vector3 v = part.Rigidbody.velocity;
            float vUp = Vector3.Dot(v, up);
            if (vUp < ejectSpeed) part.Rigidbody.AddForce(up * Mathf.Min(ejectSpeed - vUp, 3f), ForceMode.VelocityChange);
            // hold it upright on the way out
            part.Rigidbody.angularVelocity *= 0.8f;
        }
    }

    [HarmonyPatch(typeof(MissileLauncher), nameof(MissileLauncher.MissileLaunch))]
    internal static class Patch_MissileLauncher_ColdLaunch
    {
        static void Prefix(MissileLauncher __instance)
        {
            try
            {
                var cold = __instance.part != null ? __instance.part.FindModuleImplementing<ModuleColdLaunch>() : null;
                if (cold == null) return;
                float t = cold.BeginLaunch();
                if (t > 0f) __instance.dropTime = Mathf.Max(__instance.dropTime, t);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Cold launch failed, launching normally: " + e.Message);
            }
        }
    }
}
