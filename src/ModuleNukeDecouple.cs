using System.Collections;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Stock decoupler for bombs that also carry a BDArmory warhead module (e.g. ModuleNukeFuze).
    ///
    /// BDWarheadBase.OnStart calls part.force_activate(), which runs OnActive on every module of the part.
    /// On a stock ModuleDecouple that means "decouple now", so the bomb fell off the moment the vessel spawned,
    /// whatever the struts. This subclass ignores activations until the vessel has finished loading.
    /// Staging, the Decouple button and the Decouple action group all work as normal after that.
    /// </summary>
    public class ModuleNukeDecouple : ModuleDecouple
    {
        bool startupComplete;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (HighLogic.LoadedSceneIsFlight) StartCoroutine(FinishStartup());
        }

        IEnumerator FinishStartup()
        {
            // a couple of physics frames: past every module's OnStart and the vessel going off rails
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            while (vessel != null && (vessel.packed || vessel.HoldPhysics)) yield return new WaitForFixedUpdate();
            startupComplete = true;
        }

        public override void OnActive()
        {
            if (!startupComplete) return;
            base.OnActive();
        }
    }
}
