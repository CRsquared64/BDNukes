using UnityEngine;

namespace BDNukes
{
    /// <summary>Loaded from the BDNUKES_SETTINGS node in GameData/BDNukes/Settings.cfg.</summary>
    internal class NukeSettings
    {
        public bool enabled = true;
        public bool replaceStockVisuals = true;
        public float sizeScale = 1f;
        public float cloudHeightScale = 0.6f;
        public float cloudRiseTime = 90f;
        public float cloudLifetime = 300f;
        public float glowTimeScale = 1f;
        public float particleDensity = 1f;
        public float speedOfSound = 420f;
        public float screenFlash = 1f;
        public float screenTint = 1f;
        public float cameraShake = 1f;
        public float volume = 1f;
        public bool condensationRings = true;
        public float billowRotationSign = 1f;
        public bool vacuumPhysics = true;
        public float vacuumDebrisSpeed = 4000f;
        public bool magneticStreamers = true;
        public bool artificialAurora = true;

        // cloud character and regimes
        public float tropopauseFraction = 0.16f;   // tropopause altitude as a fraction of atmosphereDepth
        public float pancakeStrength = 1f;         // how strongly caps that hit the tropopause flatten and spread
        public bool stemCollars = true;            // transient condensation collars around the stem
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

        static NukeSettings current;

        public static NukeSettings Current
        {
            get
            {
                if (current == null) current = Load();
                return current;
            }
        }

        static NukeSettings Load()
        {
            var s = new NukeSettings();
            if (GameDatabase.Instance == null) return s;
            var nodes = GameDatabase.Instance.GetConfigNodes("BDNUKES_SETTINGS");
            if (nodes == null || nodes.Length == 0) return s;
            var n = nodes[0];
            n.TryGetValue("enabled", ref s.enabled);
            n.TryGetValue("replaceStockVisuals", ref s.replaceStockVisuals);
            n.TryGetValue("sizeScale", ref s.sizeScale);
            n.TryGetValue("cloudHeightScale", ref s.cloudHeightScale);
            n.TryGetValue("cloudRiseTime", ref s.cloudRiseTime);
            n.TryGetValue("cloudLifetime", ref s.cloudLifetime);
            n.TryGetValue("glowTimeScale", ref s.glowTimeScale);
            n.TryGetValue("particleDensity", ref s.particleDensity);
            n.TryGetValue("speedOfSound", ref s.speedOfSound);
            n.TryGetValue("screenFlash", ref s.screenFlash);
            n.TryGetValue("screenTint", ref s.screenTint);
            n.TryGetValue("cameraShake", ref s.cameraShake);
            n.TryGetValue("volume", ref s.volume);
            n.TryGetValue("condensationRings", ref s.condensationRings);
            n.TryGetValue("billowRotationSign", ref s.billowRotationSign);
            n.TryGetValue("vacuumPhysics", ref s.vacuumPhysics);
            n.TryGetValue("vacuumDebrisSpeed", ref s.vacuumDebrisSpeed);
            n.TryGetValue("magneticStreamers", ref s.magneticStreamers);
            n.TryGetValue("artificialAurora", ref s.artificialAurora);
            n.TryGetValue("tropopauseFraction", ref s.tropopauseFraction);
            n.TryGetValue("pancakeStrength", ref s.pancakeStrength);
            n.TryGetValue("stemCollars", ref s.stemCollars);
            n.TryGetValue("lowYieldThreshold", ref s.lowYieldThreshold);
            n.TryGetValue("waterBursts", ref s.waterBursts);
            n.TryGetValue("highAltitudeDensity", ref s.highAltitudeDensity);
            n.TryGetValue("vacuumLookDensity", ref s.vacuumLookDensity);
            n.TryGetValue("regimeBlend", ref s.regimeBlend);
            n.TryGetValue("mirvBus", ref s.mirvBus);
            n.TryGetValue("burstLOD", ref s.burstLOD);
            n.TryGetValue("minLODScale", ref s.minLODScale);
            n.TryGetValue("soundMergeWindow", ref s.soundMergeWindow);
            n.TryGetValue("maxScreenFlash", ref s.maxScreenFlash);

            s.sizeScale = Mathf.Max(0.05f, s.sizeScale);
            s.cloudRiseTime = Mathf.Max(5f, s.cloudRiseTime);
            s.cloudLifetime = Mathf.Max(s.cloudRiseTime + 10f, s.cloudLifetime);
            s.glowTimeScale = Mathf.Max(0.1f, s.glowTimeScale);
            s.particleDensity = Mathf.Clamp(s.particleDensity, 0.1f, 4f);
            s.speedOfSound = Mathf.Max(50f, s.speedOfSound);
            s.vacuumDebrisSpeed = Mathf.Max(100f, s.vacuumDebrisSpeed);
            s.tropopauseFraction = Mathf.Clamp(s.tropopauseFraction, 0.02f, 1f);
            s.pancakeStrength = Mathf.Clamp(s.pancakeStrength, 0f, 3f);
            s.lowYieldThreshold = Mathf.Max(0f, s.lowYieldThreshold);
            s.highAltitudeDensity = Mathf.Clamp(s.highAltitudeDensity, 1e-5f, 1f);
            s.vacuumLookDensity = Mathf.Clamp(s.vacuumLookDensity, 1e-6f, s.highAltitudeDensity);
            s.regimeBlend = Mathf.Clamp(s.regimeBlend, 0.02f, 2f);
            s.minLODScale = Mathf.Clamp(s.minLODScale, 0.05f, 1f);
            s.soundMergeWindow = Mathf.Max(0f, s.soundMergeWindow);
            s.maxScreenFlash = Mathf.Clamp01(s.maxScreenFlash);
            Debug.Log("[BDNukes]: Settings loaded.");
            return s;
        }
    }
}
