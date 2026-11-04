using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>What a detonation hands to the effects that outlive its cloud (fallout, scorch, fires).</summary>
    internal class NukeAftermathParams
    {
        public CelestialBody body;
        public double lat, lon, groundAlt;  // ground zero; groundAlt is ASL of the ground (0 over the sea)
        public float W, R, hb, yieldTimeScale;
        public float spawnTime, delay;
        public NukeWind wind;
        public float particleScale;         // density * burst LOD * particle budget

        // the cloud the fallout comes out of: an exponential rise like the cap's
        public float t0, tauRise, riseTime;
        public float srcTop;                // cloud height above the ground once risen (m)
        public float srcR0, srcR1;          // cloud radius at the start and once risen (m)
        public float falloutK;              // 0 clean .. 1 dirty surface burst
        public bool water;
        public Color plumeCol, plumeDark, dustTint;

        // ground effects
        public bool scorch, fires, afterglow;
        public float rScorch, rOuter;       // burn scar and thermal-flash ring radii (m)
    }

    /// <summary>
    /// Radioactive fallout from a surface or low burst. The lower part of the stabilising cloud sheds debris that
    /// falls out while the cloud drifts with the wind aloft, so the plume stretches out downwind into a long
    /// slanted band; dark streaks of rain-out hang beneath it and the deposited dust fades over minutes.
    ///
    /// The radiation hazard comes from a separate, fixed set of "model puffs" following the same paths, so it
    /// works the same whatever the visual quality (even with the plume visuals off). The dose rate follows the
    /// Way-Wigner t^-1.2 decay on a compressed game clock (see NukeRadiation for the dose).
    /// </summary>
    internal class NukeFallout : MonoBehaviour
    {
        const int ModelPuffs = 64;
        const float RefTime = 60f;          // s: falloutDoseRate is the peak rate at this age

        public static readonly List<NukeFallout> Active = new List<NukeFallout>();

        NukeAftermathParams p;
        NukeSettings s;
        float life, relStart, relDur;
        float t;
        float peakRate;     // Sv/h at the hottest point at RefTime
        float gzSigma, depWeight;

        // model puffs (radiation)
        float[] mR0, mR1, mR2, mR3;
        Vector3[] depPos;
        float[] depTime, depSigma;

        // visuals
        NukeCinematic.Layer plume, streaks;
        Vector3[] plumePos;
        float[] plumeA, plumeSize, plumeGround;
        int groundCursor;
        Vector3 sunL;
        float daylight;

        /// <summary>How long a burst's fallout lasts (s): falloutDuration for 20 kt, longer for bigger yields.</summary>
        public static float Lifetime(NukeSettings s, float W) => s.falloutDuration * Mathf.Clamp(Mathf.Pow(W / 20f, 0.15f), 0.6f, 2.5f);

        public static int BaseParticles(NukeSettings s) => s.falloutCloud ? 280 + 160 : 0;

        public static void Spawn(NukeAftermathParams p)
        {
            var go = new GameObject("BDNukes_Fallout");
            go.AddComponent<NukeFallout>().Setup(p);
        }

        void Setup(NukeAftermathParams prm)
        {
            p = prm;
            s = NukeSettings.Current;
            life = Lifetime(s, p.W);
            relStart = p.t0 + 0.3f * p.riseTime;
            relDur = 0.45f * life;
            // dirtier, bigger bursts: hotter fallout (the area grows with the plume, the peak only slowly)
            peakRate = s.falloutDoseRate * Mathf.Pow(Mathf.Max(p.W, 0.01f), 0.25f) * p.falloutK;
            gzSigma = Mathf.Max(1.5f * p.R, 0.3f * p.srcR1);
            Anchor();

            // the radiation model puffs and where each one comes down
            var rng = new System.Random(Random.Range(0, 100000));
            mR0 = new float[ModelPuffs]; mR1 = new float[ModelPuffs]; mR2 = new float[ModelPuffs]; mR3 = new float[ModelPuffs];
            depPos = new Vector3[ModelPuffs]; depTime = new float[ModelPuffs]; depSigma = new float[ModelPuffs];
            for (int i = 0; i < ModelPuffs; i++)
            {
                mR0[i] = (float)rng.NextDouble(); mR1[i] = (float)rng.NextDouble();
                mR2[i] = (float)rng.NextDouble(); mR3[i] = (float)rng.NextDouble();
                Release(mR0[i], mR1[i], mR2[i], mR3[i], out float tau, out Vector3 src, out float fall, out float r);
                depTime[i] = tau + fall;
                depPos[i] = Air(src, tau, tau + fall, 1f, mR1[i], mR3[i], r);
                depPos[i].y = 0f;
                depSigma[i] = 0.5f * r + 0.2f * p.srcR1;
            }
            // normalise so the hotline of overlapping deposits peaks at ~0.6 of ground zero
            float maxSum = 1e-3f;
            for (int i = 0; i < ModelPuffs; i++)
            {
                float sum = 0f;
                for (int j = 0; j < ModelPuffs; j++) sum += Gauss((depPos[i] - depPos[j]).sqrMagnitude, depSigma[j]);
                maxSum = Mathf.Max(maxSum, sum);
            }
            depWeight = 0.6f / maxSum;

            if (s.falloutCloud)
            {
                int seed = Random.Range(0, 100000);
                int n = Mathf.Max(8, Mathf.RoundToInt(280 * p.particleScale));
                plume = new NukeCinematic.Layer(transform, "FalloutPlume", NukeAssets.Smoke, n, seed, false);
                streaks = new NukeCinematic.Layer(transform, "FalloutStreaks", NukeAssets.Mist,
                    Mathf.Max(8, Mathf.RoundToInt(160 * p.particleScale)), seed + 1, false, 7f);
                plumePos = new Vector3[n];
                plumeA = new float[n];
                plumeSize = new float[n];
                plumeGround = new float[n];
                for (int i = 0; i < n; i++) plumeGround[i] = float.NaN;
            }
            Active.Add(this);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            Active.Remove(this);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
        }

        static float Gauss(float d2, float sigma) => Mathf.Exp(-d2 / (sigma * sigma));

        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        float Rise(float time) => 1f - Mathf.Exp(-Mathf.Max(0f, time - p.t0) / Mathf.Max(0.5f, p.tauRise));

        /// <summary>
        /// A puff's release: when it leaves the cloud (early ones are the heavy, close-in fallout), where (the
        /// cloud's lower part, drifting with the wind aloft), how long it takes to fall, and its cloud radius then.
        /// </summary>
        void Release(float r0, float r1, float r2, float r3, out float tau, out Vector3 src, out float fall, out float radius)
        {
            tau = relStart + relDur * Mathf.Pow(r0, 1.4f);
            float rise = Rise(tau);
            radius = Mathf.Lerp(p.srcR0, p.srcR1, rise);
            float top = Mathf.Max(p.R, p.hb + (p.srcTop - p.hb) * rise);
            float y = Mathf.Max(0.3f * top, top - radius * (0.25f + 0.45f * r2));
            src = p.wind.Hi(tau) + new Vector3(0f, y, 0f);
            // heavy particles fall fast and land close in; fine ones take most of the plume's life
            fall = life * (0.08f + 0.32f * (0.35f * r0 + 0.65f * Mathf.Repeat(r3 * 7.31f, 1f)));
        }

        /// <summary>Position of a released puff at time `time` (before it lands): falling, and blown by the lower winds.</summary>
        Vector3 Air(Vector3 src, float tau, float time, float q, float r1, float r3, float radius)
        {
            float ang = r3 * 2f * Mathf.PI;
            // the plume widens sideways as it falls
            float spread = radius * 0.75f * Mathf.Sqrt(r1) * (1f + 0.6f * q);
            Vector3 drift = p.wind.At(time, 0.5f) - p.wind.At(tau, 0.5f);
            Vector3 pos = src + drift + new Vector3(Mathf.Cos(ang) * spread, 0f, Mathf.Sin(ang) * spread);
            pos.y = src.y * (1f - q);
            return pos;
        }

        void Anchor()
        {
            var body = p.body;
            Vector3 pos = body.GetWorldSurfacePosition(p.lat, p.lon, p.groundAlt);
            transform.position = pos;
            Vector3 up = (pos - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        float Now => NukeFXManager.FxTime - p.spawnTime - p.delay;

        // ----------------------------------------------------------------------------------------
        // Radiation field
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Dose rate (Sv/h) at a world position that is heightAboveGround metres up. Three parts: ground zero
        /// (activated soil and the heaviest close-in fallout), the deposited fallout under the plume, both
        /// shielded by the air above the ground, and the airborne plume itself.
        /// </summary>
        public float DoseRate(Vector3 worldPos, float heightAboveGround)
        {
            if (p == null || p.body != FlightGlobals.currentMainBody) return 0f;
            float time = Now;
            if (time <= 0f || time > life) return 0f;
            Anchor(); // the floating origin may have moved since the last frame
            Vector3 l = transform.InverseTransformPoint(worldPos);
            Vector3 lh = new Vector3(l.x, 0f, l.z);

            float decay = Mathf.Pow(Mathf.Max(time, 20f) / RefTime, -1.2f);
            float fade = 1f - SS(0.75f * life, life, time);
            float gz = Gauss(lh.sqrMagnitude, gzSigma) * (1f - SS(0.3f * life, 0.6f * life, time));

            float dep = 0f, air = 0f;
            for (int i = 0; i < ModelPuffs; i++)
            {
                if (time >= depTime[i])
                {
                    dep += Gauss((lh - depPos[i]).sqrMagnitude, depSigma[i]);
                    continue;
                }
                Release(mR0[i], mR1[i], mR2[i], mR3[i], out float tau, out Vector3 src, out float fall, out float r);
                if (time < tau) continue;
                Vector3 pos = Air(src, tau, time, Mathf.Clamp01((time - tau) / fall), mR1[i], mR3[i], r);
                air += Gauss((l - pos).sqrMagnitude, 0.6f * r);
            }
            // ground shine is cut down by the air between you and the ground (~350 m e-fold for fallout gammas)
            float ground = (gz + depWeight * dep) * Mathf.Exp(-Mathf.Max(0f, heightAboveGround) / 350f);
            return peakRate * decay * fade * (ground + depWeight * air);
        }

        // ----------------------------------------------------------------------------------------
        // Visuals
        // ----------------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (p.body == null || FlightGlobals.currentMainBody != p.body)
            {
                Destroy(gameObject);
                return;
            }
            t = Now;
            if (t > life)
            {
                Destroy(gameObject);
                return;
            }
            Anchor();
            if (plume == null || t < relStart) return;

            Vector3 sunW = Planetarium.fetch != null && Planetarium.fetch.Sun != null
                ? ((Vector3)(Planetarium.fetch.Sun.position) - transform.position).normalized
                : Vector3.up;
            sunL = Quaternion.Inverse(transform.rotation) * sunW;
            daylight = Mathf.Clamp01(sunL.y * 3f + 0.3f);
            UpdateGroundCache();
            UpdatePlume();
            UpdateStreaks();
        }

        /// <summary>A few puffs a frame look up the real terrain under them, so landed fallout hugs the hills.</summary>
        void UpdateGroundCache()
        {
            int n = plumePos.Length;
            int per = Mathf.Max(1, n / 40);
            var body = p.body;
            for (int k = 0; k < per; k++)
            {
                int i = groundCursor = (groundCursor + 1) % n;
                Vector3 w = transform.TransformPoint(new Vector3(plumePos[i].x, 0f, plumePos[i].z));
                double la = body.GetLatitude(w), lo = body.GetLongitude(w);
                double g = body.pqsController != null ? body.TerrainAltitude(la, lo) : 0d;
                if (body.ocean && g < 0d) g = 0d;
                plumeGround[i] = transform.InverseTransformPoint(body.GetWorldSurfacePosition(la, lo, g)).y;
            }
        }

        void UpdatePlume()
        {
            int n = plume.Begin();
            float k = Mathf.Sqrt(p.falloutK);
            float fadeAll = 1f - SS(0.8f * life, life, t);
            for (int i = 0; i < n; i++)
            {
                float r0 = plume.r0[i], r1 = plume.r1[i], r2 = plume.r2[i], r3 = plume.r3[i];
                Release(r0, r1, r2, r3, out float tau, out Vector3 src, out float fall, out float rad);
                float age = t - tau;
                if (age <= 0f)
                {
                    plume.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    plumeA[i] = 0f;
                    continue;
                }
                float q = Mathf.Clamp01(age / fall);
                float ground = float.IsNaN(plumeGround[i]) ? 0f : plumeGround[i];
                Vector3 pos;
                float a, size;
                if (q < 1f)
                {
                    pos = Air(src, tau, t, q, r1, r3, rad);
                    pos.y = Mathf.Max(pos.y, ground + 0.15f * rad);
                    a = 0.5f * SS(0f, 12f, age);
                    size = rad * (0.28f + 0.2f * r2) * (1f + 0.9f * q);
                }
                else
                {
                    // landed: a low dusty haze that slowly creeps on downwind and settles out
                    float since = age - fall;
                    pos = Air(src, tau, tau + fall, 1f, r1, r3, rad) + (p.wind.Low(t) - p.wind.Low(tau + fall)) * 0.4f;
                    pos.y = ground + 0.12f * rad * (1f + 0.5f * r2);
                    a = 0.4f * (1f - SS(0f, 90f + 120f * r2, since));
                    size = rad * (0.4f + 0.2f * r2) * (1f + since / 200f);
                }
                a *= k * fadeAll;
                Vector3 nL = new Vector3(0f, 1f, 0f);
                float diff = Mathf.Max(0f, Vector3.Dot(nL, sunL)) * daylight;
                Color baseCol = Color.Lerp(p.plumeCol, p.plumeDark, 0.3f + 0.5f * q);
                Color col = baseCol * (0.4f + 0.22f * daylight + 0.45f * diff);
                col.a = Mathf.Clamp01(a);
                plumePos[i] = pos;
                plumeA[i] = q < 1f ? a : 0f;
                plumeSize[i] = size;
                plume.Set(i, pos, size, col, r3 * 360f);
            }
            plume.End(n);
        }

        /// <summary>Rain-out: dark streaks of falling debris hanging under the airborne plume puffs.</summary>
        void UpdateStreaks()
        {
            int n = streaks.Begin();
            int np = plumePos.Length;
            Color dark = p.water ? new Color(0.55f, 0.57f, 0.6f) : Color.Lerp(p.plumeDark, new Color(0.18f, 0.16f, 0.14f), 0.6f);
            float light = 0.45f + 0.55f * daylight;
            for (int i = 0; i < n; i++)
            {
                int j = (int)(streaks.r0[i] * np) % np;
                float pa = plumeA[j];
                if (pa <= 0.01f)
                {
                    streaks.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                Vector3 top = plumePos[j];
                float ground = float.IsNaN(plumeGround[j]) ? 0f : plumeGround[j];
                float h = top.y - ground;
                // each streak slides down from the puff to the ground and starts again
                float f = Mathf.Repeat(t * (0.05f + 0.06f * streaks.r1[i]) + streaks.r2[i], 1f);
                float jit = plumeSize[j] * 0.5f;
                Vector3 pos = top + new Vector3((streaks.r1[i] - 0.5f) * jit, -f * h, (streaks.r3[i] - 0.5f) * jit);
                float size = Mathf.Max(5f, plumeSize[j] * 0.07f) * (0.6f + 0.6f * streaks.r3[i]);
                Color col = dark * light;
                col.a = Mathf.Clamp01(0.5f * pa * SS(0f, 0.1f, f) * (1f - SS(0.7f, 1f, f)));
                streaks.Set(i, pos, size, col, 0f);
            }
            streaks.End(n);
        }
    }
}
