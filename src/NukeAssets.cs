using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Procedurally generated textures, materials and sounds. Everything is built once at first use,
    /// so the mod ships no binary assets and needs no extra dependencies.
    /// </summary>
    internal static class NukeAssets
    {
        public static Material Smoke;     // alpha-blended billowing puff
        public static Material Mist;      // alpha-blended soft round blob (condensation)
        public static Material Glow;      // additive soft glow
        public static Material Ring;      // additive thin ring (shock front)
        public static AudioClip Crack;    // shock arrival: crack + thump
        public static AudioClip Rumble;   // long rolling rumble
        public static AudioClip StockBoom;

        static bool ready;

        public static void Init()
        {
            if (ready) return;
            ready = true;

            Shader alpha = FindShader("KSP/Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended");
            Shader additive = FindShader("KSP/Particles/Additive", "Legacy Shaders/Particles/Additive", "Particles/Additive");

            Smoke = MakeMaterial(alpha, MakeCauliflowerAtlas(256, 7), 3000);
            Mist = MakeMaterial(alpha, MakeGlowTexture(128, 2.2f), 3001);
            Glow = MakeMaterial(additive, MakeGlowTexture(128, 3.5f), 3100);
            Ring = MakeMaterial(additive, MakeRingTexture(256), 3101);

            Crack = MakeCrack();
            Rumble = MakeRumble();
            StockBoom = GameDatabase.Instance != null ? GameDatabase.Instance.GetAudioClip("BDArmory/Models/explosion/nuke/nukeBoom") : null;
        }

        static Shader FindShader(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            Debug.LogWarning("[BDNukes]: No particle shader found from " + string.Join(", ", names));
            return Shader.Find("Sprites/Default");
        }

        static Material MakeMaterial(Shader shader, Texture2D tex, int queue)
        {
            var m = new Material(shader) { mainTexture = tex, renderQueue = queue };
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 0.02f);
            return m;
        }

        #region Textures

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0x7fffffff) / (float)int.MaxValue;
            }
        }

        static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed);
            float c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        static float Fbm(float x, float y, int seed, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * ValueNoise(x, y, seed + i * 31);
                norm += amp;
                x *= 2.03f; y *= 2.03f; amp *= 0.5f;
            }
            return sum / norm;
        }

        static Texture2D NewTex(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
        }

        /// <summary>
        /// 2x2 atlas of cauliflower billows. Each tile is built from dozens of overlapping lumps,
        /// each Lambert-shaded with the light coming from the top of the tile (+V), so a puff reads as
        /// a lit 3D volume when the particle is rotated to point its top at the sun.
        /// </summary>
        static Texture2D MakeCauliflowerAtlas(int tile, int seed)
        {
            int size = tile * 2;
            var tex = NewTex(size);
            var px = new Color[size * size];
            Vector3 light = new Vector3(0f, 0.8f, 0.6f).normalized;
            var rng = new System.Random(seed);
            var col = new Color[tile * tile];

            for (int t = 0; t < 4; t++)
            {
                for (int i = 0; i < col.Length; i++) col[i] = new Color(0f, 0f, 0f, 0f);

                // lumps sorted back-to-front; bigger ones towards the middle
                int lumps = 34 + rng.Next(14);
                var lx = new float[lumps]; var ly = new float[lumps]; var lr = new float[lumps];
                for (int k = 0; k < lumps; k++)
                {
                    float ang = (float)rng.NextDouble() * 2f * Mathf.PI;
                    float dist = Mathf.Pow((float)rng.NextDouble(), 0.7f) * 0.62f;
                    lx[k] = Mathf.Cos(ang) * dist;
                    ly[k] = Mathf.Sin(ang) * dist * 0.85f;
                    lr[k] = Mathf.Lerp(0.34f, 0.12f, dist / 0.62f) * (0.75f + 0.5f * (float)rng.NextDouble());
                }

                for (int k = 0; k < lumps; k++)
                {
                    for (int y = 0; y < tile; y++)
                    {
                        for (int x = 0; x < tile; x++)
                        {
                            float u = (x + 0.5f) / tile * 2f - 1f, v = (y + 0.5f) / tile * 2f - 1f;
                            float dx = (u - lx[k]) / lr[k], dy = (v - ly[k]) / lr[k];
                            float d2 = dx * dx + dy * dy;
                            if (d2 >= 1f) continue;
                            float nz = Mathf.Sqrt(1f - d2);
                            float lambert = Mathf.Max(0f, dx * light.x + dy * light.y + nz * light.z);
                            float detail = Fbm(u * 7f + k * 1.7f, v * 7f + t * 3.1f, seed + k, 3);
                            float shade = 0.42f + 0.58f * lambert;
                            shade *= 0.82f + 0.36f * detail;
                            float a = Mathf.Clamp01((1f - d2) * 3.2f) * (0.8f + 0.2f * detail);
                            int idx = y * tile + x;
                            Color dst = col[idx];
                            // "over" compositing
                            float outA = a + dst.a * (1f - a);
                            float outC = outA > 1e-5f ? (shade * a + dst.r * dst.a * (1f - a)) / outA : 0f;
                            col[idx] = new Color(outC, outC, outC, outA);
                        }
                    }
                }

                int ox = (t % 2) * tile, oy = (t / 2) * tile;
                for (int y = 0; y < tile; y++)
                {
                    for (int x = 0; x < tile; x++)
                    {
                        // keep a clear margin so mip-mapping doesn't bleed tiles together
                        float u = (x + 0.5f) / tile * 2f - 1f, v = (y + 0.5f) / tile * 2f - 1f;
                        float margin = Mathf.Clamp01((0.97f - Mathf.Max(Mathf.Abs(u), Mathf.Abs(v))) / 0.1f);
                        Color cc = col[y * tile + x];
                        cc.a *= margin;
                        px[(oy + y) * size + ox + x] = cc;
                    }
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D MakeGlowTexture(int size, float falloff)
        {
            var tex = NewTex(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(Mathf.Exp(-r * r * falloff * 2f) - Mathf.Exp(-falloff * 2f));
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D MakeRingTexture(int size)
        {
            var tex = NewTex(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float ring = Mathf.Exp(-Mathf.Pow((r - 0.9f) / 0.035f, 2f));
                    float inner = r < 0.9f ? 0.12f * Mathf.Pow(r / 0.9f, 4f) : 0f; // faint limb-brightened shell
                    float a = Mathf.Clamp01(ring + inner);
                    if (r > 0.99f) a = 0f;
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        #endregion

        #region Sounds

        const int SampleRate = 44100;

        static AudioClip MakeCrack()
        {
            float length = 4f;
            int n = (int)(SampleRate * length);
            var data = new float[n];
            var rng = new System.Random(1234);
            float lp1 = 0f, lp2 = 0f, phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                lp1 += (white - lp1) * 0.08f;
                lp2 += (lp1 - lp2) * 0.08f;

                float crack = white * Mathf.Exp(-t / 0.02f);
                float body = lp2 * 6f * Mathf.Exp(-t / 0.45f);
                float freq = Mathf.Lerp(22f, 55f, Mathf.Exp(-t / 0.35f));
                phase += 2f * Mathf.PI * freq / SampleRate;
                float thump = Mathf.Sin(phase) * Mathf.Exp(-t / 0.9f) * Mathf.Clamp01(t / 0.004f);
                data[i] = 0.7f * crack + 0.9f * body + 1.1f * thump;
            }
            Normalize(data, 0.95f);
            var clip = AudioClip.Create("bdnukesCrack", n, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static AudioClip MakeRumble()
        {
            float length = 26f;
            int n = (int)(SampleRate * length);
            var data = new float[n];
            var rng = new System.Random(4321);
            float brown = 0f, lp1 = 0f, lp2 = 0f;
            float mod = 0.6f, modTarget = 0.6f;
            int modCounter = 0;
            float lpCoef = 1f - Mathf.Exp(-2f * Mathf.PI * 140f / SampleRate);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                brown = brown * 0.997f + white * 0.06f;
                lp1 += (brown - lp1) * lpCoef;
                lp2 += (lp1 - lp2) * lpCoef;

                // rolling thunder: amplitude wanders between new random targets
                if (--modCounter <= 0)
                {
                    modCounter = (int)(SampleRate * (0.25f + (float)rng.NextDouble() * 0.6f));
                    modTarget = 0.35f + (float)rng.NextDouble() * 0.65f;
                }
                mod += (modTarget - mod) * 0.00012f;

                float env = Mathf.Clamp01(t / 0.35f) * Mathf.Exp(-t / 7.5f);
                float tail = Mathf.Clamp01((length - t) / 3f);
                data[i] = lp2 * env * mod * tail;
            }
            Normalize(data, 0.9f);
            var clip = AudioClip.Create("bdnukesRumble", n, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        static void Normalize(float[] data, float peak)
        {
            float max = 1e-6f;
            for (int i = 0; i < data.Length; i++) max = Mathf.Max(max, Mathf.Abs(data[i]));
            float k = peak / max;
            for (int i = 0; i < data.Length; i++) data[i] *= k;
        }

        #endregion
    }
}
