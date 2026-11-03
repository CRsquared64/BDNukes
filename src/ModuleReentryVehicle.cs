using System.Text;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Re-entry vehicle visuals and spin stabilisation. Above plasmaMinSpeed in atmosphere, a glowing
    /// orange-white plasma sheath forms around the nose and a fading ionised wake streams behind it, with a
    /// faint ionisation glow light. Intensity scales with speed above plasmaMinSpeed and with air density.
    /// Heating itself comes from stock ModuleAblator + an Ablator resource on the same part.
    ///
    /// The effects use a free (unparented) object placed at the stagnation point every frame, oriented with
    /// +Z downstream, and simulate in local space. World-space particles don't work at re-entry speeds, because
    /// Krakensbane keeps moving the world under the active vessel.
    /// </summary>
    public class ModuleReentryVehicle : PartModule
    {
        [KSPField] public float plasmaMinSpeed = 1500f;   // m/s
        [KSPField] public float plasmaScale = 1f;         // size multiplier
        [KSPField] public float spinRate = 0f;            // deg/s after release, 0 = off

        /// <summary>Air density (kg/m^3) at which the plasma reaches full brightness, given enough speed.</summary>
        const float FullDensity = 0.002f;

        Vessel startVessel;
        bool released;
        GameObject fx;
        ParticleSystem sheath, wake;
        Light glow;
        float bodyRadius = 0.3f;
        Bounds localBounds;
        float smoothedIntensity;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight) return;
            startVessel = vessel;
            MeasurePart();
        }

        /// <summary>Part-local bounds of the visible model, for the nose position and the plasma size.</summary>
        void MeasurePart()
        {
            bool any = false;
            localBounds = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var mf in part.FindModelComponents<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds b = mf.sharedMesh.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                    Vector3 p = part.transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { localBounds = new Bounds(p, Vector3.zero); any = true; }
                    else localBounds.Encapsulate(p);
                }
            }
            if (!any) localBounds = new Bounds(Vector3.zero, Vector3.one * 0.6f);

            // Body radius: the two smallest extents (the long axis is the nose axis).
            Vector3 ex = localBounds.extents;
            float a = ex.x, b2 = ex.y, c2 = ex.z;
            float largest = Mathf.Max(a, Mathf.Max(b2, c2));
            float sum = a + b2 + c2 - largest;
            bodyRadius = Mathf.Max(0.05f, sum / 2f);
        }

        Vector3 SpinAxis()
        {
            var pm = part.FindModuleImplementing<ModuleProceduralModel>();
            return pm != null && pm.AxisIsY ? part.transform.up : part.transform.forward;
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.packed) return;
            if (!released && startVessel != null && vessel != startVessel) released = true;
            if (!released || spinRate == 0f || part.rb == null) return;

            Vector3 axis = SpinAxis();
            float target = spinRate * Mathf.Deg2Rad;
            float current = Vector3.Dot(part.rb.angularVelocity, axis);
            // Angular acceleration (ignores the inertia tensor), limited so it spins up over a few seconds.
            float accel = Mathf.Clamp((target - current) * 2f, -Mathf.Abs(target), Mathf.Abs(target));
            part.rb.AddTorque(axis * accel, ForceMode.Acceleration);
        }

        float Intensity()
        {
            if (vessel == null || vessel.packed || !vessel.loaded) return 0f;
            CelestialBody body = vessel.mainBody;
            if (body == null || !body.atmosphere || vessel.altitude > body.atmosphereDepth) return 0f;
            float rho = (float)vessel.atmDensity;
            if (rho <= 1e-7f) return 0f;
            float speed = (float)vessel.srfSpeed;
            float excess = speed - plasmaMinSpeed;
            if (excess <= 0f) return 0f;
            float speedTerm = Mathf.Clamp01(excess / Mathf.Max(500f, plasmaMinSpeed * 0.6f));
            float densityTerm = Mathf.Clamp01(Mathf.Sqrt(rho / FullDensity));
            return speedTerm * densityTerm;
        }

        void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null) return;
            float target = Intensity();
            smoothedIntensity = Mathf.MoveTowards(smoothedIntensity, target, Time.deltaTime * 2f);

            if (smoothedIntensity <= 0.005f)
            {
                if (fx != null && fx.activeSelf) fx.SetActive(false);
                return;
            }
            if (fx == null) BuildFx();
            if (!fx.activeSelf) fx.SetActive(true);

            Vector3 v = vessel.srf_velocity;
            if (v.sqrMagnitude < 1f) return;
            Vector3 vdir = v.normalized;

            // Stagnation point: where a ray from the bounds centre along the velocity leaves the local bounds.
            Vector3 localDir = part.transform.InverseTransformDirection(vdir);
            Vector3 ex = localBounds.extents;
            float t = float.MaxValue;
            if (Mathf.Abs(localDir.x) > 1e-4f) t = Mathf.Min(t, ex.x / Mathf.Abs(localDir.x));
            if (Mathf.Abs(localDir.y) > 1e-4f) t = Mathf.Min(t, ex.y / Mathf.Abs(localDir.y));
            if (Mathf.Abs(localDir.z) > 1e-4f) t = Mathf.Min(t, ex.z / Mathf.Abs(localDir.z));
            if (t == float.MaxValue) t = 0f;
            Vector3 centre = part.transform.TransformPoint(localBounds.center);
            fx.transform.position = centre + vdir * t;
            fx.transform.rotation = Quaternion.LookRotation(-vdir);

            float k = smoothedIntensity;
            float r = bodyRadius * plasmaScale;

            var sm = sheath.main;
            sm.startSize = new ParticleSystem.MinMaxCurve(r * 2.2f * (0.6f + 0.4f * k), r * 3.2f * (0.6f + 0.4f * k));
            var se = sheath.emission;
            se.rateOverTime = 120f * k;

            var wm = wake.main;
            float wakeLength = r * 90f * k;
            wm.startSpeed = new ParticleSystem.MinMaxCurve(wakeLength / 0.7f * 0.8f, wakeLength / 0.7f * 1.2f);
            wm.startSize = new ParticleSystem.MinMaxCurve(r * 1.6f, r * 2.6f);
            var we = wake.emission;
            we.rateOverTime = 220f * k;

            glow.intensity = 2.5f * k;
            glow.range = r * 40f * (0.5f + k);
        }

        void BuildFx()
        {
            NukeAssets.Init();
            float r = bodyRadius * plasmaScale;
            fx = new GameObject("BDNukes_ReentryPlasma");

            // Sheath: a tight, bright, flickering cap of glow around the nose.
            var sheathGo = new GameObject("Sheath");
            sheathGo.transform.SetParent(fx.transform, false);
            sheath = sheathGo.AddComponent<ParticleSystem>();
            sheath.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var m = sheath.main;
            m.loop = true;
            m.simulationSpace = ParticleSystemSimulationSpace.Local;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.18f);
            m.startSpeed = new ParticleSystem.MinMaxCurve(r * 2f, r * 6f);
            m.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            m.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.92f, 0.8f, 0.9f), new Color(1f, 0.65f, 0.3f, 0.8f));
            m.maxParticles = 80;
            var sh = sheath.shape;
            sh.shapeType = ParticleSystemShapeType.Hemisphere;
            sh.radius = r * 0.9f;
            sh.rotation = new Vector3(180f, 0f, 0f); // dome faces upstream (-Z)
            var sc = sheath.colorOverLifetime;
            sc.enabled = true;
            sc.color = new ParticleSystem.MinMaxGradient(Fade(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.55f, 0.2f), 0.9f));
            var sz = sheath.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.8f, 1f, 1.4f));
            var sr = sheathGo.GetComponent<ParticleSystemRenderer>();
            sr.sharedMaterial = NukeAssets.Glow;
            sr.renderMode = ParticleSystemRenderMode.Billboard;
            sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sr.receiveShadows = false;
            sheath.Play();

            // Wake: ionised trail streaming downstream (+Z), cooling from orange-white to dull red and fading out.
            var wakeGo = new GameObject("Wake");
            wakeGo.transform.SetParent(fx.transform, false);
            wake = wakeGo.AddComponent<ParticleSystem>();
            wake.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var wm = wake.main;
            wm.loop = true;
            wm.simulationSpace = ParticleSystemSimulationSpace.Local;
            wm.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.7f);
            wm.startSpeed = r * 100f;
            wm.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            wm.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.8f, 0.55f, 0.7f), new Color(1f, 0.55f, 0.25f, 0.6f));
            wm.maxParticles = 300;
            var wsh = wake.shape;
            wsh.shapeType = ParticleSystemShapeType.Cone;
            wsh.angle = 6f;
            wsh.radius = r * 0.8f;
            var wc = wake.colorOverLifetime;
            wc.enabled = true;
            wc.color = new ParticleSystem.MinMaxGradient(Fade(new Color(1f, 0.85f, 0.6f), new Color(0.8f, 0.2f, 0.08f), 0.75f));
            var wsz = wake.sizeOverLifetime;
            wsz.enabled = true;
            wsz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 3.5f));
            var wr = wakeGo.GetComponent<ParticleSystemRenderer>();
            wr.sharedMaterial = NukeAssets.Glow;
            wr.renderMode = ParticleSystemRenderMode.Billboard;
            wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wr.receiveShadows = false;
            wake.Play();

            // Faint ionisation glow lighting the vehicle and nearby cloud.
            glow = fx.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.6f, 0.32f);
            glow.shadows = LightShadows.None;
            glow.intensity = 0f;
            glow.range = r * 20f;
        }

        static Gradient Fade(Color hot, Color cool, float startAlpha)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(hot, 0f), new GradientColorKey(cool, 1f) },
                new[] { new GradientAlphaKey(startAlpha, 0f), new GradientAlphaKey(startAlpha * 0.6f, 0.35f), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        void OnDestroy()
        {
            if (fx != null) Destroy(fx);
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Re-entry vehicle");
            sb.AppendLine($"Plasma sheath above {plasmaMinSpeed:0} m/s in atmosphere");
            if (spinRate != 0f) sb.AppendLine($"Spin stabilised: {spinRate:0} deg/s after release");
            sb.AppendLine("Use with ModuleAblator for heat shielding");
            return sb.ToString();
        }
    }
}
