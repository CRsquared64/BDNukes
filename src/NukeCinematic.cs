using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A single nuclear detonation's visuals and audio.
    ///
    /// The particle systems are used purely as renderers: every particle is emitted once and its
    /// position, size and colour are computed analytically each frame. That gives full control
    /// over the time evolution (fireball -> rolling toroidal cap, rising stem, base surge) and keeps
    /// the effect anchored to the planet surface through floating-origin shifts.
    ///
    /// Scaling follows Glasstone &amp; Dolan, "The Effects of Nuclear Weapons" (W = yield in kt):
    ///   max fireball radius  R  ~ 70 m * W^0.4
    ///   stabilised cloud top H  ~ 5.8 km * W^0.26   (scaled by cloudHeightScale for Kerbin)
    ///   cap radius              ~ 0.28 H
    /// Cloud rise is time-compressed (cloudRiseTime) so it plays out on a game-friendly timescale.
    /// </summary>
    internal class NukeCinematic : MonoBehaviour
    {
        static readonly Color Ember = new Color(1f, 0.52f, 0.16f);
        static readonly Color DustDark = new Color(0.46f, 0.37f, 0.30f);
        static readonly Color DustLight = new Color(0.80f, 0.77f, 0.74f);
        static readonly Color CondWhite = new Color(0.96f, 0.96f, 0.98f);

        // --- inputs ---
        CelestialBody body;
        double lat, lon, alt;
        float W;
        float delay;
        float spawnTime;
        NukeSettings s;

        // --- derived physics ---
        float R;            // fireball radius (m)
        float tauG;         // glow e-fold time (s)
        float riseDelta;    // cap rise above burst point (m)
        float capRadius;    // final cap radius (m)
        float tauRise;      // rise e-fold time (s)
        float riseTime;     // yield-scaled cloudRiseTime (s)
        float yieldTimeScale;
        float t0;           // time the fireball starts to rise
        float hb;           // burst height above ground (m)
        float c;            // speed of sound
        float life;
        float groundFactor; // 1 = surface burst, 0 = no ground interaction
        bool vacuum, groundFx, stemFx, ringFx, sparksFx;

        // --- scene ---
        Transform cap, ground, rings;
        Light lightFx;
        readonly List<Layer> layers = new List<Layer>();
        Layer fireCore, capSmoke, capDetail, capGlow, flash, shock, wilson, skirt, surge, stem, sparks;
        Layer[] ringLayers;

        // --- vacuum burst ---
        Layer debrisShell, streamers, ionCloud;
        Layer[] auroraLayers;
        Vector3[] auroraFootL;      // field-line footpoints in the atmosphere, root-local
        float[] auroraArrival;
        float[] shellLump;          // per-particle shell radius factor (Rayleigh-Taylor lumpiness + field elongation)
        Vector3 fieldL;             // local magnetic field direction at the burst (dipole approximation)
        float vDebris;
        bool hasField;
        AudioSource crackSrc, rumbleSrc, boomSrc;
        AudioLowPassFilter lowPass;
        bool soundPlayed, nearShakeDone;

        // per-frame cached
        float t;
        Vector3 sunL;
        float daylight;
        Vector3 capPosL;
        float capScale;
        float camDist;
        Vector3 camDirL;
        float sunRotDeg;    // billboard rotation that turns the lit top of the billow texture towards the sun

        const int ShellFilaments = 64;

        public static void Spawn(Vector3 worldPos, float yield, float delay)
        {
            var body = FlightGlobals.currentMainBody;
            if (body == null) return;
            NukeAssets.Init();
            var go = new GameObject("BDNukes_Detonation");
            go.AddComponent<NukeCinematic>().Setup(body, worldPos, Mathf.Max(yield, 0.001f), Mathf.Max(0f, delay));
        }

        void Setup(CelestialBody b, Vector3 pos, float yield, float detonationDelay)
        {
            s = NukeSettings.Current;
            body = b;
            W = yield;
            delay = detonationDelay;
            spawnTime = Time.time;

            lat = body.GetLatitude(pos);
            lon = body.GetLongitude(pos);
            alt = body.GetAltitude(pos);
            double groundAlt = body.TerrainAltitude(lat, lon);
            if (body.ocean && groundAlt < 0d) groundAlt = 0d;
            hb = Mathf.Max(0f, (float)(alt - groundAlt));

            float rho = 0f;
            if (body.atmosphere && alt < body.atmosphereDepth)
                rho = (float)FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(pos), FlightGlobals.getExternalTemperature(pos));
            vacuum = rho < NukeVacuumPhysics.VacuumDensity;
            float densRatio = Mathf.Clamp(rho / 1.225f, 0.05f, 1f);

            R = 70f * Mathf.Pow(W, 0.4f) * s.sizeScale;
            tauG = (2.5f + 2.2f * Mathf.Pow(W, 0.3f)) * s.glowTimeScale;
            float H = 5800f * Mathf.Pow(W, 0.26f) * s.cloudHeightScale * s.sizeScale;
            float capTop = Mathf.Max(H, hb + 3f * R);
            riseDelta = Mathf.Max(2f * R, (capTop * 0.85f - hb) * Mathf.Clamp(Mathf.Pow(densRatio, 0.25f), 0.4f, 1f));
            // Megaton caps spread much wider relative to their height (Tsar Bomba: ~67 km tall, ~95 km across).
            capRadius = Mathf.Max(1.6f * R, 0.28f * H, 0.45f * H * Mathf.Pow(W / 1000f, 0.15f));
            // Bigger clouds take longer to climb and to disperse.
            yieldTimeScale = Mathf.Max(1f, Mathf.Pow(W / 20f, 0.12f));
            riseTime = s.cloudRiseTime * yieldTimeScale;
            tauRise = riseTime / 2.3f;
            t0 = 0.4f * tauG;
            c = s.speedOfSound;
            life = s.cloudLifetime * yieldTimeScale;
            vDebris = s.vacuumDebrisSpeed;

            groundFactor = Mathf.Clamp01(1f - hb / (3f * R));
            groundFx = !vacuum && hb < 3f * R;
            stemFx = !vacuum && hb < 6f * R;
            ringFx = !vacuum && s.condensationRings && densRatio > 0.2f;
            sparksFx = hb < 1.5f * R;

            UpdateAnchor();
            if (vacuum) SetupVacuum(pos);
            Build();
            NukeFXManager.Instance.Register(this);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            var manager = NukeFXManager.Existing;
            if (manager != null) manager.Unregister(this);
        }

        int N(int baseCount) => Mathf.Max(1, Mathf.RoundToInt(baseCount * s.particleDensity));

        void Build()
        {
            cap = Child("Cap", transform);
            ground = Child("Ground", transform);
            ground.localPosition = new Vector3(0f, -hb, 0f);
            rings = Child("Rings", transform);

            int seed = Random.Range(0, 100000);
            flash = Add(new Layer(transform, "Flash", NukeAssets.Glow, 3, seed + 1, false));

            if (vacuum)
            {
                debrisShell = Add(new Layer(transform, "DebrisShell", NukeAssets.Glow, N(320), seed + 2, false));
                shock = Add(new Layer(transform, "ShellLimb", NukeAssets.Ring, 1, seed + 3, false));
                sparks = Add(new Layer(transform, "Fragments", NukeAssets.Glow, N(110), seed + 4, false));
                ionCloud = Add(new Layer(transform, "IonCloud", NukeAssets.Glow, N(70), seed + 5, false));
                if (hasField && s.magneticStreamers)
                    streamers = Add(new Layer(transform, "FieldStreamers", NukeAssets.Glow, N(420), seed + 6, false));
                if (auroraFootL != null)
                {
                    auroraLayers = new Layer[auroraFootL.Length];
                    for (int k = 0; k < auroraFootL.Length; k++)
                        auroraLayers[k] = Add(new Layer(transform, "Aurora" + k, NukeAssets.Glow, N(560), seed + 40 + k, false));
                }

                // Lumpy, field-elongated shell: low-order random lobes + per-particle jitter.
                var rng = new System.Random(seed);
                Vector3 RandDir() => new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f).normalized;
                Vector3 a1 = RandDir(), a2 = RandDir(), a3 = RandDir();
                shellLump = new float[debrisShell.r0.Length];
                for (int i = 0; i < shellLump.Length; i++)
                {
                    Vector3 d = debrisShell.SphereDir(i % ShellFilaments);
                    float lobes = 0.16f * Vector3.Dot(d, a1) + 0.14f * (Vector3.Dot(d, a2) * Vector3.Dot(d, a2) - 0.33f) + 0.1f * Mathf.Sin(5f * Vector3.Dot(d, a3));
                    float along = hasField ? 0.3f * Mathf.Abs(Vector3.Dot(d, fieldL)) : 0f;
                    shellLump[i] = Mathf.Max(0.5f, 1f + lobes + along + (debrisShell.r0[i] - 0.5f) * 0.12f);
                }
            }
            else
            {
                capSmoke = Add(new Layer(cap, "CapSmoke", NukeAssets.Smoke, N(320), seed + 10, true));
                capDetail = Add(new Layer(cap, "CapDetail", NukeAssets.Smoke, N(900), seed + 15, true));
                capGlow = Add(new Layer(cap, "CapGlow", NukeAssets.Glow, N(80), seed + 11, true));
                fireCore = Add(new Layer(cap, "Fireball", NukeAssets.Glow, N(45), seed + 12, true));
                shock = Add(new Layer(transform, "ShockFront", NukeAssets.Ring, 1, seed + 13, false));
                wilson = Add(new Layer(transform, "WilsonCloud", NukeAssets.Mist, N(220), seed + 14, false));
                if (ringFx)
                {
                    ringLayers = new Layer[3];
                    for (int k = 0; k < 3; k++)
                        ringLayers[k] = Add(new Layer(rings, "CondensationRing" + k, NukeAssets.Mist, N(220), seed + 20 + k, false));
                }
                if (groundFx)
                {
                    skirt = Add(new Layer(ground, "DustSkirt", NukeAssets.Smoke, N(320), seed + 30, false));
                    surge = Add(new Layer(ground, "BaseSurge", NukeAssets.Smoke, N(280), seed + 31, false));
                }
                if (stemFx) stem = Add(new Layer(ground, "Stem", NukeAssets.Smoke, N(420), seed + 32, false));
                if (sparksFx) sparks = Add(new Layer(transform, "Ejecta", NukeAssets.Glow, N(70), seed + 33, false));
            }

            var lightGo = new GameObject("NukeLight");
            lightGo.transform.SetParent(transform, false);
            lightFx = lightGo.AddComponent<Light>();
            lightFx.type = LightType.Point;
            lightFx.color = new Color(1f, 0.85f, 0.65f);
            lightFx.range = Mathf.Clamp(80f * R, 3000f, 80000f);
            lightFx.intensity = 0f;
            lightFx.shadows = LightShadows.None;

            var audioGo = new GameObject("NukeAudio");
            audioGo.transform.SetParent(transform, false);
            crackSrc = MakeSource(audioGo, NukeAssets.Crack);
            rumbleSrc = MakeSource(audioGo, NukeAssets.Rumble);
            boomSrc = MakeSource(audioGo, NukeAssets.StockBoom);
            lowPass = audioGo.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 22000f;
        }

        static Transform Child(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        Layer Add(Layer l) { layers.Add(l); return l; }

        static AudioSource MakeSource(GameObject go, AudioClip clip)
        {
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.playOnAwake = false;
            a.spatialBlend = 0f;
            a.dopplerLevel = 0f;
            a.priority = 0;
            a.loop = false;
            return a;
        }

        // ----------------------------------------------------------------------------------------
        // Time evolution helpers
        // ----------------------------------------------------------------------------------------

        static float SS(float a, float b, float x)
        {
            if (b <= a) return x >= b ? 1f : 0f;
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        float Rise(float time) => 1f - Mathf.Exp(-Mathf.Max(0f, time - t0) / tauRise);
        float CapHeight(float time) => riseDelta * Rise(time);
        float CapScale(float time) => R * (1f + (capRadius / R - 1f) * Rise(time)) * (1f + 0.12f * Mathf.Max(0f, time - riseTime) / riseTime);
        float Glow(float time) => Mathf.Exp(-Mathf.Max(0f, time) / tauG);
        float ShockRadius(float time) => R + c * Mathf.Max(0f, time);
        float FlashIntensity(float time) => time < 0f ? 0f
            : vacuum ? Mathf.Exp(-time / 0.05f) + 0.15f * Mathf.Exp(-time / 0.6f)   // X-ray pulse: no air to trap it
            : Mathf.Exp(-time / 0.12f) + 0.3f * Mathf.Exp(-time / 0.9f);
        float FadeOut => vacuum ? 1f - SS(life - 8f, life, t) : 1f - SS(life - 45f, life, t);
        float ShellRadius(float time) => R * 0.3f + vDebris * Mathf.Max(0f, time);
        float Morph => SS(0.15f * tauG, 2f * tauG + 4f, t);

        // ----------------------------------------------------------------------------------------
        // Frame update
        // ----------------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (body == null || FlightGlobals.currentMainBody != body)
            {
                Destroy(gameObject);
                return;
            }

            UpdateAnchor();
            t = Time.time - spawnTime - delay;
            if (t < 0f) return;
            if (t > life)
            {
                Destroy(gameObject);
                return;
            }

            Vector3 sunW = Planetarium.fetch != null && Planetarium.fetch.Sun != null
                ? ((Vector3)(Planetarium.fetch.Sun.position) - transform.position).normalized
                : Vector3.up;
            sunL = Quaternion.Inverse(transform.rotation) * sunW;
            daylight = Mathf.Clamp01(sunL.y * 3f + 0.3f);

            capPosL = new Vector3(0f, vacuum ? 0f : CapHeight(t), 0f);
            capScale = vacuum ? R : CapScale(t);
            cap.localPosition = capPosL;
            cap.localScale = Vector3.one * capScale;

            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - transform.position;
                camDist = toCam.magnitude;
                camDirL = Quaternion.Inverse(transform.rotation) * (toCam / Mathf.Max(1f, camDist));
                float sx = Vector3.Dot(sunW, cam.transform.right), sy = Vector3.Dot(sunW, cam.transform.up);
                sunRotDeg = Mathf.Atan2(sx, sy) * Mathf.Rad2Deg * s.billowRotationSign;
            }
            else
            {
                camDist = 1e6f;
                camDirL = Vector3.up;
            }

            UpdateLight();
            UpdateFlash();
            if (vacuum)
            {
                UpdateDebrisShell();
                UpdateFragments();
                UpdateIonCloud();
                if (streamers != null) UpdateStreamers();
                if (auroraLayers != null) for (int k = 0; k < auroraLayers.Length; k++) UpdateAurora(k);
            }
            else
            {
                UpdateFireball();
                UpdateCapSmoke();
                UpdateCapDetail();
                UpdateCapGlow();
                UpdateShock();
                UpdateWilson();
                if (ringLayers != null) for (int k = 0; k < ringLayers.Length; k++) UpdateRing(k);
                if (skirt != null) UpdateSkirt();
                if (surge != null) UpdateSurge();
                if (stem != null) UpdateStem();
                if (sparks != null) UpdateSparks();
            }
            UpdateAudioAndShake(cam);
        }

        void UpdateAnchor()
        {
            Vector3 pos = body.GetWorldSurfacePosition(lat, lon, alt);
            transform.position = pos;
            Vector3 up = (pos - (Vector3)body.position).normalized;
            // Lock yaw to the body's north so offsets computed at detonation stay fixed to the surface.
            Vector3 north = Vector3.ProjectOnPlane(NorthAxis(), up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        Vector3 NorthAxis() => ((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized;

        /// <summary>Dipole field direction (moment along the spin axis) at a world position.</summary>
        Vector3 DipoleField(Vector3 worldPos)
        {
            Vector3 m = NorthAxis();
            Vector3 r = (worldPos - (Vector3)body.position).normalized;
            return (3f * Vector3.Dot(m, r) * r - m).normalized;
        }

        void SetupVacuum(Vector3 burstWorld)
        {
            life = 25f;
            hasField = body != Planetarium.fetch?.Sun;
            if (!hasField) return;
            fieldL = Quaternion.Inverse(transform.rotation) * DipoleField(burstWorld);

            // Artificial aurora (Starfish Prime): beta particles spiral down the field lines and light up the upper
            // atmosphere where each end of the line dips into it. Trace the line both ways to find those footpoints.
            if (!s.artificialAurora || !body.atmosphere || alt < body.atmosphereDepth * 0.4) return;
            var feet = new List<Vector3>();
            var arrivals = new List<float>();
            double auroraAlt = body.atmosphereDepth * 0.75;
            float step = Mathf.Max(500f, (float)alt * 0.01f);
            for (int dir = -1; dir <= 1; dir += 2)
            {
                Vector3 p = burstWorld;
                float travelled = 0f;
                for (int i = 0; i < 4000; i++)
                {
                    p += DipoleField(p) * (dir * step);
                    travelled += step;
                    double a = body.GetAltitude(p);
                    if (a < auroraAlt)
                    {
                        feet.Add(Quaternion.Inverse(transform.rotation) * (p - burstWorld));
                        arrivals.Add(travelled);
                        break;
                    }
                    if (a > alt * 4d) break; // field line heads out into space
                }
            }
            if (feet.Count == 0) return;
            auroraFootL = feet.ToArray();
            auroraArrival = new float[arrivals.Count];
            for (int i = 0; i < arrivals.Count; i++) auroraArrival[i] = arrivals[i] / (vDebris * 3f) + 0.5f;
            life = 60f;
        }

        /// <summary>Fake volumetric lighting: sun diffuse + ambient + warm light from the fireball.</summary>
        Color Shade(Color baseCol, Vector3 nL, float fire, Vector3 toFireL)
        {
            // the billow texture already carries per-lump shading, so the macro term is gentler
            float diff = Mathf.Max(0f, Vector3.Dot(nL, sunL)) * daylight;
            float amb = 0.42f + 0.22f * daylight;
            Color col = baseCol * (amb + 0.55f * diff);
            if (fire > 0f) col += Ember * fire * (0.35f + 0.65f * Mathf.Max(0f, Vector3.Dot(nL, toFireL)));
            col.a = 1f;
            return col;
        }

        static Color WithAlpha(Color c, float a) { c.a = Mathf.Clamp01(a); return c; }

        /// <summary>Billboard rotation pointing the billow's lit top at the sun, with a little per-puff variation.</summary>
        float Billow(float rand) => sunRotDeg + (rand - 0.5f) * 50f;

        static Color FireballColor(float x)
        {
            // x = t / tauG
            if (x < 0.05f) return new Color(1f, 1f, 1f);
            if (x < 0.25f) return Color.Lerp(new Color(1f, 0.98f, 0.92f), new Color(1f, 0.88f, 0.55f), (x - 0.05f) / 0.2f);
            if (x < 0.7f) return Color.Lerp(new Color(1f, 0.88f, 0.55f), new Color(1f, 0.55f, 0.18f), (x - 0.25f) / 0.45f);
            return Color.Lerp(new Color(1f, 0.55f, 0.18f), new Color(0.75f, 0.16f, 0.04f), Mathf.Clamp01((x - 0.7f) / 1.0f));
        }

        void UpdateLight()
        {
            if (vacuum)
            {
                float x = Mathf.Clamp01(t / ShellLife);
                lightFx.intensity = (12f * FlashIntensity(t) + 2f * Mathf.Pow(1f - x, 2f)) * FadeOut;
                lightFx.color = Color.Lerp(PlasmaColor(x), new Color(0.85f, 0.9f, 1f), Mathf.Clamp01(FlashIntensity(t)));
                lightFx.range = Mathf.Clamp(ShellRadius(t) * 3f, 3000f, 200000f);
                lightFx.transform.localPosition = Vector3.zero;
                return;
            }
            float i = 8f * FlashIntensity(t) + 3.5f * Glow(t);
            lightFx.intensity = i * FadeOut;
            lightFx.color = Color.Lerp(new Color(1f, 0.6f, 0.3f), new Color(1f, 0.95f, 0.88f), Mathf.Clamp01(FlashIntensity(t)));
            lightFx.transform.localPosition = capPosL;
        }

        void UpdateFlash()
        {
            int n = flash.Begin();
            float fi = FlashIntensity(t);
            float scale = vacuum ? 3f : 1f;
            float[] sizes = { 14f, 6f, 2.4f };
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Clamp01(fi * (i == 2 ? 1f : 0.7f));
                flash.Set(i, capPosL, R * sizes[i % 3] * scale, new Color(0.92f, 0.95f, 1f, a), 0f);
            }
            flash.End(n);
        }

        // --- Fireball: white-hot core that expands, cools through yellow/orange/red and rolls into the cap ---
        void UpdateFireball()
        {
            int n = fireCore.Begin();
            float x = t / tauG;
            float grow = 1f - Mathf.Exp(-t / 0.15f);
            float m = Morph;
            Color col = FireballColor(x);
            float intensity = (t < 0.5f ? 1.3f : 1f) * Mathf.Exp(-Mathf.Max(0f, x - 0.35f) / 0.85f) * FadeOut;
            for (int i = 0; i < n; i++)
            {
                Vector3 sph = fireCore.SphereDir(i) * (0.55f * Mathf.Sqrt(fireCore.r0[i])) * grow;
                Vector3 tor = TorusPoint(fireCore.r1[i] * 2f * Mathf.PI, fireCore.r2[i] * 2f * Mathf.PI - 0.5f * t, 0.75f, 0.18f);
                Vector3 p = Vector3.Lerp(sph, tor, m);
                float size = (0.9f + 0.6f * fireCore.r3[i]) * Mathf.Max(0.05f, grow) * (1f - 0.35f * m);
                float a = intensity * (0.55f + 0.45f * fireCore.r3[i]);
                fireCore.Set(i, p, size, WithAlpha(col, a), fireCore.r3[i] * 360f + t * 8f);
            }
            fireCore.End(n);
        }

        static Vector3 TorusPoint(float theta, float phi, float ringR, float tube)
        {
            float rho = ringR + tube * Mathf.Cos(phi);
            return new Vector3(rho * Mathf.Cos(theta), tube * Mathf.Sin(phi) * 0.85f, rho * Mathf.Sin(theta));
        }

        // --- Cap: turbulent shell of the fireball that becomes a rolling toroidal vortex (the mushroom cap) ---
        void UpdateCapSmoke()
        {
            int n = capSmoke.Begin();
            float m = Morph;
            float glow = Glow(t);
            float ember = Mathf.Pow(glow, 0.55f);
            float appear = SS(0.2f * tauG, 0.9f * tauG, t) * FadeOut;
            // poloidal roll: fast while the fireball is buoyant, slowing as the cloud stabilises
            float roll = 0.55f * tauRise * (1f - Mathf.Exp(-t / tauRise)) + 0.03f * t;
            Color dust = Color.Lerp(new Color(0.55f, 0.36f, 0.27f), DustLight, SS(0.5f * riseTime, 2f * riseTime, t));
            for (int i = 0; i < n; i++)
            {
                float r0 = capSmoke.r0[i], r1 = capSmoke.r1[i], r2 = capSmoke.r2[i], r3 = capSmoke.r3[i];
                bool dome = r3 < 0.32f; // fills the centre/top for the cauliflower crown
                float theta = r1 * 2f * Mathf.PI + 0.02f * t * (r0 - 0.5f);
                float phi = r2 * 2f * Mathf.PI - roll * (0.7f + 0.6f * r0);
                float ringR = dome ? 0.32f : 0.78f;
                float tube = (dome ? 0.38f : 0.44f) * (0.55f + 0.45f * Mathf.Sqrt(r0));

                Vector3 sph = capSmoke.SphereDir(i) * (0.78f + 0.22f * r0);
                Vector3 tor = TorusPoint(theta, phi, ringR, tube);
                if (dome) tor.y = Mathf.Abs(tor.y) + 0.12f;
                Vector3 wobble = new Vector3(Mathf.Sin(t * 0.6f + r0 * 40f), Mathf.Sin(t * 0.5f + r1 * 40f), Mathf.Sin(t * 0.55f + r2 * 40f)) * 0.03f;
                Vector3 p = Vector3.Lerp(sph, tor, m) + wobble;

                Vector3 core = Vector3.Lerp(Vector3.zero, new Vector3(ringR * Mathf.Cos(theta), 0f, ringR * Mathf.Sin(theta)), m);
                Vector3 nL = (p - core).sqrMagnitude > 1e-6f ? (p - core).normalized : Vector3.up;

                float inner = 1f - Mathf.Clamp01((p - core).magnitude / 0.6f);
                float fire = ember * (0.7f + 0.8f * inner) + 0.25f * ember * Mathf.Max(0f, -nL.y);
                Color col = Shade(Color.Lerp(dust, DustDark, 0.35f * Mathf.Max(0f, -nL.y)), nL, fire, -nL);
                float size = (0.5f + 0.35f * r0) * (1f + 0.35f * m);
                capSmoke.Set(i, p, size, WithAlpha(col, 0.85f * appear), Billow(r3));
            }
            capSmoke.End(n);
        }

        // --- Cap surface detail: hundreds of small billows rolling over the torus and boiling in place ---
        void UpdateCapDetail()
        {
            int n = capDetail.Begin();
            float m = Morph;
            float ember = Mathf.Pow(Glow(t), 0.55f);
            float appear = SS(0.15f * tauG, 0.8f * tauG, t) * FadeOut;
            // the surface rolls faster than the bulk so the motion is easy to read
            float roll = 0.8f * tauRise * (1f - Mathf.Exp(-t / tauRise)) + 0.05f * t;
            Color dust = Color.Lerp(new Color(0.62f, 0.42f, 0.32f), DustLight, SS(0.5f * riseTime, 2f * riseTime, t));
            for (int i = 0; i < n; i++)
            {
                float r0 = capDetail.r0[i], r1 = capDetail.r1[i], r2 = capDetail.r2[i], r3 = capDetail.r3[i];
                bool dome = r3 < 0.3f;
                float theta = r1 * 2f * Mathf.PI + 0.015f * t * (r0 - 0.5f);
                float phi = r2 * 2f * Mathf.PI - roll * (1.1f + 0.4f * r0);
                float ringR = dome ? 0.3f : 0.78f;
                // lumpy tube surface: large bulges that drift slowly, so the silhouette keeps changing
                float lump = Mathf.Sin(3f * theta + 2f * phi + r3 * 6f) * Mathf.Sin(5f * theta - 3f * phi + t * 0.3f);
                float tube = (dome ? 0.46f : 0.5f) * (1f + 0.14f * lump);

                Vector3 sph = capDetail.SphereDir(i) * (0.92f + 0.1f * r0);
                Vector3 tor = TorusPoint(theta, phi, ringR, tube);
                if (dome) tor.y = Mathf.Abs(tor.y) + 0.14f;
                Vector3 core = Vector3.Lerp(Vector3.zero, new Vector3(ringR * Mathf.Cos(theta), 0f, ringR * Mathf.Sin(theta)), m);
                Vector3 p = Vector3.Lerp(sph, tor, m);
                Vector3 nL = (p - core).sqrMagnitude > 1e-6f ? (p - core).normalized : Vector3.up;
                p += nL * (0.035f * Mathf.Sin(t * (0.7f + r0) + r2 * 30f)); // boiling in and out

                float under = Mathf.Max(0f, -nL.y);
                float fire = ember * (0.45f + 0.9f * under);
                Color baseCol = Color.Lerp(dust, DustDark, 0.45f * under);
                Color col = Shade(baseCol, nL, fire, -nL);
                float size = (0.13f + 0.13f * r0) * (1f + 0.18f * Mathf.Sin(t * (0.8f + 1.2f * r0) + r1 * 20f)) * (1f + 0.25f * m);
                capDetail.Set(i, p, size, WithAlpha(col, 0.92f * appear), Billow(r3));
            }
            capDetail.End(n);
        }

        // --- Incandescent core glowing through the cap (the bright dome / glowing donut) ---
        void UpdateCapGlow()
        {
            int n = capGlow.Begin();
            float m = Morph;
            float g = Mathf.Pow(Glow(t), 0.75f) * SS(0f, 0.4f * tauG, t) * FadeOut;
            float roll = 0.55f * tauRise * (1f - Mathf.Exp(-t / tauRise));
            Color col = FireballColor(Mathf.Max(0.3f, t / tauG * 0.8f));
            for (int i = 0; i < n; i++)
            {
                Vector3 sph = capGlow.SphereDir(i) * 0.6f * Mathf.Sqrt(capGlow.r0[i]);
                Vector3 tor = TorusPoint(capGlow.r1[i] * 2f * Mathf.PI, capGlow.r2[i] * 2f * Mathf.PI - roll, 0.78f, 0.22f);
                capGlow.Set(i, Vector3.Lerp(sph, tor, m), 0.85f + 0.5f * capGlow.r3[i], WithAlpha(col, g * 0.6f), capGlow.r3[i] * 360f);
            }
            capGlow.End(n);
        }

        // --- Visible shock front: limb-brightened shell expanding at the speed of sound ---
        void UpdateShock()
        {
            int n = shock.Begin();
            float rs = ShockRadius(t);
            float maxR = 15f * R;
            float a = rs < maxR ? 0.35f * Mathf.Pow(1f - rs / maxR, 1.5f) * SS(0f, 0.08f, t) : 0f;
            for (int i = 0; i < n; i++) shock.Set(i, Vector3.zero, 2.1f * rs, new Color(0.95f, 0.95f, 1f, a), 0f);
            shock.End(n);
        }

        // --- Wilson condensation cloud: brief white shell that flashes into existence behind the shock ---
        void UpdateWilson()
        {
            int n = wilson.Begin();
            float tW = 3.5f * R / c;
            float vis = SS(0.03f, 0.25f * tW, t) * (1f - SS(0.6f * tW, 1.4f * tW, t));
            float rw = Mathf.Min(R * 1.2f + c * t * 0.9f, 3.6f * R);
            float glow = Mathf.Pow(Glow(t), 0.4f);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = wilson.SphereDir(i);
                if (groundFx) d.y = Mathf.Abs(d.y);
                Vector3 p = d * rw * (0.94f + 0.06f * wilson.r0[i]);
                Color col = Shade(CondWhite, d, glow * 0.9f, -d);
                wilson.Set(i, p, R * (0.5f + 0.3f * wilson.r1[i]), WithAlpha(col, 0.32f * vis), wilson.r2[i] * 360f);
            }
            wilson.End(n);
        }

        // --- Stacked condensation rings (collars) above the fireball, lit orange from below ---
        void UpdateRing(int k)
        {
            var layer = ringLayers[k];
            int n = layer.Begin();
            float y0 = R * (1.25f + 0.75f * k);
            float tk = y0 / c * 0.6f + 0.2f + 0.15f * k;
            // condensation rings are transient: they evaporate within seconds as the air re-warms
            float hold = 3f + 0.5f * tauG;
            float vis = SS(tk, tk + 0.6f, t) * (1f - SS(tk + hold, tk + hold + 4f + 0.5f * tauG, t));
            if (vis <= 0f)
            {
                for (int i = 0; i < n; i++) layer.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                layer.End(n);
                return;
            }
            float y = y0 + 0.4f * CapHeight(t);
            float rk = R * (1.5f + 0.32f * k) * (1f + 0.015f * t) * (1f + 0.4f * (Rise(t)));
            float glow = Mathf.Pow(Glow(t), 0.4f);
            for (int i = 0; i < n; i++)
            {
                float theta = (i + layer.r0[i] * 0.8f) / n * 2f * Mathf.PI + 0.015f * t * (k % 2 == 0 ? 1f : -1f);
                float rr = rk + (layer.r1[i] - 0.5f) * 0.22f * R;
                Vector3 p = new Vector3(Mathf.Cos(theta) * rr, y + (layer.r2[i] - 0.5f) * 0.12f * R, Mathf.Sin(theta) * rr);
                Vector3 toFire = (capPosL - p).normalized;
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.3f, -0.95f, Mathf.Sin(theta) * 0.3f).normalized; // underside faces the fireball
                Color col = Shade(CondWhite, Vector3.up, 0f, toFire);
                col = Color.Lerp(col, new Color(1f, 0.7f, 0.42f), glow * 0.85f * Mathf.Max(0.3f, Vector3.Dot(nL, toFire)));
                float size = R * (0.42f + 0.2f * layer.r3[i]) * (1f + 0.02f * t);
                layer.Set(i, p, size, WithAlpha(col, 0.3f * vis), layer.r3[i] * 360f);
            }
            layer.End(n);
        }

        // --- Dust skirt: the churning, fire-lit dust cloud piled around the base ---
        void UpdateSkirt()
        {
            int n = skirt.Begin();
            float tg = hb / c;
            float tt = Mathf.Max(0f, t - tg);
            float vis = SS(tg, tg + 0.5f, t) * FadeOut * groundFactor;
            float glow = Mathf.Pow(Glow(t), 0.5f);
            for (int i = 0; i < n; i++)
            {
                float r0 = skirt.r0[i], r1 = skirt.r1[i], r2 = skirt.r2[i], r3 = skirt.r3[i];
                float theta = r1 * 2f * Mathf.PI + 0.03f * tt * (r0 - 0.5f);
                float rad = R * (0.6f + 0.6f * r0) + 2.4f * R * (1f - Mathf.Exp(-tt / 5f)) * (0.45f + 0.55f * r2);
                float y = R * (0.12f + 0.45f * r2 * (1f - Mathf.Exp(-tt / 6f))) + 0.05f * CapHeight(t) * r2;
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.6f, 0.8f, Mathf.Sin(theta) * 0.6f).normalized;
                Vector3 fireL = capPosL - (p + ground.localPosition);
                float prox = Mathf.Clamp01(2.5f * R / Mathf.Max(1f, fireL.magnitude));
                Color col = Shade(Color.Lerp(DustDark, DustLight, 0.25f * r3), nL, glow * prox * 1.4f, fireL.normalized);
                float size = R * (0.5f + 0.35f * r3) * (1f + 0.7f * (1f - Mathf.Exp(-tt / 8f)));
                skirt.Set(i, p, size, WithAlpha(col, 0.92f * vis), Billow(r3));
            }
            skirt.End(n);
        }

        // --- Base surge: dust kicked up by the shock racing outward along the ground ---
        void UpdateSurge()
        {
            int n = surge.Begin();
            float rs = ShockRadius(t);
            float rg = Mathf.Sqrt(Mathf.Max(0f, rs * rs - hb * hb));
            float surgeMax = R * (2f + 8f * groundFactor);
            float surgeEnd = (surgeMax + hb) / c + 4f;
            float vis = (1f - SS(surgeEnd, surgeEnd + 25f, t)) * FadeOut;
            float glow = Mathf.Pow(Glow(t), 0.6f);
            for (int i = 0; i < n; i++)
            {
                float r0 = surge.r0[i], r1 = surge.r1[i], r2 = surge.r2[i], r3 = surge.r3[i];
                float theta = (i + r1) / n * 2f * Mathf.PI;
                float rad = Mathf.Min(rg - r0 * 0.25f * R, surgeMax * (0.85f + 0.15f * r1));
                float a = rad > 0.3f * R ? 0.6f * vis : 0f;
                float y = 0.05f * R + r2 * 0.2f * R * (1f + 0.05f * Mathf.Max(0f, t - hb / c));
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.5f, 0.85f, Mathf.Sin(theta) * 0.5f).normalized;
                Vector3 fireL = (capPosL - (p + ground.localPosition)).normalized;
                Color col = Shade(DustLight * 0.85f, nL, glow * 0.6f, fireL);
                float size = R * (0.38f + 0.25f * r3) * (1f + 0.6f * Mathf.Clamp01(rad / surgeMax));
                surge.Set(i, p, size, WithAlpha(col, a), Billow(r3));
            }
            surge.End(n);
        }

        // --- Stem: a smoke column trails the rising fireball while ground dust is sucked up to meet it.
        //     Particles flow continuously upward through the column, swirling, so the stem visibly moves. ---
        void UpdateStem()
        {
            int n = stem.Begin();
            float start = hb / c + 0.25f * tauG;
            if (t < start)
            {
                for (int i = 0; i < n; i++) stem.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                stem.End(n);
                return;
            }
            bool fromGround = groundFx;
            float yTop = Mathf.Max(R, hb + capPosL.y - 0.35f * capScale);
            // trail hanging below the cap, lengthening quickly; dust column climbing to meet it
            float trailLen = yTop * (fromGround ? 0.25f + 0.45f * SS(start, start + 10f, t) : 0.45f);
            float dustFront = fromGround ? yTop * SS(start, start + 0.3f * riseTime, t) : 0f;
            float flow = 1f / (0.5f * riseTime);
            float glow = Mathf.Pow(Glow(t), 0.6f);
            float vis = FadeOut * SS(start, start + 3f, t) * (fromGround ? 1f : 0.7f);
            for (int i = 0; i < n; i++)
            {
                float r0 = stem.r0[i], r1 = stem.r1[i], r2 = stem.r2[i], r3 = stem.r3[i];
                float u = Mathf.Repeat(r0 + t * flow * (0.7f + 0.6f * r3), 1f);
                float y = u * yTop;
                float theta = r1 * 2f * Mathf.PI + 0.15f * t * (1.3f - u);
                float stemR = 0.17f * capScale * (1f + 2.5f * Mathf.Pow(1f - u, 6f));
                float rad = stemR * (0.2f + 0.8f * Mathf.Sqrt(r2));
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad);

                float shownDust = SS(dustFront, dustFront - 0.06f * yTop, y);
                float shownTrail = SS(yTop - trailLen, yTop - trailLen + 0.08f * yTop, y);
                float a = Mathf.Max(shownDust, shownTrail) * SS(0f, 0.05f, u) * vis;

                Vector3 nL = new Vector3(Mathf.Cos(theta), 0.25f, Mathf.Sin(theta)).normalized;
                Color baseCol = Color.Lerp(DustDark, DustLight, 0.15f + u * 0.55f);
                Vector3 fireL = (capPosL - (p + ground.localPosition)).normalized;
                Color col = Shade(baseCol, nL, glow * (0.2f + 0.8f * u * u), fireL);
                float size = stemR * (1.1f + 0.7f * r3);
                stem.Set(i, p, size, WithAlpha(col, 0.85f * a), Billow(r3));
            }
            stem.End(n);
        }

        // --- Incandescent ejecta thrown out of a surface burst ---
        void UpdateSparks()
        {
            int n = sparks.Begin();
            float fade = 1f - SS(1.5f, vacuum ? 4f : 5f, t);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = sparks.SphereDir(i);
                if (!vacuum) d.y = Mathf.Abs(d.y) * 0.85f + 0.15f;
                float dist = R * (vacuum ? 3f : 1f) * (0.9f + 1.6f * sparks.r1[i]) * (1f - Mathf.Exp(-t / 1.2f));
                float drop = vacuum ? 0f : 0.25f * R * (t / 3f) * (t / 3f);
                Vector3 p = d.normalized * dist - Vector3.up * drop;
                float size = R * 0.06f * (0.5f + sparks.r2[i]);
                sparks.Set(i, p, size, new Color(1f, 0.62f, 0.25f, fade * SS(0f, 0.05f, t)), 0f);
            }
            sparks.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // Exo-atmospheric burst. No air means no fireball, no blast wave, no mushroom cloud: the energy leaves
        // as an X-ray flash, then a free-expanding shell of ionised bomb debris that cools from white through
        // violet to red. In a planetary magnetic field the plasma is channelled along field lines.
        // ----------------------------------------------------------------------------------------

        static Color PlasmaColor(float x)
        {
            // x = 0..1 over the shell's visible life
            if (x < 0.08f) return Color.Lerp(Color.white, new Color(0.75f, 0.85f, 1f), x / 0.08f);
            if (x < 0.35f) return Color.Lerp(new Color(0.75f, 0.85f, 1f), new Color(0.72f, 0.45f, 1f), (x - 0.08f) / 0.27f);
            if (x < 0.7f) return Color.Lerp(new Color(0.72f, 0.45f, 1f), new Color(1f, 0.3f, 0.45f), (x - 0.35f) / 0.35f);
            return Color.Lerp(new Color(1f, 0.3f, 0.45f), new Color(0.8f, 0.12f, 0.08f), Mathf.Clamp01((x - 0.7f) / 0.3f));
        }

        float ShellLife => 10f + 3f * Mathf.Pow(W, 0.25f);

        // Debris shell broken into Rayleigh-Taylor "fingers": each filament is a line of plasma knots
        // from the dimmer interior out to a bright, hotter tip.
        void UpdateDebrisShell()
        {
            int n = debrisShell.Begin();
            float rv = ShellRadius(t);
            float x = Mathf.Clamp01(t / ShellLife);
            // brightness falls as the shell thins out and cools
            float a = SS(0f, 0.03f, t) * Mathf.Pow(1f - x, 1.4f) * FadeOut;
            for (int i = 0; i < n; i++)
            {
                int f = i % ShellFilaments;
                float along = debrisShell.r0[i];
                Vector3 d = (debrisShell.SphereDir(f) + debrisShell.SphereDir(i) * 0.05f * (1f - along)).normalized;
                float wobble = 1f + 0.03f * Mathf.Sin(t * 2f + debrisShell.r2[i] * 30f);
                Vector3 p = d * rv * shellLump[i] * (0.45f + 0.55f * Mathf.Pow(along, 0.6f)) * wobble;
                Color col = PlasmaColor(Mathf.Clamp01(x * (1.25f - 0.45f * along)));
                float size = rv * (0.05f + 0.06f * debrisShell.r1[i]) + R * 0.2f;
                debrisShell.Set(i, p, size, WithAlpha(col, 0.6f * a * (0.3f + 0.7f * along)), debrisShell.r3[i] * 360f);
            }
            debrisShell.End(n);
            Color limb = PlasmaColor(x);

            // limb-brightened edge of the shell
            int m = shock.Begin();
            for (int i = 0; i < m; i++) shock.Set(i, Vector3.zero, 2.15f * rv, WithAlpha(limb, a * 0.4f), 0f);
            shock.End(m);
        }

        void UpdateFragments()
        {
            int n = sparks.Begin();
            for (int i = 0; i < n; i++)
            {
                float speed = vDebris * (0.25f + 0.75f * sparks.r1[i]);
                float life = 4f + 6f * sparks.r2[i];
                float x = Mathf.Clamp01(t / life);
                Vector3 p = sparks.SphereDir(i) * (R * 0.2f + speed * t);
                Color col = Color.Lerp(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.35f, 0.1f), Mathf.Sqrt(x));
                float size = R * (0.04f + 0.05f * sparks.r3[i]) + speed * t * 0.004f;
                sparks.Set(i, p, size, WithAlpha(col, (1f - x) * SS(0f, 0.02f, t)), 0f);
            }
            sparks.End(n);
        }

        // Faint, slowly dispersing cloud of ionised gas left behind the shell.
        void UpdateIonCloud()
        {
            int n = ionCloud.Begin();
            float rc = R * 2f + 0.06f * vDebris * t;
            float a = SS(0.5f, 2f, t) * (1f - SS(ShellLife, life - 8f, t)) * FadeOut;
            float shimmer = 0.85f + 0.15f * Mathf.Sin(t * 1.7f);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = ionCloud.SphereDir(i);
                if (hasField) d = (d + fieldL * Vector3.Dot(d, fieldL) * 0.8f).normalized;
                Vector3 p = d * rc * Mathf.Sqrt(ionCloud.r0[i]);
                var col = Color.Lerp(new Color(0.85f, 0.2f, 0.25f), new Color(0.5f, 0.25f, 0.9f), ionCloud.r1[i] * 0.6f);
                ionCloud.Set(i, p, rc * (0.4f + 0.3f * ionCloud.r2[i]), WithAlpha(col, 0.12f * a * shimmer), ionCloud.r3[i] * 360f);
            }
            ionCloud.End(n);
        }

        // Plasma escaping along the magnetic field in both directions, spiralling (gyro-motion) as it goes.
        void UpdateStreamers()
        {
            int n = streamers.Begin();
            Vector3 side = Vector3.Cross(fieldL, Mathf.Abs(fieldL.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 side2 = Vector3.Cross(fieldL, side);
            float a = SS(0.1f, 0.6f, t) * (1f - SS(ShellLife * 1.5f, life * 0.7f, t)) * FadeOut;
            for (int i = 0; i < n; i++)
            {
                float sign = streamers.r0[i] < 0.5f ? -1f : 1f;
                float frac = streamers.r1[i];
                float dist = (R + 1.6f * vDebris * t) * (0.15f + 0.85f * frac);
                float spiralR = dist * 0.06f * (0.3f + streamers.r2[i]);
                float ang = streamers.r3[i] * 2f * Mathf.PI + dist / Mathf.Max(1f, R * 3f) - t * 0.8f;
                Vector3 p = fieldL * (sign * dist) + (side * Mathf.Cos(ang) + side2 * Mathf.Sin(ang)) * spiralR;
                Color col = Color.Lerp(new Color(0.65f, 0.4f, 1f), new Color(1f, 0.25f, 0.35f), frac);
                // big, faint, overlapping glows read as a continuous stream rather than a line of dots
                float size = R * 1.2f + dist * 0.07f;
                streamers.Set(i, p, size, WithAlpha(col, 0.16f * a * (1f - 0.6f * frac)), streamers.r2[i] * 360f);
            }
            streamers.End(n);
        }

        // Artificial aurora curtain at a field-line footpoint: red oxygen glow up high, green lower down.
        void UpdateAurora(int k)
        {
            var layer = auroraLayers[k];
            int n = layer.Begin();
            Vector3 foot = auroraFootL[k];
            float tA = auroraArrival[k];
            float a = SS(tA, tA + 3f, t) * (1f - SS(life - 25f, life - 2f, t));
            Vector3 localUp = Quaternion.Inverse(transform.rotation) * (transform.TransformPoint(foot) - (Vector3)body.position).normalized;
            Vector3 east = Vector3.Cross(localUp, fieldL).normalized;
            if (east.sqrMagnitude < 1e-4f) east = Vector3.Cross(localUp, Vector3.forward).normalized;
            Vector3 north = Vector3.Cross(east, localUp).normalized;
            float width = Mathf.Clamp((float)alt * 0.15f, 10000f, 80000f);
            float height = (float)body.atmosphereDepth * 0.3f;
            const int columns = 48;
            float spacing = 2f * width / columns;
            for (int i = 0; i < n; i++)
            {
                // A thin, folded curtain of vertical rays: bright sharp green lower edge, fading red top.
                int col_i = i % columns;
                float colRand = Mathf.Repeat(Mathf.Sin(col_i * 12.9898f + k * 78.233f) * 43758.5453f, 1f);
                float u = (col_i + 0.5f + (layer.r0[i] - 0.5f) * 0.4f) / columns * 2f - 1f;
                float v = Mathf.Pow(layer.r1[i], 1.6f);
                float fold = Mathf.Sin(u * 4f + t * 0.12f + k) * width * 0.06f + Mathf.Sin(u * 11f - t * 0.3f) * width * 0.015f;
                Vector3 p = foot + east * (u * width) + north * fold + localUp * (v * height);
                Color col = v < 0.25f ? Color.Lerp(new Color(0.35f, 1f, 0.5f), new Color(0.5f, 0.95f, 0.45f), v / 0.25f)
                                      : Color.Lerp(new Color(0.5f, 0.95f, 0.45f), new Color(0.95f, 0.2f, 0.18f), Mathf.Clamp01((v - 0.25f) / 0.5f));
                float flicker = 0.45f + 0.55f * Mathf.Max(0f, Mathf.Sin(t * (0.5f + 1.5f * colRand) + col_i * 1.7f));
                float vertical = SS(0f, 0.03f, v) * Mathf.Exp(-v * 2.2f);
                float edge = Mathf.Pow(Mathf.Max(0f, 1f - u * u), 1.5f);
                float size = Mathf.Max(height * 0.08f, spacing * 0.9f);
                layer.Set(i, p, size, WithAlpha(col, 0.35f * a * flicker * vertical * edge), 0f);
            }
            layer.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // Sound, camera shake and screen overlay
        // ----------------------------------------------------------------------------------------

        void UpdateAudioAndShake(Camera cam)
        {
            if (cam == null) return;
            float closeness = Mathf.Clamp01(3f * R / Mathf.Max(camDist, R));

            if (vacuum)
            {
                // No air, no sound. The only thing you'd hear is debris hitting your own hull,
                // so play a heavily muffled thump if the shell passes the camera while it is still dense.
                if (!nearShakeDone && camDist < ShellRadius(t) && camDist < 25f * R)
                {
                    nearShakeDone = true;
                    float k = Mathf.Clamp01(6f * R / Mathf.Max(camDist, R));
                    lowPass.cutoffFrequency = 320f;
                    if (crackSrc.clip != null)
                    {
                        crackSrc.volume = Mathf.Max(0.15f, k) * 0.6f * s.volume * GameSettings.SHIP_VOLUME;
                        crackSrc.Play();
                    }
                    NukeFXManager.Instance.AddShake(0.6f * k + 0.05f, 1f + 2f * k);
                }
                return;
            }

            if (soundPlayed || camDist > ShockRadius(t)) return;
            soundPlayed = true;

            float vol = Mathf.Clamp01(Mathf.Pow(12f * R / (camDist + R), 0.7f));
            vol = Mathf.Max(vol, 0.08f) * s.volume * GameSettings.SHIP_VOLUME;
            lowPass.cutoffFrequency = Mathf.Lerp(22000f, 700f, Mathf.Clamp01(camDist / (40f * R)));
            if (crackSrc.clip != null) { crackSrc.volume = vol; crackSrc.Play(); }
            if (rumbleSrc.clip != null) { rumbleSrc.volume = vol * 0.9f; rumbleSrc.Play(); }
            if (boomSrc.clip != null) { boomSrc.volume = vol * 0.8f; boomSrc.Play(); }

            NukeFXManager.Instance.AddShake(closeness, 1.5f + 3f * closeness);
            NukeFXManager.Instance.AddShake(0.2f * Mathf.Max(closeness, 0.15f), 12f);
        }

        /// <summary>Screen flash and warm tint for the overlay, by distance, view direction and line of sight.</summary>
        public void GetScreenContribution(out float flashAmount, out float tintAmount)
        {
            flashAmount = tintAmount = 0f;
            if (t < 0f || body == null) return;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;

            Vector3 firePos = cap != null ? cap.position : transform.position;
            Vector3 toFire = firePos - cam.transform.position;
            float d = toFire.magnitude;
            float facing = Vector3.Dot(cam.transform.forward, toFire / Mathf.Max(1f, d));
            float vis = 0.3f + 0.7f * SS(-0.2f, 0.6f, facing);
            if (Physics.Raycast(cam.transform.position, toFire, d * 0.98f, 1 << 15)) vis *= 0.3f;

            // In vacuum nothing scatters or absorbs the X-ray/optical pulse, so it blinds from much further away.
            float reach = vacuum ? 60f * R : 10f * R;
            flashAmount = FlashIntensity(t) * Mathf.Clamp01(Mathf.Pow(reach / (d + 1f), vacuum ? 0.8f : 1.2f)) * vis;
            if (!vacuum) tintAmount = 0.35f * Mathf.Pow(Glow(t), 0.8f) * Mathf.Clamp01(15f * R / (d + 1f)) * vis;
        }

        // ----------------------------------------------------------------------------------------

        /// <summary>A ParticleSystem used as a pure renderer for analytically driven particles.</summary>
        internal class Layer
        {
            public readonly ParticleSystem ps;
            public readonly float[] r0, r1, r2, r3;
            readonly ParticleSystem.Particle[] buf;
            readonly int count;
            readonly Vector3[] sphereDirs;
            bool spawned;

            public Layer(Transform parent, string name, Material mat, int n, int seed, bool hierarchyScale)
            {
                count = n;
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.playOnAwake = false;
                main.loop = true;
                main.duration = 10f;
                main.maxParticles = n;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = hierarchyScale ? ParticleSystemScalingMode.Hierarchy : ParticleSystemScalingMode.Local;
                main.startLifetime = 100000f;
                main.startSpeed = 0f;
                main.gravityModifier = 0f;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                var em = ps.emission;
                em.enabled = false;
                var sh = ps.shape;
                sh.enabled = false;
                if (mat == NukeAssets.Smoke)
                {
                    // 2x2 billow atlas: each particle keeps one random tile for its whole life
                    var tsa = ps.textureSheetAnimation;
                    tsa.enabled = true;
                    tsa.mode = ParticleSystemAnimationMode.Grid;
                    tsa.numTilesX = 2;
                    tsa.numTilesY = 2;
                    tsa.animation = ParticleSystemAnimationType.WholeSheet;
                    tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);
                    tsa.cycleCount = 1;
                }

                var rend = go.GetComponent<ParticleSystemRenderer>();
                rend.sharedMaterial = mat;
                rend.renderMode = ParticleSystemRenderMode.Billboard;
                rend.sortMode = ParticleSystemSortMode.Distance;
                rend.minParticleSize = 0f;
                rend.maxParticleSize = 100f;
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rend.receiveShadows = false;

                buf = new ParticleSystem.Particle[n];
                r0 = new float[n]; r1 = new float[n]; r2 = new float[n]; r3 = new float[n];
                sphereDirs = new Vector3[n];
                var rng = new System.Random(seed);
                for (int i = 0; i < n; i++)
                {
                    r0[i] = (float)rng.NextDouble();
                    r1[i] = (float)rng.NextDouble();
                    r2[i] = (float)rng.NextDouble();
                    r3[i] = (float)rng.NextDouble();
                    float z = (float)rng.NextDouble() * 2f - 1f;
                    float a = (float)rng.NextDouble() * 2f * Mathf.PI;
                    float rr = Mathf.Sqrt(1f - z * z);
                    sphereDirs[i] = new Vector3(rr * Mathf.Cos(a), z, rr * Mathf.Sin(a));
                }
            }

            public Vector3 SphereDir(int i) => sphereDirs[i];

            public int Begin()
            {
                if (!spawned)
                {
                    spawned = true;
                    ps.Play();
                    var ep = new ParticleSystem.EmitParams
                    {
                        position = Vector3.zero,
                        velocity = Vector3.zero,
                        startSize = 0.001f,
                        startLifetime = 100000f,
                        startColor = new Color32(0, 0, 0, 0),
                    };
                    ps.Emit(ep, count);
                }
                return ps.GetParticles(buf);
            }

            public void Set(int i, Vector3 pos, float size, Color col, float rotDeg)
            {
                buf[i].position = pos;
                buf[i].velocity = Vector3.zero;
                buf[i].startSize = Mathf.Max(0f, size);
                buf[i].startColor = col;
                buf[i].rotation = rotDeg;
                buf[i].remainingLifetime = 100000f;
            }

            public void End(int n) { ps.SetParticles(buf, n); }
        }
    }
}
