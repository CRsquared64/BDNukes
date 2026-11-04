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
        public static AudioClip Roar;     // blast wind and afterwind close to ground zero
        public static Material Bolt;      // additive soft line for lightning (LineRenderer)
        public static AudioClip Geiger;   // one Geiger-counter click

        static bool ready;
        static float builtTextureScale;
        static Shader alpha, additive;
        static Texture2D groundNoise;

        public static void Init()
        {
            if (ready) return;
            ready = true;

            alpha = FindShader("KSP/Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended");
            additive = FindShader("KSP/Particles/Additive", "Legacy Shaders/Particles/Additive", "Particles/Additive");

            Smoke = MakeMaterial(alpha, null, 3000);
            Mist = MakeMaterial(alpha, null, 3001);
            Glow = MakeMaterial(additive, null, 3100);
            Ring = MakeMaterial(additive, null, 3101);
            // drawn after the cloud, so bolts at the cap's underside and around the stem stay visible
            Bolt = MakeMaterial(additive, MakeBoltTexture(), 3050);
            RebuildTextures();

            Crack = MakeCrack();
            Rumble = MakeRumble();
            Roar = MakeRoar();
            Geiger = MakeGeigerClick();
            StockBoom = GameDatabase.Instance != null ? GameDatabase.Instance.GetAudioClip("BDArmory/Models/explosion/nuke/nukeBoom") : null;
        }

        /// <summary>
        /// (Re)builds the particle textures at the resolution the quality setting asks for. The materials stay the
        /// same objects, so anything holding NukeAssets.Glow etc. keeps working; only their textures are swapped.
        /// </summary>
        public static void RebuildTextures()
        {
            if (!ready) { Init(); return; }
            float ts = NukeSettings.Current.textureScale;
            if (ts == builtTextureScale && Smoke.mainTexture != null) return;
            builtTextureScale = ts;
            Swap(Smoke, MakeCauliflowerAtlas(Pow2(256 * ts), 7));
            Swap(Mist, MakeGlowTexture(Pow2(128 * ts), 2.2f));
            Swap(Glow, MakeGlowTexture(Pow2(128 * ts), 3.5f));
            Swap(Ring, MakeRingTexture(Pow2(256 * ts)));
            // decals made earlier keep the old noise texture, so it isn't destroyed
            groundNoise = MakeGroundNoise(Pow2(256 * ts));
        }

        static int Pow2(float size) => Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(size)), 32, 2048);

        static void Swap(Material m, Texture2D tex)
        {
            var old = m.mainTexture;
            m.mainTexture = tex;
            if (old != null && old != tex) Object.Destroy(old);
        }

        /// <summary>A new alpha-blended (or additive) ground decal material, tiled with burn-scar noise. The caller owns it.</summary>
        public static Material MakeDecalMaterial(bool add)
        {
            Init();
            return MakeMaterial(add ? additive : alpha, groundNoise, add ? 2902 : 2900);
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

                // lumps sorted back-to-front; bigger ones towards the middle, with a wide spread of sizes
                // (a few big masses, many small knobbles) so the puff doesn't read as a cluster of equal balls
                int lumps = 48 + rng.Next(20);
                var lx = new float[lumps]; var ly = new float[lumps]; var lr = new float[lumps]; var lb = new float[lumps];
                for (int k = 0; k < lumps; k++)
                {
                    float ang = (float)rng.NextDouble() * 2f * Mathf.PI;
                    float dist = Mathf.Pow((float)rng.NextDouble(), 0.7f) * 0.62f;
                    lx[k] = Mathf.Cos(ang) * dist;
                    ly[k] = Mathf.Sin(ang) * dist * 0.85f;
                    float sizeRand = (float)rng.NextDouble();
                    lr[k] = Mathf.Lerp(0.32f, 0.09f, dist / 0.62f) * (0.45f + 1.1f * sizeRand * sizeRand);
                    lb[k] = 0.86f + 0.24f * (float)rng.NextDouble();
                }

                for (int k = 0; k < lumps; k++)
                {
                    // only the pixels inside this lump's bounding box can be touched
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((lx[k] - lr[k] + 1f) * 0.5f * tile) - 1);
                    int x1 = Mathf.Min(tile - 1, Mathf.CeilToInt((lx[k] + lr[k] + 1f) * 0.5f * tile) + 1);
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((ly[k] - lr[k] + 1f) * 0.5f * tile) - 1);
                    int y1 = Mathf.Min(tile - 1, Mathf.CeilToInt((ly[k] + lr[k] + 1f) * 0.5f * tile) + 1);
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float u = (x + 0.5f) / tile * 2f - 1f, v = (y + 0.5f) / tile * 2f - 1f;
                            float dx = (u - lx[k]) / lr[k], dy = (v - ly[k]) / lr[k];
                            float d2 = dx * dx + dy * dy;
                            if (d2 >= 1f) continue;
                            float nz = Mathf.Sqrt(1f - d2);
                            float lambert = Mathf.Max(0f, dx * light.x + dy * light.y + nz * light.z);
                            float detail = Fbm(u * 7f + k * 1.7f, v * 7f + t * 3.1f, seed + k, 3);
                            // gentler per-lump sphere shading, plus a broad top-lit gradient over the whole puff
                            float shade = (0.56f + 0.44f * lambert) * (0.86f + 0.18f * (v * 0.5f + 0.5f)) * lb[k];
                            shade *= 0.8f + 0.4f * detail;
                            // soft, noise-eroded lump edges instead of crisp discs
                            float a = Mathf.Clamp01((1f - d2) * 1.7f) * Mathf.Clamp01(1.5f - d2 * (0.4f + 1.1f * detail)) * (0.75f + 0.25f * detail);
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
                    float ring = Mathf.Exp(-Mathf.Pow((r - 0.9f) / 0.05f, 2f)); // soft enough not to read as a hard outline
                    float inner = r < 0.9f ? 0.12f * Mathf.Pow(r / 0.9f, 4f) : 0f; // faint limb-brightened shell
                    float a = Mathf.Clamp01(ring + inner) * Mathf.Clamp01((0.995f - r) / 0.06f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>Lightning channel: bright white core with a soft blue-violet halo across the line (V), flat along it (U).</summary>
        static Texture2D MakeBoltTexture()
        {
            const int w = 8, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h * 2f - 1f;
                float core = Mathf.Exp(-v * v * 60f);
                float halo = Mathf.Exp(-v * v * 5f) * 0.45f;
                var c = new Color(0.75f + 0.25f * core, 0.8f + 0.2f * core, 1f, 1f) * Mathf.Clamp01(core + halo);
                c.a = 1f;
                for (int x = 0; x < w; x++) px[y * w + x] = c;
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>
        /// Tileable charred-ground noise for the burn scar: grey value and alpha vary in blotches and fine grain,
        /// so the vertex-coloured decal doesn't look like a flat painted disc.
        /// </summary>
        static Texture2D MakeGroundNoise(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color[size * size];
            const int period = 8; // noise lattice cells across the tile; wrapped so the texture tiles seamlessly
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size * period, v = (float)y / size * period;
                    float big = TileNoise(u, v, period, 91);
                    float fine = TileNoise(u * 4f, v * 4f, period * 4, 92) * 0.5f + TileNoise(u * 8f, v * 8f, period * 8, 93) * 0.5f;
                    float g = Mathf.Clamp01(0.62f + 0.3f * big + 0.18f * (fine - 0.5f));
                    float a = Mathf.Clamp01(0.75f + 0.35f * (big - 0.5f) + 0.25f * (fine - 0.5f));
                    px[y * size + x] = new Color(g, g, g, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        static float TileNoise(float x, float y, int period, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf);
            int x0 = ((xi % period) + period) % period, y0 = ((yi % period) + period) % period;
            int x1 = (x0 + 1) % period, y1 = (y0 + 1) % period;
            float a = Hash(x0, y0, seed), b = Hash(x1, y0, seed), c = Hash(x0, y1, seed), d = Hash(x1, y1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        /// <summary>
        /// Radiation trefoil for the toolbar button: three 60-degree blades and a centre disc in a ring,
        /// supersampled for clean edges. Light grey on transparent to match the stock launcher icons.
        /// </summary>
        public static Texture2D MakeTrefoilIcon(int size, Color col)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[size * size];
            const int ss = 4;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float u = (x + (sx + 0.5f) / ss) / size * 2f - 1f, v = (y + (sy + 0.5f) / ss) / size * 2f - 1f;
                            float r = Mathf.Sqrt(u * u + v * v);
                            float ang = Mathf.Atan2(v, u) * Mathf.Rad2Deg; // 0 = right, blades point up and down-left/down-right
                            float rel = Mathf.Repeat(ang - 90f + 30f, 120f);
                            bool blade = r > 0.24f && r < 0.78f && rel < 60f;
                            bool hub = r < 0.15f;
                            bool ring = r > 0.86f && r < 0.97f;
                            if (blade || hub || ring) hit++;
                        }
                    }
                    var c = col;
                    c.a = col.a * hit / (float)(ss * ss);
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
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

                // the blast wave arrives as an N-wave: an instant jump to peak overpressure, a linear fall through
                // the negative phase and a snap back; the ground-reflected wave follows a few ms behind
                float crack = 1.6f * (NWave(t, 0.028f) + 0.55f * NWave(t - 0.011f, 0.024f)) + 0.35f * white * Mathf.Exp(-t / 0.015f);
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

        static float NWave(float t, float T) => t < 0f || t > T ? 0f : 1f - 2f * t / T;

        /// <summary>
        /// Blast wind: a gusting roar of broadband noise (air and debris) that hits with the shock and dies over a few
        /// seconds, then the weaker afterwind swelling back the other way as air rushes in under the rising fireball.
        /// </summary>
        static AudioClip MakeRoar()
        {
            float length = 12f;
            int n = (int)(SampleRate * length);
            var data = new float[n];
            var rng = new System.Random(9876);
            float lp1 = 0f, lp2 = 0f, hp = 0f, prev = 0f;
            float gust = 0.7f, gustTarget = 0.7f;
            int gustCounter = 0;
            float lpCoef = 1f - Mathf.Exp(-2f * Mathf.PI * 1400f / SampleRate);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                lp1 += (white - lp1) * lpCoef;
                lp2 += (lp1 - lp2) * lpCoef;
                hp = 0.995f * (hp + lp2 - prev); // take out the DC and the deepest lows, the rumble has those
                prev = lp2;
                if (--gustCounter <= 0)
                {
                    gustCounter = (int)(SampleRate * (0.08f + (float)rng.NextDouble() * 0.3f));
                    gustTarget = 0.4f + (float)rng.NextDouble() * 0.6f;
                }
                gust += (gustTarget - gust) * 0.0006f;
                float blast = Mathf.Clamp01(t / 0.03f) * Mathf.Exp(-t / 1.6f);
                float after = 0.35f * Mathf.Exp(-Mathf.Pow((t - 5.5f) / 2.2f, 2f));
                float tail = Mathf.Clamp01((length - t) / 2f);
                data[i] = hp * gust * (blast + after) * tail;
            }
            Normalize(data, 0.85f);
            var clip = AudioClip.Create("bdnukesRoar", n, 1, SampleRate, false);
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

        /// <summary>One Geiger-Mueller tube click: a sharp broadband tick with a short ring-down.</summary>
        static AudioClip MakeGeigerClick()
        {
            int n = (int)(SampleRate * 0.012f);
            var data = new float[n];
            var rng = new System.Random(777);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SampleRate;
                float white = (float)rng.NextDouble() * 2f - 1f;
                phase += 2f * Mathf.PI * 3200f / SampleRate;
                data[i] = (0.8f * white * Mathf.Exp(-t / 0.0008f) + 0.5f * Mathf.Sin(phase) * Mathf.Exp(-t / 0.002f)) * Mathf.Clamp01(t / 0.0001f);
            }
            Normalize(data, 0.9f);
            var clip = AudioClip.Create("bdnukesGeiger", n, 1, SampleRate, false);
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
