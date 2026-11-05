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
    ///
    /// The cloud's character (colour, cap aspect, stem, collars, skirt) comes from a NukeCloudProfile, worked
    /// out from the environment or a ModuleNukeCloudProfile on the warhead. Three looks are blended on air
    /// density: the mushroom cloud (dense air), the high-altitude red fireball (thin air) and the vacuum
    /// debris shell. Bursts at or under the sea surface add a Crossroads Baker style water column.
    ///
    /// The cloud drifts with a NukeWind (stem leaning downwind). Ground and low bursts hand over to a NukeFallout
    /// plume and NukeGroundEffects (scorch, afterglow, fires), which outlive the cloud; megaton clouds get
    /// lightning (NukeCinematicLightning.cs). Everything is sized by the quality preset and the burst LOD.
    /// </summary>
    internal partial class NukeCinematic : MonoBehaviour
    {
        static readonly Color Ember = new Color(1f, 0.52f, 0.16f);
        static readonly Color DustDark = new Color(0.46f, 0.37f, 0.30f);
        static readonly Color DustLight = new Color(0.80f, 0.77f, 0.74f);
        static readonly Color CondWhite = new Color(0.96f, 0.96f, 0.98f);
        static readonly Color Nox = new Color(0.62f, 0.42f, 0.32f);     // nitrogen oxides tint the young cloud red-brown
        static readonly Color RimCol = new Color(1f, 0.93f, 0.8f);

        // --- inputs ---
        CelestialBody body;
        double lat, lon, alt;
        float W;
        float delay;
        double spawnTime;   // NukeFXManager.FxTime at spawn (double: the clock runs for days of time warp)
        NukeSettings s;
        NukeCloudProfile prof;
        float lod = 1f;     // particle-count scale when many detonations are active
        float budget = 1f;  // extra scale that keeps the detonation under maxParticles
        bool planning;      // Build's first pass only counts particles
        int plannedBase;
        bool overOcean;
        NukeWind wind;
        Vector3 capDrift;   // this frame's downwind drift of the cap
        float windLag;      // s for the rising cloud to pick up the wind's speed
        float stemPhase;    // random phase of the stem's bulges and wiggle

        // --- aftermath handed to NukeFallout / NukeGroundEffects ---
        float falloutK;     // 0 clean airburst .. 1 dirty surface burst
        bool groundEffects, scorchFx, firesFx, debrisFx;
        float rScorch, rOuter;

        // --- derived physics ---
        float R;            // fireball radius (m)
        float tauG;         // fireball evolution e-fold time (s): drives the sphere->torus morph
        float tauGlow;      // incandescence e-fold time (s): tauG * profile glowScale
        float riseDelta;    // cap rise above burst point (m)
        float capRadius;    // final cap radius (m)
        float tauRise;      // rise e-fold time (s)
        float riseTime;     // yield-scaled cloudRiseTime (s)
        float yieldTimeScale;
        float t0;           // time the fireball starts to rise
        float hb;           // burst height above ground or sea (m)
        float groundY;      // ground (or sea surface) height relative to the burst point (m)
        float c;            // speed of sound
        float life, lifeAir, lifeVac, lifeHi, lifeWater;
        float groundFactor; // 1 = surface burst, 0 = no ground interaction
        bool groundFx, stemFx, ringFx, sparksFx;

        // --- regime weights: air (mushroom), high altitude (red fireball), vacuum (debris shell); they sum to 1 ---
        float airW, hiW, vacW;
        bool airFx, hiFx, vacFx;

        // --- water bursts ---
        bool underwater, waterSurface, waterFx;
        float depth, depthF, colH, colR, tauCol, tCollapse, tFall;
        float flashK = 1f;  // flash/light attenuation (water swallows most of an underwater flash)
        float tMax;         // s to the thermal pulse's second maximum (0.0417 W^0.44: 0.16 s at 20 kt, 4.9 s at 50 Mt)

        // --- cloud shape from the profile ---
        float capYScale;    // vertical stretch of the cap: profile aspect / the neutral 0.5
        float riseLow;      // low-yield rise factor
        Color colEarly, colLate, colDark, stemLow, stemHigh, skirtDark, skirtLight, surgeCol;

        // --- high altitude ---
        float Rh;           // high-altitude fireball radius (m): much bigger in thin air
        float hiLife;

        // --- scene ---
        Transform cap, ground, rings;
        Light lightFx;
        readonly List<Layer> layers = new List<Layer>();

        // --- atmosphere between the camera and the cloud, and the weather overhead ---
        float haze;                 // 0..1 aerial perspective: how far colours are washed toward hazeCol
        Color hazeCol = new Color(0.6f, 0.68f, 0.78f);
        float sunVis = 1f;          // direct sunlight reaching the part being shaded (cloud decks above it)
        float sunVisCap = 1f, sunVisLow = 1f, sunVisCam = 1f;
        float nextWeatherSample;
        readonly List<NukeWeather.Deck> decks = new List<NukeWeather.Deck>();

        // raymarched cloud and fireball (replaces the sprite cap, fireball, stem and skirt when available)
        NukeVolumetric volume;
        NukeVolumeState vs;
        // the refracting shock shell
        GameObject shockDome;
        Material shockMat;
        float shapeBulb;    // 0 = wide rolling cap .. 1 = tight, tall, bulbous head (picked per detonation)
        Layer fireCore, capSmoke, capDetail, capUpper, capGlow, flash, shock, wilson, skirt, surge, stem, collars, sparks;
        Layer[] ringLayers;
        Layer hiFire, airglow;
        Layer waterColumn, waterSpray, waterSurge, ringWave;

        // --- vacuum burst ---
        Layer debrisShell, shellLimb, fragments, streamers, ionCloud;
        Layer[] auroraLayers;
        Vector3[] auroraFootL;      // field-line footpoints in the atmosphere, root-local
        float[] auroraArrival;
        float[] shellLump;          // per-particle shell radius factor (Rayleigh-Taylor lumpiness + field elongation)
        Vector3 fieldL;             // local magnetic field direction at the burst (dipole approximation)
        float vDebris;
        bool hasField;
        AudioSource crackSrc, rumbleSrc, boomSrc, roarSrc;
        AudioLowPassFilter lowPass;
        bool soundPlayed, nearShakeDone;

        // per-frame cached
        float t;
        Vector3 sunL;
        float daylight;
        float rimK;         // backlit rim strength this frame
        Vector3 capPosL;
        float capScale;
        float camDist;
        Vector3 camDirL;
        float sunRotDeg;    // billboard rotation that turns the lit top of the billow texture towards the sun
        float pkT;          // pancake spreading progress (0..1)
        float capYS, capXZS, domeYS, domeXZS;

        const int ShellFilaments = 64;

        NukeRecord record;      // this detonation in the save (null if persistence is off)
        bool restored;          // rebuilt after a scene change: visuals only, no blast, crater or sound
        bool replay;            // a replay (NukeReplay.cs): restored's visuals-only path, but from t = 0 and with sound

        /// <summary>Rebuilds a recorded detonation at the age it has reached (after a scene change or a reload).</summary>
        public static void Restore(CelestialBody body, NukeRecord r)
        {
            NukeAssets.Init();
            var go = new GameObject("BDNukes_Detonation");
            try
            {
                var cin = go.AddComponent<NukeCinematic>();
                cin.record = r;
                cin.restored = true;
                r.live = cin;
                Vector3 pos = body.GetWorldSurfacePosition(r.lat, r.lon, r.alt);
                cin.Setup(body, pos, Mathf.Max(r.W, 0.001f), 0f, r.profile, NukeFXManager.LODScale());
            }
            catch
            {
                Destroy(go);
                throw;
            }
        }

        public static void Spawn(Vector3 worldPos, float yield, float delay, NukeCloudProfile profile = null)
        {
            var body = FlightGlobals.currentMainBody;
            if (body == null) return;
            NukeAssets.Init();
            float lod = NukeFXManager.LODScale();
            var go = new GameObject("BDNukes_Detonation");
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                go.AddComponent<NukeCinematic>().Setup(body, worldPos, Mathf.Max(yield, 0.001f), Mathf.Max(0f, delay), profile, lod);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > 25d)
                    Debug.Log("[BDNukes]: Detonation setup took " + sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms (" + yield + " kt)");
            }
            catch
            {
                // a half-built detonation would throw from LateUpdate every frame
                Destroy(go);
                throw;
            }
        }

        void Setup(CelestialBody b, Vector3 pos, float yield, float detonationDelay, NukeCloudProfile request, float lodScale)
        {
            s = NukeSettings.Current;
            body = b;
            W = yield;
            delay = detonationDelay;
            spawnTime = NukeFXManager.FxTime;
            lod = lodScale;

            lat = body.GetLatitude(pos);
            lon = body.GetLongitude(pos);
            alt = body.GetAltitude(pos);
            if (restored)
            {
                lat = record.lat; lon = record.lon; alt = record.alt;
                if (!replay)
                {
                    // pick up where it was: Time.time-based ages continue from the universal time since detonation
                    double age = NukePersistence.Now - record.ut;
                    if (age >= 0d) spawnTime = NukeFXManager.FxTime - age;
                    else delay = (float)-age;
                    soundPlayed = nearShakeDone = true;
                }
            }
            double terrain = body.pqsController != null ? body.TerrainAltitude(lat, lon) : 0d;
            overOcean = body.ocean && terrain < 0d;
            double groundAlt = overOcean ? 0d : terrain;
            hb = Mathf.Max(0f, (float)(alt - groundAlt));
            groundY = (float)(groundAlt - alt);

            float rho = 0f;
            if (body.atmosphere && alt < body.atmosphereDepth)
                rho = (float)FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(pos), FlightGlobals.getExternalTemperature(pos));
            float densRatio = Mathf.Clamp(rho / 1.225f, 0.05f, 1f);

            R = 70f * Mathf.Pow(W, 0.4f) * s.sizeScale;
            tMax = Mathf.Clamp(0.0417f * Mathf.Pow(W, 0.44f), 0.03f, 6f);
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
            lifeAir = s.cloudLifetime * yieldTimeScale;
            vDebris = s.vacuumDebrisSpeed;
            groundFactor = Mathf.Clamp01(1f - hb / (3f * R));

            // --- cloud character ---
            float tropo = (float)(body.atmosphere ? body.atmosphereDepth * s.tropopauseFraction : 0d);
            var env = new NukeCloudEnv
            {
                body = body, lat = lat, lon = lon, alt = alt, W = W, R = R, hb = hb,
                groundFactor = groundFactor, overOcean = overOcean,
                cloudTopASL = (float)alt + riseDelta + 0.5f * capRadius,
                tropopauseASL = tropo,
            };
            prof = NukeCloudProfile.ResolveOrAuto(request, env);
            ApplyProfile(tropo);

            // --- regime: blend the air, high-altitude and vacuum looks on log density ---
            float hiTop = s.highAltitudeDensity * (prof.hiAltBias > 0f ? 3f : 1f);
            float lr = Mathf.Log10(Mathf.Max(rho, 1e-9f));
            float lHi = Mathf.Log10(hiTop), lLo = Mathf.Log10(Mathf.Min(s.vacuumLookDensity, hiTop));
            float bw = s.regimeBlend;
            airW = SS(lHi - bw, lHi + bw, lr);
            vacW = (1f - SS(lLo - bw, lLo + bw, lr)) * (1f - airW);
            hiW = Mathf.Clamp01(1f - airW - vacW);
            airFx = airW > 0.02f;
            hiFx = hiW > 0.02f;
            vacFx = vacW > 0.02f;

            // --- water: underwater is below sea level over ocean, a surface burst is within ~R of it ---
            underwater = s.waterBursts && overOcean && alt < 0d;
            waterSurface = s.waterBursts && overOcean && !underwater && alt < R;
            waterFx = underwater || waterSurface;
            depth = underwater ? (float)-alt : 0f;
            if (underwater)
            {
                // no fireball breaks the surface: the water column, spray, surge and waves take over
                airFx = false;
                airW = 1f; hiW = vacW = 0f; hiFx = vacFx = false;
                flashK = Mathf.Lerp(0.5f, 0.06f, Mathf.Clamp01(depth / (4f * R)));
                SetupWater();
            }
            else if (waterSurface) lifeWater = 150f * yieldTimeScale; // ring waves only; the cloud itself is the air look

            groundFx = airFx && hb < 3f * R;
            stemFx = airFx && hb < 6f * R;
            ringFx = airFx && s.condensationRings && densRatio > 0.2f && prof.lowYield < 0.01f;
            sparksFx = airFx && hb < 1.5f * R;
            SetupLightning();
            SetupEarlyFx();

            // high-altitude fireball: thin air lets it grow far bigger, and it floats up fast with no mushroom
            Rh = R * Mathf.Clamp(0.8f * Mathf.Pow(1.225f / Mathf.Max(rho, 1e-4f), 1f / 3f), 1f, 8f);
            hiLife = (40f + 20f * Mathf.Pow(W, 0.15f)) * prof.glowScale;
            lifeHi = hiLife + 15f;

            UpdateAnchor();
            lifeVac = 25f;
            if (vacFx || hiFx) SetupField(pos);
            life = Mathf.Max(Mathf.Max(airFx ? lifeAir : 0f, vacFx ? lifeVac : 0f), Mathf.Max(hiFx ? Mathf.Max(lifeHi, auroraFootL != null ? lifeVac : 0f) : 0f, waterFx ? lifeWater : 0f));

            int windSeed = restored ? record.windSeed : Random.Range(0, 100000);
            // every mushroom is a little different: somewhere between a wide rolling cap and a tight bulbous head,
            // small yields leaning bulbous
            shapeBulb = restored ? record.bulb : Mathf.Clamp01(Mathf.Pow(Random.value, 0.9f) * 0.85f + 0.35f * prof.lowYield + 0.15f * (1f - SS(1f, 3f, Mathf.Log10(Mathf.Max(W, 0.01f)))));
            // windShear is the wind at a big cloud's cap (~6 km+); a small cloud tops out in the slower air lower down
            float capTopAGL = hb + riseDelta;
            float shear = Mathf.Lerp(1f, s.windShear, SS(300f, 6000f, capTopAGL));
            float windHeading = restored ? record.windHeading : NukeFXManager.Instance.WindHeading;
            wind = new NukeWind(windHeading, s.windSpeed, shear, windSeed,
                Mathf.Max(life, NukeFallout.Lifetime(s, W) + 60f));
            // the rising fireball's own updraft holds it against the wind at first: it only picks up the wind's
            // speed over about half its rise
            windLag = 0.45f * riseTime;
            PlanAftermath();
            if (s.craftScorch && !underwater && !restored) NukeScorch.Flash(pos, W, R, delay); // thermal flash chars nearby craft
            Build();
            if (!restored) PlanCrater();
            // the blast blows a hole in EVE's cloud decks within reach of the shock
            if (airFx && body.atmosphere && !restored)
                NukeCloudClearing.Blast(body, pos, alt, 4f * Mathf.Max(3f * R, 0.45f * 650f * Mathf.Pow(W, 1f / 3f) * s.sizeScale),
                    c, 300f * yieldTimeScale);
            SpawnAftermath();
            if (!restored) NukeWorldEffects.Detonation(body, lat, lon, alt + groundY, W, R, hb, overOcean, airFx && !underwater, falloutK, windHeading, wind, spawnTime, delay);
            if (debrisFx) NukeDebrisTrails.Spawn(body, lat, lon, alt + groundY, W, R, spawnTime + delay, s.particleDensity * lod * budget);
            NukeFXManager.Instance.Register(this);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            if (!restored)
            {
                var rec = new NukeRecord
                {
                    body = body.bodyName, lat = lat, lon = lon, alt = alt,
                    ut = NukePersistence.Now + delay, endUT = NukePersistence.Now + delay + life,
                    W = W, bulb = shapeBulb, windHeading = windHeading, windSeed = windSeed, profile = request, live = this,
                };
                NukePersistence.RememberForReplay(rec);    // kept for "Replay last detonation", saved or not
                if (s.persistentClouds) record = NukePersistence.Add(rec);
            }
            if (!restored || replay) NukeCinematicCamera.OnDetonation(this);
        }

        /// <summary>
        /// Decides what outlives the cloud: fallout (surface and low bursts in air or water, more for dirty ones,
        /// almost none for high airbursts) and the ground effects (scorch where the fireball touches the ground,
        /// fires within the thermal radius on land).
        /// </summary>
        void PlanAftermath()
        {
            bool atmo = (airFx || underwater) && body.atmosphere;
            float dirt = 1f - SS(0.6f * R, 3.5f * R, hb);
            falloutK = atmo ? dirt * (0.55f + 0.45f * prof.dustiness) + (stemFx ? 0.03f : 0f) : 0f;
            if (falloutK < 0.02f || !(s.falloutCloud || s.falloutRadiation)) falloutK = 0f;

            rScorch = R * Mathf.Lerp(1.8f, 1.1f, Mathf.Clamp01(hb / (1.2f * R)));
            rOuter = Mathf.Max(3f * R, 0.45f * 650f * Mathf.Pow(W, 1f / 3f) * s.sizeScale);
            scorchFx = s.scorchMarks && !overOcean && hb < 1.2f * R;
            firesFx = s.lingeringFires && !overOcean && airFx && body.atmosphereContainsOxygen && hb < 8f * R;
            if (replay)
            {
                // a replay leaves no mark: no scorch or fires, and only the fallout plume's visuals (no dose)
                scorchFx = firesFx = false;
                if (!s.falloutCloud) falloutK = 0f;
            }
            groundEffects = scorchFx || firesFx;
            debrisFx = (!restored || replay) && NukeDebrisTrails.Wanted(s, body, airFx, overOcean, hb, R);
        }

        /// <summary>
        /// Surface and near-surface bursts on land dig a crater into the terrain. It's added before the scorch decal
        /// samples the ground, so the decal drapes into it; the visible terrain is rebuilt just after the flash.
        /// </summary>
        void PlanCrater()
        {
            if (overOcean || underwater) return;
            var crater = NukeCraters.Plan(body, lat, lon, W, R, hb);
            if (crater == null) return;
            try { NukeCraters.Add(crater, delay + 0.15f); }
            catch (System.Exception e) { Debug.LogError("[BDNukes]: Couldn't add a crater: " + e); }
        }

        void SpawnAftermath()
        {
            if (falloutK <= 0f && !groundEffects) return;
            double groundAlt = alt + groundY;
            var p = new NukeAftermathParams
            {
                body = body, lat = lat, lon = lon, groundAlt = groundAlt, W = W, R = R, hb = hb,
                yieldTimeScale = yieldTimeScale, spawnTime = spawnTime, delay = delay, wind = wind,
                particleScale = s.particleDensity * lod * budget,
                falloutK = falloutK, water = prof.water || underwater,
                plumeCol = underwater ? CondWhite * 0.85f : Color.Lerp(colLate, colDark, 0.35f),
                plumeDark = underwater ? new Color(0.62f, 0.64f, 0.68f) : colDark,
                dustTint = prof.dustTint,
                scorch = scorchFx, fires = firesFx, afterglow = scorchFx && s.groundAfterglow,
                rScorch = rScorch, rOuter = rOuter,
                visualOnly = replay,
            };
            if (underwater)
            {
                // Baker: the contaminated spray and base surge, not a mushroom cap
                p.t0 = 0f; p.tauRise = tauCol; p.riseTime = tCollapse + tFall;
                p.srcTop = colH * 0.5f; p.srcR0 = colR; p.srcR1 = colR * 3f;
            }
            else
            {
                p.t0 = t0; p.tauRise = tauRise; p.riseTime = riseTime;
                p.srcTop = hb + riseDelta; p.srcR0 = R; p.srcR1 = capRadius;
            }
            if (falloutK > 0f) NukeFallout.Spawn(p);
            if (groundEffects) NukeGroundEffects.Spawn(p);
        }

        /// <summary>Turns the resolved profile into shape factors, timings and colours.</summary>
        void ApplyProfile(float tropo)
        {
            // Megaton fireballs stay incandescent longer, but nowhere near tauG's ~60 s at 50 Mt: compress the
            // glow (and the profile's glow boost) for the biggest yields, ~25-35 s of strong glow at 50 Mt.
            float mega = SS(1000f, 50000f, W);
            tauGlow = tauG * Mathf.Lerp(prof.glowScale, Mathf.Sqrt(prof.glowScale), mega) * Mathf.Lerp(1f, 0.45f, mega);
            capRadius *= prof.capSpread;
            capYScale = prof.capAspect / 0.5f;

            // low yield: squat puff on a short stem
            riseLow = Mathf.Lerp(1f, 0.45f, prof.lowYield);
            riseDelta = Mathf.Max(1.5f * R, riseDelta * riseLow);

            // pancake: the cap stalls at the tropopause instead of carrying on up
            float tropoAbove = tropo - (float)alt;
            if (prof.pancake > 0f && tropoAbove > 2.5f * R)
                riseDelta = Mathf.Lerp(riseDelta, Mathf.Min(riseDelta, tropoAbove), prof.pancake);

            float d = prof.dustiness, hum = prof.humidity;
            Color tint = prof.dustTint;
            float white = hum * (1f - d);
            // young cloud: fireball debris and NOx, tinted by whatever dust it sucked up
            colEarly = Color.Lerp(Nox, tint * 1.05f, d);
            colEarly = Color.Lerp(colEarly, CondWhite * 0.92f, 0.55f * white);
            // mature cloud: condensation whitens it unless it is full of dust
            Color dustLate = Color.Lerp(DustLight, Clamp(tint * 1.25f), d);
            colLate = Color.Lerp(CondWhite, dustLate, Mathf.Clamp01(0.35f + 0.65f * d - 0.45f * hum));
            colDark = Color.Lerp(Color.Lerp(DustDark, tint * 0.7f, d), new Color(0.66f, 0.67f, 0.71f), white);
            stemLow = Color.Lerp(Color.Lerp(DustDark, tint * 0.7f, d), CondWhite * 0.86f, white);
            stemHigh = Color.Lerp(Color.Lerp(DustLight, Clamp(tint * 1.15f), d), CondWhite, 0.6f * white);
            if (prof.water)
            {
                skirtDark = new Color(0.78f, 0.80f, 0.84f);
                skirtLight = CondWhite;
                surgeCol = CondWhite * 0.95f;
            }
            else
            {
                skirtDark = Color.Lerp(DustDark, tint * 0.8f, d);
                skirtLight = Color.Lerp(DustLight, Clamp(tint * 1.2f), d);
                surgeCol = Color.Lerp(DustLight * 0.85f, tint, 0.6f * d);
            }
        }

        static Color Clamp(Color c) => new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);

        /// <summary>Crossroads Baker scaling: column ~7.5 R tall, ~2.4 R across, collapsing into a base surge.</summary>
        void SetupWater()
        {
            depthF = Mathf.Clamp(1f - depth / (12f * R), 0.15f, 1f);
            colR = R * (0.9f + 0.4f * depthF);
            colH = R * 7.5f * depthF;
            tauCol = 0.9f + 0.12f * Mathf.Sqrt(colH);
            tCollapse = 3.2f * tauCol;
            tFall = Mathf.Sqrt(2f * colH / 4.9f);
            lifeWater = Mathf.Max(120f, tCollapse + tFall + 100f) * yieldTimeScale;
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            if (volume != null) volume.Destroy();
            if (shockMat != null) Destroy(shockMat);
            DestroyEarlyFx();
            var manager = NukeFXManager.Existing;
            if (manager != null) manager.Unregister(this);
        }

        int N(int baseCount) => Mathf.Max(1, Mathf.RoundToInt(baseCount * s.particleDensity * lod * budget));

        /// <summary>
        /// Adds a layer of baseCount (scaled by density, LOD and budget) particles, or a fixed count if !scaled.
        /// In the planning pass it only adds up the counts and returns null.
        /// </summary>
        Layer Make(Transform parent, string name, Material mat, int baseCount, int seed, bool hierarchyScale, bool scaled = true)
        {
            if (planning)
            {
                if (scaled) plannedBase += baseCount;
                return null;
            }
            return Add(new Layer(parent, name, mat, scaled ? N(baseCount) : baseCount, seed, hierarchyScale));
        }

        void Build()
        {
            cap = Child("Cap", transform);
            ground = Child("Ground", transform);
            ground.localPosition = new Vector3(0f, groundY, 0f);
            rings = Child("Rings", transform);
            int seed = Random.Range(0, 100000);
            stemPhase = (seed % 1000) * 0.00628f;

            // Pass 1 counts what the layers would cost (with the fallout and fires this burst will spawn) and
            // works out the budget that keeps the whole detonation under maxParticles; pass 2 builds them.
            planning = true;
            plannedBase = (falloutK > 0f ? NukeFallout.BaseParticles(s) : 0) + (groundEffects ? NukeGroundEffects.BaseParticles(firesFx) : 0)
                + (debrisFx ? NukeDebrisTrails.BaseParticles(W) : 0);
            BuildLayers(seed);
            float planned = plannedBase * s.particleDensity * lod;
            budget = planned > s.maxParticles ? s.maxParticles / planned : 1f;
            planning = false;
            BuildLayers(seed);
            BuildLightning(seed);
            BuildEarlyFx(seed);
            if (airFx && body.atmosphere && s.shockDistortion > 0f && NukeVolumetric.Available)
            {
                shockMat = NukeVolumetric.MakeShockMaterial();
                if (shockMat != null)
                {
                    // a transform only: the shell is drawn by the flight cameras' command buffer
                    shockDome = new GameObject("ShockDistortion");
                    shockDome.transform.SetParent(transform, false);
                }
            }
            if (airFx && !underwater && s.volumetricClouds && NukeVolumetric.Available)
            {
                volume = new NukeVolumetric(transform);
                foreach (var l in new[] { capSmoke, capDetail, capUpper, fireCore, capGlow, skirt, stem, surge, collars, wilson, machRing })
                    if (l != null) l.SetVisible(false);
                if (ringLayers != null) foreach (var l in ringLayers) if (l != null) l.SetVisible(false);
            }

            var lightGo = new GameObject("NukeLight");
            lightGo.transform.SetParent(transform, false);
            lightFx = lightGo.AddComponent<Light>();
            lightFx.type = LightType.Point;
            lightFx.color = new Color(1f, 0.85f, 0.65f);
            lightFx.range = Mathf.Clamp(80f * R, 3000f, 80000f);
            lightFx.intensity = 0f;
            lightFx.shadows = s.lightShadows ? LightShadows.Soft : LightShadows.None;

            var audioGo = new GameObject("NukeAudio");
            audioGo.transform.SetParent(transform, false);
            crackSrc = MakeSource(audioGo, NukeAssets.Crack);
            rumbleSrc = MakeSource(audioGo, NukeAssets.Rumble);
            boomSrc = MakeSource(audioGo, NukeAssets.StockBoom);
            roarSrc = MakeSource(audioGo, NukeAssets.Roar);
            // bigger bursts sound deeper and longer: a lower-pitched, stretched crack and rumble
            float pitch = Mathf.Clamp(1.15f - 0.1f * Mathf.Log10(W), 0.65f, 1.35f);
            crackSrc.pitch = rumbleSrc.pitch = roarSrc.pitch = pitch;
            lowPass = audioGo.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 22000f;
        }

        void BuildLayers(int seed)
        {
            flash = Make(transform, "Flash", NukeAssets.Glow, 3, seed + 1, false, false);

            if (vacFx)
            {
                debrisShell = Make(transform, "DebrisShell", NukeAssets.Glow, 320, seed + 2, false);
                shellLimb = Make(transform, "ShellLimb", NukeAssets.Ring, 1, seed + 3, false, false);
                fragments = Make(transform, "Fragments", NukeAssets.Glow, 110, seed + 4, false);
                ionCloud = Make(transform, "IonCloud", NukeAssets.Glow, 70, seed + 5, false);
            }
            if (debrisShell != null)
            {
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
            if ((vacFx || hiFx) && hasField && s.magneticStreamers)
                streamers = Make(transform, "FieldStreamers", NukeAssets.Glow, vacFx ? 420 : 300, seed + 6, false);
            if (auroraFootL != null)
            {
                auroraLayers = new Layer[auroraFootL.Length];
                for (int k = 0; k < auroraFootL.Length; k++)
                    auroraLayers[k] = Make(transform, "Aurora" + k, NukeAssets.Glow, vacFx ? 560 : 380, seed + 40 + k, false);
            }

            if (hiFx)
            {
                hiFire = Make(transform, "HighAltitudeFireball", NukeAssets.Glow, 240, seed + 7, false);
                airglow = Make(transform, "AirglowRing", NukeAssets.Glow, 200, seed + 8, false);
            }

            if (airFx)
            {
                bool stacked = prof.stack > 0.05f;
                // without the fine detail layer the main billows take up more of the cap
                capSmoke = Make(cap, "CapSmoke", NukeAssets.Smoke, s.capDetail ? 320 : 480, seed + 10, true);
                if (s.capDetail) capDetail = Make(cap, "CapDetail", NukeAssets.Smoke, stacked ? 700 : 900, seed + 15, true);
                if (stacked) capUpper = Make(cap, "CapUpper", NukeAssets.Smoke, 300, seed + 16, true);
                capGlow = Make(cap, "CapGlow", NukeAssets.Glow, 110, seed + 11, true);
                fireCore = Make(cap, "Fireball", NukeAssets.Glow, 45, seed + 12, true);
                if (ringFx)
                {
                    ringLayers = new Layer[3];
                    for (int k = 0; k < 3; k++)
                        ringLayers[k] = Make(rings, "CondensationRing" + k, NukeAssets.Mist, 220, seed + 20 + k, false);
                }
                if (groundFx)
                {
                    skirt = Make(ground, "DustSkirt", NukeAssets.Smoke, 320, seed + 30, false);
                    surge = Make(ground, "BaseSurge", NukeAssets.Smoke, 280, seed + 31, false);
                }
                if (stemFx)
                {
                    stem = Make(ground, "Stem", NukeAssets.Smoke, 420, seed + 32, false);
                    if (prof.stemCollars > 0)
                        collars = Make(ground, "StemCollars", NukeAssets.Mist, prof.stemCollars * 64, seed + 34, false);
                }
                if (sparksFx) sparks = Make(transform, "Ejecta", NukeAssets.Glow, 70, seed + 33, false);
                if (machFx) machRing = Make(ground, "MachStemDust", NukeAssets.Smoke, 160, seed + 35, false);
            }
            if (airFx || underwater)
            {
                shock = Make(transform, "ShockFront", NukeAssets.Ring, 1, seed + 13, false, false);
                // over water the Wilson dome is part of the column's look, so it stays on every preset
                if (s.wilsonCloud || underwater) wilson = Make(transform, "WilsonCloud", NukeAssets.Mist, 220, seed + 14, false);
            }
            if (underwater)
            {
                waterColumn = Make(ground, "WaterColumn", NukeAssets.Smoke, 520, seed + 50, false);
                waterSpray = Make(ground, "WaterSpray", NukeAssets.Smoke, 300, seed + 51, false);
                waterSurge = Make(ground, "WaterBaseSurge", NukeAssets.Smoke, 380, seed + 52, false);
            }
            if (waterFx) ringWave = Make(ground, "RingWaves", NukeAssets.Mist, 240, seed + 53, false);
            if (lightningFx) boltGlow = Make(transform, "LightningGlow", NukeAssets.Glow, BoltPool, seed + 60, false, false);
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
        float Glow(float time) => Mathf.Exp(-Mathf.Max(0f, time) / tauGlow);
        float ShockRadius(float time) => R + c * Mathf.Max(0f, time);
        float FlashIntensity(float time)
        {
            if (time < 0f) return 0f;
            // the double flash: a millisecond first pulse (a blink of a frame or two), cut off as the shock front
            // turns opaque, then the long thermal pulse peaking at tMax; ~80% of its energy is out by ~10 tMax
            float x = time / tMax;
            float x2 = x * x;
            float air = Mathf.Exp(-time / 0.025f) + 0.9f * (2f * x2 / (1f + x2 * x2));
            float vac = Mathf.Exp(-time / 0.05f) + 0.15f * Mathf.Exp(-time / 0.6f); // X-ray pulse: no air to trap it
            return flashK * Mathf.Lerp(air, vac, vacW);
        }
        // each look fades on its own clock and is weighted by how much of it the air density allows
        float FadeOut => airW * (1f - SS(lifeAir - 45f, lifeAir, t));
        float FadeVac => vacW * (1f - SS(lifeVac - 8f, lifeVac, t));
        float FadeHi => hiW * (1f - SS(lifeHi - 10f, lifeHi, t));
        float FadeWater => 1f - SS(lifeWater - 30f, lifeWater, t);
        float ShellRadius(float time) => R * 0.3f + vDebris * Mathf.Max(0f, time);
        float Morph => SS(0.15f * tauG, 2f * tauG + 4f, t);

        // high altitude: buoyant rise without a stem, slow expansion, long red glow
        float HiRise(float time) => Rh * 4f * (1f - Mathf.Exp(-Mathf.Max(0f, time) / 25f));
        float HiGrow(float time) => 1f + 0.8f * (1f - Mathf.Exp(-Mathf.Max(0f, time) / 15f));
        float HiGlow(float time) => Mathf.Exp(-Mathf.Max(0f, time) / (0.45f * hiLife));

        // water column
        float ColTop(float time) => colH * (1f - Mathf.Exp(-Mathf.Max(0f, time) / tauCol));
        float Collapse(float time) => SS(tCollapse, tCollapse + tFall, time);

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
            UpdateFxRateAudio();
            t = (float)(NukeFXManager.FxTime - spawnTime - delay);
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

            // the whole cloud drifts with the wind aloft; the stem leans from the slower air near the ground
            // first-order lag behind the wind: displacement U * (t - tau * (1 - e^-t/tau))
            capDrift = airFx ? wind.Hi(t - windLag * (1f - Mathf.Exp(-t / Mathf.Max(1f, windLag)))) : Vector3.zero;
            if (airFx) capPosL = new Vector3(0f, CapHeight(t), 0f) + capDrift;
            else if (hiFx) capPosL = new Vector3(0f, HiRise(t), 0f);
            else if (underwater) capPosL = new Vector3(0f, groundY, 0f);
            else capPosL = Vector3.zero;
            capScale = airFx ? CapScale(t) : hiFx ? Rh * HiGrow(t) : R;
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
            UpdateAtmosphere(cam);
            // silver lining: strongest when the camera looks towards the sun past the cloud
            rimK = prof.rimLight * daylight * Mathf.Pow(Mathf.Max(0f, -Vector3.Dot(camDirL, sunL)), 1.5f);

            UpdateLight();
            UpdateFlash();
            if (vacFx)
            {
                UpdateDebrisShell();
                UpdateFragments();
                UpdateIonCloud();
            }
            if (streamers != null) UpdateStreamers();
            if (auroraLayers != null) for (int k = 0; k < auroraLayers.Length; k++) UpdateAurora(k);
            if (hiFx)
            {
                UpdateHiFireball();
                UpdateAirglow();
            }
            sunVis = sunVisCap;
            if (airFx)
            {
                UpdateCapShape();
                if (volume != null) UpdateVolume();
                else
                {
                    UpdateFireball();
                    UpdateCapSmoke();
                    if (capDetail != null) UpdateCapDetail();
                    if (capUpper != null) UpdateCapUpper();
                    UpdateCapGlow();
                }
                if (ringLayers != null && volume == null) for (int k = 0; k < ringLayers.Length; k++) UpdateRing(k);
                sunVis = sunVisLow; // skirt, surge and stem live under any low cloud deck
                if (skirt != null && volume == null) UpdateSkirt();
                if (surge != null && volume == null) UpdateSurge();
                if (stem != null && volume == null) UpdateStem();
                if (collars != null && volume == null) UpdateCollars();
                if (sparks != null) UpdateSparks();
                if (lightningFx) UpdateLightning();
            }
            UpdateEarlyFx();
            sunVis = sunVisLow;
            if (shockDome != null) UpdateShockDome();
            if (shock != null) UpdateShock();
            if (wilson != null && volume == null) UpdateWilson();
            if (waterColumn != null)
            {
                UpdateWaterColumn();
                UpdateWaterSpray();
                UpdateWaterSurge();
            }
            if (ringWave != null) UpdateRingWaves();
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

        /// <summary>Magnetic field at the burst and, high enough up, the aurora footpoints along its field lines.</summary>
        void SetupField(Vector3 burstWorld)
        {
            hasField = body != Planetarium.fetch?.Sun;
            if (!hasField) return;
            fieldL = Quaternion.Inverse(transform.rotation) * DipoleField(burstWorld);

            // Artificial aurora (Starfish Prime, Teak): beta particles spiral down the field lines and light up the
            // upper atmosphere where each end of the line dips into it. Trace the line both ways to find those footpoints.
            double minAlt = body.atmosphereDepth * (vacFx ? 0.4 : 0.25);
            if (!s.artificialAurora || !body.atmosphere || alt < minAlt) return;
            var feet = new List<Vector3>();
            var arrivals = new List<float>();
            double auroraAlt = System.Math.Min(body.atmosphereDepth * 0.75, alt * 0.9);
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
            for (int i = 0; i < arrivals.Count; i++) auroraArrival[i] = arrivals[i] / (StreamerSpeed * 3f) + 0.5f;
            lifeVac = 60f;
        }

        /// <summary>Debris speed along the field: full speed in vacuum, slowed by the thin air at high altitude.</summary>
        float StreamerSpeed => vDebris * Mathf.Lerp(0.2f, 1f, vacW / Mathf.Max(1e-3f, vacW + hiW));

        /// <summary>
        /// The shock front bending the light behind it: strongest while it's young and dense, fading as it spreads
        /// out and weakens (gone by ~30 fireball radii).
        /// </summary>
        void UpdateShockDome()
        {
            float rs = ShockRadius(t);
            float k = Mathf.Clamp01(t / 0.05f) * (1f - SS(10f * R, 30f * R, rs)) * airW;
            bool on = k > 0.01f && t > 0f;
            if (!on) return;
            // Unity's sphere primitive has radius 0.5
            shockDome.transform.localPosition = Vector3.zero;
            shockDome.transform.localScale = Vector3.one * (2f * rs);
            NukeVolumetric.AddShock(shockDome.transform.localToWorldMatrix, shockMat);
            // from inside the shell its silhouette surrounds the whole view: let it go once the front has passed
            float outside = SS(0.85f, 1.2f, camDist / Mathf.Max(rs, 1f));
            shockMat.SetFloat("_Strength", 0.018f * s.shockDistortion * k * outside * Mathf.Pow(R / Mathf.Max(R, rs), 0.35f));
            // ground height in the sphere's own units (radius 0.5): a dome for surface bursts
            float gy = groundFx ? Mathf.Clamp(groundY / (2f * rs), -0.5f, 0.5f) : -1f;
            // a free air burst shows the shell as a clear bubble around the fireball
            // a real shock front is all but invisible: a faint sheen at most, the bending of the light doing the work
            shockMat.SetVector("_ShockParams", new Vector4(gy, (groundFx ? 0.1f : 0.16f) * k * outside * Mathf.Max(daylight, 0.3f), 1f, 0f));
        }

        /// <summary>
        /// Feeds the raymarched cloud from the same timeline as the sprite cloud: a white-hot ball that cools
        /// through yellow and orange while its shell rolls into the toroidal cap, the stem and skirt from the
        /// ground, the weather and the haze.
        /// </summary>
        void UpdateVolume()
        {
            float m = Morph;
            float x = t / tauGlow;
            float glow = Glow(t);
            float grow = 1f - Mathf.Exp(-t / 0.15f);
            float prevScale = vs.capScale;

            vs.groundY = groundY;
            vs.time = t;

            // ---- wind: shear leans the cap, it stretches downwind, the dust blows off, the old cloud frays ----
            vs.windDir = wind.Dir(t);
            vs.windLow = wind.lowSpeed;
            vs.windHigh = wind.hiSpeed;
            float capAGL = Mathf.Max(1f, hb + capPosL.y);
            vs.shear = Mathf.Clamp((wind.hiSpeed - wind.lowSpeed) * t / capAGL * 0.12f, 0f, 0.5f);
            vs.capStretch = Mathf.Clamp((wind.hiSpeed - 0.5f * wind.lowSpeed) * Mathf.Max(0f, t - 0.3f * riseTime) * 0.35f / Mathf.Max(capRadius, 1f), 0f, 0.8f);
            vs.dustStretch = Mathf.Clamp(wind.lowSpeed * t * 0.5f / Mathf.Max(3f * R, 1f), 0f, 2f);
            vs.dissipation = SS(0.45f * lifeAir, 0.9f * lifeAir, t);
            vs.extinction = 2f / R;
            // billows sized to the grown cap (fixed per detonation, so they don't zoom): small yields grow caps
            // tens of fireball radii across, and fireball-sized billows would cover them in fur
            // megaton caps are tens of km across: billows sized to the whole cap blur into fog, so they get relatively
            // smaller, more numerous billows (the cauliflower texture of the big tests)
            vs.noiseScale = Mathf.Lerp(1f, 2.6f, SS(2.5f, 4.5f, Mathf.Log10(Mathf.Max(W, 1f)))) / (0.5f * Mathf.Max(capRadius, 3f * R));
            vs.flowPeriod = 6f;

            // ---- fireball and cap: a ball that rolls up into the toroidal cap ----
            vs.capCenter = capPosL;
            vs.capScale = Mathf.Max(1f, capScale * Mathf.Lerp(Mathf.Max(0.05f, grow), 1f, m));
            // a rolling torus wider than it is tall, its outer edge curling down into hanging lobes
            float column = NukeVolumeShapes.Column(W, prof.lowYield);
            NukeVolumeShapes.Cap(m, pkT, 0.64f * capYS, Mathf.Max(shapeBulb, column), out vs.ringR, out vs.tube, out vs.dome, out vs.squash);
            vs.squash *= Mathf.Lerp(1f, 1.35f, column * m);
            vs.xzSpread = Mathf.Lerp(1f, Mathf.Min(capXZS, 1.7f), m) * Mathf.Lerp(1f, 0.65f, column);   // a column's head is barely wider than it
            vs.overhang = Mathf.Max(prof.overhang, 0.45f) * m * (1f - 0.6f * pkT);
            vs.capDensity = FadeOut;
            // smooth at first, turbulent within a second or two (Rayleigh-Taylor), the cap fully churning
            vs.roughness = SS(0.05f * tauG, 0.6f * tauG, t) * Mathf.Lerp(0.85f, 1f, prof.turbulence);
            float dt = Mathf.Max(1e-3f, Time.deltaTime);
            vs.growthRate = prevScale > 1f ? Mathf.Clamp((vs.capScale - prevScale) / dt / vs.capScale, 0f, 1f) : 0f;
            vs.rollRate = (0.35f * Mathf.Exp(-t / tauRise) + 0.03f) * prof.rollSpeed;

            // ---- fire: white-hot, cooling through yellow and orange, burning through soot ----
            float heatK = (t < 0.5f ? 1.3f : 1f) * Mathf.Exp(-Mathf.Max(0f, x - 0.35f) / 0.85f);
            vs.fire = 4f * heatK * FadeOut * flashK;
            vs.fireTemp = Mathf.Clamp01(1.05f - 0.75f * x);
            vs.flicker = Mathf.Lerp(0.25f, 1f, vs.roughness);
            vs.soot = Mathf.Lerp(1f, 0.25f, column) * 0.8f * SS(0.3f * tauG, 1.2f * tauG, t) * (1f - SS(3f * tauG, 6f * tauG, t)) * Mathf.Lerp(1f, 0.5f, prof.humidity);
            // the burning ring inside the vortex outlives the fireball by many seconds, even for small yields
            vs.ember = 3.2f * Mathf.Exp(-t / (3f * tauG + 15f)) * SS(0.2f * tauG, tauG, t) * FadeOut * flashK;
            vs.emberTemp = 0.64f;
            vs.emberWidth = 0.6f;
            vs.glow = Ember * ((2.5f * glow + 0.9f * vs.ember) * FadeOut);
            vs.glowRadius = 0.9f * vs.capScale;

            // ---- stem: dust column climbing from the ground to meet the trail hanging under the cap ----
            float start = hb / c + 0.25f * tauG;
            vs.stemDensity = 0f;
            if (stemFx && t > start)
            {
                bool fromGround = groundFx;
                float yTop = StemTop;
                float trailLen = yTop * (fromGround ? 0.25f + 0.45f * SS(start, start + 10f, t) : 0.45f);
                float dustFront = fromGround ? yTop * Mathf.Lerp(0.3f, 1.05f, SS(start, start + 0.08f * riseTime + 4f, t)) : -1e5f;
                vs.stemBase = new Vector3(0f, groundY, 0f) + Lean(0f);
                vs.stemTop = new Vector3(0f, groundY + yTop, 0f) + Lean(1f);
                // the low stem stands nearly upright in the weak low wind and bends over higher up
                vs.stemMid = new Vector3(Mathf.Lerp(vs.stemBase.x, vs.stemTop.x, 0.1f), groundY + 0.5f * yTop, Mathf.Lerp(vs.stemBase.z, vs.stemTop.z, 0.1f));
                float columnS = column;
                float colW = Mathf.Lerp(1f, 0.72f, Mathf.Max(shapeBulb, columnS)) * Mathf.Lerp(1f, 3.4f, columnS);
                vs.stemBaseR = StemRadius(0.35f) * colW * Mathf.Lerp(1f, 0.75f, columnS);
                vs.stemTopR = StemRadius(0.95f) * colW;
                vs.stemFlare = 1.2f * prof.stemTaper * Mathf.Lerp(1f, 0.5f, columnS);
                vs.stemDustTop = groundY + dustFront;
                vs.stemTrailBottom = groundY + yTop - trailLen;
                // a free air burst has no stem: only a burst low enough to raise dust gets a column, from the ground up
                vs.stemDensity = fromGround ? 0.9f * FadeOut * SS(start, start + 2f, t) : 0f;
                vs.updraft = (60f * Mathf.Exp(-t / riseTime) + 10f) * prof.rollSpeed;
            }

            // ---- dust skirt and base surge ----
            vs.skirtDensity = 0f;
            vs.surgeDensity = 0f;
            if (groundFx)
            {
                float tg = hb / c;
                float tt = Mathf.Max(0f, t - tg);
                float ks = prof.skirtScale;
                vs.skirtCenter = new Vector3(0f, groundY, 0f) + Lean(0f) * 0.8f;
                // a big billowing ring of dust around the foot of the stem
                vs.skirtR = R * (0.8f + 1.0f * ks * (1f - Mathf.Exp(-tt / 8f)));
                vs.skirtH = R * (0.6f + 1.0f * (1f - Mathf.Exp(-tt / 8f))) * Mathf.Lerp(1f, 0.75f, Mathf.Clamp01((ks - 1f) * 0.6f));
                // a sub-kiloton column kicks up a dust skirt far wider than its tiny fireball: a low, flat sheet
                // spreading out along the ground around the column's foot
                float growK = 1f - Mathf.Exp(-tt / 10f);
                vs.skirtR = Mathf.Lerp(vs.skirtR, (0.4f + 1.2f * growK) * capRadius, column);
                vs.skirtH = Mathf.Lerp(vs.skirtH, (0.08f + 0.1f * growK) * capRadius, column);
                vs.skirtDensity = Mathf.Lerp(0.75f, 0.16f, column) * SS(tg + 0.2f, tg + 1.2f, t) * FadeOut * groundFactor;
                vs.skirtThin = 0.35f;
                vs.outflow = (30f * Mathf.Exp(-tt / 20f) + 3f) * prof.rollSpeed;

                float rs = ShockRadius(t);
                float rg = Mathf.Sqrt(Mathf.Max(0f, rs * rs - hb * hb));
                float surgeMax = R * (2f + 4f * groundFactor) * Mathf.Lerp(1f, prof.skirtScale, 0.5f);
                float surgeEnd = (surgeMax + hb) / c + 4f;
                vs.surgeR = Mathf.Min(rg, surgeMax);
                vs.surgeH = 0.5f * R * (1f + 0.04f * Mathf.Max(0f, t - hb / c));
                vs.surgeWidth = 0.8f * R + 0.04f * vs.surgeR;
                vs.surgeDensity = 0.14f * groundFactor * SS(tg + 0.3f, tg + 1f, t) * (1f - SS(surgeEnd, surgeEnd + 25f, t)) * FadeOut;
            }

            // ---- thermal precursor and fallout plume ----
            vs.precursorDensity = 0f;
            if (groundFx && !overOcean && s.precursorDust)
            {
                float rs0 = ShockRadius(t);
                float rg0 = Mathf.Sqrt(Mathf.Max(0f, rs0 * rs0 - hb * hb));
                NukeVolumeGround.Precursor(R, t, rg0, groundFactor * (1f - SS(0.3f * R, 1.2f * R, hb)), out vs.precursorR, out vs.precursorH, out vs.precursorDensity, out vs.precursorWidth);
                vs.precursorDensity *= FadeOut;
            }
            vs.machDensity = 0f;
            if (machFx)
            {
                NukeVolumeGround.MachStem(R, hb, ShockRadius(t), c, out vs.machR, out vs.machH, out vs.machDensity, out vs.machWidth);
                vs.machDensity *= FadeOut;
            }
            vs.groundFire = 0f;
            if (groundFx && !overOcean && s.groundFire)
            {
                NukeVolumeGround.GroundFire(R, t, tauG, groundFactor * (1f - SS(0.2f * R, 0.9f * R, hb)), out vs.groundFire, out vs.groundFireR, out vs.groundFireH, out vs.groundFireTemp);
                vs.groundFire *= FadeOut * flashK;
            }
            vs.plumeDensity = 0f;
            if (falloutK > 0f && s.falloutCloud && body.atmosphere)
            {
                NukeVolumeGround.Plume(t, riseTime, Mathf.Clamp01(falloutK * 1.4f) * NukeVolumeShapes.PlumeYield(W), capPosL, vs.capScale, groundY, wind.Dir(t), wind.lowSpeed,
                    out vs.plumeLow, out vs.plumeMid, out vs.plumeTop, out vs.plumeLowR, out vs.plumeTopR, out vs.plumeDensity);
                vs.plumeDensity *= FadeOut;
            }

            // ---- Wilson cloud: a brief, patchy veil behind the shock in humid air ----
            vs.wilsonDensity = 0f;
            if (wilson != null)
            {
                float tW = Mathf.Min(2.5f * R / c, 4f);
                vs.wilsonDensity = (1f - column) * 0.3f * (0.4f + 0.8f * prof.humidity) * SS(0.03f, 0.2f * tW, t) * (1f - SS(0.5f * tW, 1.3f * tW, t)) * airW;
                vs.wilsonR = Mathf.Min(R * 1.2f + c * t * 0.9f, 3.6f * R);
                vs.wilsonThick = 0.3f * R;
                vs.wilsonHemisphere = groundFx;
            }

            // ---- condensation rings above the fireball ----
            vs.ring0 = vs.ring1 = vs.ring2 = Vector4.zero;
            if (ringLayers != null)
            {
                for (int k = 0; k < 3; k++)
                {
                    float y0 = R * (1.25f + 0.75f * k);
                    float tk = y0 / c * 0.6f + 0.2f + 0.15f * k;
                    float hold = 3f + 0.5f * tauG;
                    float vis = SS(tk, tk + 0.6f, t) * (1f - SS(tk + hold, tk + hold + 4f + 0.5f * tauG, t)) * airW;
                    float rk = R * (1.5f + 0.32f * k) * (1f + 0.015f * t) * (1f + 0.4f * Rise(t));
                    var ring = new Vector4(y0 + 0.4f * CapHeight(t), rk, 0.3f * R, 0.3f * vis * (0.6f + 0.6f * prof.humidity));
                    ring.w *= 1f - column;   // no condensation rings on a small desert column
                    if (k == 0) vs.ring0 = ring; else if (k == 1) vs.ring1 = ring; else vs.ring2 = ring;
                }
                vs.ringOffset = 0.8f * capDrift;
            }

            // ---- pileus: an ice-cap veil over the rising cap as it lifts moist air (humid air only) ----
            vs.pileusDensity = 0f;
            if (body.atmosphere && prof.humidity > 0.15f && prof.lowYield < 0.5f)
            {
                vs.pileusOffset = 0.12f * vs.capScale;
                vs.pileusThick = 0.08f * vs.capScale;
                vs.pileusDensity = (1f - column) * 0.9f * prof.humidity * airW * SS(0.4f * tauG, 1.2f * tauG, t) * (1f - SS(0.25f * riseTime, 0.55f * riseTime, t));
            }

            // ---- velum: the vast condensation veil over the rising fireball in humid air ----
            vs.velumDensity = 0f;
            if (body.atmosphere && airFx && s.velum)
            {
                float oc = Mathf.Clamp01((1f - sunVisCam) / NukeWeather.DeckOpacity);
                NukeVolumeShapes.Velum(W, t, tauG, riseTime, prof.humidity, oc, vs.capScale, (vs.ringR + vs.tube) * vs.xzSpread,
                    out vs.velumRadius, out vs.velumThick, out vs.velumDensity, out vs.velumHeight);
                vs.velumDensity *= airW * FadeOut * (1f - column);
            }

            // ---- stem collars: bells of mist forming in turn as the column climbs ----
            vs.collar0 = vs.collar1 = vs.collar2 = vs.collar3 = vs.collar4 = Vector4.zero;
            if (collars != null && vs.stemDensity > 0f)
            {
                int nc = Mathf.Clamp(prof.stemCollars, 1, 5);
                float yTop = StemTop;
                for (int k = 0; k < nc; k++)
                {
                    float frac = 0.22f + 0.6f * (k + 0.5f) / nc;
                    float tk = start + (k + 0.5f) / nc * 0.45f * riseTime;
                    float hold = 0.04f * riseTime + 3f;
                    float vis = SS(tk, tk + 3f, t) * (1f - SS(tk + hold, tk + hold + 0.06f * riseTime + 3f, t)) * FadeOut;
                    float age = Mathf.Max(0f, t - tk);
                    float u = Mathf.Min(0.95f, frac + 0.06f * age / riseTime);
                    float stemR = StemRadius(u);
                    Vector3 cpos = new Vector3(0f, groundY + u * yTop, 0f) + Lean(u);
                    var pos = new Vector4(cpos.x, cpos.y, cpos.z, 0.55f * vis);
                    var shape = new Vector4(stemR * (2f + 0.6f * SS(0f, 10f, age)), 0.35f * stemR, 0.3f, 0f);
                    switch (k)
                    {
                        case 0: vs.collar0 = pos; vs.collarS0 = shape; break;
                        case 1: vs.collar1 = pos; vs.collarS1 = shape; break;
                        case 2: vs.collar2 = pos; vs.collarS2 = shape; break;
                        case 3: vs.collar3 = pos; vs.collarS3 = shape; break;
                        default: vs.collar4 = pos; vs.collarS4 = shape; break;
                    }
                }
            }

            // ---- light and colour ----
            vs.sunDir = sunL;
            vs.daylight = daylight;
            float low = Mathf.Clamp01(1f - sunL.y * 4f);
            vs.sunCol = Color.Lerp(new Color(1.0f, 0.97f, 0.92f), new Color(1.05f, 0.66f, 0.4f), low);
            vs.sunVisCap = sunVisCap;
            vs.sunVisLow = sunVisLow;
            float overcast = Mathf.Clamp01((1f - sunVisCam) / NukeWeather.DeckOpacity);
            vs.ambient = Color.Lerp(new Color(0.46f, 0.55f, 0.7f), new Color(0.55f, 0.57f, 0.6f), overcast) * Mathf.Lerp(0.05f, 1f, daylight);
            vs.groundCol = new Color(0.16f, 0.15f, 0.11f) * Mathf.Lerp(0.05f, 1f, daylight) * Mathf.Lerp(1f, 0.5f, overcast);
            // dusty clouds (low yields, surface bursts) are dark grey-brown smoke, not pale cotton
            vs.capAlbedo = CapColor(0f) * 0.8f * Mathf.Lerp(1f, 0.6f, prof.dustiness * groundFactor);
            // a small burst's column is sunlit dust and debris, light tan, hardly any soot
            vs.capAlbedo = Color.Lerp(vs.capAlbedo, new Color(0.80f, 0.70f, 0.56f), 0.75f * column);
            vs.stemAlbedo = Color.Lerp(stemLow, stemHigh, 0.4f) * 0.85f;
            vs.dustAlbedo = Color.Lerp(skirtDark, skirtLight, 0.55f) * 0.95f;
            if (overOcean)
            {
                // over the sea the skirt, surge and column are spray and condensation: white, not dust
                vs.dustAlbedo = new Color(0.86f, 0.88f, 0.91f);
                vs.stemAlbedo = Color.Lerp(vs.stemAlbedo, new Color(0.82f, 0.84f, 0.88f), 0.7f);
            }
            vs.condAlbedo = CondWhite;
            vs.hazeCol = hazeCol;
            vs.haze = haze;
            // the baked fluid simulation is the fireball while it burns and rolls up, then hands over to the cap
            if (s.fluidSim)
            {
                // a ground burst sits on the ground; an air burst is a free ball (it only meets the ground as dust later)
                bool onGround = groundFx && hb < 0.5f * R;
                var sim = onGround || NukeVolumetric.SimAir == null ? NukeVolumetric.SimGround : NukeVolumetric.SimAir;
                float simDur = Mathf.Max(6f, 2.5f * tauG);
                // small and medium surface bursts: the whole baked mushroom, played through most of the climb
                if (onGround && !overOcean && NukeVolumetric.SimMushroom != null && W <= NukeSimPlayer.MushroomMaxYield)
                {
                    sim = NukeVolumetric.SimMushroom;
                    simDur = NukeSimPlayer.MushroomDuration(riseTime);
                }
                float floorY = sim == NukeVolumetric.SimAir ? 0f : groundY;
                NukeSimPlayer.Fill(sim, ref vs, t, simDur, floorY, capPosL,
                    capPosL.y + Mathf.Max(vs.dome, vs.tube * vs.squash) * vs.capScale - floorY, vs.capScale * (vs.ringR + vs.tube) * vs.xzSpread,
                    FadeOut, 3f * flashK, sim == NukeVolumetric.SimMushroom ? NukeSimPlayer.MushroomPlayFraction : 1f, sim == NukeVolumetric.SimMushroom);
                vs.fire *= 1f - vs.simMix;
                vs.ember *= 1f - 0.7f * vs.simMix;
            }
            else vs.simMix = 0f;
            vs.planetCenter = transform.InverseTransformPoint(body.position);
            // the ground at ground zero as a sphere: rays stop at it, so the ground in front of the cloud hides it
            // even where the terrain shader leaves no depth (Parallax), and the planet hides it from orbit
            vs.planetRadius = (float)(body.Radius + System.Math.Max(0d, alt + groundY)) - 8f;

            // quality where it shows: a cloud that's a speck on screen, or one of a salvo, gets fewer steps
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float fov = cam != null ? cam.fieldOfView : 60f;
            // the cost is pixels x steps: as the cloud fills more of the screen it gets fewer steps (the per-pixel
            // jitter hides it), so getting close doesn't tank the frame rate
            float screenFrac = 2f * Mathf.Max(vs.capScale, R) / Mathf.Max(1f, camDist * 2f * Mathf.Tan(0.5f * fov * Mathf.Deg2Rad));
            float steps = s.volumetricSteps * Mathf.Clamp(0.35f / Mathf.Max(screenFrac, 0.01f), 0.3f, 1f);
            // a reduced-resolution cloud can afford more samples per pixel
            if (s.volumetricResolution >= 2) steps *= s.volumetricResolution >= 3 ? 1.8f : 1.4f;
            int active = NukeFXManager.Existing != null ? NukeFXManager.Existing.ActiveCount : 1;
            steps *= Mathf.Clamp(1f / Mathf.Sqrt(Mathf.Max(1, active)), 0.35f, 1f);
            steps *= NukeFXManager.StepScale; // adaptive performance
            volume.Update(ref vs, Mathf.Clamp(Mathf.RoundToInt(steps), 16, 128));
        }

        /// <summary>
        /// Per frame: the haze between the camera and the cloud, the overcast above it (EVE clouds, sampled once a
        /// second) and, far off, softer and slightly merged smoke puffs so the cloud reads as one mass, not balls.
        /// </summary>
        void UpdateAtmosphere(Camera cam)
        {
            if (Time.time >= nextWeatherSample)
            {
                nextWeatherSample = Time.time + 1f;
                if (s.weatherLighting && body.atmosphere) NukeWeather.Sample(body, lat, lon, decks);
                else decks.Clear();
            }
            float groundASL = (float)(alt + groundY);
            float capASL = (float)alt + capPosL.y;
            double camAltD = cam != null ? body.GetAltitude(cam.transform.position) : alt;
            float camAlt = (float)camAltD;
            sunVisCap = NukeWeather.SunVisibility(decks, capASL);
            sunVisLow = NukeWeather.SunVisibility(decks, Mathf.Lerp(groundASL, capASL, 0.25f));
            sunVisCam = NukeWeather.SunVisibility(decks, camAlt);

            haze = 0f;
            if (s.atmosphericHaze && body.atmosphere && cam != null)
            {
                // optical depth ~ distance through air of the average density along the line of sight
                float H = Mathf.Max(1000f, (float)body.atmosphereDepth / 12f); // scale height (~5.8 km on Kerbin)
                float meanAlt = Mathf.Max(0f, 0.5f * (camAlt + Mathf.Lerp(groundASL, capASL, 0.5f)));
                float tau = camDist / s.hazeVisibility * Mathf.Exp(-meanAlt / H);
                // under a deck the air is murkier too
                tau *= 1f + 0.6f * (1f - sunVisCam);
                haze = Mathf.Min(0.88f, 1f - Mathf.Exp(-tau));
                float overcast = Mathf.Clamp01((1f - sunVisCam) / NukeWeather.DeckOpacity);
                Color clear = Color.Lerp(new Color(0.60f, 0.68f, 0.78f), new Color(0.80f, 0.62f, 0.48f), Mathf.Clamp01(1f - 2f * Mathf.Abs(daylight - 0.45f)) * 0.5f);
                hazeCol = Color.Lerp(clear, new Color(0.56f, 0.58f, 0.61f), overcast)
                          * (Mathf.Lerp(0.05f, 1f, daylight) * Mathf.Lerp(1f, 0.72f, overcast));
            }

            // far off, each billow is only a few pixels: let neighbours overlap and thin out so the edges blur
            float far = SS(25f, 160f, camDist / Mathf.Max(1f, R));
            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                if (l == null) continue;
                if (l.soft)
                {
                    l.sizeMul = 1f + 0.4f * far;
                    l.alphaMul = 1f - 0.3f * far;
                }
                else l.alphaMul = 1f - 0.75f * haze; // glows dim through the haze rather than turning grey
            }
        }

        /// <summary>Fake volumetric lighting: sun diffuse + ambient + warm light from the fireball + backlit rim.</summary>
        Color Shade(Color baseCol, Vector3 nL, float fire, Vector3 toFireL)
        {
            // the billow texture already carries per-lump shading, so the macro term is gentler
            // under an overcast deck the direct sun is gone: flat, dimmer, greyer skylight from all around
            float shade = 1f - sunVis;
            float diff = Mathf.Max(0f, Vector3.Dot(nL, sunL)) * daylight * sunVis;
            float amb = (0.42f + 0.22f * daylight) * (1f - 0.22f * shade) + 0.12f * daylight * shade;
            Color col = baseCol * (amb + 0.55f * diff);
            if (shade > 0.01f)
            {
                float grey = 0.33f * (col.r + col.g + col.b);
                col = Color.Lerp(col, new Color(grey * 0.97f, grey, grey * 1.04f), 0.4f * shade);
            }
            if (fire > 0f) col += Ember * fire * (0.35f + 0.65f * Mathf.Max(0f, Vector3.Dot(nL, toFireL)));
            if (rimK > 0.001f && sunVis > 0.05f)
            {
                float edge = 1f - Mathf.Abs(Vector3.Dot(nL, camDirL));
                col += RimCol * (rimK * sunVis * edge * edge * edge);
            }
            // aerial perspective: the air between here and the camera washes the cloud toward the sky colour
            if (haze > 0.002f) col = Color.Lerp(col, hazeCol, haze);
            col.a = 1f;
            return col;
        }

        static Color WithAlpha(Color c, float a) { c.a = Mathf.Clamp01(a); return c; }

        /// <summary>Per-puff brightness and slight warm/cool variation, so neighbouring billows don't read as identical balls.</summary>
        static Color PuffTone(float rand)
        {
            float k = Mathf.Repeat(rand * 13.71f, 1f);
            float b = 0.84f + 0.3f * k;
            float w = Mathf.Repeat(rand * 29.3f, 1f) - 0.5f;
            return new Color(b * (1f + 0.04f * w), b, b * (1f - 0.05f * w), 1f);
        }

        /// <summary>Billboard rotation pointing the billow's lit top at the sun, with a little per-puff variation.</summary>
        float Billow(float rand) => sunRotDeg + (rand - 0.5f) * 50f;

        static Color FireballColor(float x)
        {
            // x = t / tauGlow
            if (x < 0.05f) return new Color(1f, 1f, 1f);
            if (x < 0.25f) return Color.Lerp(new Color(1f, 0.98f, 0.92f), new Color(1f, 0.88f, 0.55f), (x - 0.05f) / 0.2f);
            if (x < 0.7f) return Color.Lerp(new Color(1f, 0.88f, 0.55f), new Color(1f, 0.55f, 0.18f), (x - 0.25f) / 0.45f);
            return Color.Lerp(new Color(1f, 0.55f, 0.18f), new Color(0.75f, 0.16f, 0.04f), Mathf.Clamp01((x - 0.7f) / 1.0f));
        }

        void UpdateLight()
        {
            float fi = FlashIntensity(t);
            float fade = 1f - SS(life - 8f, life, t);
            float intensity = Mathf.Lerp(8f, 12f, vacW) * fi * fade;
            Color col = Color.Lerp(new Color(1f, 0.6f, 0.3f), new Color(1f, 0.95f, 0.88f), Mathf.Clamp01(fi));
            float range = Mathf.Clamp(80f * R, 3000f, 80000f);
            if (airFx) intensity += 3.5f * Glow(t) * FadeOut;
            if (underwater) intensity += 2f * flashK * Mathf.Exp(-t / 1.5f);
            if (hiFx)
            {
                float hg = HiGlow(t) * FadeHi;
                intensity += 3f * hg;
                col = Color.Lerp(col, new Color(1f, 0.35f, 0.25f), hiW * (1f - Mathf.Clamp01(fi)));
                range = Mathf.Max(range, Mathf.Clamp(30f * Rh, 3000f, 150000f));
            }
            if (vacFx)
            {
                float x = Mathf.Clamp01(t / ShellLife);
                intensity += 2f * Mathf.Pow(1f - x, 2f) * FadeVac;
                col = Color.Lerp(col, Color.Lerp(PlasmaColor(x), new Color(0.85f, 0.9f, 1f), Mathf.Clamp01(fi)), vacW);
                range = Mathf.Max(range, Mathf.Clamp(ShellRadius(t) * 3f, 3000f, 200000f));
            }
            if (s.fireballLighting) FireballLighting(fi, fade, ref intensity, ref col, ref range);
            lightFx.intensity = intensity;
            lightFx.color = col;
            lightFx.range = range;
            lightFx.transform.localPosition = capPosL;
        }

        // ---- fireball lighting ----
        /// <summary>Most intensity the fireball's point light reaches, so even a megaton seen up close doesn't wash out.</summary>
        const float FireLightMaxIntensity = 12f;

        /// <summary>
        /// The fireball's surface temperature (K) as it cools: ~7500 K through the thermal pulse, through yellow and
        /// orange to a dull red ~1600 K as the incandescence dies (same clock as the volume's fireTemp).
        /// </summary>
        float FireballKelvin => Mathf.Lerp(1600f, 7500f, Mathf.Clamp01(1.05f - 0.75f * t / tauGlow));

        /// <summary>
        /// The fireball lighting the world: the point light gets stronger and reaches further with yield, takes the
        /// fireball's blackbody colour and fades with its incandescence, and the flight cameras add a full-screen warm
        /// pass (BDNukes/FireLight) that lights terrain and craft by their distance from the fireball.
        /// </summary>
        void FireballLighting(float fi, float fade, ref float intensity, ref Color col, ref float range)
        {
            if (underwater) return;
            // 0.4 at 10 t, 0.75 at 1 kt, 1.1 at 100 kt, 1.5 at 10 Mt and up
            float yieldK = Mathf.Clamp(0.75f + 0.18f * Mathf.Log10(Mathf.Max(W, 1e-3f)), 0.4f, 1.5f);
            float inc = 0f;   // incandescence, 1 at the start
            if (airFx) inc = Mathf.Pow(Glow(t), 0.8f) * FadeOut;
            if (hiFx) inc = Mathf.Max(inc, 0.6f * HiGlow(t) * FadeHi);
            float flash = Mathf.Clamp01(fi);
            Color fire = NukeFireLighting.Blackbody(FireballKelvin);
            // the flash is white; the glowing ball after it is its own blackbody colour
            Color fireCol = Color.Lerp(fire, new Color(0.96f, 0.97f, 1f), flash);
            float atmo = Mathf.Clamp01(airW + hiW);
            col = Color.Lerp(col, fireCol, atmo);
            intensity = Mathf.Min(intensity * Mathf.Lerp(1f, yieldK, atmo) + 5f * yieldK * inc, FireLightMaxIntensity);
            float reach = airFx ? Mathf.Clamp(60f * R, 3000f, 60000f) : hiFx ? Mathf.Clamp(25f * Rh, 3000f, 80000f) : 0f;
            range = Mathf.Max(range, reach);

            // the screen pass: the light's strength at the fireball's core, falling off by inverse square beyond it.
            // In daylight the sun hides some of it; at night it's the only light there is.
            float strength = (2.5f * flash * fade + 3f * inc) * yieldK * Mathf.Lerp(1f, 0.25f, daylight) * atmo;
            if (strength < 0.01f) return;
            float core = 1.6f * Mathf.Max(capScale, R);
            Vector3 worldPos = transform.TransformPoint(capPosL);
            // the wash reaches a few fireball sizes, not half the planet
            NukeVolumetric.AddFireLight(worldPos, core, Mathf.Clamp(8f * core, range, 80000f), fireCol * Mathf.Min(strength, 6f));
        }
        // ---- end fireball lighting ----

        static readonly float[] FlashSizes = { 14f, 6f, 2.4f };

        void UpdateFlash()
        {
            int n = flash.Begin();
            float fi = FlashIntensity(t);
            // the wide halo sprites belong to the first instants: they shrink after ~1 s, so the flash's slow tail
            // doesn't leave a huge white halo hanging over a megaton seen from far away
            float scale = (1f + 2f * vacW + hiW) * Mathf.Lerp(1f, 0.3f, SS(0.3f + tMax, 1.5f + 3f * tMax, t));
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.Clamp01(fi * (i == 2 ? 1f : 0.7f * (1f - SS(0.5f + tMax, 2f + 3f * tMax, t))));
                flash.Set(i, capPosL, R * FlashSizes[i % 3] * scale, new Color(0.92f, 0.95f, 1f, a), 0f);
            }
            flash.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // Mushroom cloud
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Per-frame cap shape: the profile's aspect stretches it tall (desert cauliflower) or squashes it flat;
        /// once the cloud reaches the tropopause it stops climbing and spreads sideways into a pancake while a
        /// small overshoot dome stays on top.
        /// </summary>
        void UpdateCapShape()
        {
            pkT = prof.pancake * SS(0.25f * riseTime, 1.1f * riseTime, t);
            capYS = Mathf.Lerp(capYScale, 0.5f, pkT);
            capXZS = 1f + pkT * s.pancakeStrength * (1f + 0.8f * SS(riseTime, 4f * riseTime, t));
            domeYS = Mathf.Lerp(capYScale, 0.9f, pkT);
            domeXZS = Mathf.Lerp(1f, 0.75f, pkT);
        }

        /// <summary>Torus point for the cap; in a pancake the ring closes in and the tube widens into a filled disc.</summary>
        Vector3 CapTorus(float theta, float phi, float ringR, float tube)
        {
            float rr = Mathf.Lerp(ringR, ringR * 0.62f, pkT);
            float tubeX = tube * (1f + 0.9f * pkT);
            float rho = rr + tubeX * Mathf.Cos(phi);
            return new Vector3(rho * Mathf.Cos(theta), tube * Mathf.Sin(phi) * 0.85f, rho * Mathf.Sin(theta));
        }

        /// <summary>Applies aspect, spread and overhang to a cap-local point.</summary>
        Vector3 CapShape(Vector3 p, bool dome)
        {
            if (!dome)
            {
                // the outer underside curls down past the rim (overhanging cauliflower)
                float rxz = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                p.y -= prof.overhang * 0.35f * SS(0.7f, 1.25f, rxz) * (1f - SS(-0.3f, 0.25f, p.y));
            }
            float ys = dome ? domeYS : capYS, xs = dome ? domeXZS : capXZS;
            return new Vector3(p.x * xs, p.y * ys, p.z * xs);
        }

        /// <summary>Puff size compensation so stretched or spread caps don't open up gaps.</summary>
        float CapPuffScale(bool dome)
        {
            float ys = dome ? domeYS : capYS, xs = dome ? domeXZS : capXZS;
            return prof.billowScale * Mathf.Lerp(1f, Mathf.Sqrt(xs * Mathf.Max(1f, ys)), 0.8f);
        }

        /// <summary>Base cap colour over time: young debris colour to the mature cloud, whitening if it pancakes.</summary>
        Color CapColor(float tint)
        {
            Color col = Color.Lerp(colEarly, colLate, SS(0.5f * riseTime, 2f * riseTime, t));
            col = Color.Lerp(col, CondWhite, 0.6f * pkT * (1f - prof.dustiness));
            return Color.Lerp(col, Color.white, tint);
        }

        // --- Fireball: white-hot core that expands, cools through yellow/orange/red and rolls into the cap ---
        void UpdateFireball()
        {
            int n = fireCore.Begin();
            float x = t / tauGlow;
            float grow = 1f - Mathf.Exp(-t / 0.15f);
            float m = Morph;
            Color col = FireballColor(x);
            float intensity = (t < 0.5f ? 1.3f : 1f) * Mathf.Exp(-Mathf.Max(0f, x - 0.35f) / 0.85f) * FadeOut;
            for (int i = 0; i < n; i++)
            {
                Vector3 sph = fireCore.SphereDir(i) * (0.55f * Mathf.Sqrt(fireCore.r0[i])) * grow;
                Vector3 tor = CapShape(CapTorus(fireCore.r1[i] * 2f * Mathf.PI, fireCore.r2[i] * 2f * Mathf.PI - 0.5f * t * prof.rollSpeed, 0.75f, 0.18f), false);
                Vector3 p = Vector3.Lerp(sph, tor, m);
                float size = (0.9f + 0.6f * fireCore.r3[i]) * Mathf.Max(0.05f, grow) * (1f - 0.35f * m) * (1f + 0.5f * pkT);
                float a = intensity * (0.55f + 0.45f * fireCore.r3[i]);
                fireCore.Set(i, p, size, WithAlpha(col, a), fireCore.r3[i] * 360f + t * 8f);
            }
            fireCore.End(n);
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
            float roll = (0.55f * tauRise * (1f - Mathf.Exp(-t / tauRise)) + 0.03f * t) * prof.rollSpeed;
            float turb = prof.turbulence;
            Color baseCap = CapColor(0f);
            float puffRing = CapPuffScale(false), puffDome = CapPuffScale(true);
            for (int i = 0; i < n; i++)
            {
                float r0 = capSmoke.r0[i], r1 = capSmoke.r1[i], r2 = capSmoke.r2[i], r3 = capSmoke.r3[i];
                bool dome = r3 < 0.32f; // fills the centre/top for the cauliflower crown
                float theta = r1 * 2f * Mathf.PI + 0.02f * t * (r0 - 0.5f);
                float phi = r2 * 2f * Mathf.PI - roll * (0.7f + 0.6f * r0);
                float ringR = dome ? 0.32f : 0.78f;
                float tube = (dome ? 0.38f : 0.44f) * (0.55f + 0.45f * Mathf.Sqrt(r0));

                Vector3 sph = capSmoke.SphereDir(i) * (0.78f + 0.22f * r0);
                Vector3 tor = dome ? TorusPoint(theta, phi, ringR, tube) : CapTorus(theta, phi, ringR, tube);
                if (dome) tor.y = Mathf.Abs(tor.y) + 0.12f;
                tor = CapShape(tor, dome);
                Vector3 wobble = new Vector3(Mathf.Sin(t * 0.6f + r0 * 40f), Mathf.Sin(t * 0.5f + r1 * 40f), Mathf.Sin(t * 0.55f + r2 * 40f)) * (0.03f * turb);
                Vector3 p = Vector3.Lerp(sph, tor, m) + wobble;

                Vector3 core = Vector3.Lerp(Vector3.zero, new Vector3(ringR * capXZS * Mathf.Cos(theta), 0f, ringR * capXZS * Mathf.Sin(theta)), m);
                Vector3 nL = (p - core).sqrMagnitude > 1e-6f ? (p - core).normalized : Vector3.up;

                float inner = 1f - Mathf.Clamp01((p - core).magnitude / 0.6f);
                float fire = ember * (0.7f + 0.8f * inner) + 0.25f * ember * Mathf.Max(0f, -nL.y);
                Color col = Shade(Color.Lerp(baseCap, colDark, 0.35f * Mathf.Max(0f, -nL.y)) * PuffTone(r3), nL, fire, -nL);
                // a few big masses among the medium billows
                float size = (0.42f + 0.3f * r0 + 0.45f * Mathf.Pow(Mathf.Repeat(r1 * 5.37f, 1f), 5f)) * (1f + 0.35f * m) * Mathf.Lerp(1f, dome ? puffDome : puffRing, m);
                capSmoke.Set(i, p, size, WithAlpha(col, 0.85f * appear), Billow(r3));
            }
            capSmoke.End(n);
        }

        static Vector3 TorusPoint(float theta, float phi, float ringR, float tube)
        {
            float rho = ringR + tube * Mathf.Cos(phi);
            return new Vector3(rho * Mathf.Cos(theta), tube * Mathf.Sin(phi) * 0.85f, rho * Mathf.Sin(theta));
        }

        // --- Cap surface detail: hundreds of small billows rolling over the torus and boiling in place ---
        void UpdateCapDetail()
        {
            int n = capDetail.Begin();
            float m = Morph;
            float ember = Mathf.Pow(Glow(t), 0.55f);
            float appear = SS(0.15f * tauG, 0.8f * tauG, t) * FadeOut;
            // the surface rolls faster than the bulk so the motion is easy to read
            float roll = (0.8f * tauRise * (1f - Mathf.Exp(-t / tauRise)) + 0.05f * t) * prof.rollSpeed;
            float turb = prof.turbulence;
            Color baseCap = CapColor(0.05f);
            float puffRing = CapPuffScale(false), puffDome = CapPuffScale(true);
            for (int i = 0; i < n; i++)
            {
                float r0 = capDetail.r0[i], r1 = capDetail.r1[i], r2 = capDetail.r2[i], r3 = capDetail.r3[i];
                bool dome = r3 < 0.3f;
                float theta = r1 * 2f * Mathf.PI + 0.015f * t * (r0 - 0.5f);
                float phi = r2 * 2f * Mathf.PI - roll * (1.1f + 0.4f * r0);
                float ringR = dome ? 0.3f : 0.78f;
                // lumpy tube surface: large bulges that drift slowly, so the silhouette keeps changing
                float lump = Mathf.Sin(3f * theta + 2f * phi + r3 * 6f) * Mathf.Sin(5f * theta - 3f * phi + t * 0.3f * turb);
                float tube = (dome ? 0.46f : 0.5f) * (1f + 0.14f * turb * lump);

                Vector3 sph = capDetail.SphereDir(i) * (0.92f + 0.1f * r0);
                Vector3 tor = dome ? TorusPoint(theta, phi, ringR, tube) : CapTorus(theta, phi, ringR, tube);
                if (dome) tor.y = Mathf.Abs(tor.y) + 0.14f;
                tor = CapShape(tor, dome);
                float coreR = ringR * (dome ? domeXZS : capXZS);
                Vector3 core = Vector3.Lerp(Vector3.zero, new Vector3(coreR * Mathf.Cos(theta), 0f, coreR * Mathf.Sin(theta)), m);
                Vector3 p = Vector3.Lerp(sph, tor, m);
                Vector3 nL = (p - core).sqrMagnitude > 1e-6f ? (p - core).normalized : Vector3.up;
                p += nL * (0.035f * turb * Mathf.Sin(t * (0.7f + r0) + r2 * 30f)); // boiling in and out

                float under = Mathf.Max(0f, -nL.y);
                float fire = ember * (0.45f + 0.9f * under);
                Color baseCol = Color.Lerp(baseCap, colDark, 0.45f * under);
                Color col = Shade(baseCol * PuffTone(r3), nL, fire, -nL);
                // mostly fine detail with the odd larger lump, not a ball pit of equal spheres
                float size = (0.09f + 0.27f * Mathf.Pow(r0, 2.2f)) * (1f + 0.18f * Mathf.Sin(t * (0.8f + 1.2f * r0) + r1 * 20f)) * (1f + 0.25f * m)
                             * Mathf.Lerp(1f, dome ? puffDome : puffRing, m);
                capDetail.Set(i, p, size, WithAlpha(col, 0.92f * appear), Billow(r3));
            }
            capDetail.End(n);
        }

        // --- Stacked cap (Tsar Bomba): a second, narrower rolling layer that climbs out of the top of the first ---
        void UpdateCapUpper()
        {
            int n = capUpper.Begin();
            float grow = SS(0.3f * riseTime, 1.2f * riseTime, t);
            float appear = grow * prof.stack * FadeOut;
            float roll = (0.6f * tauRise * (1f - Mathf.Exp(-t / tauRise)) + 0.04f * t) * prof.rollSpeed;
            float yC = (0.45f + 0.5f * grow) * domeYS;
            float xs = Mathf.Lerp(1f, capXZS, 0.4f);
            Color baseCap = Color.Lerp(CapColor(0.1f), CondWhite, 0.25f);
            float ember = Mathf.Pow(Glow(t), 0.55f) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                float r0 = capUpper.r0[i], r1 = capUpper.r1[i], r2 = capUpper.r2[i], r3 = capUpper.r3[i];
                bool dome = r3 < 0.35f;
                float theta = r1 * 2f * Mathf.PI + 0.012f * t * (r0 - 0.5f);
                float phi = r2 * 2f * Mathf.PI - roll * (0.9f + 0.4f * r0);
                float ringR = dome ? 0.2f : 0.5f;
                float lump = Mathf.Sin(4f * theta + 2f * phi + r3 * 6f) * Mathf.Sin(3f * theta - 2f * phi + t * 0.25f);
                float tube = (dome ? 0.3f : 0.32f) * (1f + 0.15f * prof.turbulence * lump);
                Vector3 tor = TorusPoint(theta, phi, ringR, tube);
                if (dome) tor.y = Mathf.Abs(tor.y) + 0.1f;
                Vector3 p = new Vector3(tor.x * xs, yC + tor.y * domeYS * 0.8f, tor.z * xs);
                Vector3 core = new Vector3(ringR * xs * Mathf.Cos(theta), yC, ringR * xs * Mathf.Sin(theta));
                Vector3 nL = (p - core).sqrMagnitude > 1e-6f ? (p - core).normalized : Vector3.up;
                float under = Mathf.Max(0f, -nL.y);
                Color col = Shade(Color.Lerp(baseCap, colDark, 0.4f * under) * PuffTone(r3), nL, ember * under, -nL);
                float size = (0.1f + 0.3f * Mathf.Pow(r0, 2f)) * prof.billowScale * (0.6f + 0.4f * grow) * Mathf.Sqrt(xs);
                capUpper.Set(i, p, size, WithAlpha(col, 0.9f * appear), Billow(r3));
            }
            capUpper.End(n);
        }

        // --- Incandescent core glowing through the cap (the bright dome / glowing donut) ---
        void UpdateCapGlow()
        {
            int n = capGlow.Begin();
            float m = Morph;
            // a pancaking megaton cap keeps its glowing core visible through the cloud for a long time
            float g = Mathf.Pow(Glow(t), 0.75f) * SS(0f, 0.4f * tauG, t) * FadeOut * (1f + 0.3f * prof.pancake);
            float roll = 0.55f * tauRise * (1f - Mathf.Exp(-t / tauRise)) * prof.rollSpeed;
            float x = Mathf.Max(0.3f, t / tauGlow * 0.8f);
            for (int i = 0; i < n; i++)
            {
                float r0 = capGlow.r0[i], r1 = capGlow.r1[i], r2 = capGlow.r2[i], r3 = capGlow.r3[i];
                Vector3 sph = capGlow.SphereDir(i) * 0.6f * Mathf.Sqrt(r0);
                Vector3 tor = CapShape(CapTorus(r1 * 2f * Mathf.PI, r2 * 2f * Mathf.PI - roll, 0.78f, 0.22f), false);
                Vector3 p = Vector3.Lerp(sph, tor, m);
                // Patchy: smaller blobs of very different strength that flare and fade as the cloud churns,
                // so the heat shows through in places instead of as one even blob. Strongest low in the cap,
                // where the hot core rolls under; the hottest patches stay yellower, the rest are deep orange.
                float patch = Mathf.Pow(Mathf.Repeat(r3 * 7.13f, 1f), 2f) * (0.55f + 0.45f * Mathf.Sin(t * (0.3f + 0.5f * r0) + r1 * 30f));
                float low = 1f - SS(-0.35f, 0.45f, p.y);
                float a = g * (0.15f + 0.85f * Mathf.Max(0f, patch)) * (0.3f + 0.7f * low);
                Color col = FireballColor(x * (1.25f - 0.5f * patch));
                float size = (0.35f + 0.45f * r3) * Mathf.Lerp(1f, Mathf.Sqrt(capXZS), m);
                capGlow.Set(i, p, size, WithAlpha(col, a * 0.75f), r3 * 360f);
            }
            capGlow.End(n);
        }

        // --- Visible shock front: limb-brightened shell expanding at the speed of sound ---
        void UpdateShock()
        {
            int n = shock.Begin();
            float rs = ShockRadius(t);
            float maxR = 8f * R;
            float w = underwater ? 0.5f * flashK + 0.2f : airW;
            float a = rs < maxR ? 0.3f * w * Mathf.Pow(1f - rs / maxR, 2f) * SS(0f, 0.08f, t) : 0f;
            // It's a faint refraction/condensation front: only worth drawing up close. Seen from far away
            // (high altitude or orbit) it would just be a hard ring outline around the burst.
            a *= 1f - SS(2f * rs + 4f * R, 5f * rs + 15f * R, camDist);
            Vector3 centre = underwater ? new Vector3(0f, groundY, 0f) : Vector3.zero;
            for (int i = 0; i < n; i++) shock.Set(i, centre, 2.1f * rs, new Color(0.95f, 0.95f, 1f, a), 0f);
            shock.End(n);
        }

        // --- Wilson condensation cloud: brief white shell that flashes into existence behind the shock.
        //     Over water it is a big, longer-lived dome that hides the base of the column (Crossroads Baker). ---
        void UpdateWilson()
        {
            int n = wilson.Begin();
            // a Wilson cloud lasts a few seconds whatever the yield (megatons would otherwise keep a white
            // shell, limb-brightened into a ring from afar, for most of a minute)
            float tW = Mathf.Min(3.5f * R / c, 6f) * (waterFx ? 2.5f : 1f);
            float vis = SS(0.03f, 0.25f * tW, t) * (1f - SS(0.6f * tW, 1.4f * tW, t)) * (underwater ? 1f : airW);
            float rw = Mathf.Min(R * 1.2f + c * t * 0.9f, (waterFx ? 5f : 3.6f) * R);
            float glow = underwater ? 0f : Mathf.Pow(Glow(t), 0.4f);
            Vector3 centre = underwater ? new Vector3(0f, groundY, 0f) : Vector3.zero;
            float alpha = waterFx ? 0.5f : 0.32f * (0.7f + 0.6f * prof.humidity);
            for (int i = 0; i < n; i++)
            {
                Vector3 d = wilson.SphereDir(i);
                if (groundFx || waterFx) d.y = Mathf.Abs(d.y);
                Vector3 p = centre + d * rw * (0.94f + 0.06f * wilson.r0[i]);
                Color col = Shade(CondWhite, d, glow * 0.9f, -d);
                wilson.Set(i, p, R * (0.5f + 0.3f * wilson.r1[i]) * (waterFx ? 1.4f : 1f), WithAlpha(col, alpha * vis), wilson.r2[i] * 360f);
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
            float vis = SS(tk, tk + 0.6f, t) * (1f - SS(tk + hold, tk + hold + 4f + 0.5f * tauG, t)) * airW;
            if (vis <= 0f)
            {
                for (int i = 0; i < n; i++) layer.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                layer.End(n);
                return;
            }
            float y = y0 + 0.4f * CapHeight(t);
            float rk = R * (1.5f + 0.32f * k) * (1f + 0.015f * t) * (1f + 0.4f * (Rise(t)));
            float glow = Mathf.Pow(Glow(t), 0.4f);
            float alpha = 0.3f * (0.6f + 0.6f * prof.humidity);
            for (int i = 0; i < n; i++)
            {
                float theta = (i + layer.r0[i] * 0.8f) / n * 2f * Mathf.PI + 0.015f * t * (k % 2 == 0 ? 1f : -1f);
                float rr = rk + (layer.r1[i] - 0.5f) * 0.22f * R;
                Vector3 p = new Vector3(Mathf.Cos(theta) * rr, y + (layer.r2[i] - 0.5f) * 0.12f * R, Mathf.Sin(theta) * rr) + 0.8f * capDrift;
                Vector3 toFire = (capPosL - p).normalized;
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.3f, -0.95f, Mathf.Sin(theta) * 0.3f).normalized; // underside faces the fireball
                Color col = Shade(CondWhite, Vector3.up, 0f, toFire);
                col = Color.Lerp(col, new Color(1f, 0.7f, 0.42f), glow * 0.85f * Mathf.Max(0.3f, Vector3.Dot(nL, toFire)));
                float size = R * (0.42f + 0.2f * layer.r3[i]) * (1f + 0.02f * t);
                layer.Set(i, p, size, WithAlpha(col, alpha * vis), layer.r3[i] * 360f);
            }
            layer.End(n);
        }

        // --- Dust skirt: the churning, fire-lit dust cloud piled around the base. The profile scales it:
        //     a desert shot boils out into a huge, low, ground-hugging skirt; over water it is white spray. ---
        void UpdateSkirt()
        {
            int n = skirt.Begin();
            float tg = hb / c;
            float tt = Mathf.Max(0f, t - tg);
            float vis = SS(tg, tg + 0.5f, t) * FadeOut * groundFactor;
            float glow = Mathf.Pow(Glow(t), 0.5f);
            float ks = prof.skirtScale;
            float spread = 2.4f * ks;
            float flat = Mathf.Lerp(1f, 0.6f, Mathf.Clamp01((ks - 1f) * 0.6f)); // wide skirts hug the ground
            float turb = prof.turbulence;
            for (int i = 0; i < n; i++)
            {
                float r0 = skirt.r0[i], r1 = skirt.r1[i], r2 = skirt.r2[i], r3 = skirt.r3[i];
                float theta = r1 * 2f * Mathf.PI + 0.03f * tt * (r0 - 0.5f);
                float rad = R * (0.6f + 0.6f * r0) + spread * R * (1f - Mathf.Exp(-tt / (5f + 2f * ks))) * (0.45f + 0.55f * r2);
                float y = R * flat * (0.12f + 0.45f * r2 * (1f - Mathf.Exp(-tt / 6f))) + 0.05f * CapHeight(t) * r2 * flat;
                // boiling: lumps churn up and down as the skirt spreads
                y += R * 0.08f * turb * Mathf.Sin(t * (0.4f + 0.5f * r0) + r3 * 25f) * r2;
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad) + Lean(0f) * (0.5f + r2);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.6f, 0.8f, Mathf.Sin(theta) * 0.6f).normalized;
                Vector3 fireL = capPosL - (p + ground.localPosition);
                float prox = Mathf.Clamp01(2.5f * R / Mathf.Max(1f, fireL.magnitude));
                Color col = Shade(Color.Lerp(skirtDark, skirtLight, 0.25f + 0.25f * r3 * r2), nL, glow * prox * 1.4f, fireL.normalized);
                float size = R * (0.5f + 0.35f * r3) * (1f + 0.7f * (1f - Mathf.Exp(-tt / 8f))) * Mathf.Pow(Mathf.Max(0.3f, ks), 0.6f);
                skirt.Set(i, p, size, WithAlpha(col, 0.92f * vis), Billow(r3));
            }
            skirt.End(n);
        }

        // --- Base surge: dust (or spray, at sea) kicked up by the shock racing outward along the ground ---
        void UpdateSurge()
        {
            int n = surge.Begin();
            float rs = ShockRadius(t);
            float rg = Mathf.Sqrt(Mathf.Max(0f, rs * rs - hb * hb));
            float surgeMax = R * (2f + 8f * groundFactor) * Mathf.Lerp(1f, prof.skirtScale, 0.5f);
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
                Color col = Shade(surgeCol, nL, glow * 0.6f, fireL);
                float size = R * (0.38f + 0.25f * r3) * (1f + 0.6f * Mathf.Clamp01(rad / surgeMax));
                surge.Set(i, p, size, WithAlpha(col, a), Billow(r3));
            }
            surge.End(n);
        }

        /// <summary>Unspread cap scale: the stem follows the fireball's size, not the pancake's spread.</summary>
        float StemScale => capScale / Mathf.Max(0.3f, prof.capSpread);

        /// <summary>
        /// Stem radius at height fraction u (0 ground .. 1 cap): profile width with a flared, broadened base, and
        /// slowly shifting bulges and necks along its length so it isn't a uniform cylinder.
        /// </summary>
        float StemRadius(float u)
        {
            float b = 1f - u;
            float flare = 1f + prof.stemTaper * (2.5f * Mathf.Pow(b, 6f) + 0.6f * b * b);
            float irregular = 1f + prof.turbulence * (0.16f * Mathf.Sin(u * 9f + stemPhase + 0.05f * t) + 0.08f * Mathf.Sin(u * 23f - 0.11f * t + 2f * stemPhase));
            return 0.17f * StemScale * prof.stemWidth * flare * irregular;
        }

        float StemTop => Mathf.Max(R, hb + capPosL.y - 0.35f * capScale * Mathf.Min(capYS, 1.5f));

        /// <summary>Downwind lean of the stem at height fraction u: slow air at the base, the cap's drift at the top.</summary>
        Vector3 Lean(float u) => Vector3.Lerp(wind.Low(t) * 0.3f, capDrift, u);

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
            float yTop = StemTop;
            // trail hanging below the cap, lengthening quickly; dust column climbing to meet it
            float trailLen = yTop * (fromGround ? 0.25f + 0.45f * SS(start, start + 10f, t) : 0.45f);
            // the dust column is joined to the skirt from the start and races up to meet the trail
            float dustFront = fromGround ? yTop * Mathf.Lerp(0.3f, 1.05f, SS(start, start + 0.08f * riseTime + 4f, t)) : 0f;
            float flow = prof.rollSpeed / (0.5f * riseTime);
            float glow = Mathf.Pow(Glow(t), 0.6f);
            float vis = FadeOut * SS(start, start + 2f, t) * (fromGround ? 1f : 0.7f);
            float turb = prof.turbulence;
            for (int i = 0; i < n; i++)
            {
                float r0 = stem.r0[i], r1 = stem.r1[i], r2 = stem.r2[i], r3 = stem.r3[i];
                float u = Mathf.Repeat(r0 + t * flow * (0.7f + 0.6f * r3), 1f);
                float y = u * yTop;
                float b2 = (1f - u) * (1f - u);
                float theta = r1 * 2f * Mathf.PI + 0.15f * t * (1.3f - u) * turb;
                float stemR = StemRadius(u);
                // churning near the base: puffs surge in and out as the inflow swirls up
                float rad = stemR * (0.2f + 0.8f * Mathf.Sqrt(r2)) * (1f + 0.4f * turb * b2 * Mathf.Sin(t * (0.5f + r0) + r3 * 20f));
                // and the column snakes a little along its length
                Vector3 wiggle = new Vector3(Mathf.Sin(u * 7f + 0.1f * t + stemPhase), 0f, Mathf.Cos(u * 5f - 0.08f * t + stemPhase)) * (0.25f * stemR * turb);
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad) + wiggle + Lean(u);

                float shownDust = SS(dustFront, dustFront - 0.06f * yTop, y);
                float shownTrail = SS(yTop - trailLen, yTop - trailLen + 0.08f * yTop, y);
                // nothing fades out at the bottom, so the column stands on the skirt
                float a = Mathf.Max(shownDust, shownTrail) * vis;

                Vector3 nL = new Vector3(Mathf.Cos(theta), 0.25f, Mathf.Sin(theta)).normalized;
                Color baseCol = Color.Lerp(stemLow, stemHigh, 0.15f + u * 0.7f) * (0.9f + 0.2f * r2);
                Vector3 fireL = (capPosL - (p + ground.localPosition)).normalized;
                Color col = Shade(baseCol, nL, glow * (0.2f + 0.8f * u * u), fireL);
                float size = stemR * (1.1f + 0.7f * r3) * (1f + 0.35f * b2);
                stem.Set(i, p, size, WithAlpha(col, 0.85f * a), Billow(r3));
            }
            stem.End(n);
        }

        // --- Stem collars: smooth bell-shaped condensation skirts wrapped around the stem at a few heights.
        //     Each forms as the rising column pushes moist air through its condensation level, drifts up a little
        //     with the flow, then evaporates. ---
        void UpdateCollars()
        {
            int n = collars.Begin();
            int nc = Mathf.Max(1, prof.stemCollars);
            int per = Mathf.Max(1, n / nc);
            float start = hb / c + 0.25f * tauG;
            float yTop = StemTop;
            float glow = Mathf.Pow(Glow(t), 0.6f);
            for (int i = 0; i < n; i++)
            {
                int k = i % nc, j = i / nc;
                float frac = 0.22f + 0.6f * (k + 0.5f) / nc;
                // each forms in turn as the column climbs, and evaporates within seconds: one or two at a time
                float tk = start + (k + 0.5f) / nc * 0.45f * riseTime;
                float hold = 0.04f * riseTime + 3f;
                float vis = SS(tk, tk + 3f, t) * (1f - SS(tk + hold, tk + hold + 0.06f * riseTime + 3f, t)) * FadeOut;
                if (vis <= 0.001f)
                {
                    collars.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                float r0 = collars.r0[i], r1 = collars.r1[i], r2 = collars.r2[i], r3 = collars.r3[i];
                float age = Mathf.Max(0f, t - tk);
                float u = Mathf.Min(0.95f, frac + 0.06f * age / riseTime);
                float yk = u * yTop;
                float stemR = StemRadius(u);
                // bell: the outer edge droops below the attachment ring
                float out01 = r1;
                // a wide, thick, soft band of mist rather than a crisp ring
                float rr = stemR * (1f + (0.9f + 0.5f * SS(0f, 10f, age)) * out01);
                float theta = (j + r0 * 0.6f) / per * 2f * Mathf.PI + 0.02f * t * (k % 2 == 0 ? 1f : -1f);
                float y = yk - 0.3f * (rr - stemR) + (r2 - 0.5f) * 0.45f * stemR;
                Vector3 p = new Vector3(Mathf.Cos(theta) * rr, y, Mathf.Sin(theta) * rr) + Lean(u);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.7f, 0.7f, Mathf.Sin(theta) * 0.7f).normalized;
                Vector3 fireL = (capPosL - (p + ground.localPosition)).normalized;
                // sun-lit and a touch grey, not glowing white
                Color col = Shade(Color.Lerp(CondWhite, stemHigh, 0.25f) * 0.82f, nL, glow * 0.08f, fireL);
                float size = stemR * (0.9f + 0.5f * r3) * (1f + 0.5f * out01);
                collars.Set(i, p, size, WithAlpha(col, 0.2f * vis * (1f - 0.6f * out01)), r3 * 360f);
            }
            collars.End(n);
        }

        // --- Incandescent ejecta thrown out of a surface burst ---
        void UpdateSparks()
        {
            int n = sparks.Begin();
            float fade = (1f - SS(1.5f, 5f, t)) * airW;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = sparks.SphereDir(i);
                d.y = Mathf.Abs(d.y) * 0.85f + 0.15f;
                float dist = R * (0.9f + 1.6f * sparks.r1[i]) * (1f - Mathf.Exp(-t / 1.2f));
                float drop = 0.25f * R * (t / 3f) * (t / 3f);
                Vector3 p = d.normalized * dist - Vector3.up * drop;
                float size = R * 0.06f * (0.5f + sparks.r2[i]);
                sparks.Set(i, p, size, new Color(1f, 0.62f, 0.25f, fade * SS(0f, 0.05f, t)), 0f);
            }
            sparks.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // Water bursts (Crossroads Baker). The bubble vents through the surface as a hollow column of
        // water, crowned with a cauliflower of spray; the column falls back and the falling water
        // spreads out as a dense white base surge, while ring waves run out across the sea.
        // ----------------------------------------------------------------------------------------

        void UpdateWaterColumn()
        {
            int n = waterColumn.Begin();
            float top = ColTop(t);
            float slump = Collapse(t);
            slump *= slump;
            float vis = SS(0f, 0.3f, t) * (1f - SS(tCollapse + 0.6f * tFall, tCollapse + tFall + 10f, t)) * FadeWater;
            for (int i = 0; i < n; i++)
            {
                float r0 = waterColumn.r0[i], r1 = waterColumn.r1[i], r2 = waterColumn.r2[i], r3 = waterColumn.r3[i];
                float u = r0;
                float theta = r1 * 2f * Mathf.PI + 0.05f * t * (r2 - 0.5f);
                // the column slumps from the top down and sheds water outward as it falls
                float y = u * top * (1f - 0.9f * slump);
                float rad = colR * (0.85f + 0.25f * u + 0.15f * r2) * (1f + slump * (0.8f + 1.2f * (1f - u)));
                Vector3 wobble = new Vector3(Mathf.Sin(t * 0.7f + r0 * 40f), 0f, Mathf.Sin(t * 0.6f + r2 * 40f)) * (0.05f * colR);
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, y, Mathf.Sin(theta) * rad) + wobble;
                Vector3 nL = new Vector3(Mathf.Cos(theta), 0.3f, Mathf.Sin(theta)).normalized;
                Color col = Shade(Color.Lerp(new Color(0.76f, 0.79f, 0.83f), CondWhite, 0.4f + 0.6f * u), nL, 0f, Vector3.up);
                float size = colR * (0.45f + 0.25f * r3) * (1f + 0.4f * u) * (1f + slump);
                waterColumn.Set(i, p, size, WithAlpha(col, 0.85f * vis * SS(0f, 0.04f, u)), Billow(r3));
            }
            waterColumn.End(n);
        }

        void UpdateWaterSpray()
        {
            int n = waterSpray.Begin();
            float top = ColTop(t);
            float grow = 1f - Mathf.Exp(-t / (1.5f * tauCol));
            float slump = Collapse(t);
            Vector3 centre = new Vector3(0f, top * (1f - 0.9f * slump * slump), 0f);
            float vis = SS(0.2f, 1f, t) * (1f - SS(tCollapse + 0.3f * tFall, tCollapse + tFall + 5f, t)) * FadeWater;
            for (int i = 0; i < n; i++)
            {
                float r0 = waterSpray.r0[i], r1 = waterSpray.r1[i], r3 = waterSpray.r3[i];
                Vector3 d = waterSpray.SphereDir(i);
                d.y = Mathf.Abs(d.y) * 0.8f + 0.1f;
                d.Normalize();
                // some puffs ride out on spray jets, giving the cauliflower its ragged plumes
                float jet = r0 < 0.35f ? 1.6f : 1f;
                float rad = colR * (0.6f + 1.3f * grow) * (0.5f + 0.5f * Mathf.Sqrt(r1)) * jet * (1f + 0.8f * slump);
                Vector3 p = centre + d * rad - Vector3.up * (slump * 0.3f * colR * r0);
                Color col = Shade(CondWhite, d, 0f, Vector3.up);
                float size = colR * (0.5f + 0.35f * r3) * (0.5f + 0.8f * grow) * (1f + slump);
                waterSpray.Set(i, p, size, WithAlpha(col, 0.85f * vis), Billow(r3));
            }
            waterSpray.End(n);
        }

        void UpdateWaterSurge()
        {
            int n = waterSurge.Begin();
            float ts = tCollapse + 0.25f * tFall;
            float tt = Mathf.Max(0f, t - ts);
            float radMax = R * (6f + 8f * depthF) * Mathf.Sqrt(Mathf.Max(0.3f, prof.skirtScale));
            float radS = colR + (radMax - colR) * (1f - Mathf.Exp(-tt / (25f * yieldTimeScale)));
            float hS = R * (0.5f + 0.9f * depthF) * (0.4f + 0.6f * (1f - Mathf.Exp(-tt / 10f)));
            float vis = SS(ts, ts + 3f, t) * (1f - SS(lifeWater * 0.6f, lifeWater - 5f, t)) * FadeWater;
            for (int i = 0; i < n; i++)
            {
                float r0 = waterSurge.r0[i], r1 = waterSurge.r1[i], r2 = waterSurge.r2[i], r3 = waterSurge.r3[i];
                float theta = r1 * 2f * Mathf.PI + 0.01f * tt * (r0 - 0.5f);
                bool front = r0 < 0.6f;
                float radial, y;
                if (front)
                {
                    // the leading edge rolls over itself as it advances
                    float phi = r3 * 2f * Mathf.PI - tt * 0.15f * prof.rollSpeed;
                    radial = radS * (0.9f + 0.08f * r2) + Mathf.Cos(phi) * 0.15f * hS;
                    y = hS * (0.5f + 0.45f * Mathf.Sin(phi));
                }
                else
                {
                    radial = radS * Mathf.Sqrt(r2) * 0.9f;
                    y = hS * 0.35f * r3;
                }
                Vector3 p = new Vector3(Mathf.Cos(theta) * radial, Mathf.Max(0.05f * R, y), Mathf.Sin(theta) * radial);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.6f, 0.8f, Mathf.Sin(theta) * 0.6f).normalized;
                Color col = Shade(CondWhite * 0.95f, nL, 0f, Vector3.up);
                float size = R * (0.5f + 0.3f * r3) * (1f + 0.6f * SS(0f, 40f, tt)) * Mathf.Max(1f, Mathf.Sqrt(radMax / (14f * R)));
                waterSurge.Set(i, p, size, WithAlpha(col, 0.75f * vis * (front ? 1f : 0.6f)), Billow(r3));
            }
            waterSurge.End(n);
        }

        // --- Ring waves running out across the sea surface from the burst point ---
        void UpdateRingWaves()
        {
            int n = ringWave.Begin();
            const int waves = 3;
            int per = Mathf.Max(1, n / waves);
            float r0Wave = underwater ? colR : R;
            for (int i = 0; i < n; i++)
            {
                int k = i % waves, j = i / waves;
                float age = t - (1.5f + 7f * k);
                if (age <= 0f)
                {
                    ringWave.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                float fade = SS(0f, 2f, age) * (1f - SS(30f, 90f, age)) / (1f + age / 30f);
                float rw = r0Wave + R * 0.35f * age;
                float theta = (j + ringWave.r0[i] * 0.6f) / per * 2f * Mathf.PI;
                float rr = rw * (1f + (ringWave.r1[i] - 0.5f) * 0.03f);
                Vector3 p = new Vector3(Mathf.Cos(theta) * rr, 0.02f * R, Mathf.Sin(theta) * rr);
                Color col = Shade(CondWhite * 0.9f, Vector3.up, 0f, Vector3.up);
                float size = R * (0.25f + 0.15f * ringWave.r3[i]) * (1f + age / 40f) * Mathf.Max(1f, rw / (8f * R));
                ringWave.Set(i, p, size, WithAlpha(col, 0.45f * fade * (waterFx ? FadeWater : 1f) * (k == 0 ? 1f : 0.7f)), ringWave.r2[i] * 360f);
            }
            ringWave.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // High-altitude burst (Teak, Orange). The air is too thin to stop the X-rays close in, so the fireball
        // grows far bigger and glows red (excited oxygen) instead of white. It floats up fast as one great
        // bubble with no stem or mushroom, and a red airglow ring spreads outward below it.
        // ----------------------------------------------------------------------------------------

        void UpdateHiFireball()
        {
            int n = hiFire.Begin();
            float grow = HiGrow(t) * (1f - Mathf.Exp(-t / 0.3f));
            float g = HiGlow(t);
            float a = SS(0f, 0.05f, t) * FadeHi;
            Vector3 c0 = new Vector3(0f, HiRise(t), 0f);
            float flashW = Mathf.Exp(-t / 0.8f);
            float turb = prof.turbulence;
            for (int i = 0; i < n; i++)
            {
                float r0 = hiFire.r0[i], r1 = hiFire.r1[i], r2 = hiFire.r2[i], r3 = hiFire.r3[i];
                bool core = r3 < 0.3f;
                Vector3 d = hiFire.SphereDir(i);
                float rad = Rh * grow * (core ? 0.45f * Mathf.Sqrt(r0) : 0.6f + 0.4f * Mathf.Pow(r0, 0.33f));
                // a rising bubble: flattened underside, slightly drawn-out top, slowly churning
                Vector3 p = d * rad;
                p.y *= p.y < 0f ? 0.7f : 1.1f;
                p += new Vector3(Mathf.Sin(t * 0.4f + r0 * 30f), Mathf.Sin(t * 0.35f + r1 * 30f), Mathf.Sin(t * 0.45f + r2 * 30f)) * (0.07f * Rh * grow * turb);
                Color col = core
                    ? Color.Lerp(new Color(1f, 0.3f, 0.15f), new Color(1f, 0.75f, 0.4f), g)
                    : Color.Lerp(new Color(0.75f, 0.08f, 0.1f), new Color(1f, 0.32f, 0.2f), g);
                col = Color.Lerp(col, Color.white, flashW);
                float alpha = a * (core ? 0.35f : 0.18f) * (0.4f + 0.6f * g);
                float size = Rh * grow * (core ? 0.6f : 0.55f + 0.3f * r3);
                hiFire.Set(i, c0 + p, size, WithAlpha(col, alpha), r3 * 360f + t * 3f);
            }
            hiFire.End(n);
        }

        void UpdateAirglow()
        {
            int n = airglow.Begin();
            float rr = Mathf.Min(Rh * (1.2f + 0.3f * t), 40f * Rh);
            float a = FadeHi * SS(0.3f, 2.5f, t) * (1f - SS(0.35f * hiLife, hiLife, t)) * (1f - SS(25f * Rh, 40f * Rh, rr));
            for (int i = 0; i < n; i++)
            {
                float r0 = airglow.r0[i], r1 = airglow.r1[i], r2 = airglow.r2[i], r3 = airglow.r3[i];
                float theta = (i + r0 * 0.7f) / n * 2f * Mathf.PI;
                float rad = rr * (1f + (r1 - 0.5f) * 0.08f);
                Vector3 p = new Vector3(Mathf.Cos(theta) * rad, -0.35f * Rh + (r2 - 0.5f) * 0.05f * rr, Mathf.Sin(theta) * rad);
                Color col = Color.Lerp(new Color(1f, 0.16f, 0.12f), new Color(0.85f, 0.12f, 0.3f), r3 * 0.5f);
                float shimmer = 0.6f + 0.4f * Mathf.Sin(theta * 3f + t * 0.2f + r3 * 6f);
                airglow.Set(i, p, 0.06f * rr + 0.6f * Rh, WithAlpha(col, 0.14f * a * shimmer), r3 * 360f);
            }
            airglow.End(n);
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
            float a = SS(0f, 0.03f, t) * Mathf.Pow(1f - x, 1.4f) * FadeVac;
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
            int m = shellLimb.Begin();
            for (int i = 0; i < m; i++) shellLimb.Set(i, Vector3.zero, 2.15f * rv, WithAlpha(limb, a * 0.4f), 0f);
            shellLimb.End(m);
        }

        void UpdateFragments()
        {
            int n = fragments.Begin();
            for (int i = 0; i < n; i++)
            {
                float speed = vDebris * (0.25f + 0.75f * fragments.r1[i]);
                float fragLife = 4f + 6f * fragments.r2[i];
                float x = Mathf.Clamp01(t / fragLife);
                Vector3 p = fragments.SphereDir(i) * (R * 0.2f + speed * t);
                Color col = Color.Lerp(new Color(1f, 0.95f, 0.85f), new Color(1f, 0.35f, 0.1f), Mathf.Sqrt(x));
                float size = R * (0.04f + 0.05f * fragments.r3[i]) + speed * t * 0.004f;
                fragments.Set(i, p, size, WithAlpha(col, (1f - x) * SS(0f, 0.02f, t) * vacW), 0f);
            }
            fragments.End(n);
        }

        // Faint, slowly dispersing cloud of ionised gas left behind the shell.
        void UpdateIonCloud()
        {
            int n = ionCloud.Begin();
            float rc = R * 2f + 0.06f * vDebris * t;
            float a = SS(0.5f, 2f, t) * (1f - SS(ShellLife, lifeVac - 8f, t)) * FadeVac;
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
        // At high altitude the thin air slows it and it glows as red/green auroral streamers instead.
        void UpdateStreamers()
        {
            int n = streamers.Begin();
            Vector3 side = Vector3.Cross(fieldL, Mathf.Abs(fieldL.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            Vector3 side2 = Vector3.Cross(fieldL, side);
            float vacShare = vacW / Mathf.Max(1e-3f, vacW + hiW);
            float v = StreamerSpeed;
            float streamLife = Mathf.Lerp(lifeHi, lifeVac, vacShare);
            float shellLife = Mathf.Lerp(hiLife * 0.5f, ShellLife, vacShare);
            float w = vacFx ? FadeVac + 0.8f * FadeHi : 0.8f * FadeHi;
            float a = SS(0.1f, 0.6f, t) * (1f - SS(shellLife * 1.5f, streamLife * 0.7f, t)) * w;
            float scale = hiFx && !vacFx ? Rh : R;
            for (int i = 0; i < n; i++)
            {
                float sign = streamers.r0[i] < 0.5f ? -1f : 1f;
                float frac = streamers.r1[i];
                float dist = (scale + 1.6f * v * t) * (0.15f + 0.85f * frac);
                float spiralR = dist * 0.06f * (0.3f + streamers.r2[i]);
                float ang = streamers.r3[i] * 2f * Mathf.PI + dist / Mathf.Max(1f, scale * 3f) - t * 0.8f;
                Vector3 p = fieldL * (sign * dist) + (side * Mathf.Cos(ang) + side2 * Mathf.Sin(ang)) * spiralR;
                Color vacCol = Color.Lerp(new Color(0.65f, 0.4f, 1f), new Color(1f, 0.25f, 0.35f), frac);
                Color hiCol = Color.Lerp(new Color(0.45f, 1f, 0.5f), new Color(1f, 0.2f, 0.18f), frac);
                Color col = Color.Lerp(hiCol, vacCol, vacShare);
                // big, faint, overlapping glows read as a continuous stream rather than a line of dots
                float size = scale * 1.2f + dist * 0.07f;
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
            float a = SS(tA, tA + 3f, t) * (1f - SS(lifeVac - 25f, lifeVac - 2f, t)) * (vacW + hiW);
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
            float airSound = airW + 0.4f * hiW;

            if (airSound < 0.25f)
            {
                // No air, no sound. The only thing you'd hear is debris hitting your own hull,
                // so play a heavily muffled thump if the shell passes the camera while it is still dense.
                if (!nearShakeDone && camDist < ShellRadius(t) && camDist < 25f * R)
                {
                    nearShakeDone = true;
                    float k = Mathf.Clamp01(6f * R / Mathf.Max(camDist, R));
                    lowPass.cutoffFrequency = 320f;
                    if (crackSrc.clip != null && NukeFXManager.Instance.TryClaimBlastSound())
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
            vol = Mathf.Max(vol, 0.08f) * s.volume * GameSettings.SHIP_VOLUME * Mathf.Clamp01(airSound);
            lowPass.cutoffFrequency = Mathf.Lerp(22000f, 700f, Mathf.Clamp01(camDist / (40f * R)));
            // a salvo arriving together plays one crack/rumble, not a stack of identical ones
            if (NukeFXManager.Instance.TryClaimBlastSound())
            {
                // the sharp crack carries badly: far off it's mostly the low rolling rumble that arrives
                float sharp = Mathf.Lerp(1f, 0.35f, SS(10f * R, 60f * R, camDist));
                if (crackSrc.clip != null) { crackSrc.volume = vol * sharp; crackSrc.Play(); }
                if (rumbleSrc.clip != null) { rumbleSrc.volume = vol * 0.9f; rumbleSrc.Play(); }
                if (boomSrc.clip != null) { boomSrc.volume = vol * 0.8f * sharp; boomSrc.Play(); }
                // inside the strong-overpressure zone the shock is followed by a roaring blast wind and afterwind
                float windK = 1f - SS(3f * R, 12f * R, camDist);
                if (roarSrc.clip != null && windK > 0.02f) { roarSrc.volume = vol * windK; roarSrc.Play(); }
            }

            NukeFXManager.Instance.AddShake(closeness * airSound, 1.5f + 3f * closeness);
            NukeFXManager.Instance.AddShake(0.2f * Mathf.Max(closeness, 0.15f) * airSound, 12f);
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
            float reach = 10f * R * airW + 25f * R * hiW + 60f * R * vacW;
            // the eye (or camera) stops down after the first instants, so a megaton's seconds-long thermal pulse
            // reads as a blinding glare rather than seconds of solid white screen
            float adapt = Mathf.Lerp(1f, 0.4f, SS(0.3f, 4f, t));
            flashAmount = FlashIntensity(t) * adapt * Mathf.Clamp01(Mathf.Pow(reach / (d + 1f), Mathf.Lerp(1.2f, 0.8f, vacW))) * vis;
            if (airFx) tintAmount = 0.35f * airW * Mathf.Pow(Glow(t), 0.8f) * Mathf.Clamp01(15f * R / (d + 1f)) * vis;
            if (hiFx) tintAmount = Mathf.Max(tintAmount, 0.2f * hiW * HiGlow(t) * Mathf.Clamp01(15f * Rh / (d + 1f)) * vis);
        }

        /// <summary>Photo mode: blast sounds play at the effects rate and pause while the effects are frozen.</summary>
        void UpdateFxRateAudio()
        {
            float rate = NukeFXManager.FxRate;
            if (rate == audioRate) return;
            bool freeze = rate <= 0f, wasFrozen = audioRate <= 0f;
            audioRate = rate;
            FxRateAudio(crackSrc, rate, freeze, wasFrozen);
            FxRateAudio(rumbleSrc, rate, freeze, wasFrozen);
            FxRateAudio(boomSrc, rate, freeze, wasFrozen);
        }

        float audioRate = 1f;

        static void FxRateAudio(AudioSource a, float rate, bool freeze, bool wasFrozen)
        {
            if (a == null) return;
            if (freeze) { if (a.isPlaying) a.Pause(); }
            else
            {
                a.pitch = rate;
                if (wasFrozen) a.UnPause();
            }
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
            readonly Vector3 streakVel;
            bool spawned;
            /// <summary>A billowy smoke layer (vs. a glow): gets the far-distance softening.</summary>
            public readonly bool soft;
            /// <summary>Per-frame multipliers the owner sets for distance and haze.</summary>
            public float sizeMul = 1f, alphaMul = 1f;
            float simRate = 1f;

            /// <param name="stretch">If above 0, particles are drawn as vertical streaks this many sizes long (rain-out).</param>
            public Layer(Transform parent, string name, Material mat, int n, int seed, bool hierarchyScale, float stretch = 0f)
            {
                count = n;
                soft = mat == NukeAssets.Smoke;
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
                if (stretch > 0f)
                {
                    // stretched along the (local, downward) velocity; the length comes from lengthScale alone
                    rend.renderMode = ParticleSystemRenderMode.Stretch;
                    rend.lengthScale = stretch;
                    rend.velocityScale = 0f;
                    streakVel = Vector3.down * 0.01f;
                }
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

            public int Count => count;

            /// <summary>Hides (or shows) the whole layer without touching its particles.</summary>
            public void SetVisible(bool on)
            {
                var r = ps.GetComponent<ParticleSystemRenderer>();
                if (r.enabled != on) r.enabled = on;
            }

            public int Begin()
            {
                // photo mode: the particles are placed analytically, but keep Unity's own clock in step too
                float rate = NukeFXManager.ParticleRate;
                if (rate != simRate) { simRate = rate; var main = ps.main; main.simulationSpeed = rate; }
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
                buf[i].velocity = streakVel;
                buf[i].startSize = Mathf.Max(0f, size * sizeMul);
                col.a *= alphaMul;
                buf[i].startColor = col;
                buf[i].rotation = rotDeg;
                buf[i].remainingLifetime = 100000f;
            }

            public void End(int n) { ps.SetParticles(buf, n); }
        }
    }
}
