using UnityEngine;

namespace BDNukes
{
    /// <summary>What the cinematic camera needs to know about a running detonation.</summary>
    internal partial class NukeCinematic
    {
        internal CelestialBody Body => body;

        /// <summary>Seconds since the flash (negative during a fuze delay).</summary>
        internal float Age => (float)(NukeFXManager.FxTime - spawnTime - delay);

        internal float Life => life;
        internal float RiseTime => riseTime;
        internal float FireballRadius => R;

        /// <summary>The burst point now (safe across floating-origin shifts: worked out from lat/lon/alt).</summary>
        internal Vector3 BurstWorld => body.GetWorldSurfacePosition(lat, lon, alt);

        /// <summary>
        /// How big the whole thing gets (m): the larger of the final cap radius and half the final cloud height for
        /// a mushroom, the risen fireball for high-altitude bursts, the water column at sea, the debris shell in vacuum.
        /// </summary>
        internal float CameraFrameSize
        {
            get
            {
                float size = 3f * R;
                if (airFx) size = Mathf.Max(size, capRadius, 0.5f * (hb + riseDelta + 0.5f * capRadius));
                if (hiFx) size = Mathf.Max(size, 2.5f * Rh);
                if (waterFx) size = Mathf.Max(size, 0.5f * colH, 2.5f * colR);
                if (vacFx) size = Mathf.Max(size, 0.4f * ShellRadius(0.5f * lifeVac));
                return size;
            }
        }

        /// <summary>
        /// Where to aim: roughly the centre of mass of what's visible. For a mushroom it follows the rising cap
        /// (pulled down toward the ground by the stem when there is one), drifting with the wind.
        /// </summary>
        internal Vector3 CameraFocus()
        {
            Vector3 basePos = BurstWorld;
            float tt = Mathf.Max(0f, Age);
            Vector3 up = (basePos - (Vector3)body.position).normalized;
            float h;
            Vector3 drift = Vector3.zero;
            if (airFx)
            {
                float capH = CapHeight(tt);
                h = stemFx ? Mathf.Lerp(groundY, capH, 0.72f) : capH;
                drift = transform.rotation * capDrift;
            }
            else if (hiFx) h = HiRise(tt);
            else if (underwater) h = groundY + 0.4f * ColTop(tt);
            else h = 0f;
            return basePos + up * h + drift;
        }
    }

    /// <summary>
    /// Optional automatic camera (setting cinematicCamera, toggled with Alt+F6): when a detonation goes off within
    /// visual range of the active vessel, the flight camera's target is moved to a helper transform at the cloud's
    /// centre, pulled back to a few cloud sizes (so the whole cloud stays framed while it rises) and slowly orbited.
    /// The existing camera shake still applies (it works on the rendered camera, not the controller).
    ///
    /// Any camera input (right/middle mouse, scroll, camera keys, Escape or the hotkey) hands control back and
    /// restores the camera's previous target, mode, distance, pitch and heading. A vessel switch, map view, IVA,
    /// the cloud ending or a scene change also end it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeCinematicCamera : MonoBehaviour
    {
        static NukeCinematicCamera instance;

        NukeCinematic target;
        GameObject pivot;
        bool active;
        float desiredDist, endTime, graceUntil;

        // the camera as it was
        FlightCamera.Modes savedMode;
        FlightCamera.TargetMode savedTargetMode;
        Transform savedTarget;
        Vessel savedVessel;
        Part savedPart;
        float savedDist, savedPitch, savedHdg, savedMaxDist;

        public static bool Active => instance != null && instance.active;

        /// <summary>Called by every new detonation (and replay) once it's set up.</summary>
        public static void OnDetonation(NukeCinematic c)
        {
            if (instance == null) return;
            try { instance.Consider(c); }
            catch (System.Exception e)
            {
                Debug.LogWarning("[BDNukes]: Cinematic camera couldn't start: " + e.Message);
                instance.End(true);
            }
        }

        void Awake()
        {
            instance = this;
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoad);
        }

        void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoad);
            End(true);
            if (instance == this) instance = null;
        }

        void OnVesselChange(Vessel v) { End(false); }   // the game targets the new vessel itself
        void OnSceneLoad(GameScenes s) { End(true); }

        static bool CameraIsFlight()
        {
            if (MapView.MapIsEnabled) return false;
            var cm = CameraManager.Instance;
            return cm == null || cm.currentCameraMode == CameraManager.CameraMode.Flight;
        }

        void Consider(NukeCinematic c)
        {
            var s = NukeSettings.Current;
            if (!s.cinematicCamera || active || c == null || c.Body == null) return;
            var fc = FlightCamera.fetch;
            var v = FlightGlobals.ActiveVessel;
            if (fc == null || v == null || !CameraIsFlight() || c.Body != v.mainBody) return;
            float size = c.CameraFrameSize;
            float range = Mathf.Max(s.cinematicCameraRange, 25f * size);
            if ((c.BurstWorld - v.CoM).magnitude > range) return;
            Begin(c, size, fc);
        }

        void Begin(NukeCinematic c, float size, FlightCamera fc)
        {
            var s = NukeSettings.Current;
            savedMode = fc.mode;
            savedTargetMode = fc.targetMode;
            savedTarget = fc.Target;
            savedVessel = fc.vesselTarget;
            savedPart = fc.partTarget;
            savedDist = fc.Distance;
            savedPitch = fc.camPitch;
            savedHdg = fc.camHdg;
            savedMaxDist = fc.maxDistance;

            target = c;
            pivot = new GameObject("BDNukes_CinematicCameraPivot");
            pivot.transform.position = c.CameraFocus();
            // a safe distance that frames the whole risen cloud; never inside a few fireball radii
            desiredDist = Mathf.Max(s.cinematicCameraDistance * size, 6f * c.FireballRadius, 250f);
            fc.maxDistance = Mathf.Max(savedMaxDist, desiredDist * 1.25f);
            fc.SetTargetTransform(pivot.transform);
            fc.setModeImmediate(FlightCamera.Modes.FREE);
            fc.SetDistance(desiredDist);
            fc.camPitch = Mathf.Clamp(0.12f, fc.minPitch, fc.maxPitch);
            active = true;
            graceUntil = Time.unscaledTime + 0.4f;
            // follow the rise and a while after; never longer than the cloud
            endTime = Time.unscaledTime + Mathf.Max(0f, -c.Age) + Mathf.Min(c.Life, c.RiseTime * 1.3f + 45f);
            ScreenMessages.PostScreenMessage("BDNukes cinematic camera (move the camera or press Esc to take over)", 3f, ScreenMessageStyle.UPPER_CENTER);
        }

        /// <summary>Hands the camera back. restoreTarget = false when the game is retargeting it anyway (vessel switch).</summary>
        void End(bool restoreTarget)
        {
            if (!active) return;
            active = false;
            var fc = FlightCamera.fetch;
            if (fc != null)
            {
                try
                {
                    fc.maxDistance = savedMaxDist;
                    if (restoreTarget)
                    {
                        var av = FlightGlobals.ActiveVessel;
                        if (savedTargetMode == FlightCamera.TargetMode.Part && savedPart != null && savedPart.vessel == av) fc.SetTargetPart(savedPart);
                        else if (savedTargetMode == FlightCamera.TargetMode.Vessel && savedVessel != null && savedVessel == av) fc.SetTargetVessel(savedVessel);
                        else if (savedTargetMode == FlightCamera.TargetMode.Transform && savedTarget != null) fc.SetTargetTransform(savedTarget);
                        else if (av != null) fc.SetTargetVessel(av);
                        fc.SetDistance(Mathf.Clamp(savedDist, fc.minDistance, fc.maxDistance));
                        fc.camPitch = savedPitch;
                        fc.camHdg = savedHdg;
                    }
                    else if (fc.Target == (pivot != null ? pivot.transform : null))
                    {
                        // nothing retargeted it: don't leave the camera on a transform that's about to go
                        var av = FlightGlobals.ActiveVessel;
                        if (av != null) fc.SetTargetVessel(av);
                    }
                    // the orbit runs in free mode: hand back the player's camera mode either way (also when the
                    // vessel it was following has died and the camera fell back to the active one)
                    fc.setModeImmediate(savedMode);
                }
                catch (System.Exception e) { Debug.LogWarning("[BDNukes]: Cinematic camera couldn't restore the camera: " + e.Message); }
            }
            target = null;
            if (pivot != null) Destroy(pivot);
            pivot = null;
        }

        bool PlayerTookControl()
        {
            if (Time.unscaledTime < graceUntil) return false;
            if (Input.GetKeyDown(KeyCode.Escape)) return true;
            if (Input.GetMouseButton(1) || Input.GetMouseButton(2)) return true;
            if (Mathf.Abs(Input.GetAxis("Mouse ScrollWheel")) > 1e-4f) return true;
            return GameSettings.CAMERA_ORBIT_UP.GetKey() || GameSettings.CAMERA_ORBIT_DOWN.GetKey()
                || GameSettings.CAMERA_ORBIT_LEFT.GetKey() || GameSettings.CAMERA_ORBIT_RIGHT.GetKey()
                || GameSettings.ZOOM_IN.GetKey() || GameSettings.ZOOM_OUT.GetKey()
                || GameSettings.CAMERA_MODE.GetKeyDown() || GameSettings.CAMERA_RESET.GetKeyDown();
        }

        void Update()
        {
            var s = NukeSettings.Current;
            if (NukeSettings.AltKeyDown(s.cinematicCameraKey))
            {
                if (active) End(true);
                else
                {
                    s.cinematicCamera = !s.cinematicCamera;
                    ScreenMessages.PostScreenMessage("BDNukes cinematic camera: " + (s.cinematicCamera ? "on" : "off"), 2f, ScreenMessageStyle.UPPER_CENTER);
                }
                return;
            }
            if (!active) return;

            var fc = FlightCamera.fetch;
            if (target == null || target.Body == null || fc == null || pivot == null || FlightGlobals.ActiveVessel == null
                || !CameraIsFlight() || Time.unscaledTime > endTime || !s.cinematicCamera)
            {
                End(true);
                return;
            }
            if (PlayerTookControl())
            {
                End(true);
                return;
            }
            if (fc.targetMode != FlightCamera.TargetMode.Transform || (fc.Target != null && fc.Target != pivot.transform))
            {
                // something else (another mod, a part focus) took the camera: let it have it
                End(false);
                return;
            }

            pivot.transform.position = target.CameraFocus();
            fc.SetDistance(desiredDist);
            fc.camHdg += Time.unscaledDeltaTime * 2f * Mathf.PI / Mathf.Max(20f, s.cinematicCameraOrbitPeriod);
            // ease to a low, slightly raised view of the cloud
            fc.camPitch = Mathf.Lerp(fc.camPitch, Mathf.Clamp(0.12f, fc.minPitch, fc.maxPitch), 1f - Mathf.Exp(-Time.unscaledDeltaTime));
        }
    }
}
