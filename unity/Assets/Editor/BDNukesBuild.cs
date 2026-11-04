// Builds the BDNukes shader bundle and renders test images of the volumetric cloud.
//
//   Unity.exe -batchmode -quit -projectPath <this folder> -executeMethod BDNukesBuild.BuildAll
//   Unity.exe -batchmode -quit -projectPath <this folder> -executeMethod BDNukesBuild.RenderTests
//
// BuildAll writes GameData/BDNukes/Shaders/bdnukes-windows.shaders (Windows / DirectX 11, what KSP 1.12 runs on).
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BDNukesBuild
{
    const string NoisePath = "Assets/Noise/BDNukesBillows.asset";
    const string ShaderPath = "Assets/Shaders/BDNukesVolumetricCloud.shader";
    const string BundleName = "bdnukes-windows.shaders";

    static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

    // ------------------------------------------------------------------------------------------------------
    // Noise: R = Perlin-Worley billows (rounded cauliflower lumps), G = Worley fBm for fine erosion
    // ------------------------------------------------------------------------------------------------------

    public static Texture3D MakeNoise(int size)
    {
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
                    // Perlin-Worley: Perlin remapped by the billows, so it keeps the round lumps but breaks their regularity
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
                grads[i] = UnityEngine.Random.onUnitSphere;
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

    public static void BuildAll()
    {
        EnsureNoise();
        AssetDatabase.Refresh();
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null) throw new Exception("shader missing: " + ShaderPath);
        if (ShaderUtil.ShaderHasError(shader)) throw new Exception("shader has compile errors");

        string outDir = Path.Combine(RepoRoot, "unity", "Build");
        Directory.CreateDirectory(outDir);
        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = new[] { ShaderPath, NoisePath },
        };
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
    // Test renders: a mock 20 kt surface burst a minute in, from a few viewpoints and weathers
    // ------------------------------------------------------------------------------------------------------

    public static void NoiseStats()
    {
        var noise = EnsureNoise();
        var px = noise.GetPixels32(0);
        long sr = 0, sg = 0; int minR = 255, maxR = 0, minG = 255, maxG = 0;
        foreach (var c in px) { sr += c.r; sg += c.g; minR = Math.Min(minR, c.r); maxR = Math.Max(maxR, c.r); minG = Math.Min(minG, c.g); maxG = Math.Max(maxG, c.g); }
        Debug.Log(string.Format("[BDNukesBuild] noise {0} {1}x{2}x{3} fmt {4}: R {5}..{6} mean {7}, G {8}..{9} mean {10}",
            noise.name, noise.width, noise.height, noise.depth, noise.format, minR, maxR, sr / px.Length, minG, maxG, sg / px.Length));
    }

    static bool debugNoise;

    public static void RenderTests()
    {
        NoiseStats();
        var noise = EnsureNoise();
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null || ShaderUtil.ShaderHasError(shader)) throw new Exception("shader missing or has errors");
        string outDir = Path.Combine(RepoRoot, "unity", "Build", "tests");
        Directory.CreateDirectory(outDir);

        Render(shader, noise, Path.Combine(outDir, "01_clear_8km.png"), new Vector3(0f, 300f, -8000f), 1f, 0f, 1f);
        Render(shader, noise, Path.Combine(outDir, "02_clear_3km_low.png"), new Vector3(0f, 50f, -3500f), 1f, 0f, 1f);
        Render(shader, noise, Path.Combine(outDir, "03_overcast_8km.png"), new Vector3(0f, 300f, -8000f), 0.2f, 0.15f, 1f);
        Render(shader, noise, Path.Combine(outDir, "04_far_40km_haze.png"), new Vector3(0f, 2000f, -40000f), 1f, 0.45f, 1f);
        Render(shader, noise, Path.Combine(outDir, "05_fireball_1s.png"), new Vector3(0f, 150f, -3000f), 1f, 0f, 0.0f);
        Render(shader, noise, Path.Combine(outDir, "06_fireball_rolling_6s.png"), new Vector3(0f, 150f, -4000f), 1f, 0f, 0.12f);
        Render(shader, noise, Path.Combine(outDir, "07_cooling_20s.png"), new Vector3(0f, 200f, -6000f), 1f, 0f, 0.35f);
        debugNoise = true;
        Render(shader, noise, Path.Combine(outDir, "99_debug_noise.png"), new Vector3(0f, 300f, -8000f), 1f, 0f, 1f);
        debugNoise = false;
        Debug.Log("[BDNukesBuild] test renders written to " + outDir);
    }

    static void Render(Shader shader, Texture3D noise, string file, Vector3 camPos, float sunVis, float haze, float age)
    {
        const int W = 1280, H = 720;
        var root = new GameObject("TestRoot");
        try
        {
            // ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.SetParent(root.transform);
            ground.transform.localScale = new Vector3(20000f, 1f, 20000f);
            var gm = new Material(Shader.Find("Unlit/Color"));
            gm.color = Color.Lerp(new Color(0.22f, 0.3f, 0.2f), new Color(0.6f, 0.68f, 0.78f), haze);
            ground.GetComponent<Renderer>().sharedMaterial = gm;

            // the cloud: same parameter layout NukeCinematic sends, for a 20 kt burst (R ~ 230 m)
            float R = 230f;
            float rise = Mathf.Sqrt(age);
            float capScale = Mathf.Lerp(R, 3.2f * R, rise);
            float capY = Mathf.Lerp(R * 0.9f, 2600f, rise);
            float m = Mathf.Clamp01(age / 0.3f);          // ball -> rolling torus
            float heat = Mathf.Clamp01(1f - age / 0.4f);  // white-hot -> cold smoke
            var cloudRoot = new GameObject("Cloud");
            cloudRoot.transform.SetParent(root.transform);
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(box.GetComponent<Collider>());
            box.transform.SetParent(cloudRoot.transform);
            var mat = new Material(shader);
            mat.SetTexture("_Noise", noise);
            Vector3 drift = new Vector3(300f * age, 0f, 0f);
            Vector3 capC = new Vector3(0f, capY, 0f) + drift;
            float skirtR = Mathf.Lerp(1.2f * R, 3.5f * R, age);
            Vector3 bmin = new Vector3(-Mathf.Max(1.6f * capScale, skirtR) - 50f + Mathf.Min(0f, drift.x), -20f, -Mathf.Max(1.6f * capScale, skirtR) - 50f);
            Vector3 bmax = new Vector3(Mathf.Max(1.6f * capScale, skirtR) + 50f + Mathf.Max(0f, drift.x), capY + 1.2f * capScale, Mathf.Max(1.6f * capScale, skirtR) + 50f);
            box.transform.localPosition = 0.5f * (bmin + bmax);
            box.transform.localScale = bmax - bmin;
            mat.SetMatrix("_WorldToCloud", cloudRoot.transform.worldToLocalMatrix);
            mat.SetMatrix("_CloudToWorld", cloudRoot.transform.localToWorldMatrix);
            mat.SetVector("_BoxMin", bmin);
            mat.SetVector("_BoxMax", bmax);
            mat.SetVector("_CapXZ", new Vector4(1f, 0.3f, 0f, 0f));
            mat.SetVector("_CapCenter", new Vector4(capC.x, capC.y, capC.z, capScale));
            mat.SetVector("_CapShape", new Vector4(Mathf.Lerp(0.05f, 0.55f, m), Mathf.Lerp(0.3f, 0.5f, m), Mathf.Lerp(0.85f, 0.6f, m), Mathf.Lerp(1f, 0.8f, m)));
            float stemTopR = 0.17f * capScale * 0.9f;
            mat.SetVector("_StemBase", new Vector4(0f, 0f, 0f, stemTopR * 1.1f));
            mat.SetVector("_StemTop", new Vector4(capC.x, capY - 0.3f * capScale, capC.z, stemTopR));
            mat.SetVector("_StemVis", new Vector4(capY * Mathf.Lerp(0.2f, 1.1f, m), capY * Mathf.Lerp(0.95f, 0.4f, m), 0.9f, 2.2f));
            mat.SetVector("_SkirtCenter", new Vector4(0f, 0f, 0f, skirtR));
            mat.SetVector("_Skirt", new Vector4(0.35f * R, 0.9f * m, 0.35f, 0f));
            mat.SetVector("_Density", new Vector4(1f, 7f / capScale, 0.3f, 1f / (1.3f * capScale)));
            mat.SetVector("_Anim", new Vector4(60f * age, 2.5f * rise, 0f, 0f));
            mat.SetVector("_Fire", new Vector4(3f * heat, Mathf.Lerp(0.15f, 1f, heat), 0.7f, 0f));
            Vector3 sun = new Vector3(-0.5f, 0.6f, 0.45f).normalized;
            mat.SetVector("_SunDir", new Vector4(sun.x, sun.y, sun.z, 1f));
            mat.SetVector("_SunCol", new Vector4(1.25f, 1.18f, 1.05f, sunVis));
            mat.SetVector("_SunLow", new Vector4(sunVis, 0f, 0f, 0f));
            Color amb = Color.Lerp(new Color(0.42f, 0.5f, 0.62f), new Color(0.5f, 0.52f, 0.55f), 1f - sunVis);
            mat.SetVector("_Ambient", amb);
            mat.SetVector("_Albedo", new Color(0.82f, 0.7f, 0.58f));
            mat.SetVector("_AlbedoDark", new Color(0.45f, 0.36f, 0.3f));
            float glow = heat * heat * 2f;
            mat.SetVector("_Glow", new Vector4(1.0f * glow, 0.45f * glow, 0.15f * glow, 0.6f * capScale));
            mat.SetVector("_GlowCenter", capC);
            Color hc = Color.Lerp(new Color(0.62f, 0.7f, 0.8f), new Color(0.56f, 0.58f, 0.61f), 1f - sunVis);
            mat.SetVector("_HazeCol", new Vector4(hc.r, hc.g, hc.b, haze));
            mat.SetVector("_March", new Vector4(64f, 5f, 1f, debugNoise ? 1f : 0f));
            box.GetComponent<Renderer>().sharedMaterial = mat;

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(root.transform);
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = camPos;
            cam.transform.LookAt(new Vector3(0f, capY * 0.55f, 0f));
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 200000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.Lerp(new Color(0.45f, 0.62f, 0.85f), new Color(0.6f, 0.62f, 0.65f), 1f - sunVis);
            cam.depthTextureMode = DepthTextureMode.Depth;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var img = new Texture2D(W, H, TextureFormat.RGB24, false);
            img.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            img.Apply();
            File.WriteAllBytes(file, img.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(img);
            UnityEngine.Object.DestroyImmediate(mat);
            UnityEngine.Object.DestroyImmediate(gm);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
