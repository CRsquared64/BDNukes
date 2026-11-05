using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Project Orion nuclear pulse propulsion: small nuclear pulse units are thrown out behind a pusher plate and
    /// detonated one at a time, and each burst kicks the plate (and, through the shock absorbers, the ship).
    ///
    /// Each pulse uses one pulse unit (resource BDNukesPulseUnit) and delivers a fixed impulse along the thrust
    /// axis. The main throttle sets the pulse rate (one pulse every pulseInterval seconds at full throttle), not
    /// the size of the kick. The impulse is spread over the shock-absorber stroke (strokeTime) and shared out
    /// over the vessel's parts by mass, so the pulse doesn't tear the ship apart at its joints.
    ///
    /// No BDArmory nuke is used: the pulse is a self-contained flash (point light + an expanding, fading glow
    /// billboard behind the plate, and a faint plasma disc in vacuum) and a small explosion sound in atmosphere.
    /// Start it by staging or the Activate button / action group.
    /// </summary>
    public class ModuleOrionPulseEngine : PartModule
    {
        /// <summary>Impulse per pulse (kN*s, which is t*m/s in KSP units).</summary>
        [KSPField] public float pulseImpulse = 6000f;

        /// <summary>Seconds between pulses at full throttle.</summary>
        [KSPField] public float pulseInterval = 0.8f;

        /// <summary>Below this throttle the engine doesn't pulse at all.</summary>
        [KSPField] public float minThrottle = 0.02f;

        [KSPField] public string resourceName = "BDNukesPulseUnit";
        [KSPField] public float unitsPerPulse = 1f;

        /// <summary>Shock-absorber stroke: the time each pulse's impulse is spread over.</summary>
        [KSPField] public float strokeTime = 0.25f;

        /// <summary>Share of the impulse put on the engine part itself; the rest is spread over every part by mass.</summary>
        [KSPField] public float plateShare = 0.3f;

        /// <summary>Transform on the plate's outer face, forward pointing out behind the ship. Falls back to the part's -Y.</summary>
        [KSPField] public string plateTransformName = "pusherPlate";

        /// <summary>Distance behind the plate at which pulse units go off (m).</summary>
        [KSPField] public float pulseDistance = 25f;

        /// <summary>Radius of the fireball flash at its largest (m).</summary>
        [KSPField] public float flashRadius = 14f;

        /// <summary>Lifetime of the flash (s).</summary>
        [KSPField] public float flashTime = 0.3f;

        [KSPField] public string soundPath = "BDArmory/Sounds/explode1";
        [KSPField] public float soundVolume = 0.45f;

        [KSPField(isPersistant = true)] public bool engineActive;

        [KSPField(guiActive = true, guiName = "Orion")]
        public string status = "Off";

        [KSPField(guiActive = true, guiName = "Pulse rate")]
        public string pulseRateText = "0 /s";

        [KSPField(guiActive = true, guiName = "Pulse units")]
        public string unitsText = "";

        Transform plate;
        int resourceId;
        float phase;
        readonly List<float> strokes = new List<float>();
        int pulseCount;

        GameObject fx;
        ParticleSystem flashPs, discPs;
        Light flashLight;
        float lightStart = -10f;
        float lightPeak;
        AudioSource audioSource;
        AudioClip pulseClip;

        public override bool IsStageable() => true;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            var def = PartResourceLibrary.Instance?.GetDefinition(resourceName);
            resourceId = def != null ? def.id : resourceName.GetHashCode();
            plate = string.IsNullOrEmpty(plateTransformName) ? null : part.FindModelTransform(plateTransformName);
            UpdateUI(0f);
            if (!HighLogic.LoadedSceneIsFlight) return;

            NukeAssets.Init();
            BuildFX();
            audioSource = part.gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 1f;
            audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
            audioSource.minDistance = 30f;
            audioSource.maxDistance = 4000f;
            pulseClip = GameDatabase.Instance != null ? GameDatabase.Instance.GetAudioClip(soundPath) : null;
        }

        public override void OnActive()
        {
            base.OnActive();
            Activate();
        }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Activate Orion Drive")]
        public void ActivateEvent() { Activate(); }

        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Shut Down Orion Drive", active = false)]
        public void ShutdownEvent() { Shutdown(); }

        [KSPAction("Toggle Orion Drive")]
        public void ToggleAction(KSPActionParam param)
        {
            if (param.type == KSPActionType.Deactivate || (param.type == KSPActionType.Toggle && engineActive)) Shutdown();
            else Activate();
        }

        [KSPAction("Activate Orion Drive")]
        public void ActivateAction(KSPActionParam param) { Activate(); }

        [KSPAction("Shut Down Orion Drive")]
        public void ShutdownAction(KSPActionParam param) { Shutdown(); }

        void Activate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            engineActive = true;
            phase = 0.75f; // first pulse comes quickly once the throttle is up
        }

        void Shutdown()
        {
            engineActive = false;
            phase = 0f;
        }

        Vector3 ThrustDir => plate != null ? -plate.forward : part.transform.up;
        Vector3 PlatePos => plate != null ? plate.position : part.transform.position - part.transform.up * 2f;

        float Throttle => vessel != null ? Mathf.Clamp01(vessel.ctrlState.mainThrottle) : 0f;

        // ----------------------------------------------------------------------------------------
        // Pulses and impulse
        // ----------------------------------------------------------------------------------------

        void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || part.rb == null) return;
            float dt = TimeWarp.fixedDeltaTime;
            if (vessel.packed)
            {
                strokes.Clear();
                return;
            }

            if (engineActive)
            {
                float thr = Throttle;
                if (thr >= minThrottle)
                {
                    phase += dt * thr / Mathf.Max(0.05f, pulseInterval);
                    if (phase >= 1f)
                    {
                        phase -= 1f;
                        if (phase > 1f) phase = 0f; // never more than one pulse per physics frame
                        Pulse();
                    }
                }
            }

            ApplyStrokes(dt);
        }

        void Pulse()
        {
            double got = part.RequestResource(resourceId, (double)unitsPerPulse);
            if (got < unitsPerPulse * 0.999f)
            {
                if (got > 0d) part.RequestResource(resourceId, -got); // give back a partial draw
                if (engineActive)
                {
                    Shutdown();
                    ScreenMessages.PostScreenMessage(part.partInfo.title + ": pulse unit magazine empty", 4f, ScreenMessageStyle.UPPER_CENTER);
                }
                return;
            }
            pulseCount++;
            strokes.Add(Mathf.Max(TimeWarp.fixedDeltaTime, strokeTime));
            PulseFX();
        }

        void ApplyStrokes(float dt)
        {
            if (strokes.Count == 0) return;
            float stroke = Mathf.Max(TimeWarp.fixedDeltaTime, strokeTime);
            float impulse = 0f;
            for (int i = strokes.Count - 1; i >= 0; i--)
            {
                float t = Mathf.Min(dt, strokes[i]);
                impulse += pulseImpulse * t / stroke;
                strokes[i] -= t;
                if (strokes[i] <= 1e-5f) strokes.RemoveAt(i);
            }
            if (impulse <= 0f) return;

            Vector3 force = ThrustDir * (impulse / dt); // kN, applied for this frame
            float share = Mathf.Clamp01(plateShare);
            part.rb.AddForce(force * share, ForceMode.Force);

            float total = 0f;
            var parts = vessel.parts;
            for (int i = 0; i < parts.Count; i++) if (parts[i].rb != null) total += parts[i].rb.mass;
            if (total <= 0f) { part.rb.AddForce(force * (1f - share), ForceMode.Force); return; }
            Vector3 rest = force * (1f - share) / total;
            for (int i = 0; i < parts.Count; i++)
            {
                var rb = parts[i].rb;
                if (rb != null) rb.AddForce(rest * rb.mass, ForceMode.Force);
            }
        }

        // ----------------------------------------------------------------------------------------
        // Flash, plasma disc, sound
        // ----------------------------------------------------------------------------------------

        void BuildFX()
        {
            fx = new GameObject("BDNukes_OrionFX");
            fx.transform.SetParent(part.transform, false);

            flashPs = MakeSystem("Flash", fx.transform, false);
            var col = flashPs.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 1f, 1f), 0f), new GradientColorKey(new Color(1f, 0.78f, 0.5f), 0.35f), new GradientColorKey(new Color(1f, 0.4f, 0.15f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.3f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
            var size = flashPs.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.25f, 6f, 6f), new Keyframe(0.4f, 0.85f), new Keyframe(1f, 1f)));

            // the plasma disc lies across the thrust axis: billboards aligned to the plate transform
            var discGo = new GameObject("PlasmaDisc");
            discGo.transform.SetParent(plate != null ? plate : part.transform, false);
            if (plate == null) discGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            discPs = MakeSystem("PlasmaDisc", discGo.transform, true);
            var dcol = discPs.colorOverLifetime;
            dcol.enabled = true;
            var dg = new Gradient();
            dg.SetKeys(
                new[] { new GradientColorKey(new Color(0.75f, 0.8f, 1f), 0f), new GradientColorKey(new Color(0.55f, 0.45f, 1f), 1f) },
                new[] { new GradientAlphaKey(0.5f, 0f), new GradientAlphaKey(0.25f, 0.4f), new GradientAlphaKey(0f, 1f) });
            dcol.color = new ParticleSystem.MinMaxGradient(dg);
            var dsize = discPs.sizeOverLifetime;
            dsize.enabled = true;
            dsize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.3f, 1f, 1f));

            var lightGo = new GameObject("PulseLight");
            lightGo.transform.SetParent(fx.transform, false);
            flashLight = lightGo.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.color = new Color(1f, 0.9f, 0.75f);
            flashLight.shadows = LightShadows.None;
            flashLight.range = flashRadius * 30f;
            flashLight.intensity = 0f;
            flashLight.enabled = false;
        }

        ParticleSystem MakeSystem(string name, Transform parent, bool localAlign)
        {
            ParticleSystem ps;
            if (parent.gameObject.GetComponent<ParticleSystem>() == null && parent.name == name)
                ps = parent.gameObject.AddComponent<ParticleSystem>();
            else
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                ps = go.AddComponent<ParticleSystem>();
            }
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.maxParticles = 64;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = NukeAssets.Glow;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = localAlign ? ParticleSystemRenderSpace.Local : ParticleSystemRenderSpace.View;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 100f; // the flash is big on screen up close
            ps.Play();
            return ps;
        }

        /// <summary>0 in vacuum, 1 in thick air.</summary>
        float AirFactor => vessel != null ? Mathf.Clamp01((float)vessel.staticPressurekPa / 30f) : 0f;

        void PulseFX()
        {
            if (fx == null) return;
            Vector3 dir = ThrustDir;
            Vector3 pos = PlatePos - dir * pulseDistance;
            float air = AirFactor;
            float t = Mathf.Max(0.05f, flashTime);

            // core flash plus a few offset puffs; in air the fireball is a bit bigger and lasts a little longer
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                startSize = flashRadius * 2f * (1f + 0.3f * air),
                startLifetime = t * (1f + 0.4f * air),
                startColor = Color.white,
                rotation = Random.Range(0f, 360f),
                applyShapeToPosition = false,
            };
            flashPs.Emit(ep, 1);
            for (int i = 0; i < 3; i++)
            {
                ep.position = pos + Random.insideUnitSphere * flashRadius * 0.4f;
                ep.startSize = flashRadius * Random.Range(0.8f, 1.3f);
                ep.startLifetime = t * Random.Range(0.6f, 1f);
                ep.startColor = new Color(1f, 0.85f, 0.65f, 0.8f);
                ep.rotation = Random.Range(0f, 360f);
                flashPs.Emit(ep, 1);
            }

            // faint plasma disc between the burst and the plate, only out of the air
            float vac = 1f - Mathf.Clamp01((float)(vessel != null ? vessel.staticPressurekPa : 0d) / 5f);
            if (vac > 0.01f)
            {
                var dp = new ParticleSystem.EmitParams
                {
                    position = PlatePos - dir * pulseDistance * 0.45f,
                    startSize = flashRadius * 5f,
                    startLifetime = t * 1.5f,
                    startColor = new Color(1f, 1f, 1f, 0.5f * vac),
                    rotation = Random.Range(0f, 360f),
                    applyShapeToPosition = false,
                };
                discPs.Emit(dp, 1);
            }

            flashLight.transform.position = pos;
            lightPeak = 6f + 2f * air;
            lightStart = Time.time;
            flashLight.intensity = lightPeak;
            flashLight.enabled = true;

            if (audioSource != null && pulseClip != null && air > 0.01f)
            {
                audioSource.pitch = Random.Range(0.85f, 1.05f);
                audioSource.PlayOneShot(pulseClip, soundVolume * air * GameSettings.SHIP_VOLUME * NukeSettings.Current.volume);
            }
        }

        void Update()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (flashLight != null && flashLight.enabled)
            {
                float k = (Time.time - lightStart) / Mathf.Max(0.05f, flashTime);
                if (k >= 1f) { flashLight.enabled = false; flashLight.intensity = 0f; }
                else flashLight.intensity = lightPeak * (1f - k) * (1f - k);
            }
            UpdateUI(engineActive ? Throttle : 0f);
        }

        void UpdateUI(float thr)
        {
            Events["ActivateEvent"].active = !engineActive;
            Events["ShutdownEvent"].active = engineActive;
            bool pulsing = engineActive && thr >= minThrottle;
            float rate = pulsing ? thr / Mathf.Max(0.05f, pulseInterval) : 0f;
            pulseRateText = rate.ToString("0.00") + " /s";
            if (!engineActive) status = "Off";
            else if (!pulsing) status = "Active (throttle up to pulse)";
            else status = $"Pulsing ({rate * pulseImpulse:0} kN avg)";

            if (part != null && HighLogic.LoadedSceneIsFlight)
            {
                part.GetConnectedResourceTotals(resourceId, out double amount, out double max);
                unitsText = $"{amount:0} / {max:0}";
            }
            else
            {
                var r = part != null ? part.Resources[resourceName] : null;
                unitsText = r != null ? $"{r.amount:0} / {r.maxAmount:0}" : "";
            }
        }

        void OnDestroy()
        {
            if (fx != null) Destroy(fx);
            if (discPs != null) Destroy(discPs.gameObject);
        }

        public override string GetInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nuclear pulse propulsion");
            sb.AppendLine($"Impulse per pulse: {pulseImpulse:0} kN*s");
            sb.AppendLine($"Pulse rate: {1f / Mathf.Max(0.05f, pulseInterval):0.00} /s at full throttle");
            sb.AppendLine($"Average thrust: {pulseImpulse / Mathf.Max(0.05f, pulseInterval):0} kN at full throttle");
            var def = PartResourceLibrary.Instance?.GetDefinition(resourceName);
            if (def != null && def.density > 0f)
            {
                float isp = pulseImpulse / (unitsPerPulse * def.density * 9.80665f);
                sb.AppendLine($"Effective Isp: {isp:0} s");
                sb.AppendLine($"Propellant: {def.displayName} ({unitsPerPulse:0.#} per pulse)");
            }
            sb.AppendLine("Throttle sets the pulse rate. Not shown in the stock delta-v readout.");
            return sb.ToString();
        }
    }
}
