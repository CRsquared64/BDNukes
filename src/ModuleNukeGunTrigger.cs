using System.Collections;
using System.Reflection;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// One-press firing for nuclear guns (the M29 Davy Crockett), like launching a missile.
    /// BDArmory's ModuleWeapon only fires while the weapon is enabled and the fire key or a hold
    /// action group is held. This module gives a single "Fire" button and action group: it enables the
    /// weapon if needed, holds BDArmory's own action-group fire flag until exactly one round has left
    /// the barrel, then releases it.
    /// </summary>
    public class ModuleNukeGunTrigger : PartModule
    {
        [KSPField] public string fireLabel = "Fire Nuclear Round";

        /// <summary>Give up if no round has left the barrel after this many seconds (reloading, no ammo, blocked).</summary>
        [KSPField] public float fireTimeout = 3f;

        [KSPField(guiActive = true, guiName = "Launcher")]
        public string status = "Ready";

        PartModule weapon;
        FieldInfo holdFiring, ammoCount, weaponState;
        MethodInfo enableWeapon;
        bool firing;

        public override void OnStart(StartState state)
        {
            Events["FireEvent"].guiName = fireLabel;
            Actions["FireAction"].guiName = fireLabel;
            if (!HighLogic.LoadedSceneIsFlight) return;

            foreach (PartModule pm in part.Modules)
            {
                if (pm != null && pm.moduleName == "ModuleWeapon") { weapon = pm; break; }
            }
            if (weapon == null)
            {
                status = "No weapon";
                return;
            }
            var t = weapon.GetType();
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            holdFiring = t.GetField("agHoldFiring", Any);
            ammoCount = t.GetField("ammoCount", Any);
            weaponState = t.GetField("weaponState", Any);
            enableWeapon = t.GetMethod("EnableWeapon", Any);
            if (holdFiring == null || ammoCount == null || enableWeapon == null)
            {
                status = "Unsupported BDArmory";
                Debug.LogWarning("[BDNukes]: ModuleNukeGunTrigger could not find ModuleWeapon's fire controls; manual firing disabled.");
            }
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Fire Nuclear Round")]
        public void FireEvent() { Fire(); }

        [KSPAction("Fire Nuclear Round")]
        public void FireAction(KSPActionParam param)
        {
            if (param.type == KSPActionType.Activate || param.type == KSPActionType.Toggle) Fire();
        }

        void Fire()
        {
            if (firing || weapon == null || holdFiring == null || ammoCount == null || enableWeapon == null) return;
            if (!vessel.isActiveVessel)
            {
                ScreenMessages.PostScreenMessage("Switch to this vessel to fire the " + part.partInfo.title, 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }
            StartCoroutine(FireOneRound());
        }

        IEnumerator FireOneRound()
        {
            firing = true;
            double before = (double)ammoCount.GetValue(weapon);
            if (before < 1d)
            {
                status = "Out of rounds";
                firing = false;
                yield break;
            }

            if (weaponState != null && weaponState.GetValue(weapon).ToString() != "Enabled")
                enableWeapon.Invoke(weapon, new object[] { false });

            status = "Firing";
            float deadline = Time.time + fireTimeout;
            while (Time.time < deadline && (double)ammoCount.GetValue(weapon) >= before)
            {
                holdFiring.SetValue(weapon, true);
                yield return null;
            }
            holdFiring.SetValue(weapon, false);

            bool fired = (double)ammoCount.GetValue(weapon) < before;
            status = fired ? "Reloading" : "Not ready";
            if (!fired) ScreenMessages.PostScreenMessage(part.partInfo.title + " did not fire (reloading or not ready)", 3f, ScreenMessageStyle.UPPER_CENTER);
            firing = false;
        }

        public override string GetInfo()
        {
            return "One-press manual fire: \"" + fireLabel + "\" button and action group.";
        }
    }
}
