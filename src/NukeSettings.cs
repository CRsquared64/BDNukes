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

            s.sizeScale = Mathf.Max(0.05f, s.sizeScale);
            s.cloudRiseTime = Mathf.Max(5f, s.cloudRiseTime);
            s.cloudLifetime = Mathf.Max(s.cloudRiseTime + 10f, s.cloudLifetime);
            s.glowTimeScale = Mathf.Max(0.1f, s.glowTimeScale);
            s.particleDensity = Mathf.Clamp(s.particleDensity, 0.1f, 4f);
            s.speedOfSound = Mathf.Max(50f, s.speedOfSound);
            s.vacuumDebrisSpeed = Mathf.Max(100f, s.vacuumDebrisSpeed);
            Debug.Log("[BDNukes]: Settings loaded.");
            return s;
        }
    }
}
