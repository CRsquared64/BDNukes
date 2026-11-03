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
        Transform shakenTransform;
        Quaternion appliedShake = Quaternion.identity;
        float noiseSeed;
        Texture2D white;

        float lastBlastSound = -1000f;

        public void Register(NukeCinematic c) { if (!active.Contains(c)) active.Add(c); }
        public void Unregister(NukeCinematic c) { active.Remove(c); }

        /// <summary>
        /// Particle-count scale for a new detonation: with N detonations alive (including the new one),
        /// each new one gets ~1/sqrt(N) of the particles so a salvo or MIRV stays playable.
        /// </summary>
        public static float LODScale()
        {
            var s = NukeSettings.Current;
            if (!s.burstLOD || instance == null) return 1f;
            int n = 1;
            for (int i = 0; i < instance.active.Count; i++) if (instance.active[i] != null) n++;
            return Mathf.Clamp(1f / Mathf.Sqrt(n), s.minLODScale, 1f);
        }

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
            white = Texture2D.whiteTexture;
        }

        void OnDestroy()
        {
            RestoreCamera();
            if (instance == this) instance = null;
        }

        void Update()
        {
            // Undo last frame's shake before the camera controller runs.
            RestoreCamera();
        }

        void LateUpdate()
        {
            float amp = 0f;
            for (int i = shakes.Count - 1; i >= 0; i--)
            {
                float age = (Time.time - shakes[i].start) / shakes[i].duration;
                if (age >= 1f) { shakes.RemoveAt(i); continue; }
                float k = 1f - age;
                amp += shakes[i].amplitude * k * k;
            }
            amp = Mathf.Min(amp, 1.5f) * NukeSettings.Current.cameraShake;
            if (amp < 0.002f || FlightCamera.fetch == null) return;

            float t = Time.time * 22f;
            float deg = amp * 2.5f;
            var euler = new Vector3(
                (Mathf.PerlinNoise(t, noiseSeed) - 0.5f) * 2f * deg,
                (Mathf.PerlinNoise(noiseSeed, t) - 0.5f) * 2f * deg,
                (Mathf.PerlinNoise(t * 0.7f, noiseSeed + 7f) - 0.5f) * deg);
            shakenTransform = FlightCamera.fetch.transform;
            appliedShake = Quaternion.Euler(euler);
            shakenTransform.localRotation = shakenTransform.localRotation * appliedShake;
        }

        void RestoreCamera()
        {
            if (shakenTransform != null && appliedShake != Quaternion.identity)
            {
                shakenTransform.localRotation = shakenTransform.localRotation * Quaternion.Inverse(appliedShake);
            }
            appliedShake = Quaternion.identity;
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
