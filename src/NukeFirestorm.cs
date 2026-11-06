using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A firestorm after a big burst over land (setting firestorm): within the thermal ignition radius (~1.6 km *
    /// W^0.41, Kerbin-scaled) scattered fires break out from a few seconds to a minute after the flash, grow until
    /// they run together, and send up smoke columns that lean downwind and draw in toward one big column over the
    /// middle, for several minutes, then die down. A pooled set of at most 40 fires, each a few flame billboards
    /// and a smoke column, drawn with the plugin's own particle layers; it stops drawing beyond a few tens of km.
    /// Runs on the effects clock (NukeFXManager.FxTime), so photo mode slows and freezes it with the cloud.
    /// </summary>
    internal class NukeFirestorm : MonoBehaviour
    {
        const int MaxFires = 40;
        const int FlamesPerFire = 4;
        const int PuffsPerColumn = 16;
        const float SmokeRiseTime = 80f;   // s for a smoke puff to climb its column

        CelestialBody body;
        double lat, lon, groundAlt;
        double spawnTime;
        float delay, life, rG, t, daylight;
        NukeWind wind;
        NukeSettings s;

        int n;
        Vector3[] pos;
        float[] ignite, size, colH;
        Vector3 centre;
        NukeCinematic.Layer flames, glow, smoke;
        Light light;
        bool hidden;

        /// <summary>Thermal ignition radius (m): ~1.6 km * W^0.41, scaled for Kerbin like the other blast radii.</summary>
        public static float IgnitionRadius(float W, float sizeScale) => 0.45f * 1600f * Mathf.Pow(Mathf.Max(W, 0.001f), 0.41f) * sizeScale;

        public static int BaseParticles() => MaxFires * (FlamesPerFire + 1 + PuffsPerColumn);

        public static void Spawn(CelestialBody body, double lat, double lon, double groundAlt, float W, float R, float hb,
            NukeWind wind, double spawnTime, float delay)
        {
            var s = NukeSettings.Current;
            float rT = IgnitionRadius(W, s.sizeScale);
            // the flash has to reach the ground hard enough: on the ground out to sqrt(rT^2 - hb^2)
            float g = rT * rT - hb * hb;
            if (g <= 0f) return;
            float rGround = Mathf.Sqrt(g);
            if (rGround < 0.3f * rT || rGround < 300f) return;
            var go = new GameObject("BDNukes_Firestorm");
            go.AddComponent<NukeFirestorm>().Setup(body, lat, lon, groundAlt, W, R, rGround, wind, spawnTime, delay);
        }

        void Setup(CelestialBody b, double la, double lo, double gAlt, float W, float R, float rGround, NukeWind w, double sTime, float d)
        {
            s = NukeSettings.Current;
            body = b; lat = la; lon = lo; groundAlt = gAlt;
            wind = w; spawnTime = sTime; delay = d; rG = rGround;
            life = s.firestormDuration * Mathf.Clamp(Mathf.Pow(W / 100f, 0.1f), 0.7f, 1.6f);
            Anchor();

            var rng = new System.Random(Random.Range(0, 100000));
            n = Mathf.Clamp(Mathf.RoundToInt(MaxFires * s.particleDensity), 8, MaxFires);
            pos = new Vector3[n];
            ignite = new float[n];
            size = new float[n];
            colH = new float[n];
            // each fire covers its share of the burning area; grown to full size they overlap and merge
            float spacing = rG * Mathf.Sqrt(Mathf.PI / n);
            float r0 = Mathf.Min(1.2f * R, 0.25f * rG);
            int land = 0;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                bool water = true;
                Vector3 gp = Vector3.zero;
                float r = 0f;
                for (int tries = 0; tries < 5 && water; tries++)
                {
                    r = Mathf.Lerp(r0, rG, Mathf.Sqrt((float)rng.NextDouble()));
                    float a = (float)rng.NextDouble() * 2f * Mathf.PI;
                    gp = GroundPoint(Mathf.Cos(a) * r, Mathf.Sin(a) * r, out water);
                }
                pos[i] = gp;
                size[i] = water ? 0f : 0.55f * spacing * (0.7f + 0.6f * (float)rng.NextDouble());
                // close in catches first (hotter flash); the outer fires take up to a minute to get going
                ignite[i] = 2f + 58f * Mathf.Pow(r / rG, 1.5f) * (0.5f + 0.5f * (float)rng.NextDouble());
                colH[i] = Mathf.Clamp(0.8f * rG, 1500f, 12000f) * (0.7f + 0.5f * (float)rng.NextDouble());
                if (!water) { land++; sum += gp; }
            }
            if (land < 3)
            {
                Destroy(gameObject);
                return;
            }
            centre = sum / land;
            int seed = rng.Next(0, 100000);
            flames = new NukeCinematic.Layer(transform, "FirestormFlames", NukeAssets.Glow, n * FlamesPerFire, seed, false);
            glow = new NukeCinematic.Layer(transform, "FirestormGlow", NukeAssets.Glow, n, seed + 1, false);
            smoke = new NukeCinematic.Layer(transform, "FirestormSmoke", NukeAssets.Smoke, n * PuffsPerColumn, seed + 2, false);

            var lightGo = new GameObject("FirestormLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = centre + new Vector3(0f, Mathf.Clamp(0.08f * rG, 50f, 2000f), 0f);
            light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.5f, 0.2f);
            light.range = 2f * rG;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            NukeLightBudget.Register(light);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            Debug.Log("[BDNukes]: Firestorm: " + land + " fires out to " + rG.ToString("F0") + " m, " + life.ToString("F0") + " s.");
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy() { GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange); }

        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        void Anchor()
        {
            Vector3 p = body.GetWorldSurfacePosition(lat, lon, groundAlt);
            transform.position = p;
            Vector3 up = (p - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        Vector3 GroundPoint(float x, float z, out bool water)
        {
            Vector3 w = transform.TransformPoint(new Vector3(x, 0f, z));
            double la = body.GetLatitude(w), lo = body.GetLongitude(w);
            double g = body.pqsController != null ? body.TerrainAltitude(la, lo, true) : groundAlt;
            water = body.ocean && g < 0d;
            if (water) g = 0d;
            return transform.InverseTransformPoint(body.GetWorldSurfacePosition(la, lo, g));
        }

        /// <summary>How hard fire i burns now (0..1): catches, spreads, holds, then burns out over the last half.</summary>
        float Level(int i)
        {
            float ign = t - ignite[i];
            if (ign <= 0f || size[i] <= 0f) return 0f;
            return SS(0f, 15f, ign) * (1f - SS(0.45f * life, life, t)) * Mathf.Lerp(1f, 0.5f, t / life);
        }

        /// <summary>A fire's size: it starts as a few blazes and spreads to meet its neighbours over a minute or two.</summary>
        float Grown(int i) => size[i] * Mathf.Lerp(0.25f, 1f, SS(0f, 100f, t - ignite[i]));

        void LateUpdate()
        {
            if (body == null || FlightGlobals.currentMainBody != body)
            {
                Destroy(gameObject);
                return;
            }
            t = (float)(NukeFXManager.FxTime - spawnTime - delay);
            if (t > life)
            {
                Destroy(gameObject);
                return;
            }
            Anchor();
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float camDist = cam != null ? (cam.transform.position - transform.position).magnitude : 0f;
            bool cull = t < 0f || camDist > Mathf.Max(80000f, 8f * rG);
            if (cull != hidden)
            {
                hidden = cull;
                flames.SetVisible(!cull);
                glow.SetVisible(!cull);
                smoke.SetVisible(!cull);
                light.enabled = !cull;
            }
            if (cull) return;

            Vector3 sunW = Planetarium.fetch != null && Planetarium.fetch.Sun != null
                ? ((Vector3)(Planetarium.fetch.Sun.position) - transform.position).normalized
                : Vector3.up;
            daylight = Mathf.Clamp01(Vector3.Dot(sunW, transform.up) * 3f + 0.3f);
            float night = Mathf.Lerp(1f, 0.35f, daylight);
            UpdateFlames(night);
            UpdateSmoke(night);
        }

        void UpdateFlames(float night)
        {
            int m = flames.Begin();
            int mg = glow.Begin();
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                float lv = Level(i);
                total += lv;
                float sz = Grown(i);
                for (int k = 0; k < FlamesPerFire; k++)
                {
                    int j = i * FlamesPerFire + k;
                    if (j >= m) break;
                    if (lv <= 0.005f) { flames.Set(j, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f); continue; }
                    // a few blazes spread over the fire's patch, each flickering on its own
                    float ang = flames.r0[j] * 2f * Mathf.PI, rr = Mathf.Sqrt(flames.r1[j]) * 0.45f * sz;
                    float flick = 0.7f + 0.3f * Mathf.Sin(t * (5f + 3f * flames.r2[j]) + j) * Mathf.Sin(t * (1.9f + flames.r3[j]) + 2f * j);
                    float fs = sz * (0.35f + 0.25f * flames.r2[j]) * (0.85f + 0.15f * flick);
                    Vector3 p = pos[i] + new Vector3(Mathf.Cos(ang) * rr, 0.25f * fs, Mathf.Sin(ang) * rr);
                    flames.Set(j, p, fs, new Color(1f, 0.62f + 0.12f * flames.r3[j], 0.26f, lv * flick * (0.45f + 0.55f * night)), flames.r3[j] * 360f);
                }
                if (i < mg)
                {
                    // the low red glow under the fire, lighting its own smoke at night
                    if (lv <= 0.005f) glow.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    else glow.Set(i, pos[i] + new Vector3(0f, 0.1f * sz, 0f), 1.6f * sz, new Color(1f, 0.28f, 0.08f, 0.22f * lv * night), 0f);
                }
            }
            flames.End(m);
            glow.End(mg);
            float avg = total / Mathf.Max(1, n);
            light.intensity = 2.2f * avg * night * (0.85f + 0.15f * Mathf.Sin(t * 5f));
        }

        void UpdateSmoke(float night)
        {
            int m = smoke.Begin();
            Vector3 dir = wind != null ? wind.Dir(t) : Vector3.zero;
            float lowS = wind != null ? wind.lowSpeed : 0f, hiS = wind != null ? wind.hiSpeed : 0f;
            float light = 0.38f + 0.55f * daylight;
            for (int i = 0; i < m; i++)
            {
                int c = i / PuffsPerColumn;
                float r0 = smoke.r0[i], r1 = smoke.r1[i], r2 = smoke.r2[i], r3 = smoke.r3[i];
                float lv = c < n ? Level(c) : 0f;
                if (lv <= 0.01f)
                {
                    smoke.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                float u = Mathf.Repeat(r0 + t / SmokeRiseTime * (0.8f + 0.4f * r3), 1f);
                float sz = Grown(c);
                float h = colH[c];
                float psize = Mathf.Max(60f, sz * 0.9f) * (0.5f + 1.8f * u) * (0.8f + 0.4f * r2);
                // a tall column has only a handful of puffs: they must overlap, or from afar it is a dotted line
                float spacing = h / PuffsPerColumn;
                float minSize = 2.4f * spacing * (0.6f + 0.8f * u) * (0.8f + 0.4f * r2);
                float thin = psize < minSize ? Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(minSize / Mathf.Max(1f, psize) / 6f)) : 1f;
                psize = Mathf.Max(psize, minSize);
                float ang = r1 * 2f * Mathf.PI;
                // the columns are drawn in toward the middle as they climb (the firestorm's own updraft), and the
                // puff at height u left the fire u * SmokeRiseTime ago, so the wind there has carried it that far
                Vector3 basePos = Vector3.Lerp(pos[c], centre, 0.6f * u * u);
                Vector3 drift = dir * (Mathf.Lerp(lowS, hiS, u) * u * SmokeRiseTime);
                Vector3 p = basePos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (0.3f * psize * u)
                            + new Vector3(0f, u * h + 0.2f * sz, 0f) + drift;
                Color col = new Color(0.17f, 0.16f, 0.15f) * light + new Color(1f, 0.4f, 0.12f) * (0.5f * lv * Mathf.Pow(1f - u, 3f) * night);
                col.a = Mathf.Clamp01(0.6f * thin * lv * SS(0f, 0.06f, u) * (1f - SS(0.65f, 1f, u)));
                smoke.Set(i, p, psize, col, r3 * 360f);
            }
            smoke.End(m);
        }
    }
}
