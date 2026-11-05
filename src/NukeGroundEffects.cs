using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// What a burst leaves on the ground around ground zero:
    ///  - a charred burn scar where the fireball touched the ground, inside a lighter ring of thermal-flash
    ///    bleached ground, as a decal mesh draped over the terrain (sampled once from the PQS heights);
    ///  - a dull red thermal afterglow of the scorched ground, fading over a minute or two (it shows best at night);
    ///  - scattered fires within the thermal radius on land, with smoke columns leaning downwind, dimming over minutes.
    /// A Unity Projector would need a projector shader KSP doesn't ship, so the decal is a mesh drawn with the
    /// stock particle shaders, lifted off the terrain a little more the further away the camera is.
    /// </summary>
    internal class NukeGroundEffects : MonoBehaviour
    {
        NukeAftermathParams p;
        NukeSettings s;
        float t, life, fireLife, glowTau;
        float daylight;

        // scorch decal and afterglow
        Transform decal;
        Material scorchMat, glowMat;
        MeshRenderer scorchRend, glowRend;

        // fires
        NukeCinematic.Layer fireGlow, smoke;
        Vector3[] firePos;
        float[] fireIgnite, fireSize, colHeight;
        int nFires, nCols;
        const int PuffsPerColumn = 18;
        const float SmokeRiseTime = 50f; // s for a smoke puff to climb its column
        Light fireLight;
        bool firesDone;

        public static int BaseParticles(bool fires) => fires ? 40 * 2 + 10 * PuffsPerColumn : 0;

        public static void Spawn(NukeAftermathParams p)
        {
            var go = new GameObject("BDNukes_GroundEffects");
            go.AddComponent<NukeGroundEffects>().Setup(p);
        }

        void Setup(NukeAftermathParams prm)
        {
            p = prm;
            s = NukeSettings.Current;
            Anchor();
            fireLife = s.fireDuration * Mathf.Clamp(Mathf.Pow(p.W / 20f, 0.1f), 0.7f, 1.6f);
            glowTau = 25f * Mathf.Clamp(Mathf.Pow(p.W / 20f, 0.2f), 0.6f, 3f);
            life = Mathf.Max(p.scorch ? s.scorchLifetime : 0f, p.fires ? fireLife : 0f, p.afterglow ? 6f * glowTau : 0f);
            int seed = Random.Range(0, 100000);
            if (p.scorch) BuildDecal(seed);
            if (p.fires) BuildFires(seed + 7);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
        }

        void OnSceneChange(GameScenes scene) { Destroy(gameObject); }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
            if (scorchMat != null) Destroy(scorchMat);
            if (glowMat != null) Destroy(glowMat);
            if (decal != null)
            {
                // the scorch mesh and the afterglow's copy of it
                foreach (var mf in decal.GetComponentsInChildren<MeshFilter>(true))
                    if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            }
        }

        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
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

        /// <summary>Ground surface under a local (x, z), in local coordinates; water = sea under it.</summary>
        Vector3 GroundPoint(float x, float z, out bool water)
        {
            var body = p.body;
            Vector3 w = transform.TransformPoint(new Vector3(x, 0f, z));
            double la = body.GetLatitude(w), lo = body.GetLongitude(w);
            double g = body.pqsController != null ? body.TerrainAltitude(la, lo, true) : p.groundAlt;
            water = body.ocean && g < 0d;
            if (water) g = 0d;
            return transform.InverseTransformPoint(body.GetWorldSurfacePosition(la, lo, g));
        }

        // ----------------------------------------------------------------------------------------
        // Burn scar, bleached ring and afterglow
        // ----------------------------------------------------------------------------------------

        void BuildDecal(int seed)
        {
            var rng = new System.Random(seed);
            float ph1 = (float)rng.NextDouble() * 6.3f, ph2 = (float)rng.NextDouble() * 6.3f, ph3 = (float)rng.NextDouble() * 6.3f;
            // ragged angular pattern: hills and buildings shadow the flash in radial streaks
            float Streak(float a) => 0.5f + 0.5f * (0.5f * Mathf.Sin(7f * a + ph1) + 0.3f * Mathf.Sin(13f * a + ph2) + 0.2f * Mathf.Sin(29f * a + ph3));

            int nr = Mathf.Clamp(Mathf.RoundToInt(22f * Mathf.Sqrt(s.particleDensity)), 10, 40);
            int ns = nr * 3;
            int nv = 1 + nr * ns;
            var verts = new Vector3[nv];
            var uvs = new Vector2[nv];
            var cols = new Color[nv];
            var glow = new Color[nv];
            float rS = p.rScorch, rO = p.rOuter;
            float tiles = Mathf.Max(2f, rO / Mathf.Max(50f, rS * 0.4f)); // noise texture repeats across the decal
            Color black = new Color(0.05f, 0.045f, 0.04f), brown = new Color(0.24f, 0.17f, 0.11f), bleach = new Color(0.88f, 0.85f, 0.78f);

            for (int i = 0; i < nv; i++)
            {
                float r, a;
                if (i == 0) { r = 0f; a = 0f; }
                else
                {
                    int ring = (i - 1) / ns + 1, seg = (i - 1) % ns;
                    r = rO * Mathf.Pow(ring / (float)nr, 1.3f); // finer near the centre
                    a = seg / (float)ns * 2f * Mathf.PI;
                }
                float x = Mathf.Cos(a) * r, z = Mathf.Sin(a) * r;
                verts[i] = GroundPoint(x, z, out bool water);
                uvs[i] = new Vector2(x / rO * tiles, z / rO * tiles);
                float n = Streak(a);
                float rn = r / rS;
                float burn = 1f - SS(0.7f, 1.15f, rn + 0.18f * (n - 0.5f));
                float ringA = rn < 0.85f ? 0f : 0.24f * SS(0.85f, 1.3f, rn) * Mathf.Pow(1f - SS(rS, rO, r), 1.5f) * (0.55f + 0.45f * n);
                Color charC = Color.Lerp(brown, black, SS(0.2f, 0.9f, burn));
                Color c = Color.Lerp(bleach, charC, Mathf.Min(1f, burn * 1.5f));
                c.a = water || i > nv - ns - 1 ? 0f : Mathf.Max(0.9f * burn, ringA);
                cols[i] = c;
                float g = (1f - SS(0.3f, 1.05f, rn)) * (0.6f + 0.4f * n) * (water ? 0f : 1f);
                glow[i] = new Color(1f, 0.3f, 0.07f) * g;
            }

            var tris = new int[(ns + (nr - 1) * ns * 2) * 3];
            int ti = 0;
            for (int sgm = 0; sgm < ns; sgm++)
            {
                tris[ti++] = 0; tris[ti++] = 1 + (sgm + 1) % ns; tris[ti++] = 1 + sgm;
            }
            for (int ring = 1; ring < nr; ring++)
            {
                int a0 = 1 + (ring - 1) * ns, b0 = 1 + ring * ns;
                for (int sgm = 0; sgm < ns; sgm++)
                {
                    int s1 = (sgm + 1) % ns;
                    tris[ti++] = a0 + sgm; tris[ti++] = a0 + s1; tris[ti++] = b0 + sgm;
                    tris[ti++] = a0 + s1; tris[ti++] = b0 + s1; tris[ti++] = b0 + sgm;
                }
            }

            var mesh = new Mesh { name = "BDNukesScorch" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors = cols;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            var go = new GameObject("Scorch");
            go.transform.SetParent(transform, false);
            decal = go.transform;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            scorchRend = go.AddComponent<MeshRenderer>();
            scorchMat = NukeAssets.MakeDecalMaterial(false);
            SoftOff(scorchMat);
            scorchRend.sharedMaterial = scorchMat;
            NoShadows(scorchRend);
            scorchRend.enabled = false;

            if (p.afterglow)
            {
                var gmesh = Instantiate(mesh);
                gmesh.colors = glow;
                var ggo = new GameObject("Afterglow");
                ggo.transform.SetParent(decal, false);
                ggo.AddComponent<MeshFilter>().sharedMesh = gmesh;
                glowRend = ggo.AddComponent<MeshRenderer>();
                glowMat = NukeAssets.MakeDecalMaterial(true);
                SoftOff(glowMat);
                glowRend.sharedMaterial = glowMat;
                NoShadows(glowRend);
                glowRend.enabled = false;
            }
        }

        // soft particles would fade a ground-hugging decal out entirely
        static void SoftOff(Material m) { if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 5f); }

        static void NoShadows(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void UpdateDecal(float camDist)
        {
            // lift it clear of the terrain's own LOD error, which grows with distance
            decal.localPosition = new Vector3(0f, Mathf.Clamp(1f + 0.003f * camDist, 1f, 300f), 0f);
            float fade = SS(0f, 0.6f, t) * (1f - SS(0.7f * s.scorchLifetime, s.scorchLifetime, t));
            bool on = fade > 0.002f;
            if (scorchRend.enabled != on) scorchRend.enabled = on;
            if (on)
            {
                float light = 0.5f * (0.3f + 0.7f * daylight);
                scorchMat.SetColor("_TintColor", new Color(light, light, light, 0.5f * fade));
            }
            if (glowRend != null)
            {
                float g = SS(0f, 0.5f, t) * Mathf.Exp(-t / glowTau) * Mathf.Lerp(1f, 0.15f, daylight);
                bool gon = g > 0.004f && t < 6f * glowTau;
                if (glowRend.enabled != gon) glowRend.enabled = gon;
                if (gon) glowMat.SetColor("_TintColor", new Color(0.5f * g, 0.5f * g, 0.5f * g, 0.5f));
            }
        }

        // ----------------------------------------------------------------------------------------
        // Lingering fires and smoke
        // ----------------------------------------------------------------------------------------

        void BuildFires(int seed)
        {
            var rng = new System.Random(seed);
            nFires = Mathf.Max(4, Mathf.RoundToInt(40 * p.particleScale));
            nCols = Mathf.Clamp(Mathf.RoundToInt(10 * p.particleScale), 2, 16);
            nCols = Mathf.Min(nCols, nFires);
            firePos = new Vector3[nFires];
            fireIgnite = new float[nFires];
            fireSize = new float[nFires];
            colHeight = new float[nCols];
            float r0 = 0.5f * p.rScorch, r1 = 0.95f * p.rOuter;
            float baseSize = Mathf.Clamp(0.012f * p.rOuter, 12f, 250f);
            for (int i = 0; i < nFires; i++)
            {
                bool water = true;
                Vector3 g = Vector3.zero;
                // the sea doesn't burn: try a few spots, give up on this fire if they're all water
                for (int tries = 0; tries < 4 && water; tries++)
                {
                    float r = Mathf.Lerp(r0, r1, Mathf.Sqrt((float)rng.NextDouble()));
                    float a = (float)rng.NextDouble() * 2f * Mathf.PI;
                    g = GroundPoint(Mathf.Cos(a) * r, Mathf.Sin(a) * r, out water);
                }
                firePos[i] = g;
                fireSize[i] = water ? 0f : baseSize * (0.6f + 0.8f * (float)rng.NextDouble());
                fireIgnite[i] = (float)rng.NextDouble() * 6f;
                if (i < nCols) colHeight[i] = Mathf.Clamp(0.25f * p.rOuter, 200f, 6000f) * (0.6f + 0.6f * (float)rng.NextDouble());
            }
            fireGlow = new NukeCinematic.Layer(transform, "Fires", NukeAssets.Glow, nFires * 2, seed + 1, false);
            smoke = new NukeCinematic.Layer(transform, "FireSmoke", NukeAssets.Smoke, nCols * PuffsPerColumn, seed + 2, false);

            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, Mathf.Clamp(0.05f * p.rOuter, 30f, 800f), 0f);
            fireLight = lightGo.AddComponent<Light>();
            fireLight.type = LightType.Point;
            fireLight.color = new Color(1f, 0.55f, 0.22f);
            fireLight.range = 1.5f * p.rOuter;
            fireLight.intensity = 0f;
            fireLight.shadows = LightShadows.None;
            NukeLightBudget.Register(fireLight);
        }

        float FireLevel(int i)
        {
            float ign = t - fireIgnite[i];
            if (ign <= 0f || fireSize[i] <= 0f) return 0f;
            return SS(0f, 4f, ign) * (1f - SS(0.5f * fireLife, fireLife, t)) * Mathf.Lerp(1f, 0.4f, t / fireLife);
        }

        void UpdateFires()
        {
            if (firesDone) return;
            if (t > fireLife)
            {
                firesDone = true;
                fireGlow.SetVisible(false);
                smoke.SetVisible(false);
                fireLight.enabled = false;
                return;
            }
            float night = Mathf.Lerp(1f, 0.35f, daylight);
            int n = fireGlow.Begin();
            float total = 0f;
            for (int i = 0; i < nFires; i++)
            {
                float lv = FireLevel(i);
                total += lv;
                float flick = 0.75f + 0.25f * Mathf.Sin(t * (6f + 3f * fireGlow.r0[i]) + i) * Mathf.Sin(t * (2.3f + fireGlow.r1[i]) + 2f * i);
                Vector3 pos = firePos[i] + new Vector3(0f, 0.3f * fireSize[i], 0f);
                if (2 * i < n) fireGlow.Set(2 * i, pos, fireSize[i] * (0.8f + 0.2f * flick), new Color(1f, 0.72f, 0.32f, lv * flick * (0.5f + 0.5f * night)), fireGlow.r2[i] * 360f);
                if (2 * i + 1 < n) fireGlow.Set(2 * i + 1, pos, fireSize[i] * 3.5f, new Color(1f, 0.3f, 0.1f, 0.25f * lv * flick * night), 0f);
            }
            fireGlow.End(n);
            float avg = total / Mathf.Max(1, nFires);
            fireLight.intensity = 1.6f * avg * night * (0.85f + 0.15f * Mathf.Sin(t * 7f));

            int m = smoke.Begin();
            Vector3 drift = p.wind.Dir(t) * p.wind.lowSpeed;
            float light = 0.4f + 0.55f * daylight;
            for (int i = 0; i < m; i++)
            {
                int c = i / PuffsPerColumn;
                float r0 = smoke.r0[i], r1 = smoke.r1[i], r2 = smoke.r2[i], r3 = smoke.r3[i];
                float lv = FireLevel(c);
                if (lv <= 0.01f)
                {
                    smoke.Set(i, Vector3.zero, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                float u = Mathf.Repeat(r0 + t / SmokeRiseTime * (0.8f + 0.4f * r3), 1f);
                float h = colHeight[c];
                float size = fireSize[c] * 2.2f * (0.4f + 1.6f * u) * (0.8f + 0.4f * r2);
                float ang = r1 * 2f * Mathf.PI;
                // the puff at height u left the fire u * SmokeRiseTime ago, so the wind has carried it that far
                Vector3 pos = firePos[c] + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (0.3f * size * u)
                              + new Vector3(0f, u * h + 0.3f * fireSize[c], 0f) + drift * (u * SmokeRiseTime);
                Color col = new Color(0.21f, 0.2f, 0.19f) * light + new Color(1f, 0.42f, 0.12f) * (0.45f * lv * Mathf.Pow(1f - u, 3f) * night);
                col.a = Mathf.Clamp01(0.55f * lv * SS(0f, 0.08f, u) * (1f - SS(0.6f, 1f, u)));
                smoke.Set(i, pos, size, col, r3 * 360f);
            }
            smoke.End(m);
        }

        // ----------------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (p.body == null || FlightGlobals.currentMainBody != p.body)
            {
                Destroy(gameObject);
                return;
            }
            t = (float)(NukeFXManager.FxTime - p.spawnTime - p.delay);
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
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float camDist = cam != null ? (cam.transform.position - transform.position).magnitude : 1e5f;

            if (decal != null) UpdateDecal(camDist);
            if (fireGlow != null) UpdateFires();
        }
    }
}
