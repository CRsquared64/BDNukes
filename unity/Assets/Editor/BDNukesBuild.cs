// Builds the BDNukes shader bundle and renders test contact sheets of the volumetric cloud.
//
//   powershell -File unity/run-unity.ps1 BuildAll      -> GameData/BDNukes/Shaders/bdnukes-windows.shaders
//   powershell -File unity/run-unity.ps1 RenderTests   -> unity/Build/tests/*.png
//
// The test renders drive the shader through BDNukes.NukeVolumeShader (Assets/Shared), the same code the plugin
// uses, with a standalone timeline of a surface burst (NukeVolumeTestTimeline).
using System;
using System.IO;
using BDNukes;
using UnityEditor;
using UnityEngine;

public static class BDNukesBuild
{
    const string NoisePath = "Assets/Noise/BDNukesBillows.asset";
    const string ShaderPath = "Assets/Shaders/BDNukesVolumetricCloud.shader";
    const string BundleName = "bdnukes-windows.shaders";

    static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

    // ------------------------------------------------------------------------------------------------------
    // Noise: R = Perlin-Worley billows (rounded cauliflower lumps), G = Worley fBm for fine erosion and wisps
    // ------------------------------------------------------------------------------------------------------

    public static Texture3D MakeNoise(int size)
    {
        // RGBA32: Texture3D.SetPixels32 silently writes nothing for two-channel formats in 2019.4
        var tex = new Texture3D(size, size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            name = "BDNukesBillows",
        };
        var cols = new Color32[size * size * size];
        var w4 = new WorleyGrid(4, 11);
        var w8 = new WorleyGrid(8, 23);
        var w16 = new WorleyGrid(16, 37);
        var w32 = new WorleyGrid(32, 53);
        var perlin = new TilingPerlin(97);
        float inv = 1f / size;
        for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x * inv, py = y * inv, pz = z * inv;
                    float wf = 0.625f * (1f - w4.F1(px, py, pz)) + 0.25f * (1f - w8.F1(px, py, pz)) + 0.125f * (1f - w16.F1(px, py, pz));
                    float pf = 0.5f + 0.5f * (0.6f * perlin.Noise(px, py, pz, 4) + 0.3f * perlin.Noise(px, py, pz, 8) + 0.1f * perlin.Noise(px, py, pz, 16));
                    float billow = Mathf.Clamp01(Remap(pf, wf - 1f, 1f, 0f, 1f));
                    float detail = 0.625f * (1f - w8.F1(px, py, pz)) + 0.25f * (1f - w16.F1(px, py, pz)) + 0.125f * (1f - w32.F1(px, py, pz));
                    cols[x + size * (y + size * z)] = new Color32((byte)(billow * 255f), (byte)(Mathf.Clamp01(detail) * 255f), 0, 255);
                }
        tex.SetPixels32(cols);
        tex.Apply(true, false);
        return tex;
    }

    static float Remap(float v, float a, float b, float c, float d) => c + (v - a) / Mathf.Max(1e-5f, b - a) * (d - c);

    class WorleyGrid
    {
        readonly int n;
        readonly Vector3[] pts;
        public WorleyGrid(int cells, int seed)
        {
            n = cells;
            var rng = new System.Random(seed);
            pts = new Vector3[n * n * n];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
        }
        public float F1(float x, float y, float z)
        {
            x *= n; y *= n; z *= n;
            int cx = Mathf.FloorToInt(x), cy = Mathf.FloorToInt(y), cz = Mathf.FloorToInt(z);
            float best = 9f;
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ix = cx + dx, iy = cy + dy, iz = cz + dz;
                        var f = pts[Mod(ix) + n * (Mod(iy) + n * Mod(iz))];
                        float ex = ix + f.x - x, ey = iy + f.y - y, ez = iz + f.z - z;
                        float d = ex * ex + ey * ey + ez * ez;
                        if (d < best) best = d;
                    }
            return Mathf.Clamp01(Mathf.Sqrt(best) / 0.9f);
        }
        int Mod(int i) => ((i % n) + n) % n;
    }

    class TilingPerlin
    {
        readonly int[] perm = new int[512];
        readonly Vector3[] grads = new Vector3[256];
        public TilingPerlin(int seed)
        {
            var rng = new System.Random(seed);
            var p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                p[i] = i;
                var g = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
                grads[i] = g.normalized;
            }
            for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); int t = p[i]; p[i] = p[j]; p[j] = t; }
            for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
        }
        float Grad(int ix, int iy, int iz, int per, float fx, float fy, float fz)
        {
            ix = ((ix % per) + per) % per; iy = ((iy % per) + per) % per; iz = ((iz % per) + per) % per;
            var g = grads[perm[perm[perm[ix] + iy] + iz] & 255];
            return g.x * fx + g.y * fy + g.z * fz;
        }
        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        public float Noise(float x, float y, float z, int per)
        {
            x *= per; y *= per; z *= per;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y), iz = Mathf.FloorToInt(z);
            float fx = x - ix, fy = y - iy, fz = z - iz;
            float u = Fade(fx), v = Fade(fy), w = Fade(fz);
            float a = Mathf.Lerp(Mathf.Lerp(Grad(ix, iy, iz, per, fx, fy, fz), Grad(ix + 1, iy, iz, per, fx - 1, fy, fz), u),
                                 Mathf.Lerp(Grad(ix, iy + 1, iz, per, fx, fy - 1, fz), Grad(ix + 1, iy + 1, iz, per, fx - 1, fy - 1, fz), u), v);
            float b = Mathf.Lerp(Mathf.Lerp(Grad(ix, iy, iz + 1, per, fx, fy, fz - 1), Grad(ix + 1, iy, iz + 1, per, fx - 1, fy, fz - 1), u),
                                 Mathf.Lerp(Grad(ix, iy + 1, iz + 1, per, fx, fy - 1, fz - 1), Grad(ix + 1, iy + 1, iz + 1, per, fx - 1, fy - 1, fz - 1), u), v);
            return Mathf.Lerp(a, b, w) * 1.6f;
        }
    }

    static Texture3D EnsureNoise()
    {
        var tex = AssetDatabase.LoadAssetAtPath<Texture3D>(NoisePath);
        if (tex != null) return tex;
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Noise"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        tex = MakeNoise(128);
        AssetDatabase.CreateAsset(tex, NoisePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[BDNukesBuild] noise generated in " + sw.ElapsedMilliseconds + " ms");
        return tex;
    }

    // ------------------------------------------------------------------------------------------------------
    // Bundle
    // ------------------------------------------------------------------------------------------------------

    static Shader LoadShader()
    {
        AssetDatabase.Refresh();
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null) throw new Exception("shader missing: " + ShaderPath);
        foreach (var m in ShaderUtil.GetShaderMessages(shader))
            Debug.Log("[BDNukesBuild] shader " + m.severity + ": " + m.message + " (line " + m.line + ")");
        if (ShaderUtil.ShaderHasError(shader)) throw new Exception("shader has compile errors");
        return shader;
    }

    public static void BuildAll()
    {
        EnsureNoise();
        LoadShader();
        string outDir = Path.Combine(RepoRoot, "unity", "Build");
        Directory.CreateDirectory(outDir);
        var names = new System.Collections.Generic.List<string> { ShaderPath, "Assets/Shaders/BDNukesCoverageHole.shader", "Assets/Shaders/BDNukesVolumeComposite.shader", "Assets/Shaders/BDNukesCloudShadow.shader", "Assets/Shaders/BDNukesShockDistortion.shader", "Assets/Shaders/BDNukesFireLight.shader", NoisePath };
        names.AddRange(BDNukesSimImport.ImportAll());
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = names.ToArray() };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new Exception("bundle build failed");
        string dest = Path.Combine(RepoRoot, "GameData", "BDNukes", "Shaders");
        Directory.CreateDirectory(dest);
        File.Copy(Path.Combine(outDir, BundleName), Path.Combine(dest, BundleName), true);
        Debug.Log("[BDNukesBuild] bundle written to " + Path.Combine(dest, BundleName));
    }

    // ------------------------------------------------------------------------------------------------------
    // Test contact sheets
    // ------------------------------------------------------------------------------------------------------

    struct Shot
    {
        public float W, t, sunVis, haze;
        public Vector3 cam;
        public float lookFrac;   // look-at height as a fraction of the cap top
        public float fov;
        public bool debug;
        public int div;          // 2 or 3: render through the reduced-resolution compositor
        public bool air;         // a free air burst 2 km up (air sim, no stem, skirt or surge)
        public float bulb;       // mushroom shape: 0 wide rolling cap .. 1 tight bulbous head
        public float humidity;   // 0 = use the default (0.7)
    }

    static NukeSimData simGround, simAir, simMushroom;

    static NukeSimData LoadSim(string variant)
    {
        var metaAsset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Sim/" + variant + "/" + variant + "_meta.json");
        if (metaAsset == null) return null;
        var d = new NukeSimData { meta = JsonUtility.FromJson<NukeSimMeta>(metaAsset.text) };
        d.frames = new Texture[d.meta.times.Length];
        for (int i = 0; i < d.frames.Length; i++)
            d.frames[i] = AssetDatabase.LoadAssetAtPath<Texture3D>("Assets/Sim/" + variant + "/f" + i.ToString("00") + ".asset");
        return d.Valid ? d : null;
    }

    public static void RenderTests()
    {
        BDNukesSimImport.ImportAll();
        AssetDatabase.Refresh();
        simGround = LoadSim("ground");
        simAir = LoadSim("air");
        simMushroom = LoadSim("mushroom");
        Debug.Log("[BDNukesBuild] fireball sim: " + (simGround != null ? simGround.frames.Length + " frames" : "none"));
        var noise = EnsureNoise();
        var shader = LoadShader();
        string outDir = Path.Combine(RepoRoot, "unity", "Build", "tests");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "*.png")) File.Delete(f);

        // a 20 kt surface burst over its life, from 7 km
        var life = new[] { 0.3f, 1.5f, 4f, 10f, 20f, 40f, 75f, 140f };
        Sheet(shader, noise, Path.Combine(outDir, "1_life_20kt.png"), 4, Array.ConvertAll(life, t => new Shot
        { W = 20f, t = t, sunVis = 1f, haze = 0.05f, cam = new Vector3(0f, 250f, -7000f), lookFrac = 0.5f, fov = 38f }));

        // motion: one second apart, close up on the cap (it should churn, not slide or pulse)
        var motion = new[] { 40f, 41f, 42f, 43f };
        Sheet(shader, noise, Path.Combine(outDir, "2_motion_cap.png"), 2, Array.ConvertAll(motion, t => new Shot
        { W = 20f, t = t, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 900f, -3200f), lookFrac = 1f, fov = 34f }));

        // fireball, close: smooth white-hot ball turning into rolling fire
        var fire = new[] { 0.2f, 0.8f, 2f, 4f, 7f, 12f };
        Sheet(shader, noise, Path.Combine(outDir, "3_fireball.png"), 3, Array.ConvertAll(fire, t => new Shot
        { W = 20f, t = t, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 150f, -2500f), lookFrac = 0.9f, fov = 40f }));

        // weather and distance at 60 s
        Sheet(shader, noise, Path.Combine(outDir, "4_weather.png"), 2, new[]
        {
            new Shot { W = 20f, t = 60f, sunVis = 1f, haze = 0.05f, cam = new Vector3(0f, 300f, -8000f), lookFrac = 0.5f, fov = 38f },
            new Shot { W = 20f, t = 60f, sunVis = 0.18f, haze = 0.12f, cam = new Vector3(0f, 300f, -8000f), lookFrac = 0.5f, fov = 38f },
            new Shot { W = 20f, t = 60f, sunVis = 1f, haze = 0.45f, cam = new Vector3(0f, 2000f, -40000f), lookFrac = 0.5f, fov = 20f },
            new Shot { W = 20f, t = 60f, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 50f, -3000f), lookFrac = 0.75f, fov = 50f },
        });

        // wind: the cloud from the side as it drifts, shears and stretches downwind, then frays and thins
        var windT = new[] { 60f, 150f, 300f, 480f };
        Sheet(shader, noise, Path.Combine(outDir, "6_wind_20kt.png"), 2, Array.ConvertAll(windT, t => new Shot
        { W = 20f, t = t, sunVis = 1f, haze = 0.05f, cam = new Vector3(0f, 400f, -14000f), lookFrac = 0.5f, fov = 42f }));

        // a Davy Crockett (0.02 kt): small fireball, a cap many fireball radii across
        var davy = new[] { 3f, 12f, 35f, 80f };
        Sheet(shader, noise, Path.Combine(outDir, "7_davy.png"), 2, Array.ConvertAll(davy, t => new Shot
        { W = 0.02f, t = t, sunVis = 1f, haze = 0.03f, cam = new Vector3(0f, 60f, -2500f), lookFrac = 0.5f, fov = 40f }));

        // reduced resolution: full vs 1/2 vs 1/3 through the compositor, low camera with the ground cutting the stem
        Sheet(shader, noise, Path.Combine(outDir, "8_resolution.png"), 3, new[]
        {
            new Shot { W = 20f, t = 30f, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 20f, -3500f), lookFrac = 0.6f, fov = 45f, div = 1 },
            new Shot { W = 20f, t = 30f, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 20f, -3500f), lookFrac = 0.6f, fov = 45f, div = 2 },
            new Shot { W = 20f, t = 30f, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 20f, -3500f), lookFrac = 0.6f, fov = 45f, div = 3 },
        });

        // the cloud's shadow on the ground, seen from above and to the side
        Sheet(shader, noise, Path.Combine(outDir, "9_shadow.png"), 2, new[]
        {
            new Shot { W = 20f, t = 60f, sunVis = 1f, haze = 0f, cam = new Vector3(6000f, 9000f, -9000f), lookFrac = 0.15f, fov = 50f },
            new Shot { W = 20f, t = 60f, sunVis = 0.2f, haze = 0f, cam = new Vector3(6000f, 9000f, -9000f), lookFrac = 0.15f, fov = 50f },
        });

        // an air burst 2 km up: a free fireball rolling up into a ring
        var airT = new[] { 0.3f, 1.5f, 4f, 8f, 14f, 30f };
        Sheet(shader, noise, Path.Combine(outDir, "10_airburst.png"), 3, Array.ConvertAll(airT, t => new Shot
        { W = 20f, t = t, sunVis = 1f, haze = 0f, cam = new Vector3(0f, 2600f, -3200f), lookFrac = 0.93f, fov = 38f, air = true }));

        // mushroom shapes: wide rolling cap .. tight bulbous head
        Sheet(shader, noise, Path.Combine(outDir, "11_shapes.png"), 3, new[]
        {
            new Shot { W = 20f, t = 45f, sunVis = 1f, haze = 0.03f, cam = new Vector3(0f, 250f, -7000f), lookFrac = 0.5f, fov = 38f, bulb = 0f, humidity = 0.1f },
            new Shot { W = 20f, t = 45f, sunVis = 1f, haze = 0.03f, cam = new Vector3(0f, 250f, -7000f), lookFrac = 0.5f, fov = 38f, bulb = 0.5f, humidity = 0.1f },
            new Shot { W = 20f, t = 45f, sunVis = 1f, haze = 0.03f, cam = new Vector3(0f, 250f, -7000f), lookFrac = 0.5f, fov = 38f, bulb = 1f, humidity = 0.1f },
        });

        // the velum over a humid megaton, rising
        var velT = new[] { 8f, 20f, 40f };
        Sheet(shader, noise, Path.Combine(outDir, "12_velum_1Mt.png"), 3, Array.ConvertAll(velT, t => new Shot
        { W = 1000f, t = t, sunVis = 1f, haze = 0.08f, cam = new Vector3(0f, 400f, -35000f), lookFrac = 0.55f, fov = 42f, humidity = 0.9f }));

        // a megaton
        var mt = new[] { 3f, 15f, 45f, 120f };
        Sheet(shader, noise, Path.Combine(outDir, "5_life_1Mt.png"), 2, Array.ConvertAll(mt, t => new Shot
        { W = 1000f, t = t, sunVis = 1f, haze = 0.1f, cam = new Vector3(0f, 500f, -30000f), lookFrac = 0.5f, fov = 40f }));

        Debug.Log("[BDNukesBuild] test renders written to " + outDir);
    }

    static void Sheet(Shader shader, Texture3D noise, string file, int cols, Shot[] shots)
    {
        const int W = 640, H = 400;
        int rows = (shots.Length + cols - 1) / cols;
        var sheet = new Texture2D(W * cols, H * rows, TextureFormat.RGB24, false);
        for (int i = 0; i < shots.Length; i++)
        {
            var img = Render(shader, noise, shots[i], W, H);
            int cx = i % cols, cy = rows - 1 - i / cols;
            sheet.SetPixels(cx * W, cy * H, W, H, img.GetPixels());
            UnityEngine.Object.DestroyImmediate(img);
        }
        sheet.Apply();
        File.WriteAllBytes(file, sheet.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(sheet);
    }

    static Texture2D Render(Shader shader, Texture3D noise, Shot shot, int W, int H)
    {
        var root = new GameObject("TestRoot");
        try
        {
            var v = NukeVolumeTestTimeline.At(shot.W, shot.t, shot.sunVis, shot.haze, shot.bulb, shot.humidity > 0f ? shot.humidity : 0.7f);
            float tauG = 2.5f + 2.2f * Mathf.Pow(shot.W, 0.3f);
            if (shot.air)
            {
                // the timeline's cloud, lifted 2 km: a free fireball with no ground interaction
                v.groundY = -2000f;
                v.stemDensity = v.skirtDensity = v.surgeDensity = 0f;
                v.wilsonHemisphere = false;
                v.capCenter.y -= 0.9f * (70f * Mathf.Pow(shot.W, 0.4f));
            }
            // the planet (Kerbin-sized) under the test ground: rays stop at the ground sphere, as in KSP
            v.planetCenter = new Vector3(0f, (shot.air ? -2000f : 0f) - 600000f, 0f);
            v.planetRadius = 600000f - 8f;
            var simUse = shot.air && simAir != null ? simAir : simGround;
            float simDur = Mathf.Max(6f, 2.5f * tauG);
            if (!shot.air && simMushroom != null && shot.W <= NukeSimPlayer.MushroomMaxYield)
            {
                simUse = simMushroom;
                simDur = NukeSimPlayer.MushroomDuration(90f);
            }
            float floorY = shot.air ? 0f : v.groundY;
            NukeSimPlayer.Fill(simUse, ref v, shot.t, simDur, floorY, v.capCenter,
                v.capCenter.y + Mathf.Max(v.dome, v.tube * v.squash) * v.capScale - floorY, v.capScale * (v.ringR + v.tube) * v.xzSpread, 1f, 3f, simUse == simMushroom ? NukeSimPlayer.MushroomPlayFraction : 1f);
            v.fire *= 1f - v.simMix;
            v.ember *= 1f - 0.7f * v.simMix;

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.SetParent(root.transform);
            ground.transform.localScale = new Vector3(40000f, 1f, 40000f);
            // a lit ground that writes depth (it has a shadow-caster pass), like KSP's terrain
            // the resolution sheet uses a ground that writes no depth (like Parallax's terrain): the ground sphere alone must hide the cloud
            var gm = new Material(Shader.Find(shot.div > 0 ? "Unlit/Color" : "Legacy Shaders/Diffuse"));
            gm.color = Color.Lerp(new Color(0.24f, 0.33f, 0.22f) * Mathf.Lerp(0.6f, 1f, shot.sunVis), new Color(0.6f, 0.68f, 0.78f), shot.haze);
            ground.GetComponent<Renderer>().sharedMaterial = gm;
            var lightGo = new GameObject("Sun");
            lightGo.transform.SetParent(root.transform);
            var sunLight = lightGo.AddComponent<Light>();
            sunLight.type = LightType.Directional;
            sunLight.intensity = Mathf.Lerp(0.35f, 1f, shot.sunVis);
            sunLight.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.LookRotation(-v.sunDir.normalized);
            RenderSettings.ambientLight = new Color(0.45f, 0.5f, 0.55f);

            var cloudRoot = new GameObject("Cloud");
            cloudRoot.transform.SetParent(root.transform);
            if (shot.air) cloudRoot.transform.position = new Vector3(0f, 2000f, 0f);
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(box.GetComponent<Collider>());
            box.transform.SetParent(cloudRoot.transform);
            var mat = new Material(shader);
            mat.SetTexture("_Noise", noise);
            NukeVolumeShader.Bounds(ref v, out var min, out var max);
            box.transform.localPosition = 0.5f * (min + max);
            box.transform.localScale = max - min;
            NukeVolumeShader.Apply(mat, ref v, cloudRoot.transform.worldToLocalMatrix, min, max, 96, shot.debug);
            box.GetComponent<Renderer>().sharedMaterial = mat;
            // the ground shadow, drawn directly (full resolution)
            var shadowShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/BDNukesCloudShadow.shader");
            Material shadowMat = null;
            if (shadowShader != null && !ShaderUtil.ShaderHasError(shadowShader))
            {
                var sbox = GameObject.CreatePrimitive(PrimitiveType.Cube);
                UnityEngine.Object.DestroyImmediate(sbox.GetComponent<Collider>());
                sbox.transform.SetParent(cloudRoot.transform);
                NukeVolumeShader.ShadowBounds(ref v, min, max, out var smin, out var smax);
                sbox.transform.localPosition = 0.5f * (smin + smax);
                sbox.transform.localScale = smax - smin;
                shadowMat = new Material(shadowShader);
                shadowMat.SetTexture("_Noise", noise);
                NukeVolumeShader.Apply(shadowMat, ref v, cloudRoot.transform.worldToLocalMatrix, min, max, 96);
                shadowMat.SetVector("_ShadowMin", smin);
                shadowMat.SetVector("_ShadowMax", smax);
                shadowMat.SetVector("_ShadowParams", new Vector4(0.75f, 6f * (max.y - v.groundY), 0f, 0f));
                sbox.GetComponent<Renderer>().sharedMaterial = shadowMat;
            }
            UnityEngine.Rendering.CommandBuffer cb = null;
            Material compositeMat = null;

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(root.transform);
            var cam = camGo.AddComponent<Camera>();
            float capTop = v.capCenter.y + v.capScale * 0.6f + (shot.air ? 2000f : 0f);
            // frame the whole cloud: back off as it grows (keeping the given direction and elevation)
            float need = capTop * 1.9f / Mathf.Tan(shot.fov * 0.5f * Mathf.Deg2Rad) * 0.5f;
            Vector3 camPos = shot.cam;
            if (!shot.debug && shot.lookFrac < 0.99f && camPos.magnitude < need) camPos = camPos.normalized * need;
            cam.transform.position = camPos;
            cam.transform.position = camPos + new Vector3(v.capCenter.x * 0.5f, 0f, 0f);
            cam.transform.LookAt(new Vector3(v.capCenter.x * 0.5f, capTop * shot.lookFrac, 0f));
            cam.fieldOfView = shot.fov;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 300000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.Lerp(Color.Lerp(new Color(0.42f, 0.6f, 0.86f), new Color(0.6f, 0.62f, 0.65f), 1f - shot.sunVis), new Color(0.62f, 0.7f, 0.8f), shot.haze);
            cam.depthTextureMode = DepthTextureMode.Depth;
            if (shot.div > 1)
            {
                box.GetComponent<Renderer>().enabled = false;
                compositeMat = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/BDNukesVolumeComposite.shader"));
                cb = new UnityEngine.Rendering.CommandBuffer { name = "BDNukes volume" };
                var items = new System.Collections.Generic.List<NukeVolumeCompositor.Item>
                {
                    new NukeVolumeCompositor.Item { mesh = box.GetComponent<MeshFilter>().sharedMesh, matrix = box.transform.localToWorldMatrix, material = mat },
                };
                NukeVolumeCompositor.Build(cb, items, compositeMat, shot.div);
                cam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, cb);
            }
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var img = new Texture2D(W, H, TextureFormat.RGB24, false);
            img.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            img.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            if (cb != null) { cam.RemoveAllCommandBuffers(); cb.Release(); }
            if (compositeMat != null) UnityEngine.Object.DestroyImmediate(compositeMat);
            if (shadowMat != null) UnityEngine.Object.DestroyImmediate(shadowMat);
            UnityEngine.Object.DestroyImmediate(mat);
            UnityEngine.Object.DestroyImmediate(gm);
            return img;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
