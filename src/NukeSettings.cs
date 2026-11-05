using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace BDNukes
{
    internal enum NukeQuality { Low, Medium, High, Ultra, Custom }

    /// <summary>
    /// The settings a graphics quality preset drives. Custom uses the values from the cfg; the other
    /// presets replace them (the cfg values are kept so switching back to Custom restores them).
    /// </summary>
    internal class NukeQualityValues
    {
        public float particleDensity = 1f;
        public float textureScale = 1f;         // procedural texture resolution: 0.5, 1 or 2
        public bool capDetail = true;
        public bool stemCollars = true;
        public bool condensationRings = true;
        public bool wilsonCloud = true;
        public bool artificialAurora = true;
        public bool falloutCloud = true;
        public bool lightning = true;
        public bool scorchMarks = true;
        public bool lingeringFires = true;
        public bool lightShadows = false;
        public float lodStrength = 0.5f;        // burst LOD exponent: N detonations -> N^-lodStrength of the particles
        public int maxParticles = 9000;         // per detonation, including its fallout and fires

        public NukeQualityValues Clone() => (NukeQualityValues)MemberwiseClone();

        public static NukeQualityValues Preset(NukeQuality q)
        {
            switch (q)
            {
                case NukeQuality.Low:
                    return new NukeQualityValues
                    {
                        particleDensity = 0.35f, textureScale = 0.5f, capDetail = false, stemCollars = false, condensationRings = false,
                        wilsonCloud = false, artificialAurora = false, falloutCloud = false, lightning = false, scorchMarks = false,
                        lingeringFires = false, lightShadows = false, lodStrength = 1f, maxParticles = 2500,
                    };
                case NukeQuality.Medium:
                    return new NukeQualityValues
                    {
                        particleDensity = 0.65f, textureScale = 1f, capDetail = true, stemCollars = true, condensationRings = true,
                        wilsonCloud = true, artificialAurora = true, falloutCloud = true, lightning = false, scorchMarks = true,
                        lingeringFires = true, lightShadows = false, lodStrength = 0.75f, maxParticles = 5000,
                    };
                case NukeQuality.Ultra:
                    return new NukeQualityValues
                    {
                        particleDensity = 1.6f, textureScale = 2f, capDetail = true, stemCollars = true, condensationRings = true,
                        wilsonCloud = true, artificialAurora = true, falloutCloud = true, lightning = true, scorchMarks = true,
                        lingeringFires = true, lightShadows = true, lodStrength = 0.35f, maxParticles = 20000,
                    };
                default: // High: the 1.0 look
                    return new NukeQualityValues();
            }
        }
    }

    /// <summary>
    /// Loaded from the BDNUKES_SETTINGS node in GameData/BDNukes/Settings.cfg, then overridden by
    /// GameData/BDNukes/PluginData/Settings.cfg (written by the in-game settings window), if it exists.
    /// </summary>
    internal class NukeSettings
    {
        public bool enabled = true;
        public bool replaceStockVisuals = true;
        public float sizeScale = 1f;
        public float cloudHeightScale = 0.6f;
        public float cloudRiseTime = 90f;
        public float cloudLifetime = 300f;
        public float glowTimeScale = 1f;
        public float speedOfSound = 420f;
        public float screenFlash = 1f;
        public float screenTint = 1f;
        public float cameraShake = 1f;
        public float volume = 1f;
        public float billowRotationSign = 1f;
        public bool vacuumPhysics = true;
        public float vacuumDebrisSpeed = 4000f;
        public bool magneticStreamers = true;

        // graphics quality: the preset fills the fields below it unless it is Custom
        public NukeQuality quality = NukeQuality.High;
        public float particleDensity = 1f;
        public float textureScale = 1f;
        public bool capDetail = true;
        public bool stemCollars = true;            // transient condensation collars around the stem
        public bool condensationRings = true;
        public bool wilsonCloud = true;
        public bool artificialAurora = true;
        public bool falloutCloud = true;           // fallout plume visuals (the radiation hazard has its own switch)
        public bool lightning = true;
        public bool scorchMarks = true;
        public bool lingeringFires = true;
        public bool lightShadows = false;
        public float lodStrength = 0.5f;
        public int maxParticles = 9000;
        /// <summary>The cfg's own values for the preset-driven keys, used when quality = Custom.</summary>
        public NukeQualityValues custom = new NukeQualityValues();

        // cloud character and regimes
        public float tropopauseFraction = 0.16f;   // tropopause altitude as a fraction of atmosphereDepth
        public float pancakeStrength = 1f;         // how strongly caps that hit the tropopause flatten and spread
        public float lowYieldThreshold = 0.1f;     // kt; below this, squat dusty puff, short stem, no rings
        public bool waterBursts = true;            // Crossroads Baker style water column for surface/underwater bursts
        public float highAltitudeDensity = 0.05f;  // kg/m^3; below this the cloud turns into the high-altitude look
        public float vacuumLookDensity = 0.002f;   // kg/m^3; below this the look is pure vacuum (debris shell)
        public float regimeBlend = 0.35f;          // half-width (decades of density) of the blend between looks

        // many simultaneous bursts
        public bool mirvBus = true;                // ModuleMIRVBus releases its extra warheads
        public bool burstLOD = true;               // fewer particles per detonation when many are active
        public float minLODScale = 0.25f;          // floor on the particle-count scale
        public float soundMergeWindow = 0.4f;      // s; no new crack/rumble if another started this recently
        public float maxScreenFlash = 0.9f;        // cap on the combined screen flash

        // wind, fallout and radiation
        public float windSpeed = 8f;               // m/s near the ground; one steady direction per scene with slow veer
        public float windShear = 2f;               // wind aloft (cloud cap) = windSpeed * windShear
        public float falloutDuration = 600f;       // s for a 20 kt surface burst; yield-scaled
        public bool falloutRadiation = true;       // gameplay: dose to kerbals in the fallout footprint
        public float falloutDoseRate = 400f;       // Sv/h near ground zero 60 s after a 1 kt dirty surface burst (x W^0.25)
        public float sicknessDose = 1f;            // Sv: radiation sickness warning
        public float lethalDose = 6f;              // Sv: the kerbal dies
        public float probeECDrain = 0.02f;         // EC/s drained from probe cores per Sv/h (0 = unmanned vessels unaffected)
        public bool geigerCounter = true;          // faint Geiger crackle when the active vessel is in the fallout

        // ground effects and lightning
        public float scorchLifetime = 1800f;       // s before the burn scar has faded away
        public bool groundAfterglow = true;        // scorched ground glows dull red for a while (most visible at night)
        public float fireDuration = 240f;          // s the lingering fires burn (yield-scaled)
        public float lightningMinYield = 1000f;    // kt; bigger bursts get lightning in the cloud (more in humid air)

        // debris trails
        public bool debrisTrails = true;           // surface bursts on land throw out arcing chunks trailing dark smoke

        // volumetric clouds
        public bool volumetricClouds = true;       // raymarched cloud and fluid fireball (needs the shader bundle, Windows)
        public int volumetricSteps = 64;
        public bool fluidSim = true;
        public bool velum = true;
        public bool precursorDust = true;
        public bool groundFire = true;
        public bool persistentClouds = true;       // clouds survive scene changes (switch vessel, reload) and carry on             // fire burning on the ground under a low burst          // the thermal precursor: a hot dust layer racing ahead of the shock                  // the vast condensation veil over rising fireballs in humid air
        public float shockDistortion = 1f;         // how strongly the shock front bends the light behind it (0 = off)
        public bool cloudShadows = true;           // the volumetric cloud casts its shadow on the ground
        public float cloudShadowStrength = 0.6f;
        public int volumetricResolution = 2;       // 1 = full, 2 = half, 3 = third resolution (composited back with a depth-aware upsample)               // the fireball plays a baked fluid simulation (in the shader bundle)           // raymarch samples per pixel: quality vs GPU cost

        // volumetric quality preset and adaptive performance
        public NukeQuality volumetricQuality = NukeQuality.High; // sets volumetricSteps, volumetricResolution and cloudShadows; Custom keeps them as set
        public bool adaptivePerformance = true;    // while a cloud is drawn and the frame rate stays under target, drop steps, then resolution
        public float targetFrameRate = 50f;        // fps the adaptive quality tries to hold (unscaled frame time)

        // atmosphere and weather
        public bool atmosphericHaze = true;        // distant clouds wash out toward the sky colour (aerial perspective)
        public float hazeVisibility = 40000f;      // m of sea-level air that hides ~63% of the cloud's own colour
        public float nukeImpulseScale = 0.15f;     // scales BDArmory's blast push from nukes only (1 = BDArmory's own)
        public float nukeMaxDeltaV = 120f;         // m/s: most speed one nuclear hit can give a part (0 = no cap)
        public bool cloudClearing = true;          // blasts blow holes in EVE's volumetric cloud decks, which heal over minutes
        public bool weatherLighting = true;        // EVE volumetric overcast blocks the sun on the cloud (if EVE is installed)

        // early-time effects: rope tricks, Mach stem dust ring, cloud-deck flash
        public bool ropeTricks = true;             // tower/low bursts under ~100 kt: bright spikes run down the guy wires for the first instant
        public bool machStemDust = true;           // air bursts at 0.5-8 R: the Mach stem raises a thin, bright dust ring running out along the ground
        public bool cloudDeckFlash = true;         // EVE cloud decks near the burst light up with the fireball (white, then orange)

        // terrain craters
        public bool craters = true;                // surface bursts on land leave a persistent crater in the terrain
        public float craterScale = 1f;             // multiplies crater radius and depth
        public int maxCraters = 100;               // per body; the oldest is filled in beyond this
        public bool craterPartialRebuild = true;
        public bool protectSites = true;           // craters leave the space centre and other built-up sites' ground alone   // rebuild only the quads near the crater; off = RebuildSphere (multi-second freeze)
        // photo mode (Alt+F7 cycles normal / slow motion / frozen)
        public float photoModeSlowRate = 0.25f;    // effects speed in slow motion (1 = normal)
        // thermal scorch on craft
        public bool craftScorch = true;            // the flash chars the side of loaded vessels facing the fireball

        // ---- fireball lighting ----
        public bool fireballLighting = true;       // the fireball lights terrain and craft: a stronger point light plus a screen-space warm glow pass
        // ---- end fireball lighting ----

        // ---- high-altitude effects: artificial radiation belts and high-altitude EMP ----
        public bool radiationBelts = true;         // bursts above the atmosphere of a magnetised body inject a trapped-electron belt
        public string beltBodies = "Kerbin, Jool, Eve"; // bodies with a magnetosphere (comma separated)
        public bool beltAllAtmospheres = false;    // also give every other body with an atmosphere a magnetosphere
        public float beltMinAltitude = 70000f;     // m; lowest burst that feeds a belt (at least the top of the atmosphere)
        public float beltHalfLifeDays = 30f;       // Kerbin days; slow decay of the belt
        public float beltFastDecayDays = 1f;       // Kerbin days; e-folding time of the quickly lost part of the injection
        public float beltFastFraction = 0.6f;      // fraction of the injected electrons lost on the fast time scale
        public float beltDoseRate = 2f;            // Sv/h at the belt's heart right after a 1 Mt burst (linear in yield)
        public bool beltDamage = true;             // belt dose degrades solar panels and kills probe electronics
        public float beltPanelDose = 30f;          // Sv for solar panels to drop to 1/e of their output
        public float beltPanelMinEfficiency = 0.1f; // panels never drop below this fraction
        public float beltFailDose = 60f;           // Sv (+/-30% per part) at which an uncrewed probe core fails
        public float beltCrewShielding = 0.25f;    // fraction of the belt dose rate that reaches the crew inside the hull
        public bool hemp = true;                   // bursts at 30-400 km over an atmosphere EMP craft low down within the horizon circle
        public bool showRadiationBelts = true;     // draw the belts in map view and the tracking station

        void ReadBelts(ConfigNode n)
        {
            n.TryGetValue("radiationBelts", ref radiationBelts);
            n.TryGetValue("beltBodies", ref beltBodies);
            n.TryGetValue("beltAllAtmospheres", ref beltAllAtmospheres);
            n.TryGetValue("beltMinAltitude", ref beltMinAltitude);
            n.TryGetValue("beltHalfLifeDays", ref beltHalfLifeDays);
            n.TryGetValue("beltFastDecayDays", ref beltFastDecayDays);
            n.TryGetValue("beltFastFraction", ref beltFastFraction);
            n.TryGetValue("beltDoseRate", ref beltDoseRate);
            n.TryGetValue("beltDamage", ref beltDamage);
            n.TryGetValue("beltPanelDose", ref beltPanelDose);
            n.TryGetValue("beltPanelMinEfficiency", ref beltPanelMinEfficiency);
            n.TryGetValue("beltFailDose", ref beltFailDose);
            n.TryGetValue("beltCrewShielding", ref beltCrewShielding);
            n.TryGetValue("hemp", ref hemp);
            n.TryGetValue("showRadiationBelts", ref showRadiationBelts);
        }

        void ValidateBelts()
        {
            if (beltBodies == null) beltBodies = "";
            beltMinAltitude = Mathf.Max(0f, beltMinAltitude);
            beltHalfLifeDays = Mathf.Clamp(beltHalfLifeDays, 0.1f, 100000f);
            beltFastDecayDays = Mathf.Clamp(beltFastDecayDays, 0.01f, 1000f);
            beltFastFraction = Mathf.Clamp01(beltFastFraction);
            beltDoseRate = Mathf.Max(0f, beltDoseRate);
            beltPanelDose = Mathf.Max(0.1f, beltPanelDose);
            beltPanelMinEfficiency = Mathf.Clamp01(beltPanelMinEfficiency);
            beltFailDose = Mathf.Max(0.1f, beltFailDose);
            beltCrewShielding = Mathf.Clamp01(beltCrewShielding);
        }
        // ---- end high-altitude effects ----
        // ---- player tools: range overlay, cinematic camera, replay ----
        public bool cinematicCamera = false;         // the camera flies out to frame and orbit nearby detonations
        public float cinematicCameraDistance = 3.5f; // camera distance in cloud sizes (the larger of cap radius and half the cloud height)
        public float cinematicCameraOrbitPeriod = 240f; // s for one full orbit around the cloud
        public float cinematicCameraRange = 60000f;  // m: detonations further than this (or 25 cloud sizes, if more) from the active vessel are ignored
        public int rangeOverlayPoints = 128;         // points per effects-range ring
        // hotkeys, all with Alt held (KeyCode names). Alt+F9 is stock KSP's "load a named save", so the camera is on F6.
        public KeyCode rangeOverlayKey = KeyCode.F8;
        public KeyCode cinematicCameraKey = KeyCode.F6;
        public KeyCode replayKey = KeyCode.F10;

        /// <summary>True on the frame Alt + key goes down.</summary>
        public static bool AltKeyDown(KeyCode key) =>
            key != KeyCode.None && Input.GetKeyDown(key) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        void ReadPlayerTools(ConfigNode n)
        {
            n.TryGetValue("cinematicCamera", ref cinematicCamera);
            n.TryGetValue("cinematicCameraDistance", ref cinematicCameraDistance);
            n.TryGetValue("cinematicCameraOrbitPeriod", ref cinematicCameraOrbitPeriod);
            n.TryGetValue("cinematicCameraRange", ref cinematicCameraRange);
            n.TryGetValue("rangeOverlayPoints", ref rangeOverlayPoints);
            n.TryGetEnum("rangeOverlayKey", ref rangeOverlayKey, KeyCode.F8);
            n.TryGetEnum("cinematicCameraKey", ref cinematicCameraKey, KeyCode.F6);
            n.TryGetEnum("replayKey", ref replayKey, KeyCode.F10);
            cinematicCameraDistance = Mathf.Clamp(cinematicCameraDistance, 1.5f, 10f);
            cinematicCameraOrbitPeriod = Mathf.Clamp(cinematicCameraOrbitPeriod, 20f, 3600f);
            cinematicCameraRange = Mathf.Clamp(cinematicCameraRange, 1000f, 1000000f);
            rangeOverlayPoints = Mathf.Clamp(rangeOverlayPoints, 16, 512);
        }

        void SavePlayerTools(ConfigNode n)
        {
            n.AddValue("cinematicCamera", B(cinematicCamera));
            n.AddValue("cinematicCameraDistance", F(cinematicCameraDistance));
            n.AddValue("cinematicCameraOrbitPeriod", F(cinematicCameraOrbitPeriod));
            n.AddValue("cinematicCameraRange", F(cinematicCameraRange));
            n.AddValue("rangeOverlayPoints", rangeOverlayPoints.ToString(CultureInfo.InvariantCulture));
            n.AddValue("rangeOverlayKey", rangeOverlayKey.ToString());
            n.AddValue("cinematicCameraKey", cinematicCameraKey.ToString());
            n.AddValue("replayKey", replayKey.ToString());
        }
        // ---- end player tools ----
        // ---- world effects: flattened forests, fallout footprint, firestorm ----
        public bool flattenTrees = true;           // blasts flatten Parallax trees and bushes out to ~5 psi (all scatters over the crater); needs Parallax
        public bool falloutFootprint = true;       // ground bursts leave a downwind fallout hot zone: a faint ground tint and a slow dose, kept in the save
        public float footprintDays = 20f;          // game days the footprint takes to fade away
        public float footprintDose = 1f;           // multiplies the footprint's dose rate (0 = cosmetic only)
        public bool firestorm = true;              // big bursts over land start a firestorm within the thermal radius
        public float firestormMinYield = 10f;      // kt; smaller bursts only get the lingering fires
        public float firestormDuration = 600f;     // s the firestorm burns (yield-scaled)
        // ---- end world effects ----

        static NukeSettings current;

        public static NukeSettings Current
        {
            get
            {
                if (current == null) current = Load();
                return current;
            }
        }

        /// <summary>GameData/BDNukes/PluginData/Settings.cfg, next to the Plugins folder this DLL lives in.</summary>
        public static string UserFile
        {
            get
            {
                string plugins = Path.GetDirectoryName(typeof(NukeSettings).Assembly.Location) ?? ".";
                return Path.Combine(Path.Combine(Path.GetDirectoryName(plugins) ?? plugins, "PluginData"), "Settings.cfg");
            }
        }

        public static void Reload() { current = Load(); }

        static NukeSettings Load()
        {
            var s = new NukeSettings();
            if (GameDatabase.Instance != null)
            {
                var nodes = GameDatabase.Instance.GetConfigNodes("BDNUKES_SETTINGS");
                if (nodes != null && nodes.Length > 0) s.Read(nodes[0]);
            }
            // PluginData isn't in the GameDatabase, so the user file is read directly
            try
            {
                string path = UserFile;
                if (File.Exists(path))
                {
                    var root = ConfigNode.Load(path);
                    var n = root?.GetNode("BDNUKES_SETTINGS");
                    if (n != null)
                    {
                        s.Read(n);
                        Debug.Log("[BDNukes]: User settings loaded from PluginData.");
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't read PluginData settings: " + e.Message); }

            s.Validate();
            s.ApplyQuality();
            s.ApplyVolumetricQuality();
            Debug.Log("[BDNukes]: Settings loaded (quality " + s.quality + ").");
            return s;
        }

        void Read(ConfigNode n)
        {
            n.TryGetValue("enabled", ref enabled);
            n.TryGetValue("replaceStockVisuals", ref replaceStockVisuals);
            n.TryGetValue("sizeScale", ref sizeScale);
            n.TryGetValue("cloudHeightScale", ref cloudHeightScale);
            n.TryGetValue("cloudRiseTime", ref cloudRiseTime);
            n.TryGetValue("cloudLifetime", ref cloudLifetime);
            n.TryGetValue("glowTimeScale", ref glowTimeScale);
            n.TryGetValue("speedOfSound", ref speedOfSound);
            n.TryGetValue("screenFlash", ref screenFlash);
            n.TryGetValue("screenTint", ref screenTint);
            n.TryGetValue("cameraShake", ref cameraShake);
            n.TryGetValue("volume", ref volume);
            n.TryGetValue("billowRotationSign", ref billowRotationSign);
            n.TryGetValue("vacuumPhysics", ref vacuumPhysics);
            n.TryGetValue("vacuumDebrisSpeed", ref vacuumDebrisSpeed);
            n.TryGetValue("magneticStreamers", ref magneticStreamers);

            string q = n.GetValue("quality");
            if (!string.IsNullOrEmpty(q))
            {
                try { quality = (NukeQuality)Enum.Parse(typeof(NukeQuality), q.Trim(), true); }
                catch (Exception) { Debug.LogWarning("[BDNukes]: Unknown quality '" + q + "', using " + quality); }
            }
            var c = custom;
            n.TryGetValue("particleDensity", ref c.particleDensity);
            n.TryGetValue("textureScale", ref c.textureScale);
            n.TryGetValue("capDetail", ref c.capDetail);
            n.TryGetValue("stemCollars", ref c.stemCollars);
            n.TryGetValue("condensationRings", ref c.condensationRings);
            n.TryGetValue("wilsonCloud", ref c.wilsonCloud);
            n.TryGetValue("artificialAurora", ref c.artificialAurora);
            n.TryGetValue("falloutCloud", ref c.falloutCloud);
            n.TryGetValue("lightning", ref c.lightning);
            n.TryGetValue("scorchMarks", ref c.scorchMarks);
            n.TryGetValue("lingeringFires", ref c.lingeringFires);
            n.TryGetValue("lightShadows", ref c.lightShadows);
            n.TryGetValue("lodStrength", ref c.lodStrength);
            n.TryGetValue("maxParticles", ref c.maxParticles);

            n.TryGetValue("tropopauseFraction", ref tropopauseFraction);
            n.TryGetValue("pancakeStrength", ref pancakeStrength);
            n.TryGetValue("lowYieldThreshold", ref lowYieldThreshold);
            n.TryGetValue("waterBursts", ref waterBursts);
            n.TryGetValue("highAltitudeDensity", ref highAltitudeDensity);
            n.TryGetValue("vacuumLookDensity", ref vacuumLookDensity);
            n.TryGetValue("regimeBlend", ref regimeBlend);
            n.TryGetValue("mirvBus", ref mirvBus);
            n.TryGetValue("burstLOD", ref burstLOD);
            n.TryGetValue("minLODScale", ref minLODScale);
            n.TryGetValue("soundMergeWindow", ref soundMergeWindow);
            n.TryGetValue("maxScreenFlash", ref maxScreenFlash);

            n.TryGetValue("windSpeed", ref windSpeed);
            n.TryGetValue("windShear", ref windShear);
            n.TryGetValue("falloutDuration", ref falloutDuration);
            n.TryGetValue("falloutRadiation", ref falloutRadiation);
            n.TryGetValue("falloutDoseRate", ref falloutDoseRate);
            n.TryGetValue("sicknessDose", ref sicknessDose);
            n.TryGetValue("lethalDose", ref lethalDose);
            n.TryGetValue("probeECDrain", ref probeECDrain);
            n.TryGetValue("geigerCounter", ref geigerCounter);
            n.TryGetValue("scorchLifetime", ref scorchLifetime);
            n.TryGetValue("groundAfterglow", ref groundAfterglow);
            n.TryGetValue("fireDuration", ref fireDuration);
            n.TryGetValue("lightningMinYield", ref lightningMinYield);
            n.TryGetValue("debrisTrails", ref debrisTrails);
            n.TryGetValue("volumetricClouds", ref volumetricClouds);
            n.TryGetValue("volumetricSteps", ref volumetricSteps);
            n.TryGetValue("fluidSim", ref fluidSim);
            n.TryGetValue("velum", ref velum);
            n.TryGetValue("precursorDust", ref precursorDust);
            n.TryGetValue("groundFire", ref groundFire);
            n.TryGetValue("persistentClouds", ref persistentClouds);
            n.TryGetValue("photoModeSlowRate", ref photoModeSlowRate);
            n.TryGetValue("craftScorch", ref craftScorch);
            n.TryGetValue("volumetricResolution", ref volumetricResolution);
            n.TryGetValue("cloudShadows", ref cloudShadows);
            n.TryGetValue("shockDistortion", ref shockDistortion);
            n.TryGetValue("cloudShadowStrength", ref cloudShadowStrength);
            n.TryGetValue("atmosphericHaze", ref atmosphericHaze);
            n.TryGetValue("hazeVisibility", ref hazeVisibility);
            n.TryGetValue("weatherLighting", ref weatherLighting);
            n.TryGetValue("cloudClearing", ref cloudClearing);
            // early-time effects
            n.TryGetValue("ropeTricks", ref ropeTricks);
            n.TryGetValue("machStemDust", ref machStemDust);
            n.TryGetValue("cloudDeckFlash", ref cloudDeckFlash);
            n.TryGetValue("nukeImpulseScale", ref nukeImpulseScale);
            n.TryGetValue("nukeMaxDeltaV", ref nukeMaxDeltaV);
            n.TryGetValue("craters", ref craters);
            n.TryGetValue("craterScale", ref craterScale);
            n.TryGetValue("maxCraters", ref maxCraters);
            n.TryGetValue("craterPartialRebuild", ref craterPartialRebuild);
            n.TryGetValue("protectSites", ref protectSites);

            string vq = n.GetValue("volumetricQuality");
            if (!string.IsNullOrEmpty(vq))
            {
                try { volumetricQuality = (NukeQuality)Enum.Parse(typeof(NukeQuality), vq.Trim(), true); }
                catch (Exception) { Debug.LogWarning("[BDNukes]: Unknown volumetricQuality '" + vq + "', using " + volumetricQuality); }
            }
            // a file that sets the values by hand but names no preset (older user settings) keeps them
            else if (n.HasValue("volumetricSteps") || n.HasValue("volumetricResolution") || n.HasValue("cloudShadows"))
                volumetricQuality = NukeQuality.Custom;
            n.TryGetValue("adaptivePerformance", ref adaptivePerformance);
            n.TryGetValue("targetFrameRate", ref targetFrameRate);
            n.TryGetValue("fireballLighting", ref fireballLighting);
            ReadBelts(n); // high-altitude effects
            ReadPlayerTools(n);
            // ---- world effects ----
            n.TryGetValue("flattenTrees", ref flattenTrees);
            n.TryGetValue("falloutFootprint", ref falloutFootprint);
            n.TryGetValue("footprintDays", ref footprintDays);
            n.TryGetValue("footprintDose", ref footprintDose);
            n.TryGetValue("firestorm", ref firestorm);
            n.TryGetValue("firestormMinYield", ref firestormMinYield);
            n.TryGetValue("firestormDuration", ref firestormDuration);
            // ---- end world effects ----
        }

        /// <summary>The volumetric preset's steps, resolution divisor and shadows. False for Custom.</summary>
        public static bool VolumetricPreset(NukeQuality q, out int steps, out int resolution, out bool shadows)
        {
            switch (q)
            {
                case NukeQuality.Low: steps = 32; resolution = 3; shadows = false; return true;
                case NukeQuality.Medium: steps = 48; resolution = 2; shadows = true; return true;
                case NukeQuality.High: steps = 64; resolution = 2; shadows = true; return true;
                case NukeQuality.Ultra: steps = 96; resolution = 1; shadows = true; return true;
                default: steps = 64; resolution = 2; shadows = true; return false;
            }
        }

        /// <summary>Copies the volumetric preset (unless Custom) into volumetricSteps, volumetricResolution and cloudShadows.</summary>
        public void ApplyVolumetricQuality()
        {
            if (!VolumetricPreset(volumetricQuality, out int st, out int res, out bool sh)) return;
            volumetricSteps = st;
            volumetricResolution = res;
            cloudShadows = sh;
        }

        /// <summary>Clamps everything to sane ranges. Also called by the settings window after every change.</summary>
        public void Validate()
        {
            sizeScale = Mathf.Max(0.05f, sizeScale);
            cloudRiseTime = Mathf.Max(5f, cloudRiseTime);
            cloudLifetime = Mathf.Max(cloudRiseTime + 10f, cloudLifetime);
            glowTimeScale = Mathf.Max(0.1f, glowTimeScale);
            speedOfSound = Mathf.Max(50f, speedOfSound);
            vacuumDebrisSpeed = Mathf.Max(100f, vacuumDebrisSpeed);
            ValidateQuality(custom);
            tropopauseFraction = Mathf.Clamp(tropopauseFraction, 0.02f, 1f);
            pancakeStrength = Mathf.Clamp(pancakeStrength, 0f, 3f);
            lowYieldThreshold = Mathf.Max(0f, lowYieldThreshold);
            highAltitudeDensity = Mathf.Clamp(highAltitudeDensity, 1e-5f, 1f);
            vacuumLookDensity = Mathf.Clamp(vacuumLookDensity, 1e-6f, highAltitudeDensity);
            regimeBlend = Mathf.Clamp(regimeBlend, 0.02f, 2f);
            minLODScale = Mathf.Clamp(minLODScale, 0.05f, 1f);
            soundMergeWindow = Mathf.Max(0f, soundMergeWindow);
            maxScreenFlash = Mathf.Clamp01(maxScreenFlash);
            windSpeed = Mathf.Clamp(windSpeed, 0f, 60f);
            windShear = Mathf.Clamp(windShear, 0.5f, 5f);
            falloutDuration = Mathf.Clamp(falloutDuration, 30f, 7200f);
            falloutDoseRate = Mathf.Max(0f, falloutDoseRate);
            lethalDose = Mathf.Max(0.1f, lethalDose);
            sicknessDose = Mathf.Clamp(sicknessDose, 0.01f, lethalDose);
            probeECDrain = Mathf.Max(0f, probeECDrain);
            scorchLifetime = Mathf.Clamp(scorchLifetime, 10f, 36000f);
            fireDuration = Mathf.Clamp(fireDuration, 10f, 3600f);
            lightningMinYield = Mathf.Max(1f, lightningMinYield);
            hazeVisibility = Mathf.Clamp(hazeVisibility, 2000f, 500000f);
            nukeImpulseScale = Mathf.Clamp(nukeImpulseScale, 0f, 1f);
            nukeMaxDeltaV = Mathf.Clamp(nukeMaxDeltaV, 0f, 5000f);
            volumetricSteps = Mathf.Clamp(volumetricSteps, 16, 128);
            volumetricResolution = Mathf.Clamp(volumetricResolution, 1, 3);
            cloudShadowStrength = Mathf.Clamp01(cloudShadowStrength);
            shockDistortion = Mathf.Clamp(shockDistortion, 0f, 3f);
            craterScale = Mathf.Clamp(craterScale, 0.1f, 5f);
            maxCraters = Mathf.Clamp(maxCraters, 1, 1000);
            targetFrameRate = Mathf.Clamp(targetFrameRate, 15f, 240f);
            photoModeSlowRate = Mathf.Clamp(photoModeSlowRate, 0.01f, 1f);
            ValidateBelts(); // high-altitude effects
            // ---- world effects ----
            footprintDays = Mathf.Clamp(footprintDays, 0.1f, 1000f);
            footprintDose = Mathf.Clamp(footprintDose, 0f, 10f);
            firestormMinYield = Mathf.Max(0f, firestormMinYield);
            firestormDuration = Mathf.Clamp(firestormDuration, 30f, 3600f);
            // ---- end world effects ----
        }

        static void ValidateQuality(NukeQualityValues q)
        {
            q.particleDensity = Mathf.Clamp(q.particleDensity, 0.1f, 4f);
            q.textureScale = q.textureScale < 0.75f ? 0.5f : q.textureScale < 1.5f ? 1f : 2f;
            q.lodStrength = Mathf.Clamp(q.lodStrength, 0f, 1.5f);
            q.maxParticles = Mathf.Clamp(q.maxParticles, 500, 60000);
        }

        /// <summary>The preset (or the cfg's custom values) in force.</summary>
        public NukeQualityValues QualityValues => quality == NukeQuality.Custom ? custom : NukeQualityValues.Preset(quality);

        /// <summary>Copies the preset (or the custom values) into the live fields.</summary>
        public void ApplyQuality()
        {
            var q = QualityValues;
            particleDensity = q.particleDensity;
            textureScale = q.textureScale;
            capDetail = q.capDetail;
            stemCollars = q.stemCollars;
            condensationRings = q.condensationRings;
            wilsonCloud = q.wilsonCloud;
            artificialAurora = q.artificialAurora;
            falloutCloud = q.falloutCloud;
            lightning = q.lightning;
            scorchMarks = q.scorchMarks;
            lingeringFires = q.lingeringFires;
            lightShadows = q.lightShadows;
            lodStrength = q.lodStrength;
            maxParticles = q.maxParticles;
        }

        /// <summary>Switches to Custom, starting from whatever is in force now, so a single toggle can be changed.</summary>
        public NukeQualityValues BeginCustom()
        {
            if (quality != NukeQuality.Custom)
            {
                custom = QualityValues.Clone();
                quality = NukeQuality.Custom;
            }
            return custom;
        }

        /// <summary>Writes the window-editable settings to PluginData/Settings.cfg (creating the folder).</summary>
        public bool Save()
        {
            try
            {
                string path = UserFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var root = new ConfigNode();
                var n = root.AddNode("BDNUKES_SETTINGS");
                n.AddValue("enabled", B(enabled));
                n.AddValue("replaceStockVisuals", B(replaceStockVisuals));
                n.AddValue("sizeScale", F(sizeScale));
                n.AddValue("cloudHeightScale", F(cloudHeightScale));
                n.AddValue("cloudRiseTime", F(cloudRiseTime));
                n.AddValue("cloudLifetime", F(cloudLifetime));
                n.AddValue("screenFlash", F(screenFlash));
                n.AddValue("screenTint", F(screenTint));
                n.AddValue("cameraShake", F(cameraShake));
                n.AddValue("volume", F(volume));
                n.AddValue("magneticStreamers", B(magneticStreamers));
                n.AddValue("burstLOD", B(burstLOD));

                n.AddValue("quality", quality.ToString());
                var c = custom;
                n.AddValue("particleDensity", F(c.particleDensity));
                n.AddValue("textureScale", F(c.textureScale));
                n.AddValue("capDetail", B(c.capDetail));
                n.AddValue("stemCollars", B(c.stemCollars));
                n.AddValue("condensationRings", B(c.condensationRings));
                n.AddValue("wilsonCloud", B(c.wilsonCloud));
                n.AddValue("artificialAurora", B(c.artificialAurora));
                n.AddValue("falloutCloud", B(c.falloutCloud));
                n.AddValue("lightning", B(c.lightning));
                n.AddValue("scorchMarks", B(c.scorchMarks));
                n.AddValue("lingeringFires", B(c.lingeringFires));
                n.AddValue("lightShadows", B(c.lightShadows));
                n.AddValue("lodStrength", F(c.lodStrength));
                n.AddValue("maxParticles", c.maxParticles.ToString(CultureInfo.InvariantCulture));

                n.AddValue("windSpeed", F(windSpeed));
                n.AddValue("falloutDuration", F(falloutDuration));
                n.AddValue("falloutRadiation", B(falloutRadiation));
                n.AddValue("geigerCounter", B(geigerCounter));
                n.AddValue("scorchLifetime", F(scorchLifetime));
                n.AddValue("groundAfterglow", B(groundAfterglow));
                n.AddValue("fireDuration", F(fireDuration));
                n.AddValue("debrisTrails", B(debrisTrails));
                n.AddValue("volumetricClouds", B(volumetricClouds));
                n.AddValue("volumetricSteps", volumetricSteps.ToString(CultureInfo.InvariantCulture));
                n.AddValue("fluidSim", B(fluidSim));
                n.AddValue("velum", B(velum));
                n.AddValue("precursorDust", B(precursorDust));
                n.AddValue("groundFire", B(groundFire));
                n.AddValue("persistentClouds", B(persistentClouds));
                n.AddValue("volumetricResolution", volumetricResolution.ToString(CultureInfo.InvariantCulture));
                n.AddValue("cloudShadows", B(cloudShadows));
                n.AddValue("shockDistortion", F(shockDistortion));
                n.AddValue("cloudShadowStrength", F(cloudShadowStrength));
                n.AddValue("atmosphericHaze", B(atmosphericHaze));
                n.AddValue("hazeVisibility", F(hazeVisibility));
                n.AddValue("weatherLighting", B(weatherLighting));
                n.AddValue("cloudClearing", B(cloudClearing));
                // early-time effects
                n.AddValue("ropeTricks", B(ropeTricks));
                n.AddValue("machStemDust", B(machStemDust));
                n.AddValue("cloudDeckFlash", B(cloudDeckFlash));
                n.AddValue("nukeImpulseScale", F(nukeImpulseScale));
                n.AddValue("nukeMaxDeltaV", F(nukeMaxDeltaV));
                n.AddValue("craters", B(craters));
                n.AddValue("craterScale", F(craterScale));
                n.AddValue("craterPartialRebuild", B(craterPartialRebuild));
                n.AddValue("protectSites", B(protectSites));
                n.AddValue("volumetricQuality", volumetricQuality.ToString());
                n.AddValue("adaptivePerformance", B(adaptivePerformance));
                n.AddValue("targetFrameRate", F(targetFrameRate));

                n.AddValue("photoModeSlowRate", F(photoModeSlowRate));

                // thermal scorch on craft
                n.AddValue("craftScorch", B(craftScorch));
                // ---- fireball lighting ----
                n.AddValue("fireballLighting", B(fireballLighting));
                // ---- end fireball lighting ----
                SavePlayerTools(n);
                // ---- world effects ----
                n.AddValue("flattenTrees", B(flattenTrees));
                n.AddValue("falloutFootprint", B(falloutFootprint));
                n.AddValue("footprintDays", F(footprintDays));
                n.AddValue("footprintDose", F(footprintDose));
                n.AddValue("firestorm", B(firestorm));
                n.AddValue("firestormMinYield", F(firestormMinYield));
                n.AddValue("firestormDuration", F(firestormDuration));
                // ---- end world effects ----
                root.Save(path, "BDNukes user settings, written by the in-game settings window. Overrides GameData/BDNukes/Settings.cfg; delete this file to go back to the defaults.");
                Debug.Log("[BDNukes]: Settings saved to " + path);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Couldn't save settings: " + e);
                return false;
            }
        }

        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string B(bool v) => v ? "true" : "false";
    }
}
