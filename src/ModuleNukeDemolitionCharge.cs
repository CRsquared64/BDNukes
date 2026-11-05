using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Timer fuze for hand-placed atomic demolition munitions (the W54 SADM "backpack nuke").
    /// The charge is a cargo part: a kerbal takes it from a craft's inventory and drops or places it on EVA, where
    /// it becomes a vessel of its own. Set the timer (and the yield, via ModuleNukeYieldSelector), press Arm, and
    /// walk away: when the timer runs out the part's BDModuleNuke goes off, so BDArmory's own blast damage is used
    /// and the BDNukes cinematic plays.
    ///
    /// Arm / Disarm / timer / yield work from the part menu in flight, from an EVA kerbal standing next to it
    /// (unfocused part menu) and from action groups. The countdown is kept as a UT end time, so it survives
    /// save/load and vessel switching; a charge whose timer ran out while it was unloaded goes off as soon as it
    /// is loaded again.
    /// </summary>
    public class ModuleNukeDemolitionCharge : PartModule
    {
        /// <summary>Countdown length in seconds, set before arming.</summary>
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 4f,
                  guiName = "Timer", guiUnits = " s")]
        [UI_FloatRange(minValue = 10f, maxValue = 600f, stepIncrement = 10f, scene = UI_Scene.All)]
        public float timerSetting = 60f;

        [KSPField] public float minTimer = 10f;
        [KSPField] public float maxTimer = 600f;

        /// <summary>How close (m) an EVA kerbal has to be to use the part menu.</summary>
        [KSPField] public float interactionRange = 4f;

        [KSPField(isPersistant = true)] public bool armed;

        /// <summary>Universal time of detonation while armed.</summary>
        [KSPField(isPersistant = true)] public double detonateUT = -1d;

        [KSPField(guiActive = true, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, guiName = "Status")]
        public string status = "Safe";

        /// <summary>Countdown seconds at which an on-screen warning is posted.</summary>
        static readonly int[] Announce = { 60, 30, 10, 5, 4, 3, 2, 1 };

        // the stock timer steps offered by the EVA "Timer +/-" buttons
        static readonly float[] TimerSteps = { 10f, 20f, 30f, 45f, 60f, 90f, 120f, 180f, 240f, 300f, 420f, 600f };

        PartModule nuke;
        MethodInfo nukeDetonate;
        FieldInfo nukeHasDetonated;
        ModuleNukeYieldSelector yieldSelector;
        AudioSource audioSource;
        AudioClip armClip, disarmClip, beepClip;
        int lastAnnounced = int.MaxValue;
        float startTime;
        float retryTime;
        int retries;
        bool detonated;

        double UT => Planetarium.GetUniversalTime();
        double Remaining => armed ? detonateUT - UT : -1d;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            timerSetting = Mathf.Clamp(timerSetting, minTimer, maxTimer);
            foreach (var ctrl in new[] { Fields["timerSetting"].uiControlEditor, Fields["timerSetting"].uiControlFlight })
            {
                if (ctrl is UI_FloatRange range)
                {
                    range.minValue = minTimer;
                    range.maxValue = maxTimer;
                }
            }
            Fields["timerSetting"].guiUnfocusedRange = interactionRange;
            Fields["status"].guiUnfocusedRange = interactionRange;
            foreach (BaseEvent e in Events)
                if (e.guiActiveUnfocused) e.unfocusedRange = interactionRange;

            FindNuke();
            yieldSelector = part.FindModuleImplementing<ModuleNukeYieldSelector>();
            if (yieldSelector != null)
            {
                // let an EVA kerbal standing next to it dial the yield too
                var f = yieldSelector.Fields["selectedYield"];
                if (f != null) { f.guiActiveUnfocused = true; f.guiUnfocusedRange = interactionRange; }
                var r = yieldSelector.Fields["yieldReadout"];
                if (r != null) { r.guiActiveUnfocused = true; r.guiUnfocusedRange = interactionRange; }
            }
            else Events["CycleYieldEvent"].guiActiveUnfocused = false;

            if (!HighLogic.LoadedSceneIsFlight)
            {
                armed = false;
                detonateUT = -1d;
                UpdateUI();
                return;
            }

            startTime = Time.time;
            audioSource = part.gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 1f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 2f;
            audioSource.maxDistance = 60f;
            if (GameDatabase.Instance != null)
            {
                armClip = GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/armOn");
                disarmClip = GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/armOff");
                beepClip = GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/click");
            }

            if (armed)
            {
                // don't replay the warnings already passed before the save
                double rem = Remaining;
                lastAnnounced = int.MaxValue;
                foreach (int a in Announce) if (rem < a) lastAnnounced = a;
            }
            UpdateUI();
        }

        void FindNuke()
        {
            if (nuke != null) return;
            foreach (PartModule pm in part.Modules)
            {
                if (pm == null || pm.moduleName != "BDModuleNuke") continue;
                nuke = pm;
                nukeDetonate = pm.GetType().GetMethod("Detonate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                nukeHasDetonated = pm.GetType().GetField("hasDetonated", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                return;
            }
        }

        // ----------------------------------------------------------------------------------------
        // Part menu, EVA menu and action groups
        // ----------------------------------------------------------------------------------------

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, externalToEVAOnly = true,
                  requireFullControl = false, guiActiveUncommand = true, guiName = "Arm")]
        public void ArmEvent() { Arm(); }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, externalToEVAOnly = true,
                  requireFullControl = false, guiActiveUncommand = true, guiName = "Disarm", active = false)]
        public void DisarmEvent() { Disarm(); }

        [KSPEvent(guiActive = false, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, externalToEVAOnly = true,
                  requireFullControl = false, guiActiveUncommand = true, guiName = "Timer +")]
        public void TimerUpEvent() { StepTimer(+1); }

        [KSPEvent(guiActive = false, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, externalToEVAOnly = true,
                  requireFullControl = false, guiActiveUncommand = true, guiName = "Timer -")]
        public void TimerDownEvent() { StepTimer(-1); }

        [KSPEvent(guiActive = false, guiActiveEditor = false, guiActiveUnfocused = true, unfocusedRange = 4f, externalToEVAOnly = true,
                  requireFullControl = false, guiActiveUncommand = true, guiName = "Next Yield")]
        public void CycleYieldEvent() { CycleYield(); }

        [KSPAction("Arm Demolition Charge")]
        public void ArmAction(KSPActionParam param)
        {
            if (param.type == KSPActionType.Deactivate) Disarm();
            else Arm();
        }

        [KSPAction("Disarm Demolition Charge")]
        public void DisarmAction(KSPActionParam param) { Disarm(); }

        void Arm()
        {
            if (!HighLogic.LoadedSceneIsFlight || armed || detonated) return;
            if (nuke == null || nukeDetonate == null)
            {
                Message(Title + ": no warhead to arm");
                return;
            }
            timerSetting = Mathf.Clamp(timerSetting, minTimer, maxTimer);
            armed = true;
            detonateUT = UT + timerSetting;
            lastAnnounced = int.MaxValue;
            Play(armClip, 0.8f);
            Message($"{Title} ARMED: {Clock(timerSetting)} to detonation", 4f);
            UpdateUI();
        }

        void Disarm()
        {
            if (!armed || detonated) return;
            armed = false;
            detonateUT = -1d;
            Play(disarmClip, 0.8f);
            Message(Title + " disarmed", 3f);
            UpdateUI();
        }

        void StepTimer(int dir)
        {
            if (armed) return;
            float t = timerSetting;
            float next = dir > 0 ? maxTimer : minTimer;
            if (dir > 0) { foreach (float s in TimerSteps) if (s > t + 0.5f) { next = s; break; } }
            else { for (int i = TimerSteps.Length - 1; i >= 0; i--) if (TimerSteps[i] < t - 0.5f) { next = TimerSteps[i]; break; } }
            timerSetting = Mathf.Clamp(next, minTimer, maxTimer);
            Play(beepClip, 0.4f);
        }

        void CycleYield()
        {
            if (armed || yieldSelector == null) return;
            var f = yieldSelector.Fields["selectedYield"];
            var choose = f?.uiControlFlight as UI_ChooseOption;
            if (choose == null || choose.options == null || choose.options.Length == 0) return;
            int i = Array.IndexOf(choose.options, yieldSelector.selectedYield);
            yieldSelector.selectedYield = choose.options[(i + 1) % choose.options.Length]; // the selector applies it next frame
            Play(beepClip, 0.4f);
            Message(Title + " yield: " + yieldSelector.selectedYield, 2f);
        }

        // ----------------------------------------------------------------------------------------
        // Countdown
        // ----------------------------------------------------------------------------------------

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight || part == null) return;
            UpdateUI();
            if (!armed || detonated) return;

            double rem = Remaining;
            foreach (int a in Announce)
            {
                if (rem <= a && a < lastAnnounced && rem > a - 1)
                {
                    lastAnnounced = a;
                    Message($"{Title}: detonation in {Clock(a)}", a > 5 ? 3f : 1f);
                    Play(beepClip, a <= 5 ? 0.9f : 0.6f);
                    break;
                }
                if (rem <= a - 1 && a < lastAnnounced) lastAnnounced = a; // passed during warp or a lag spike
            }

            // the blast needs physics: come out of time warp just before it
            if (rem < 10d && TimeWarp.CurrentRateIndex > 0 && TimeWarp.fetch != null)
                TimeWarp.SetRate(0, true);

            CheckDetonate(rem);
        }

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !armed || detonated) return;
            CheckDetonate(Remaining);
        }

        void CheckDetonate(double rem)
        {
            if (rem > 0d) return;
            // a charge loaded with its timer already run out waits a moment for the scene (and BDArmory) to settle
            if (Time.time < startTime + 1.5f || Time.time < retryTime) return;
            FireNuke();
        }

        void FireNuke()
        {
            FindNuke();
            if (nuke == null || nukeDetonate == null)
            {
                Debug.LogWarning("[BDNukes]: " + Title + " has ModuleNukeDemolitionCharge but no BDModuleNuke to detonate.");
                armed = false;
                return;
            }
            detonated = true;
            try
            {
                nukeDetonate.Invoke(nuke, null);
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Demolition charge detonation failed: " + e);
            }
            if (nukeHasDetonated != null && nukeHasDetonated.GetValue(nuke) is bool done && !done && ++retries < 10)
            {
                // refused (e.g. BDArmory not ready yet): stay armed and try again shortly
                detonated = false;
                retryTime = Time.time + 0.5f;
                return;
            }
            armed = false;
        }

        // ----------------------------------------------------------------------------------------

        void UpdateUI()
        {
            bool flight = HighLogic.LoadedSceneIsFlight;
            Events["ArmEvent"].active = flight && !armed && !detonated;
            Events["DisarmEvent"].active = flight && armed && !detonated;
            Events["TimerUpEvent"].active = Events["TimerDownEvent"].active = flight && !armed && !detonated;
            Events["CycleYieldEvent"].active = flight && !armed && !detonated && yieldSelector != null;
            Fields["timerSetting"].guiActive = !armed;
            Fields["timerSetting"].guiActiveUnfocused = !armed;
            if (yieldSelector != null)
            {
                // the yield is fixed once armed
                var sel = yieldSelector.Fields["selectedYield"];
                var read = yieldSelector.Fields["yieldReadout"];
                if (sel != null) sel.guiActive = sel.guiActiveUnfocused = !armed;
                if (read != null) read.guiActive = read.guiActiveUnfocused = armed;
            }

            if (detonated) status = "Detonated";
            else if (armed) status = "ARMED  " + Clock(Math.Max(0d, Remaining));
            else status = "Safe";
        }

        static string Clock(double seconds)
        {
            int s = Mathf.CeilToInt((float)seconds);
            if (s < 60) return s + " s";
            return $"{s / 60}:{s % 60:00}";
        }

        string Title => part.partInfo != null ? part.partInfo.title : part.name;

        void Message(string text, float duration = 3f)
        {
            ScreenMessages.PostScreenMessage(text, duration, ScreenMessageStyle.UPPER_CENTER);
        }

        void Play(AudioClip clip, float volume)
        {
            if (audioSource == null || clip == null) return;
            audioSource.PlayOneShot(clip, volume * GameSettings.SHIP_VOLUME * NukeSettings.Current.volume);
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Atomic demolition timer");
            sb.AppendLine($"Timer: {Clock(minTimer)} to {Clock(maxTimer)}");
            sb.AppendLine("Arm / Disarm from the part menu, on EVA or by action group");
            sb.AppendLine("Carry in an inventory and place on EVA");
            return sb.ToString();
        }
    }
}
