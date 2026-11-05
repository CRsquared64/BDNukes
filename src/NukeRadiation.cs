using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// The fallout radiation hazard (setting falloutRadiation). Twice a second every loaded vessel is checked
    /// against every active NukeFallout field:
    ///  - each kerbal on board (or on EVA) accumulates the dose; past sicknessDose there's a warning, and at
    ///    lethalDose the kerbal dies the stock way (removed from the part, then ProtoCrewMember.Die());
    ///  - probe cores lose electric charge in proportion to the dose rate (probeECDrain), and are otherwise fine.
    /// Doses are kept per kerbal name for the game session. The active vessel's dose rate drives a faint Geiger
    /// crackle and the dosimeter in the settings window.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeRadiation : MonoBehaviour
    {
        const float Tick = 0.5f;

        public static NukeRadiation Instance { get; private set; }

        /// <summary>Accumulated dose (Sv) per kerbal name, kept for the game session.</summary>
        static readonly Dictionary<string, float> doses = new Dictionary<string, float>();
        static readonly HashSet<string> warned = new HashSet<string>();

        /// <summary>Dose rate at the active vessel (Sv/h), updated every tick.</summary>
        public float ActiveRate { get; private set; }

        float nextTick;
        bool warnedRate;
        float nextClick;
        AudioSource geiger;
        readonly List<KeyValuePair<Part, ProtoCrewMember>> kills = new List<KeyValuePair<Part, ProtoCrewMember>>();

        public static float DoseOf(ProtoCrewMember c) => c != null && doses.TryGetValue(c.name, out float d) ? d : 0f;

        /// <summary>Writes the doses into the save (NukeDoseScenario), so they follow quicksaves, reverts and other saves.</summary>
        internal static void SaveDoses(ConfigNode node)
        {
            foreach (var kv in doses)
            {
                var n = node.AddNode("KERBAL");
                n.AddValue("name", kv.Key);
                n.AddValue("dose", kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("warned", warned.Contains(kv.Key));
            }
        }

        /// <summary>Replaces the doses with the ones in the save being loaded.</summary>
        internal static void LoadDoses(ConfigNode node)
        {
            doses.Clear();
            warned.Clear();
            foreach (var n in node.GetNodes("KERBAL"))
            {
                string name = n.GetValue("name");
                float d = 0f;
                bool w = false;
                if (string.IsNullOrEmpty(name) || !n.TryGetValue("dose", ref d) || d <= 0f) continue;
                doses[name] = d;
                if (n.TryGetValue("warned", ref w) && w) warned.Add(name);
            }
        }

        /// <summary>
        /// Adds a dose from another source (the artificial radiation belt) to a kerbal. With a part (loaded vessel)
        /// the usual sickness warning and death apply; without one (unloaded vessel) the dose only accumulates, and
        /// a lethal dose takes effect the next time the vessel is loaded and checked.
        /// </summary>
        internal static void AddDose(Part part, ProtoCrewMember c, float sv, bool warn)
        {
            if (c == null || c.rosterStatus == ProtoCrewMember.RosterStatus.Dead) return;
            var s = NukeSettings.Current;
            doses.TryGetValue(c.name, out float d0);
            float d = d0 + Mathf.Max(0f, sv);
            if (d > 0f) doses[c.name] = d;
            if (part == null) return;
            if (d >= s.lethalDose) Kill(part, c);
            else if (d >= s.sicknessDose && warned.Add(c.name) && warn)
                ScreenMessages.PostScreenMessage($"{c.displayName} has radiation sickness ({d:0.0} Sv)", 6f, ScreenMessageStyle.UPPER_CENTER);
        }

        void Awake()
        {
            Instance = this;
            var go = new GameObject("BDNukes_Geiger");
            go.transform.SetParent(transform, false);
            geiger = go.AddComponent<AudioSource>();
            geiger.playOnAwake = false;
            geiger.spatialBlend = 0f;
            geiger.loop = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Total dose rate (Sv/h) from every active fallout field at a vessel.</summary>
        static float RateAt(Vessel v)
        {
            double ground = v.terrainAltitude;
            if (v.mainBody != null && v.mainBody.ocean && ground < 0d) ground = 0d;
            float h = v.Landed || v.Splashed ? 0f : (float)(v.altitude - ground);
            float rate = 0f;
            Vector3 pos = v.CoMD;
            var list = NukeFallout.Active;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) rate += list[i].DoseRate(pos, Mathf.Max(0f, h));
            // the ground shine of old fallout footprints, long after their airborne fields have gone
            rate += NukeFootprint.DoseRate(v, Mathf.Max(0f, h));
            return rate;
        }

        void Update()
        {
            var s = NukeSettings.Current;
            if (Time.time >= nextTick)
            {
                float dt = nextTick > 0f ? Mathf.Min(Time.time - nextTick + Tick, 2f) : Tick;
                nextTick = Time.time + Tick;
                ActiveRate = 0f;
                if (s.falloutRadiation && (NukeFallout.Active.Count > 0 || NukeFootprint.AnyOn(FlightGlobals.currentMainBody))) TickDose(dt, s);
            }
            UpdateGeiger(s);
        }

        void TickDose(float dt, NukeSettings s)
        {
            kills.Clear();
            var vessels = FlightGlobals.VesselsLoaded;
            for (int vi = 0; vi < vessels.Count; vi++)
            {
                var v = vessels[vi];
                if (v == null || v.state == Vessel.State.DEAD) continue;
                float rate = RateAt(v);
                if (v == FlightGlobals.ActiveVessel) ActiveRate = rate;
                if (rate < 1e-4f) continue;
                float dose = rate * dt / 3600f;
                bool active = v == FlightGlobals.ActiveVessel;

                for (int pi = 0; pi < v.parts.Count; pi++)
                {
                    var part = v.parts[pi];
                    var crew = part.protoModuleCrew;
                    for (int ci = 0; ci < crew.Count; ci++)
                    {
                        var c = crew[ci];
                        if (c == null || c.rosterStatus == ProtoCrewMember.RosterStatus.Dead) continue;
                        doses.TryGetValue(c.name, out float d0);
                        float d = d0 + dose;
                        doses[c.name] = d;
                        if (d >= s.lethalDose) kills.Add(new KeyValuePair<Part, ProtoCrewMember>(part, c));
                        else if (d >= s.sicknessDose && warned.Add(c.name) && active)
                            ScreenMessages.PostScreenMessage($"{c.displayName} has radiation sickness ({d:0.0} Sv)", 6f, ScreenMessageStyle.UPPER_CENTER);
                    }
                    if (s.probeECDrain > 0f) DrainProbe(part, rate * s.probeECDrain * dt);
                }
            }
            for (int i = 0; i < kills.Count; i++) Kill(kills[i].Key, kills[i].Value);

            if (ActiveRate > 0.05f && !warnedRate)
            {
                warnedRate = true;
                ScreenMessages.PostScreenMessage($"Radiation! Fallout dose rate {FormatRate(ActiveRate)}", 5f, ScreenMessageStyle.UPPER_CENTER);
            }
            else if (ActiveRate < 0.01f) warnedRate = false;
        }

        static void DrainProbe(Part part, float ec)
        {
            if (ec <= 0f) return;
            var cmd = part.FindModuleImplementing<ModuleCommand>();
            if (cmd == null || cmd.minimumCrew > 0) return;
            part.RequestResource("ElectricCharge", (double)ec);
        }

        /// <summary>Stock-safe death: take the kerbal out of the part (seat, portrait, vessel crew list), then Die().</summary>
        static void Kill(Part part, ProtoCrewMember c)
        {
            try
            {
                var v = part.vessel;
                string msg = $"{c.displayName} has died of radiation poisoning ({DoseOf(c):0.0} Sv)";
                if (v != null && v.isEVA)
                {
                    // an EVA kerbal is its own part: the stock part death kills the kerbal with it
                    part.explode();
                }
                else
                {
                    part.RemoveCrewmember(c);
                    c.Die();
                    GameEvents.onCrewKilled.Fire(new EventReport(FlightEvents.CREW_KILLED, part, c.name, "radiation"));
                    if (v != null)
                    {
                        v.CrewListSetDirty();
                        GameEvents.onVesselCrewWasModified.Fire(v);
                    }
                }
                ScreenMessages.PostScreenMessage(msg, 8f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("[BDNukes]: " + msg);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[BDNukes]: Failed to kill irradiated kerbal " + c?.name + ": " + e);
            }
        }

        /// <summary>A faint Geiger crackle: random (Poisson) clicks, faster in hotter fallout.</summary>
        void UpdateGeiger(NukeSettings s)
        {
            // fallout and footprints, plus any radiation front from a burst in space passing over
            float rate = (s.falloutRadiation ? ActiveRate : 0f) + NukeRadiationWave.ActiveRate;
            if (!s.geigerCounter || rate < 0.005f || NukeAssets.Geiger == null) return;
            if (Time.time < nextClick) return;
            float cps = Mathf.Clamp(4f * Mathf.Sqrt(rate * 20f), 0.5f, 60f);
            nextClick = Time.time + -Mathf.Log(Mathf.Max(1e-3f, Random.value)) / cps;
            geiger.PlayOneShot(NukeAssets.Geiger, 0.25f * s.volume * GameSettings.SHIP_VOLUME * Random.Range(0.6f, 1f));
        }

        public static string FormatRate(float svPerHour)
        {
            if (svPerHour < 1f) return (svPerHour * 1000f).ToString("0.0") + " mSv/h";
            return svPerHour.ToString("0.00") + " Sv/h";
        }

        public static string FormatDose(float sv)
        {
            if (sv < 1f) return (sv * 1000f).ToString("0") + " mSv";
            return sv.ToString("0.00") + " Sv";
        }
    }

    /// <summary>
    /// Keeps the kerbals' accumulated doses in the save game. Without it they lived in a static table for the whole
    /// session: they leaked from one save into another and survived a quickload or revert to before the exposure,
    /// so a kerbal brought back by a revert could die again at once.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class NukeDoseScenario : ScenarioModule
    {
        public override void OnLoad(ConfigNode node) => NukeRadiation.LoadDoses(node);
        public override void OnSave(ConfigNode node) => NukeRadiation.SaveDoses(node);
    }
}
