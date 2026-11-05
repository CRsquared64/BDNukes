using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Test renders of the sea waves (BDNukes/WaterWave): the same wave train and shock ring as NukeWaterWaves, on a
/// flat sea, so their look can be checked without the game. Sheet 14 of RenderTests.
/// </summary>
public static class BDNukesWaterTest
{
    struct Shot { public float W, t, hb, depth; public Vector3 cam; public Vector3 look; public float fov; public float sunElev; }

    public static void Sheet(string outDir)
    {
        string only = Environment.GetEnvironmentVariable("BDNUKES_TESTS");
        if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(','), "14") < 0) return;
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/BDNukesWaterWave.shader");
        if (shader == null || !shader.isSupported) { Debug.LogError("[BDNukesBuild] water wave shader missing or unsupported"); return; }
        var shots = new[]
        {
            // Baker (23 kt at 27 m): the wave train from a low boat, then from above
            new Shot { W = 23f, t = 30f, depth = 27f, cam = new Vector3(0f, 80f, -2600f), look = new Vector3(0f, 0f, -900f), fov = 50f, sunElev = 0.5f },
            new Shot { W = 23f, t = 60f, depth = 27f, cam = new Vector3(0f, 2500f, -4500f), look = Vector3.zero, fov = 50f, sunElev = 0.5f },
            // a megaton at the surface, low evening sun
            new Shot { W = 1000f, t = 120f, depth = 0f, hb = 50f, cam = new Vector3(0f, 300f, -14000f), look = new Vector3(0f, 0f, -7000f), fov = 50f, sunElev = 0.12f },
            // a 20 kt air burst 600 m up: the shock ring on the water, from 3 km up
            new Shot { W = 20f, t = 6f, hb = 600f, cam = new Vector3(0f, 3000f, -5000f), look = Vector3.zero, fov = 55f, sunElev = 0.6f },
        };
        const int W = 640, H = 400, cols = 2;
        int rows = (shots.Length + cols - 1) / cols;
        var sheet = new Texture2D(W * cols, H * rows, TextureFormat.RGB24, false);
        for (int i = 0; i < shots.Length; i++)
        {
            var img = Render(shader, shots[i], W, H);
            sheet.SetPixels((i % cols) * W, (rows - 1 - i / cols) * H, W, H, img.GetPixels());
            UnityEngine.Object.DestroyImmediate(img);
        }
        sheet.Apply();
        File.WriteAllBytes(Path.Combine(outDir, "14_water_waves.png"), sheet.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(sheet);
    }

    static Texture2D Render(Shader shader, Shot s, int W, int H)
    {
        var root = new GameObject("WaterTestRoot");
        try
        {
            float R = 70f * Mathf.Pow(s.W, 0.4f);
            float lambda = Mathf.Clamp(600f * Mathf.Pow(s.W / 23f, 0.3f), 60f, 4000f);
            float f = s.depth > 0f ? 1f : (s.hb < R ? Mathf.Lerp(0.45f, 0.2f, s.hb / R) : 0.2f * Mathf.Exp(-(s.hb - R) / (0.7f * R)));
            float A0 = f * 1550f * Mathf.Pow(s.W, 0.54f);
            float g = 9.81f, cW = Mathf.Sqrt(g * lambda / (2f * Mathf.PI));
            float front = 0.5f * lambda + cW * s.t;
            bool waves = A0 / lambda >= 0.4f;
            bool ring = s.depth <= 0f && s.hb > 0.25f * R;

            Vector3 sun = new Vector3(0.3f, s.sunElev, 1f).normalized;
            float elev = sun.y;
            Color sunCol = Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.97f, 0.9f), Mathf.Clamp01(elev * 3f)) * 1.1f;
            Color sky = Color.Lerp(new Color(0.85f, 0.6f, 0.5f), new Color(0.55f, 0.68f, 0.85f), Mathf.Clamp01(elev * 2.5f));

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(root.transform);
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = s.cam;
            cam.transform.LookAt(s.look);
            cam.fieldOfView = s.fov;
            cam.nearClipPlane = 1f; cam.farClipPlane = 200000f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = sky;

            // the stock sea: a flat, plain plane, as the waves sit over KSP's own ocean
            var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
            sea.transform.SetParent(root.transform);
            sea.transform.localScale = Vector3.one * 20000f;
            var seaMat = new Material(Shader.Find("Unlit/Color")) { color = Color.Lerp(new Color(0.05f, 0.12f, 0.17f), sky, 0.35f) };
            sea.GetComponent<Renderer>().sharedMaterial = seaMat;

            var mat = new Material(shader);
            mat.SetVector("_WSunDir", sun);
            mat.SetVector("_WUp", Vector3.up);
            mat.SetColor("_WSunColor", sunCol);
            mat.SetColor("_WSkyColor", sky);
            mat.SetColor("_WDeepColor", new Color(0.03f, 0.09f, 0.13f));
            mat.SetColor("_WAmbient", sky * 0.35f + new Color(0.01f, 0.012f, 0.015f));
            mat.SetVector("_WFade", new Vector4(60000f, 160000f, 0f, 0f));

            if (waves)
            {
                float rOut = front + 0.15f * lambda, rIn = Mathf.Max(0f, front - 4.8f * lambda);
                AddBand(root, mat, 64, 360, j => Mathf.Lerp(rIn, rOut, j), (r, fj) =>
                {
                    float xi = front - r, k = 2f * Mathf.PI / lambda;
                    float A = Mathf.Min(0.08f * lambda * f, A0 / Mathf.Max(r, 0.5f * lambda));
                    float env = xi <= 0f || xi > 5f * lambda ? 0f : Mathf.Exp(-xi / (1.6f * lambda)) * Mathf.Clamp01(1f - (xi - 4f * lambda) / lambda);
                    env *= Mathf.Clamp01(s.t / (0.5f * lambda / cW));
                    A *= env;
                    float ph = k * xi;
                    float eta = A * Mathf.Sin(ph), slope = -A * k * Mathf.Cos(ph);
                    float steep = A * k;
                    float foam = Mathf.Clamp01((steep - 0.08f) * 6f) * Mathf.Clamp01(Mathf.Sin(ph) * 1.5f);
                    float Ar = Mathf.Min(0.08f * lambda * f, A0 / Mathf.Max(r, 0.5f * lambda));
                    float show = Mathf.Clamp01(Mathf.Abs(slope) / Mathf.Max(0.004f, 0.55f * Ar * k)) * 0.7f + foam * 0.6f;
                    float edge = Mathf.Clamp01(fj * 6f) * Mathf.Clamp01((1f - fj) * 12f);
                    return new Vector4(0.35f + Mathf.Max(eta, 0.12f * eta), slope, foam, Mathf.Clamp01(show) * edge) ;
                }, false);
            }
            if (ring)
            {
                float cs = 420f;
                float rs = R + cs * s.t, gg = rs * rs - s.hb * s.hb;
                float rg = gg > 0f ? Mathf.Sqrt(gg) : 0f;
                float w = Mathf.Max(0.08f * R, 15f) * (1f + rg / (6f * R));
                float rOut = rg + 2.5f * w, rIn = Mathf.Max(0f, rg - Mathf.Max(3f * R, 12f * w));
                AddBand(root, mat, 24, 360, j => Mathf.Lerp(rIn, rOut, 1f - (1f - j) * (1f - j)), (r, fj) =>
                {
                    float d = rg - r;
                    float strength = Mathf.Pow(Mathf.Clamp01(3.5f * R / rs), 0.7f) * Mathf.Clamp01(1f - rs / (18f * R));
                    float frontK = Mathf.Exp(-(d * d) / (w * w));
                    float behind = d > 0f ? Mathf.Exp(-d / Mathf.Max(R, 3f * w)) * Mathf.Exp(-s.t / 60f) * 0.55f : 0f;
                    float slick = Mathf.Clamp01(strength * (frontK + behind) * 1.6f);
                    float foam = Mathf.Clamp01(strength * frontK * 1.5f - 0.1f);
                    return new Vector4(0.3f, 0f, foam, Mathf.Clamp01(slick * 0.9f + foam)) ;
                }, true, (r) =>
                {
                    float d = rg - r;
                    float strength = Mathf.Pow(Mathf.Clamp01(3.5f * R / rs), 0.7f) * Mathf.Clamp01(1f - rs / (18f * R));
                    float frontK = Mathf.Exp(-(d * d) / (w * w));
                    float behind = d > 0f ? Mathf.Exp(-d / Mathf.Max(R, 3f * w)) * Mathf.Exp(-s.t / 60f) * 0.55f : 0f;
                    return Mathf.Clamp01(strength * (frontK + behind) * 1.6f);
                });
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
            UnityEngine.Object.DestroyImmediate(mat);
            UnityEngine.Object.DestroyImmediate(seaMat);
            return img;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    /// <summary>A ring band; profile(r, row fraction) gives (height, outward slope, foam, alpha).</summary>
    static void AddBand(GameObject root, Material mat, int rows, int na, Func<float, float> radius, Func<float, float, Vector4> profile,
        bool slickBand, Func<float, float> slickOf = null)
    {
        int nv = (rows + 1) * (na + 1);
        var v = new Vector3[nv]; var n = new Vector3[nv]; var c = new Color[nv];
        int idx = 0;
        for (int j = 0; j <= rows; j++)
        {
            float fj = j / (float)rows, r = radius(fj);
            Vector4 p = profile(r, fj);
            float slick = slickBand && slickOf != null ? slickOf(r) : 0f;
            for (int i = 0; i <= na; i++, idx++)
            {
                float th = i * 2f * Mathf.PI / na;
                float cs = Mathf.Cos(th), sn = Mathf.Sin(th);
                v[idx] = new Vector3(sn * r, p.x, cs * r);
                n[idx] = (Vector3.up - new Vector3(sn, 0f, cs) * p.y).normalized;
                c[idx] = new Color(p.z, 0f, slick, p.w);
            }
        }
        var tri = new int[rows * na * 6];
        int k = 0;
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < na; i++)
            {
                int a = j * (na + 1) + i, b = a + na + 1;
                tri[k++] = a; tri[k++] = b; tri[k++] = a + 1;
                tri[k++] = a + 1; tri[k++] = b; tri[k++] = b + 1;
            }
        var m = new Mesh();
        if (nv > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = v; m.normals = n; m.colors = c; m.triangles = tri;
        var go = new GameObject("Band");
        go.transform.SetParent(root.transform);
        go.AddComponent<MeshFilter>().sharedMesh = m;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }
}
