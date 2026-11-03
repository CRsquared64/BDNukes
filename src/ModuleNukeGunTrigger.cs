using System.Collections;
using System.Reflection;
using BDArmory.Utils;
using BDArmory.Weapons;
using HarmonyLib;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// One-press firing for nuclear guns (the M29 Davy Crockett), like launching a missile.
    /// BDArmory's ModuleWeapon only fires while the weapon is enabled and the fire key or a hold
    /// action group is held. This module gives a single "Fire" button and action group: it enables the
    /// weapon if needed, holds BDArmory's own action-group fire flag until exactly one round has left
    /// the barrel, then releases it.
    ///
    /// BDArmory also refuses to fire a player-controlled gun while the mouse is over any UI. Pressing the
    /// part-menu button leaves the mouse right on that menu, so while a trigger is firing,
    /// GUIUtils.CheckMouseIsOnGui reports false (see the patch below).
    /// </summary>
    public class ModuleNukeGunTrigger : PartModule
    {
        [KSPField] public string fireLabel = "Fire Nuclear Round";

        /// <summary>Give up if no round has left the barrel after this many seconds.</summary>
        [KSPField] public float fireTimeout = 3f;

        [KSPField(guiActive = true, guiName = "Launcher")]
        public string status = "Ready";

        /// <summary>Number of triggers currently holding fire; while above zero BDArmory's mouse-over-UI check is bypassed.</summary>
        internal static int firingCount;

        ModuleWeapon weapon;
        FieldInfo holdFiring;
        bool firing;

        public override void OnStart(StartState state)
        {
            Events["FireEvent"].guiName = fireLabel;
            Actions["FireAction"].guiName = fireLabel;
            if (!HighLogic.LoadedSceneIsFlight) return;

            weapon = part.FindModuleImplementing<ModuleWeapon>();
            holdFiring = AccessTools.Field(typeof(ModuleWeapon), "agHoldFiring");
            if (weapon == null) status = "No weapon";
            else if (holdFiring == null)
            {
                status = "Unsupported BDArmory";
                Debug.LogWarning("[BDNukes]: ModuleNukeGunTrigger could not find ModuleWeapon.agHoldFiring; manual firing disabled.");
            }
        }

        void OnDisable()
        {
            // never leave the UI bypass on if the part dies mid-shot
            if (firing) { firing = false; firingCount = Mathf.Max(0, firingCount - 1); }
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
            if (firing || weapon == null || holdFiring == null) return;
            if (!vessel.isActiveVessel)
            {
                Message("Switch to this vessel to fire the " + part.partInfo.title);
                return;
            }
            string reason = NotReadyReason();
            if (reason != null)
            {
                status = reason;
                Message(part.partInfo.title + ": " + reason);
                return;
            }
            StartCoroutine(FireOneRound());
        }

        /// <summary>The ModuleWeapon conditions that would stop a shot, in plain words; null when it can fire.</summary>
        string NotReadyReason()
        {
            if (weapon.ammoCount < 1d) return "Out of rounds";
            if (weapon.isReloading) return "Reloading";
            float gap = 60f / Mathf.Max(0.01f, weapon.roundsPerMinute) * TimeWarp.CurrentRate;
            float wait = gap - weapon.timeSinceFired;
            if (wait > 0f) return $"Reloading ({wait:0} s)";
            if (TimeWarp.WarpMode == TimeWarp.Modes.HIGH && TimeWarp.CurrentRate > 1f) return "Not during time warp";
            return null;
        }

        IEnumerator FireOneRound()
        {
            firing = true;
            firingCount++;
            double before = weapon.ammoCount;
            try
            {
                if (weapon.weaponState != ModuleWeapon.WeaponStates.Enabled) weapon.EnableWeapon();
                status = "Firing";
            }
            catch
            {
                firing = false;
                firingCount = Mathf.Max(0, firingCount - 1);
                throw;
            }

            float deadline = Time.time + fireTimeout;
            while (Time.time < deadline && weapon != null && weapon.ammoCount >= before)
            {
                holdFiring.SetValue(weapon, true);
                yield return null;
            }
            if (weapon != null) holdFiring.SetValue(weapon, false);
            firing = false;
            firingCount = Mathf.Max(0, firingCount - 1);
            if (weapon == null) yield break;

            if (weapon.ammoCount < before)
            {
                status = "Fired";
                yield break;
            }
            string reason = weapon.pointingAtSelf ? "Barrel blocked by own vessel" : (NotReadyReason() ?? "Did not fire");
            status = reason;
            Message(part.partInfo.title + ": " + reason);
        }

        void Message(string text)
        {
            ScreenMessages.PostScreenMessage(text, 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        public override string GetInfo()
        {
            return "One-press manual fire: \"" + fireLabel + "\" button and action group.";
        }
    }

    /// <summary>While a ModuleNukeGunTrigger is firing, the mouse on its part menu must not block the shot.</summary>
    [HarmonyPatch(typeof(GUIUtils), nameof(GUIUtils.CheckMouseIsOnGui))]
    internal static class Patch_GUIUtils_CheckMouseIsOnGui
    {
        static bool Prefix(ref bool __result)
        {
            if (ModuleNukeGunTrigger.firingCount <= 0) return true;
            __result = false;
            return false;
        }
    }
}
