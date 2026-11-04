using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Debris smoke trails from surface and near-surface bursts on land: chunks of ground thrown out of the
    /// crater on ballistic arcs, each trailing dark grey-brown smoke, with a glowing orange-white head for the
    /// first second or two.
    ///
    /// Like the rest of the effect, the particle systems are pure renderers (NukeCinematic.Layer): every
    /// projectile's path is worked out analytically in a frame anchored to ground zero (lat/lon/alt, north
    /// locked), so it stays put through floating-origin and Krakensbane shifts. A trail is a string of puffs,
    /// one dropped every so often along the path; each grows, drifts up a little and fades with its own age.
    /// A projectile dies when it reaches the terrain (CelestialBody.TerrainAltitude) or runs out of flight time.
    /// </summary>
    internal class NukeDebrisTrails : MonoBehaviour
    {
        const int PuffsPerTrail = 28;          // base count, scaled by particle density / LOD / budget
        const float PuffFade = 16f;            // s for a trail puff to grow and fade away

        static readonly Color SmokeDark = new Color(0.17f, 0.15f, 0.13f);
        static readonly Color SmokeBrown = new Color(0.33f, 0.27f, 0.21f);
        static readonly Color HotCore = new Color(1f, 0.9f, 0.7f);
        static readonly Color HotHalo = new Color(1f, 0.48f, 0.14f);

        CelestialBody body;
        double lat, lon, groundAlt;
        float startTime, life, t, daylight;

        // per projectile
        int n, puffs;
        Vector3[] origin, vel;
        float[] tLaunch, tMaxFlight, tDeath, puffSize, glowTau;
        Vector3 gravL;

        NukeCinematic.Layer smoke, heads;

        /// <summary>Projectiles for a yield: 12 for a kiloton or less, up to 40 for megatons.</summary>
        static int Count(float W) => Mathf.Clamp(Mathf.RoundToInt(12f * Mathf.Pow(Mathf.Max(W, 0.001f), 0.2f)), 12, 40);

        /// <summary>Unscaled particle cost, for NukeCinematic's maxParticles budget.</summary>
        public static int BaseParticles(float W) => Count(W) * (PuffsPerTrail + 2);

        /// <summary>Land bursts in air within about a fireball radius of the ground.</summary>
        public static bool Wanted(NukeSettings s, CelestialBody body, bool airFx, bool overOcean, float hb, float R)
            => s.debrisTrails && airFx && !overOcean && body.atmosphere && hb < R;

        /// <param name="groundAlt">Ground altitude under the burst (m ASL).</param>
        /// <param name="startTime">Time.time of the detonation (after any fuze delay).</param>
        /// <param name="particleScale">Particle density x burst LOD x the detonation's budget.</param>
        public static void Spawn(CelestialBody body, double lat, double lon, double groundAlt, float W, float R,
            float startTime, float particleScale)
        {
            var go = new GameObject("BDNukes_DebrisTrails");
            try
            {
                go.AddComponent<NukeDebrisTrails>().Setup(body, lat, lon, groundAlt, W, R, startTime, particleScale);
            }
            catch
            {
                Destroy(go);
                throw;
            }
        }

        void Setup(CelestialBody b, double la, double lo, double gAlt, float W, float R, float start, float particleScale)
        {
            var s = NukeSettings.Current;
            body = b; lat = la; lon = lo; groundAlt = gAlt; startTime = start;
            Anchor();

            n = Count(W);
            puffs = Mathf.Clamp(Mathf.RoundToInt(PuffsPerTrail * particleScale), 8, 60);
            // never more than a quarter of the detonation's particle cap
            int cap = Mathf.Max(n * 10, s.maxParticles / 4);
            if (n * (puffs + 2) > cap) puffs = Mathf.Max(8, cap / n - 2);

            double rad = body.Radius + gAlt;
            float g = (float)(body.gravParameter / (rad * rad));
            gravL = new Vector3(0f, -g, 0f);

            origin = new Vector3[n];
            vel = new Vector3[n];
            tLaunch = new float[n];
            tMaxFlight = new float[n];
            tDeath = new float[n];
            puffSize = new float[n];
            glowTau = new float[n];
            // the fireball reaches full size in tMax: debris bursts out of it over that time
            float tBurst = Mathf.Clamp(0.0417f * Mathf.Pow(W, 0.44f), 0.03f, 2f);
            var rng = new System.Random(Random.Range(0, 100000));
            float last = 0f;
            for (int i = 0; i < n; i++)
            {
                float az = (float)rng.NextDouble() * 2f * Mathf.PI;
                // elevation 25-72 degrees: a wide cone, no grazing throws and none straight up
                float elev = Mathf.Lerp(25f, 72f, (float)rng.NextDouble()) * Mathf.Deg2Rad;
                // speed for a flat-ground range of 2-6 R, more of them short
                float range = R * Mathf.Lerp(2f, 6f, Mathf.Pow((float)rng.NextDouble(), 1.4f));
                float v = Mathf.Sqrt(range * g / Mathf.Max(0.2f, Mathf.Sin(2f * elev)));
                Vector3 hor = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
                vel[i] = hor * (v * Mathf.Cos(elev)) + Vector3.up * (v * Mathf.Sin(elev));
                origin[i] = hor * (0.3f * R * (float)rng.NextDouble()) + Vector3.up * (0.05f * R);
                tLaunch[i] = tBurst * (0.3f + 0.7f * (float)rng.NextDouble());
                tMaxFlight[i] = Mathf.Lerp(6f, 12f, (float)rng.NextDouble());
                tDeath[i] = tMaxFlight[i];
                puffSize[i] = Mathf.Clamp(0.05f * R, 4f, 80f) * (0.7f + 0.6f * (float)rng.NextDouble());
                glowTau[i] = 0.8f + 1.4f * (float)rng.NextDouble();
                last = Mathf.Max(last, tLaunch[i] + tMaxFlight[i]);
            }
            life = last + PuffFade + 1f;

            int seed = rng.Next(100000);
            smoke = new NukeCinematic.Layer(transform, "DebrisSmoke", NukeAssets.Smoke, n * puffs, seed, false);
            heads = new NukeCinematic.Layer(transform, "DebrisHeads", NukeAssets.Glow, n * 2, seed + 1, false);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
        }

        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        void Anchor()
        {
            Vector3 pos = body.GetWorldSurfacePosition(lat, lon, groundAlt);
            transform.position = pos;
            Vector3 up = (pos - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        Vector3 PathAt(int i, float tau) => origin[i] + vel[i] * tau + 0.5f * tau * tau * gravL;

        /// <summary>True if the local point is at or under the ground (or the sea surface).</summary>
        bool UnderGround(Vector3 local)
        {
            Vector3 w = transform.TransformPoint(local);
            double la = body.GetLatitude(w), lo = body.GetLongitude(w);
            double g = body.pqsController != null ? body.TerrainAltitude(la, lo) : groundAlt;
            if (body.ocean && g < 0d) g = 0d;
            return body.GetAltitude(w) <= g;
        }

        void LateUpdate()
        {
            if (body == null || FlightGlobals.currentMainBody != body)
            {
                Destroy(gameObject);
                return;
            }
            t = Time.time - startTime;
            if (t < 0f) return;
            if (t > life)
            {
                Destroy(gameObject);
                return;
            }
            Anchor();
            Vector3 sunW = Planetarium.fetch != null && Planetarium.fetch.Sun != null
                ? ((Vector3)(Planetarium.fetch.Sun.position) - transform.position).normalized
                : Vector3.up;
            daylight = Mathf.Clamp01(Vector3.Dot(sunW, transform.up) * 3f + 0.3f);

            // ground strikes: only while coming down, so the launch from inside the crater doesn't count
            for (int i = 0; i < n; i++)
            {
                float tau = t - tLaunch[i];
                if (tau <= 0.3f || tau >= tDeath[i]) continue;
                if (vel[i].y + gravL.y * tau < 0f && UnderGround(PathAt(i, tau))) tDeath[i] = tau;
            }

            UpdateHeads();
            UpdateSmoke();
        }

        void UpdateHeads()
        {
            int m = heads.Begin();
            float night = Mathf.Lerp(1f, 0.45f, daylight);
            for (int i = 0; i < n; i++)
            {
                float tau = t - tLaunch[i];
                float k = tau > 0f && tau < tDeath[i] ? Mathf.Exp(-tau / glowTau[i]) * SS(0f, 0.08f, tau) : 0f;
                Vector3 pos = PathAt(i, Mathf.Clamp(tau, 0f, tDeath[i]));
                float flick = 0.85f + 0.15f * Mathf.Sin(t * (17f + 9f * heads.r0[i]) + i);
                float sz = puffSize[i];
                if (2 * i < m) heads.Set(2 * i, pos, sz * (0.5f + 0.3f * k), new Color(HotCore.r, HotCore.g, HotCore.b, k * flick), 0f);
                if (2 * i + 1 < m) heads.Set(2 * i + 1, pos, sz * 2.2f, new Color(HotHalo.r, HotHalo.g, HotHalo.b, 0.45f * k * flick * night), heads.r1[i] * 360f);
            }
            heads.End(m);
        }

        void UpdateSmoke()
        {
            int m = smoke.Begin();
            float light = 0.35f + 0.6f * daylight;
            for (int idx = 0; idx < m; idx++)
            {
                int i = idx / puffs, k = idx % puffs;
                float tau = t - tLaunch[i];
                // puff k is dropped at tauK along the path, if the projectile was still flying then
                float tauK = (k + 0.3f * smoke.r0[idx]) / puffs * tMaxFlight[i];
                if (tau < tauK || tauK > tDeath[i])
                {
                    smoke.Set(idx, Vector3.zero, 0f, new Color(0f, 0f, 0f, 0f), 0f);
                    continue;
                }
                float age = tau - tauK;
                float u = age / PuffFade;
                if (u >= 1f)
                {
                    smoke.Set(idx, Vector3.zero, 0f, new Color(0f, 0f, 0f, 0f), 0f);
                    continue;
                }
                float r1 = smoke.r1[idx], r2 = smoke.r2[idx], r3 = smoke.r3[idx];
                float size = puffSize[i] * (0.6f + 2.4f * Mathf.Sqrt(u)) * (0.8f + 0.4f * r2);
                // the hot smoke lifts a little and spreads off the path as it ages
                float ang = r1 * 2f * Mathf.PI;
                Vector3 pos = PathAt(i, tauK)
                              + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (0.25f * size * u)
                              + new Vector3(0f, 1.5f * age, 0f);
                Color col = Color.Lerp(SmokeDark, SmokeBrown, r3) * light;
                // fresh puffs from the first second or so of flight pick up the head's glow
                float hot = Mathf.Exp(-tauK / glowTau[i]) * Mathf.Exp(-age / 0.5f);
                col += HotHalo * (0.7f * hot);
                col.a = Mathf.Clamp01(0.7f * SS(0f, 0.04f, u) * (1f - SS(0.25f, 1f, u)));
                smoke.Set(idx, pos, size, col, r3 * 360f);
            }
            smoke.End(m);
        }
    }
}
