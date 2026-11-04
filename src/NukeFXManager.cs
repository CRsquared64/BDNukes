using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// One per flight scene. Draws the screen flash / warm tint overlay and drives camera shake
    /// for all active detonations.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal class NukeFXManager : MonoBehaviour
    {
        static NukeFXManager instance;

        public static NukeFXManager Instance
        {
            get
            {
                if (instance == null) instance = new GameObject("BDNukes_Manager").AddComponent<NukeFXManager>();
                return instance;
            }
        }

        /// <summary>The manager if one exists, without creating it (safe during scene teardown).</summary>
        public static NukeFXManager Existing => instance;

        readonly List<NukeCinematic> active = new List<NukeCinematic>();

        struct Shake { public float amplitude, duration, start; }
        readonly List<Shake> shakes = new List<Shake>();
        // the shake is applied to each flight camera only while it renders (onPreCull -> onPostRender) and its
        // exact rotation put back afterwards: the camera controller never sees it, so nothing can accumulate
        Quaternion shake = Quaternion.identity;
        readonly Dictionary<Camera, Quaternion> savedRotation = new Dictionary<Camera, Quaternion>();
        float noiseSeed;
        Texture2D white;

        float lastBlastSound = -1000f;

        // ---- adaptive performance: a runtime quality level for the volumetric cloud ----
        // Each level first trims raymarch steps, then coarsens the resolution divisor. The level drops when the
        // smoothed (unscaled) frame time stays over the target for a second while a cloud is drawn, and rises
        // again after a few seconds of clear headroom. A drop soon after a rise doubles the wait before the next
        // rise (up to 30 s), so a scene sitting right at the target settles instead of flip-flopping.
        static readonly float[] LevelSteps = { 1f, 0.8f, 0.65f, 0.5f, 0.5f, 0.5f };
        static readonly int[] LevelDivisor = { 0, 0, 0, 0, 1, 2 };
        const float DropAfter = 1f, RaiseAfter = 4f, MaxRaiseWait = 30f, ResetAfter = 10f;
        static int adaptLevel;
        static float smoothedFrame = 1f / 60f;
        float overTime, underTime, idleTime, raiseWait = RaiseAfter, lastRaise = -1000f;

        /// <summary>Multiplier on the per-cloud raymarch step count from adaptive performance (1 = none).</summary>
        public static float StepScale => LevelSteps[adaptLevel];

        /// <summary>Added to the configured resolution divisor by adaptive performance (0 = none).</summary>
        public static int ExtraDivisor => LevelDivisor[adaptLevel];

        /// <summary>The smoothed, unscaled frame rate the adaptive quality is watching.</summary>
        public static float SmoothedFPS => 1f / Mathf.Max(1e-4f, smoothedFrame);

        /// <summary>A one-line readout of the runtime quality scale for the settings window.</summary>
        public static string AdaptiveStatus()
        {
            var s = NukeSettings.Current;
            if (!s.adaptivePerformance) return "Adaptive quality: off";
            int div = Mathf.Clamp(s.volumetricResolution + ExtraDivisor, 1, 3);
            return $"Adaptive quality: steps x{StepScale:0.00}, resolution {(div == 1 ? "full" : "1/" + div)} ({SmoothedFPS:0} fps)";
        }

        /// <summary>Highest level that still changes something: no point raising the divisor past a third.</summary>
        static int MaxLevel()
        {
            int baseDiv = Mathf.Clamp(NukeSettings.Current.volumetricResolution, 1, 3);
            int max = LevelSteps.Length - 1;
            while (max > 0 && LevelDivisor[max] > 0 && baseDiv + LevelDivisor[max - 1] >= 3) max--;
            return max;
        }

        void UpdateAdaptive(bool cloudDrawn)
        {
            // clamp hitches (scene loads, the pause menu) so one long frame can't drag the average for seconds
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            smoothedFrame += (dt - smoothedFrame) * (1f - Mathf.Exp(-dt / 0.3f));
            var s = NukeSettings.Current;
            if (!s.adaptivePerformance || !s.volumetricClouds)
            {
                adaptLevel = 0;
                overTime = underTime = idleTime = 0f;
                return;
            }
            float now = Time.unscaledTime;
            if (!cloudDrawn)
            {
                // nothing to measure: hold the level for a while, then start the next cloud fresh
                overTime = underTime = 0f;
                idleTime += dt;
                if (idleTime > ResetAfter) { adaptLevel = 0; raiseWait = RaiseAfter; }
                return;
            }
            idleTime = 0f;
            adaptLevel = Mathf.Min(adaptLevel, MaxLevel());
            float target = 1f / s.targetFrameRate;
            // dead band between 80 % and 105 % of the target frame time: nothing moves inside it
            if (smoothedFrame > target * 1.05f) { overTime += dt; underTime = 0f; }
            else if (smoothedFrame < target * 0.8f) { underTime += dt; overTime = 0f; }
            else { overTime = 0f; underTime = 0f; }

            if (overTime >= DropAfter && adaptLevel < MaxLevel())
            {
                adaptLevel++;
                overTime = 0f;
                // dropping straight back after a rise: that rise was too eager, wait longer next time
                if (now - lastRaise < raiseWait + DropAfter + 1f) raiseWait = Mathf.Min(raiseWait * 2f, MaxRaiseWait);
            }
            else if (underTime >= raiseWait && adaptLevel > 0)
            {
                adaptLevel--;
                underTime = 0f;
                lastRaise = now;
            }
            else if (underTime >= MaxRaiseWait)
            {
                // a long stable stretch at full quality: forgive earlier oscillation
                raiseWait = Mathf.Max(RaiseAfter, raiseWait * 0.5f);
                underTime = 0f;
            }
        }

        public void Register(NukeCinematic c) { if (!active.Contains(c)) active.Add(c); }
        public void Unregister(NukeCinematic c) { active.Remove(c); }

        /// <summary>
        /// Particle-count scale for a new detonation: with N detonations alive (including the new one),
        /// each new one gets N^-lodStrength of the particles (1/sqrt(N) on High) so a salvo or MIRV stays playable.
        /// </summary>
        public static float LODScale()
        {
            var s = NukeSettings.Current;
            if (!s.burstLOD || instance == null) return 1f;
            int n = 1;
            for (int i = 0; i < instance.active.Count; i++) if (instance.active[i] != null) n++;
            return Mathf.Clamp(Mathf.Pow(n, -s.lodStrength), s.minLODScale, 1f);
        }

        /// <summary>Number of detonations still drawing their cloud.</summary>
        public int ActiveCount => active.Count;

        /// <summary>
        /// The scene's prevailing wind heading (degrees from north), picked once per flight scene so every cloud,
        /// plume and smoke column in a salvo drifts the same way. Each detonation adds its own small veer.
        /// </summary>
        public float WindHeading { get; private set; }

        /// <summary>
        /// Claims the crack/rumble for a shock arriving now. Returns false if another detonation's
        /// started within soundMergeWindow, so a salvo doesn't stack dozens of identical cracks.
        /// </summary>
        public bool TryClaimBlastSound()
        {
            if (Time.time - lastBlastSound < NukeSettings.Current.soundMergeWindow) return false;
            lastBlastSound = Time.time;
            return true;
        }

        public void AddShake(float amplitude, float duration)
        {
            if (amplitude <= 0.001f || duration <= 0f) return;
            shakes.Add(new Shake { amplitude = amplitude, duration = duration, start = Time.time });
        }

        void Awake()
        {
            noiseSeed = Random.value * 100f;
            WindHeading = Random.Range(0f, 360f);
            white = Texture2D.whiteTexture;
        }

        void OnEnable()
        {
            Camera.onPreCull += ShakeCamera;
            Camera.onPostRender += UnshakeCamera;
        }

        void OnDisable()
        {
            Camera.onPreCull -= ShakeCamera;
            Camera.onPostRender -= UnshakeCamera;
            RestoreAllCameras();
        }

        void OnDestroy()
        {
            RestoreAllCameras();
            NukeCloudClearing.RestoreAll();
            NukeVolumetric.ReleaseCompositor();
            adaptLevel = 0;
            if (instance == this) instance = null;
        }

        static bool IsShakeable(Camera cam)
        {
            if (cam == null || FlightCamera.fetch == null) return false;
            var cams = FlightCamera.fetch.cameras;
            if (cams != null) for (int i = 0; i < cams.Length; i++) if (cams[i] == cam) return true;
            return ScaledCamera.Instance != null && ScaledCamera.Instance.cam == cam;
        }

        void ShakeCamera(Camera cam)
        {
            if (shake == Quaternion.identity || savedRotation.ContainsKey(cam) || !IsShakeable(cam)) return;
            var tr = cam.transform;
            savedRotation[cam] = tr.rotation;
            tr.rotation = tr.rotation * shake;
        }

        void UnshakeCamera(Camera cam)
        {
            if (cam == null || !savedRotation.TryGetValue(cam, out var rot)) return;
            cam.transform.rotation = rot;
            savedRotation.Remove(cam);
        }

        void RestoreAllCameras()
        {
            foreach (var kv in savedRotation) if (kv.Key != null) kv.Key.transform.rotation = kv.Value;
            savedRotation.Clear();
            shake = Quaternion.identity;
        }

        void LateUpdate()
        {
            NukeCloudClearing.Update();
            UpdateAdaptive(NukeVolumetric.QueuedClouds > 0);
            NukeVolumetric.UpdateCompositor();
            float amp = 0f;
            for (int i = shakes.Count - 1; i >= 0; i--)
            {
                float age = (Time.time - shakes[i].start) / shakes[i].duration;
                if (age >= 1f) { shakes.RemoveAt(i); continue; }
                float k = 1f - age;
                amp += shakes[i].amplitude * k * k;
            }
            amp = Mathf.Min(amp, 1.5f) * NukeSettings.Current.cameraShake;
            if (amp < 0.002f || FlightCamera.fetch == null)
            {
                shake = Quaternion.identity;
                return;
            }

            float t = Time.time * 22f;
            float deg = amp * 2.5f;
            var euler = new Vector3(
                (Mathf.PerlinNoise(t, noiseSeed) - 0.5f) * 2f * deg,
                (Mathf.PerlinNoise(noiseSeed, t) - 0.5f) * 2f * deg,
                (Mathf.PerlinNoise(t * 0.7f, noiseSeed + 7f) - 0.5f) * deg);
            shake = Quaternion.Euler(euler);
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || active.Count == 0) return;
            // Overlapping flashes combine like stacked translucent layers (1 - product of (1 - f)), and the
            // total is capped so a salvo can't white the screen out for seconds on end.
            float clear = 1f, tint = 0f;
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] == null) continue;
                active[i].GetScreenContribution(out float f, out float w);
                clear *= 1f - Mathf.Clamp01(f);
                tint = Mathf.Max(tint, w);
            }
            var s = NukeSettings.Current;
            float flash = Mathf.Min(Mathf.Clamp01((1f - clear) * s.screenFlash), s.maxScreenFlash);
            tint = Mathf.Clamp01(tint * s.screenTint);
            if (flash < 0.003f && tint < 0.003f) return;

            GUI.depth = 1000;
            var rect = new Rect(0, 0, Screen.width, Screen.height);
            var old = GUI.color;
            if (tint > 0.003f)
            {
                GUI.color = new Color(1f, 0.52f, 0.18f, tint);
                GUI.DrawTexture(rect, white);
            }
            if (flash > 0.003f)
            {
                GUI.color = new Color(1f, 0.98f, 0.94f, flash);
                GUI.DrawTexture(rect, white);
            }
            GUI.color = old;
        }
    }
}
