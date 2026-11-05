using System;
using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// What a burst at or over the sea does to the water itself.
    ///
    /// Waves: an underwater or surface burst digs a cavity in the sea, and its collapse sends out a train of waves
    /// (Crossroads Baker, 23 kt at 27 m deep: a first wave 28 m high at 300 m, still 2 m at 6 km). Height falls off as
    /// 1/r, scaled as W^0.54 (Glasstone); the waves are deep-water ones, as long as the cavity is wide (~600 m for 23 kt,
    /// ~ W^0.3), so they run at sqrt(g wavelength / 2 pi): ~30 m/s for Baker, faster for megatons. A low air burst
    /// makes much smaller ones. Where the sea shoals they steepen and break white on the shore; islands shadow them.
    /// The wave is drawn as a band of mesh over the ocean, following the planet's curve, and it moves what floats:
    /// each part is carried with the water's orbital motion and lifted on the crests (tipping boats as it passes).
    ///
    /// Shock ring: where an air burst's blast wave meets the sea it roughens the surface, and a dark ring sweeps out
    /// across the water at the shock's speed (seen in film of air bursts over the Pacific), slowly calming behind.
    /// </summary>
    internal class NukeWaterWaves : MonoBehaviour
    {
        const int NA = 420;           // segments around
        const int NRW = 180;          // rows across the wave band
        const int NRS = 24;           // rows across the shock ring band
        const int GA = 256, GR = 160; // sea-depth grid (around x out)
        const float MaxLife = 900f;

        CelestialBody body;
        double lat, lon;
        double spawnTime;
        float delay;
        float t;
        float W, R, hb, cSound;
        bool waves, shockRing;
        float lambda, cFront, k0, kMin, kMax, f, rMax, life, A0;
        float bodyR, gee;

        // sea depth around the burst (m, negative over land), filled a few rows a frame; then "open": 1 where the
        // wave can reach from the burst along that bearing, falling to 0 behind land
        float[] depthGrid, openGrid;
        int gridFilled;
        bool gridDone;
        float gridStep;

        Mesh waveMesh, shockMesh;
        Vector3[] wv, wn, sv, sn;
        readonly List<Vector4> wf = new List<Vector4>(), sf = new List<Vector4>();   // the flat sea's normals and the height above it (uv2)
        Color[] wc, sc;
        Material mat;
        GameObject waveGo, shockGo;

        static readonly List<NukeWaterWaves> active = new List<NukeWaterWaves>();

        /// <param name="alt">burst altitude above sea level (negative underwater)</param>
        public static void Spawn(CelestialBody body, double lat, double lon, double alt, float W, float R, float c,
            double spawnTime, float delay)
        {
            var s = NukeSettings.Current;
            if (!s.waterWaves || body == null || !body.ocean || W <= 0f) return;
            double terrain = body.pqsController != null ? body.TerrainAltitude(lat, lon, true) : 0d;
            if (terrain >= 0d) return;   // over land
            float hb = Mathf.Max(0f, (float)alt);
            float depth = (float)Math.Max(0d, -alt);
            float seaDepth = (float)-terrain;

            float lambda = Mathf.Clamp(600f * Mathf.Pow(W / 23f, 0.3f), 60f, 4000f);
            // how much of Baker's wave this burst makes: all of it under water, less at the surface, little from the air
            float f;
            if (depth > 0f) f = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(depth / Mathf.Max(5f, 0.6f * R)));
            else if (hb < R) f = Mathf.Lerp(0.45f, 0.2f, hb / R);
            else f = 0.2f * Mathf.Exp(-(hb - R) / (0.7f * R));
            // a burst in a shallow sea can't throw much water: it digs the bottom instead
            f *= Mathf.Clamp(Mathf.Sqrt(seaDepth / (0.25f * lambda)), 0.25f, 1f);
            float a0 = f * 1550f * Mathf.Pow(W, 0.54f) / lambda;   // height a wavelength out
            bool waves = a0 >= 0.4f;
            bool ring = depth <= 0f && hb > 0.25f * R && hb < 12f * R && body.atmosphere;
            if (!waves && !ring) return;

            var go = new GameObject("BDNukes_WaterWaves");
            go.AddComponent<NukeWaterWaves>().Setup(body, lat, lon, W, R, hb, c, lambda, f, waves, ring, spawnTime, delay);
        }

        void Setup(CelestialBody b, double la, double lo, float w, float r, float h, float c, float lam, float ff,
            bool wav, bool ring, double sTime, float d)
        {
            body = b; lat = la; lon = lo; W = w; R = r; hb = h; cSound = Mathf.Max(50f, c);
            lambda = lam; f = ff; waves = wav; shockRing = ring; spawnTime = sTime; delay = d;
            bodyR = (float)body.Radius;
            gee = (float)(body.GeeASL * 9.80665);
            // the cavity makes waves around its own size; longer ones lead, shorter ones follow (see Wave)
            k0 = 2f * Mathf.PI / lambda;
            kMin = k0 * Mathf.Exp(-2.2f * Spread);
            kMax = k0 * Mathf.Exp(2.2f * Spread);
            cFront = Mathf.Sqrt(gee / (4f * kMin));                 // the leading edge's speed (the longest waves' group speed)
            A0 = f * 1550f * Mathf.Pow(W, 0.54f);
            float rFade = A0 / 0.25f;                               // where the waves are down to a quarter metre
            life = waves ? Mathf.Min(MaxLife, rFade / cFront) : 0f;
            float ringEnd = shockRing ? RingEnd() : 0f;
            if (shockRing) life = Mathf.Max(life, ringEnd / cSound + 40f);
            rMax = Mathf.Max(waves ? Mathf.Min(rFade, cFront * life) + lambda : 0f, ringEnd + 2f * R);
            rMax = Mathf.Min(rMax, 0.4f * bodyR);
            gridStep = rMax / GR;
            depthGrid = new float[GA * GR];
            openGrid = new float[GA * GR];
            for (int i = 0; i < openGrid.Length; i++) { depthGrid[i] = 1000f; openGrid[i] = 1f; }

            mat = NukeVolumetric.MakeWaterWaveMaterial();
            if (mat != null)
            {
                if (waves) waveMesh = MakeMesh("Waves", NRW, out waveGo, out wv, out wn, out wc);
                if (shockRing) shockMesh = MakeMesh("ShockRing", NRS, out shockGo, out sv, out sn, out sc);
            }
            else Debug.Log("[BDNukes]: Water wave shader not in the bundle: the waves still move boats but aren't drawn.");
            Anchor();
            active.Add(this);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
            Debug.Log($"[BDNukes]: Sea {(waves ? $"waves: {A0 / lambda:F1} m high a wavelength ({lambda:F0} m) out, leading edge at {cFront:F0} m/s" : "")}{(waves && shockRing ? "; " : "")}{(shockRing ? "shock ring on the water" : "")}.");
        }

        /// <summary>How far out the shock still visibly marks the water.</summary>
        float RingEnd()
        {
            float rs = 18f * R;
            return Mathf.Sqrt(Mathf.Max(0f, rs * rs - hb * hb));
        }

        Mesh MakeMesh(string name, int rows, out GameObject go, out Vector3[] v, out Vector3[] n, out Color[] c)
        {
            go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.layer = 15;   // local scenery, like the ocean
            var m = new Mesh { name = "BDNukes" + name };
            m.MarkDynamic();
            int nv = (rows + 1) * (NA + 1);
            if (nv > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            v = new Vector3[nv]; n = new Vector3[nv]; c = new Color[nv];
            var flats = rows == NRW ? wf : sf;
            flats.Clear();
            for (int i = 0; i < nv; i++) flats.Add(new Vector4(0f, 1f, 0f, 0f));
            var tri = new int[rows * NA * 6];
            int k = 0;
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < NA; i++)
                {
                    int a = j * (NA + 1) + i, b = a + NA + 1;
                    tri[k++] = a; tri[k++] = b; tri[k++] = a + 1;
                    tri[k++] = a + 1; tri[k++] = b; tri[k++] = b + 1;
                }
            m.vertices = v; m.normals = n; m.colors = c;
            m.SetUVs(1, flats);
            m.triangles = tri;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f * rMax);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return m;
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            active.Remove(this);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            if (waveMesh != null) Destroy(waveMesh);
            if (shockMesh != null) Destroy(shockMesh);
            if (mat != null) Destroy(mat);
        }

        void Anchor()
        {
            Vector3 p = body.GetWorldSurfacePosition(lat, lon, 0d);
            transform.position = p;
            Vector3 up = (p - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        // ---------------------------------------------------------------- the sea around the burst

        void FillGrid()
        {
            if (gridDone || body.pqsController == null) { gridDone = true; return; }
            int budget = 1500;
            while (budget-- > 0 && gridFilled < GA * GR)
            {
                int ia = gridFilled / GR, ir = gridFilled % GR;
                float th = ia * 2f * Mathf.PI / GA, r = (ir + 0.5f) * gridStep;
                Vector3 w = transform.TransformPoint(SpherePoint(r, Mathf.Cos(th), Mathf.Sin(th), 0f, out _));
                double la = body.GetLatitude(w), lo = body.GetLongitude(w);
                depthGrid[gridFilled] = (float)-body.TerrainAltitude(la, lo, true);
                gridFilled++;
            }
            if (gridFilled < GA * GR) return;
            // shadows behind land: along each bearing the wave is stopped by the first land it meets (soft edges, and
            // a little leak round small islands from the bearings beside it)
            var raw = new float[GA * GR];
            for (int ia = 0; ia < GA; ia++)
            {
                float open = 1f;
                for (int ir = 0; ir < GR; ir++)
                {
                    float dpt = depthGrid[ia * GR + ir];
                    if (dpt < 0f) open = 0f;
                    raw[ia * GR + ir] = open;
                }
            }
            for (int ia = 0; ia < GA; ia++)
                for (int ir = 0; ir < GR; ir++)
                {
                    float sum = 0f;
                    for (int da = -2; da <= 2; da++) sum += raw[((ia + da + GA) % GA) * GR + ir];
                    openGrid[ia * GR + ir] = Mathf.Max(raw[ia * GR + ir], 0.6f * sum / 5f) * (depthGrid[ia * GR + ir] > 0f ? 1f : 0f);
                }
            gridDone = true;
        }

        void SampleSea(float r, float cosT, float sinT, out float depth, out float open)
        {
            float th = Mathf.Atan2(sinT, cosT);
            if (th < 0f) th += 2f * Mathf.PI;
            float fa = th / (2f * Mathf.PI) * GA, fr = r / gridStep - 0.5f;
            if (fr >= GR - 1) { depth = 1000f; open = 1f; return; }
            int a0 = (int)fa % GA, a1 = (a0 + 1) % GA;
            fr = Mathf.Max(0f, fr);
            int r0 = Mathf.Min((int)fr, GR - 1), r1 = Mathf.Min(r0 + 1, GR - 1);
            float ta = fa - Mathf.Floor(fa), tr = fr - r0;
            depth = Mathf.Lerp(Mathf.Lerp(depthGrid[a0 * GR + r0], depthGrid[a0 * GR + r1], tr), Mathf.Lerp(depthGrid[a1 * GR + r0], depthGrid[a1 * GR + r1], tr), ta);
            open = gridDone ? Mathf.Lerp(Mathf.Lerp(openGrid[a0 * GR + r0], openGrid[a0 * GR + r1], tr), Mathf.Lerp(openGrid[a1 * GR + r0], openGrid[a1 * GR + r1], tr), ta) : 1f;
        }

        /// <summary>A point h above the sea, r along the surface from the burst on bearing (cos, sin), in the anchor frame.</summary>
        Vector3 SpherePoint(float r, float c, float s, float h, out Vector3 normal)
        {
            float phi = r / bodyR;
            float sp = Mathf.Sin(phi), half = Mathf.Sin(0.5f * phi);
            normal = new Vector3(sp * s, Mathf.Cos(phi), sp * c);
            return new Vector3(sp * bodyR * s, -2f * half * half * bodyR, sp * bodyR * c) + normal * h;
        }

        // ---------------------------------------------------------------- the wave train

        // The waves of a sudden disturbance in deep water (Cauchy-Poisson): at distance r and time t the sea there
        // carries waves of wavenumber k = g t^2 / (4 r^2), with phase g t^2 / (4 r). The longest waves run out first;
        // behind them come shorter, steeper and slower ones, so the rings multiply and tighten toward the middle as they
        // spread. Which of them are strong depends on the cavity that made them: a band of wavenumbers around k0.
        const float Spread = 0.55f;   // width of that band (in ln k)

        float RIn => Mathf.Max(5f, t * Mathf.Sqrt(gee / (4f * kMax)));
        float ROut => Mathf.Max(10f, t * Mathf.Sqrt(gee / (4f * kMin)));

        /// <summary>Sea surface height (m), its rate of rise (m/s), its slope outward, and the outward speed of the
        /// water at the surface (m/s), r from the burst. "open"/"depth" from SampleSea.</summary>
        void Wave(float r, float depth, float open, out float eta, out float etaT, out float slope, out float u, out float foam)
        {
            eta = etaT = slope = u = foam = 0f;
            if (!waves || t <= 0.5f || open <= 0f || r <= 1f) return;
            float k = gee * t * t / (4f * r * r);
            float x = Mathf.Log(k / k0) / Spread;
            if (x * x > 4.84f * 1.3f) return;
            float weight = Mathf.Exp(-0.5f * x * x);
            float A = Mathf.Min(0.08f * lambda * f, A0 / Mathf.Max(r, 0.5f * lambda)) * weight * open;
            // the cavity's own first seconds: the waves rise as it forms
            A *= Mathf.Clamp01(t / (0.5f * lambda / Mathf.Sqrt(gee / k0)));
            // shoaling: once the sea is shallower than half a wavelength the wave slows, bunches up and grows
            float dpt = Mathf.Max(depth, 0.5f);
            float half = Mathf.PI / k;
            float shoal = dpt < half ? Mathf.Min(Mathf.Pow(half / dpt, 0.25f), 2.5f) : 1f;
            A *= shoal;
            // water can't stand steeper than this: the rest breaks (whitecaps)
            float steep = A * k;
            if (steep > 0.14f) { A = 0.14f / k; }
            float ph = gee * t * t / (4f * r);
            float c = Mathf.Cos(ph), sn = Mathf.Sin(ph);
            float omega = Mathf.Sqrt(gee * k);
            eta = A * c;
            etaT = -A * omega * sn;
            slope = A * k * sn;
            u = A * omega * c;   // orbital velocity at the surface (deep water)
            // whitecaps on steep crests; surf where the wave breaks on a shelving shore (a crest higher than ~0.78 x depth)
            foam = Mathf.Clamp01((steep - 0.07f) * 8f) * Mathf.Clamp01(c * 1.5f);
            if (eta > 0.78f * dpt) { foam = 1f; eta = 0.78f * dpt + 0.3f * (eta - 0.78f * dpt); }
            foam = Mathf.Max(foam, Mathf.Clamp01(1f - dpt / 6f) * Mathf.Clamp01(Mathf.Abs(eta) / 0.5f));
        }

        // ---------------------------------------------------------------- the shock ring (air bursts)

        void Ring(float r, out float slick, out float foam)
        {
            slick = foam = 0f;
            if (!shockRing || t <= 0f) return;
            float rs = R + cSound * t;
            float g = rs * rs - hb * hb;
            if (g <= 0f) return;
            float rg = Mathf.Sqrt(g);
            float d = rg - r;                      // > 0 behind the front
            float w = Mathf.Max(0.08f * R, 15f) * (1f + rg / (6f * R));
            if (d < -w) return;
            // the strength of the shock where it meets the sea: falls with the slant distance
            float strength = Mathf.Pow(Mathf.Clamp01(3.5f * R / rs), 0.7f) * Mathf.Clamp01(1f - rs / (18f * R));
            float front = Mathf.Exp(-(d * d) / (w * w));
            float behind = d > 0f ? Mathf.Exp(-d / (Mathf.Max(R, 3f * w))) * Mathf.Exp(-t / 60f) * 0.55f : 0f;
            slick = Mathf.Clamp01(strength * (front + behind) * 1.6f);
            // close in, the front tears spray off the crests: a white edge
            foam = Mathf.Clamp01(strength * front * 1.5f - 0.1f);
        }

        // ---------------------------------------------------------------- per frame

        void LateUpdate()
        {
            if (body == null || !FlightGlobals.ready || HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                if (HighLogic.LoadedScene != GameScenes.FLIGHT) Destroy(gameObject);
                return;
            }
            t = (float)(NukeFXManager.FxTime - spawnTime - delay);
            if (t > life) { Destroy(gameObject); return; }
            Anchor();
            FillGrid();
            bool near = FlightGlobals.currentMainBody == body;
            if (waveGo != null) waveGo.SetActive(near && t > 0f);
            if (shockGo != null) shockGo.SetActive(near && t > 0f);
            if (!near || t <= 0f || mat == null) return;
            SetLighting();
            if (waveMesh != null) BuildWave();
            if (shockMesh != null) BuildRing();
        }

        void SetLighting()
        {
            Vector3 up = transform.up;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            Vector3 toSun = sun != null ? ((Vector3)(sun.position - (Vector3d)transform.position)).normalized : up;
            float elev = Vector3.Dot(toSun, up);
            float day = Mathf.Clamp01((elev + 0.08f) / 0.25f);
            // the low sun reddens through more air
            Color sunCol = Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.97f, 0.9f), Mathf.Clamp01(elev * 3f)) * 1.1f * day;
            Color sky = Color.Lerp(new Color(0.85f, 0.6f, 0.5f), new Color(0.55f, 0.68f, 0.85f), Mathf.Clamp01(elev * 2.5f)) * Mathf.Lerp(0.04f, 1f, day);
            mat.SetVector("_WSunDir", toSun);
            mat.SetVector("_WUp", up);
            mat.SetColor("_WSunColor", sunCol);
            mat.SetColor("_WSkyColor", sky);
            mat.SetColor("_WDeepColor", new Color(0.03f, 0.09f, 0.13f));
            mat.SetColor("_WAmbient", sky * 0.35f + new Color(0.01f, 0.012f, 0.015f));
            mat.SetVector("_WFade", new Vector4(60000f, 160000f, 0f, 0f));
        }

        void BuildWave()
        {
            // rows evenly spaced in the waves' phase, so every crest gets the same number of rows however tight
            float rOut = ROut, rIn = RIn;
            float g4 = gee * t * t / 4f;
            float phOut = g4 / rOut, phIn = g4 / rIn;
            int idx = 0;
            for (int j = 0; j <= NRW; j++)
            {
                float fj = j / (float)NRW;
                float r = g4 / Mathf.Lerp(phIn, phOut, fj);
                float edge = Mathf.Clamp01(fj * 6f) * Mathf.Clamp01((1f - fj) * 12f);
                for (int i = 0; i <= NA; i++, idx++)
                {
                    float th = i * 2f * Mathf.PI / NA;
                    float c = Mathf.Cos(th), s = Mathf.Sin(th);
                    SampleSea(r, c, s, out float depth, out float open);
                    Wave(r, depth, open, out float eta, out _, out float slope, out _, out float foam);
                    // drawn just above the sea so the ocean doesn't swallow the troughs: their depth shows in the light
                    float h = 0.35f + Mathf.Max(eta, 0.12f * eta);
                    Vector3 p = SpherePoint(r, c, s, h, out Vector3 nUp);
                    Vector3 outward = new Vector3(s, 0f, c);
                    wv[idx] = p;
                    wn[idx] = (nUp - outward * slope).normalized;
                    wf[idx] = new Vector4(nUp.x, nUp.y, nUp.z, eta);
                    // the wave's local height (its envelope, not the height at this phase): how strongly it shades
                    float kk = Mathf.Max(1e-5f, gee * t * t / (4f * r * r));
                    float amp = Mathf.Sqrt(eta * eta + slope * slope / (kk * kk));
                    float shade = Mathf.Clamp01(amp / 0.5f);
                    float sea = depth > 0f ? 1f : 0f;
                    wc[idx] = new Color(foam, edge * sea * Mathf.Clamp01(t / 2f), 0f, shade);
                }
            }
            waveMesh.vertices = wv; waveMesh.normals = wn; waveMesh.colors = wc; waveMesh.SetUVs(1, wf);
        }

        void BuildRing()
        {
            float rs = R + cSound * t;
            float g = rs * rs - hb * hb;
            float rg = g > 0f ? Mathf.Sqrt(g) : 0f;
            float w = Mathf.Max(0.08f * R, 15f) * (1f + rg / (6f * R));
            float rOut = rg + 2.5f * w;
            float rIn = Mathf.Max(0f, rg - Mathf.Max(3f * R, 12f * w));
            int idx = 0;
            for (int j = 0; j <= NRS; j++)
            {
                float fj = j / (float)NRS;
                // more rows near the front
                float r = Mathf.Lerp(rIn, rOut, 1f - (1f - fj) * (1f - fj));
                for (int i = 0; i <= NA; i++, idx++)
                {
                    float th = i * 2f * Mathf.PI / NA;
                    float c = Mathf.Cos(th), s = Mathf.Sin(th);
                    SampleSea(r, c, s, out float depth, out _);
                    Ring(r, out float slick, out float foam);
                    Vector3 p = SpherePoint(r, c, s, 0.3f, out Vector3 nUp);
                    sv[idx] = p;
                    sn[idx] = nUp;
                    sf[idx] = new Vector4(nUp.x, nUp.y, nUp.z, 0f);
                    float sea = depth > 0f ? 1f : 0f;
                    sc[idx] = new Color(foam, sea, slick, slick);
                }
            }
            shockMesh.vertices = sv; shockMesh.normals = sn; shockMesh.colors = sc; shockMesh.SetUVs(1, sf);
        }

        // ---------------------------------------------------------------- what floats

        void FixedUpdate()
        {
            if (!waves || body == null || !FlightGlobals.ready || t <= 0f) return;
            if (FlightGlobals.VesselsLoaded == null) return;
            Vector3d center = body.position;
            Vector3 up0 = transform.up, north0 = transform.forward, east0 = transform.right;
            float rOutP = ROut + 0.1f * lambda, rInP = RIn;
            for (int vi = 0; vi < FlightGlobals.VesselsLoaded.Count; vi++)
            {
                Vessel v = FlightGlobals.VesselsLoaded[vi];
                if (v == null || v.packed || v.mainBody != body) continue;
                if (v.altitude > 0.1f * lambda + 50f) continue;
                for (int pi = 0; pi < v.parts.Count; pi++)
                {
                    Part p = v.parts[pi];
                    if (p == null || p.rb == null || p.physicalSignificance != Part.PhysicalSignificance.FULL) continue;
                    Vector3 rel = (Vector3)((Vector3d)p.rb.worldCenterOfMass - center);
                    float alt = rel.magnitude - bodyR;
                    Vector3 pUp = rel.normalized;
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(pUp, up0), -1f, 1f));
                    float r = ang * bodyR;
                    if (r > rOutP || r < rInP) continue;
                    Vector3 flat = Vector3.ProjectOnPlane(pUp, up0);
                    float c = Vector3.Dot(flat, north0), s = Vector3.Dot(flat, east0);
                    float m = Mathf.Sqrt(c * c + s * s);
                    if (m < 1e-6f) { c = 1f; s = 0f; } else { c /= m; s /= m; }
                    SampleSea(r, c, s, out float depth, out float open);
                    if (depth <= 0f) continue;
                    Wave(r, depth, open, out float eta, out float etaT, out _, out float u, out _);
                    float size = Mathf.Clamp(p.prefabSize.magnitude * 0.5f, 0.5f, 6f);
                    float sub = eta - alt;                         // how far under the raised surface
                    if (sub < -size) continue;
                    float decay = Mathf.Exp(-Mathf.Max(0f, -alt) * 2f * Mathf.PI / lambda);
                    // the water's own motion at this point: out along the surface and up/down with it
                    Vector3 outward = Vector3.ProjectOnPlane((Vector3)(pUp - up0 * Vector3.Dot(pUp, up0)), pUp);
                    outward = outward.sqrMagnitude > 1e-12f ? outward.normalized : Vector3.zero;
                    Vector3 vWater = (outward * u + pUp * etaT) * decay;
                    Vector3 vPart = (Vector3)(p.rb.velocity + Krakensbane.GetFrameVelocity()) - (Vector3)v.mainBody.getRFrmVel(p.rb.worldCenterOfMass);
                    Vector3 acc = (vWater - vPart) * 0.9f * Mathf.Clamp01(sub / size + 0.5f);
                    // the sea is raised (or lowered) under the part: hold it up on the crest as the stock sea would at 0
                    float wet = Mathf.Clamp01((sub + 0.5f * size) / size);
                    float stockWet = Mathf.Clamp01((-alt + 0.5f * size) / size);
                    float lift = (wet - stockWet) * gee * 1.6f * Mathf.Clamp((float)p.buoyancy, 0.3f, 2f);
                    acc += pUp * lift;
                    p.rb.AddForce(acc * open, ForceMode.Acceleration);
                }
            }
        }
    }
}
