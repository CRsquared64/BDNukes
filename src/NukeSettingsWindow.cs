using KSP.UI.Screens;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Stock toolbar (ApplicationLauncher) button in flight, with a procedurally drawn trefoil, that opens an IMGUI
    /// window for the quality preset, the main toggles and sliders, and a fallout dosimeter. Edits change the live
    /// settings and apply to new detonations; "Save" writes them to GameData/BDNukes/PluginData/Settings.cfg.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeSettingsWindow : MonoBehaviour
    {
        const string LockId = "BDNukesSettingsWindow";
        static Rect rect = new Rect(260f, 90f, 340f, 10f);
        static readonly string[] Qualities = { "Low", "Medium", "High", "Ultra", "Custom" };

        ApplicationLauncherButton button;
        Texture2D icon;
        bool show, uiHidden, locked;
        Vector2 scroll;
        string status = "";
        float statusUntil;
        int windowId;

        void Awake()
        {
            windowId = GetInstanceID();
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(RemoveButton);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
        }

        void Start()
        {
            if (ApplicationLauncher.Ready) AddButton();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(RemoveButton);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            RemoveButton(GameScenes.FLIGHT);
            SetLock(false);
            if (icon != null) Destroy(icon);
        }

        void OnHideUI() { uiHidden = true; }
        void OnShowUI() { uiHidden = false; }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            if (icon == null) icon = NukeAssets.MakeTrefoilIcon(38, new Color(0.88f, 0.88f, 0.88f, 1f));
            button = ApplicationLauncher.Instance.AddModApplication(() => show = true, () => show = false,
                null, null, null, null, ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, icon);
        }

        void RemoveButton(GameScenes scene)
        {
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        void SetLock(bool on)
        {
            if (on == locked) return;
            locked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        void OnGUI()
        {
            if (!show || uiHidden)
            {
                SetLock(false);
                return;
            }
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(windowId, rect, DrawWindow, "BDNukes", GUILayout.Width(340f));
            Vector2 mouse = Event.current.mousePosition;
            SetLock(rect.Contains(mouse));
        }

        void DrawWindow(int id)
        {
            var s = NukeSettings.Current;
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(560f, Screen.height * 0.7f)));

            GUILayout.Label("Graphics quality");
            int q = GUILayout.Toolbar((int)s.quality, Qualities);
            if (q != (int)s.quality)
            {
                // picking Custom brings back the saved custom values; only editing a value under a preset
                // (below) starts a new custom set from that preset
                s.quality = (NukeQuality)q;
                s.ApplyQuality();
            }

            // the preset-driven values: editing one switches to Custom
            var v = s.QualityValues;
            bool capDetail = Toggle("Cap detail billows", v.capDetail);
            bool collars = Toggle("Stem collars", v.stemCollars);
            bool rings = Toggle("Condensation rings", v.condensationRings);
            bool wilson = Toggle("Wilson cloud", v.wilsonCloud);
            bool aurora = Toggle("Artificial aurora", v.artificialAurora);
            bool fallout = Toggle("Fallout plume", v.falloutCloud);
            bool lightning = Toggle("Lightning in big clouds", v.lightning);
            bool scorch = Toggle("Scorched ground", v.scorchMarks);
            bool fires = Toggle("Lingering fires", v.lingeringFires);
            bool shadows = Toggle("Light shadows (slow)", v.lightShadows);
            float density = Slider("Particle density", v.particleDensity, 0.1f, 4f, "0.00");
            float texScale = Slider("Texture scale (0.5 / 1 / 2)", v.textureScale, 0.5f, 2f, "0.0");
            float lodStr = Slider("Burst LOD strength", v.lodStrength, 0f, 1.5f, "0.00");
            // snap to 100s only when the slider moves, so a cfg value like 9050 isn't rewritten every frame
            float maxPf = Slider("Max particles per burst", v.maxParticles, 500f, 30000f, "0");
            int maxP = maxPf == v.maxParticles ? v.maxParticles : Mathf.RoundToInt(maxPf / 100f) * 100;
            if (capDetail != v.capDetail || collars != v.stemCollars || rings != v.condensationRings || wilson != v.wilsonCloud
                || aurora != v.artificialAurora || fallout != v.falloutCloud || lightning != v.lightning || scorch != v.scorchMarks
                || fires != v.lingeringFires || shadows != v.lightShadows || !Mathf.Approximately(density, v.particleDensity)
                || !Mathf.Approximately(texScale, v.textureScale) || !Mathf.Approximately(lodStr, v.lodStrength) || maxP != v.maxParticles)
            {
                var c = s.BeginCustom();
                c.capDetail = capDetail; c.stemCollars = collars; c.condensationRings = rings; c.wilsonCloud = wilson;
                c.artificialAurora = aurora; c.falloutCloud = fallout; c.lightning = lightning; c.scorchMarks = scorch;
                c.lingeringFires = fires; c.lightShadows = shadows; c.particleDensity = density; c.textureScale = texScale;
                c.lodStrength = lodStr; c.maxParticles = maxP;
                s.Validate();
                s.ApplyQuality();
            }

            GUILayout.Space(6f);
            GUILayout.Label("Effect");
            s.enabled = Toggle("Cinematic effect enabled", s.enabled);
            s.replaceStockVisuals = Toggle("Hide BDArmory's stock nuke visuals", s.replaceStockVisuals);
            s.magneticStreamers = Toggle("Magnetic field streamers", s.magneticStreamers);
            s.burstLOD = Toggle("Burst LOD (many detonations)", s.burstLOD);
            s.groundAfterglow = Toggle("Ground afterglow", s.groundAfterglow);
            s.debrisTrails = Toggle("Debris smoke trails", s.debrisTrails);
            s.craftScorch = Toggle("Flash scorches nearby craft", s.craftScorch);
            s.sizeScale = Slider("Size scale", s.sizeScale, 0.25f, 3f, "0.00");
            s.cloudHeightScale = Slider("Cloud height scale", s.cloudHeightScale, 0.2f, 2f, "0.00");
            s.cloudRiseTime = Slider("Cloud rise time (s)", s.cloudRiseTime, 10f, 600f, "0");
            s.cloudLifetime = Slider("Cloud lifetime (s)", s.cloudLifetime, 30f, 1800f, "0");
            s.windSpeed = Slider("Wind speed (m/s)", s.windSpeed, 0f, 40f, "0.0");
            s.scorchLifetime = Slider("Scorch lifetime (s)", s.scorchLifetime, 60f, 7200f, "0");
            s.fireDuration = Slider("Fire duration (s)", s.fireDuration, 10f, 900f, "0");
            s.screenFlash = Slider("Screen flash", s.screenFlash, 0f, 1f, "0.00");
            s.cameraShake = Slider("Camera shake", s.cameraShake, 0f, 2f, "0.00");
            s.volume = Slider("Volume", s.volume, 0f, 2f, "0.00");

            GUILayout.Space(6f);
            GUILayout.Label("Volumetric clouds");
            s.volumetricClouds = Toggle("Raymarched cloud and fireball", s.volumetricClouds);
            s.fluidSim = Toggle("Simulated fireball (baked fluid sim)", s.fluidSim);
            s.velum = Toggle("Condensation veil in humid air", s.velum);
            s.shockDistortion = Slider("Shock wave light bending", s.shockDistortion, 0f, 3f, "0.00");
            GUILayout.Label("Cloud quality");
            int vq = GUILayout.Toolbar((int)s.volumetricQuality, Qualities);
            if (vq != (int)s.volumetricQuality)
            {
                // Custom keeps the values as they are now; a preset overwrites them
                s.volumetricQuality = (NukeQuality)vq;
                s.ApplyVolumetricQuality();
            }
            // the preset-driven values: editing one by hand switches to Custom
            bool cloudShadows = Toggle("Cloud shadow on the ground", s.cloudShadows);
            int volRes = Mathf.RoundToInt(Slider("Cloud resolution (1 full, 2 half, 3 third)", s.volumetricResolution, 1f, 3f, "0"));
            int volSteps = Mathf.RoundToInt(Slider("Raymarch steps (quality)", s.volumetricSteps, 16f, 128f, "0"));
            if (cloudShadows != s.cloudShadows || volRes != s.volumetricResolution || volSteps != s.volumetricSteps)
            {
                s.cloudShadows = cloudShadows; s.volumetricResolution = volRes; s.volumetricSteps = volSteps;
                s.volumetricQuality = NukeQuality.Custom;
            }
            s.adaptivePerformance = Toggle("Adaptive quality (hold the frame rate)", s.adaptivePerformance);
            s.targetFrameRate = Mathf.Round(Slider("Target frame rate (fps)", s.targetFrameRate, 20f, 144f, "0"));
            GUILayout.Label(NukeFXManager.AdaptiveStatus());
            s.fireballLighting = Toggle("Fireball lights the ground and craft", s.fireballLighting);

            GUILayout.Space(6f);
            GUILayout.Label("Atmosphere and weather");
            s.atmosphericHaze = Toggle("Distance haze on the cloud", s.atmosphericHaze);
            s.hazeVisibility = Slider("Haze visibility (m)", s.hazeVisibility, 5000f, 200000f, "0");
            s.weatherLighting = Toggle("Overcast dims the cloud (EVE clouds)", s.weatherLighting);
            s.cloudClearing = Toggle("Blasts clear EVE clouds", s.cloudClearing);
            s.nukeImpulseScale = Slider("Blast push on craft (x BDArmory)", s.nukeImpulseScale, 0f, 1f, "0.00");
            s.nukeMaxDeltaV = Slider("Max blast speed per hit (m/s, 0 = no cap)", s.nukeMaxDeltaV, 0f, 1000f, "0");

            GUILayout.Space(6f);
            GUILayout.Label("Terrain craters");
            s.craters = Toggle("Persistent craters from surface bursts", s.craters);
            s.craterScale = Slider("Crater size scale", s.craterScale, 0.25f, 3f, "0.00");
            s.craterPartialRebuild = Toggle("Fast partial terrain rebuild (off: full rebuild, freezes)", s.craterPartialRebuild);
            GUILayout.Label(NukeCraters.Status());
            if (GUILayout.Button("Fill in all craters")) NukeCraters.ClearAll();

            GUILayout.Space(6f);
            GUILayout.Label("Fallout radiation");
            s.falloutRadiation = Toggle("Radiation hazard (kills kerbals)", s.falloutRadiation);
            s.geigerCounter = Toggle("Geiger counter sound", s.geigerCounter);
            s.falloutDuration = Slider("Fallout duration (s, 20 kt)", s.falloutDuration, 60f, 3600f, "0");
            s.Validate();
            DrawDosimeter(s);

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
            {
                bool ok = s.Save();
                NukeAssets.RebuildTextures();
                Status(ok ? "Saved to PluginData/Settings.cfg" : "Save failed (see the log)");
            }
            if (GUILayout.Button("Revert"))
            {
                NukeSettings.Reload();
                NukeAssets.RebuildTextures();
                Status("Reloaded from file");
            }
            if (GUILayout.Button("Close"))
            {
                show = false;
                if (button != null) button.SetFalse(false);
            }
            GUILayout.EndHorizontal();
            if (Time.realtimeSinceStartup < statusUntil) GUILayout.Label(status);
            else GUILayout.Label("Changes apply to new detonations.");
            GUI.DragWindow();
        }

        void DrawDosimeter(NukeSettings s)
        {
            var rad = NukeRadiation.Instance;
            float rate = rad != null ? rad.ActiveRate : 0f;
            GUILayout.Label("Dosimeter: " + (s.falloutRadiation ? NukeRadiation.FormatRate(rate) : "off")
                + (NukeFallout.Active.Count > 0 ? $"  ({NukeFallout.Active.Count} fallout field{(NukeFallout.Active.Count == 1 ? "" : "s")})" : ""));
            var v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            var crew = v.GetVesselCrew();
            for (int i = 0; i < crew.Count; i++)
            {
                float d = NukeRadiation.DoseOf(crew[i]);
                string state = d >= s.lethalDose ? "lethal" : d >= s.sicknessDose ? "radiation sickness" : "ok";
                GUILayout.Label($"  {crew[i].displayName}: {NukeRadiation.FormatDose(d)} ({state})");
            }
            GUILayout.Label($"  Sickness at {s.sicknessDose:0.#} Sv, death at {s.lethalDose:0.#} Sv");
        }

        void Status(string msg)
        {
            status = msg;
            statusUntil = Time.realtimeSinceStartup + 4f;
        }

        static bool Toggle(string label, bool value) => GUILayout.Toggle(value, " " + label);

        static float Slider(string label, float value, float min, float max, string fmt)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(190f));
            GUILayout.Label(value.ToString(fmt), GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            // a cfg value outside the slider's range is kept unless the slider is actually moved
            float shown = Mathf.Clamp(value, min, max);
            float r = GUILayout.HorizontalSlider(shown, min, max);
            return r == shown ? value : r;
        }
    }
}
