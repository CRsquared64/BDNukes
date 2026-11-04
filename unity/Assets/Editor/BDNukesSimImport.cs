// Imports the Blender fireball bakes (unity/Sim/out/<variant>: meta.json + frame_NNN.raw) as 3D textures for the
// shader bundle:
//   Assets/Sim/<variant>/f00.asset ... (Texture3D RGBA32: R smoke density, G flame, B temperature; Unity axes)
//   Assets/Sim/<variant>/<variant>_meta.json (TextAsset: frame times and per-frame plume top / radius, in R)
// Blender is z-up: Blender (x, y, z) -> Unity (x, z, y).
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using BDNukes;
using UnityEngine;

public static class BDNukesSimImport
{
    [Serializable]
    class BakeMeta
    {
        public string variant;
        public int[] resolution;
        public float[] boundsMin, boundsMax;
        public float[] times;
    }

    static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

    public static string[] ImportAll()
    {
        var assets = new List<string>();
        string outRoot = Path.Combine(RepoRoot, "unity", "Sim", "out");
        if (!Directory.Exists(outRoot)) return assets.ToArray();
        foreach (var dir in Directory.GetDirectories(outRoot))
            if (File.Exists(Path.Combine(dir, "meta.json"))) assets.AddRange(Import(dir));
        return assets.ToArray();
    }

    static string[] Import(string dir)
    {
        var bm = JsonUtility.FromJson<BakeMeta>(File.ReadAllText(Path.Combine(dir, "meta.json")));
        int bx = bm.resolution[0], by = bm.resolution[1], bz = bm.resolution[2];
        int n = bx * by * bz;
        int frames = bm.times.Length;
        string variant = bm.variant;
        string assetDir = "Assets/Sim/" + variant;
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Sim", variant));

        // pass 1: maxima for normalisation
        var data = new List<float[]>();
        float maxD = 1e-6f, maxF = 1e-6f, maxT = 1e-6f;
        for (int f = 0; f < frames; f++)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, string.Format("frame_{0:000}.raw", f)));
            var arr = new float[3 * n];
            Buffer.BlockCopy(bytes, 0, arr, 0, Math.Min(bytes.Length, arr.Length * 4));
            for (int i = 0; i < n; i++)
            {
                maxD = Mathf.Max(maxD, arr[i]);
                maxF = Mathf.Max(maxF, arr[n + i]);
                maxT = Mathf.Max(maxT, arr[2 * n + i]);
            }
            data.Add(arr);
        }

        var meta = new NukeSimMeta
        {
            variant = variant, resX = bx, resY = bz, resZ = by,
            boundsMin = new[] { bm.boundsMin[0], bm.boundsMin[2], bm.boundsMin[1] },
            boundsMax = new[] { bm.boundsMax[0], bm.boundsMax[2], bm.boundsMax[1] },
            times = bm.times, top = new float[frames], radius = new float[frames],
            densityScale = maxD, flameScale = maxF, tempScale = maxT,
        };
        float cellX = (bm.boundsMax[0] - bm.boundsMin[0]) / bx, cellZ = (bm.boundsMax[2] - bm.boundsMin[2]) / bz;
        var outAssets = new List<string>();
        for (int f = 0; f < frames; f++)
        {
            var arr = data[f];
            float frameMaxD = 1e-6f;
            for (int i = 0; i < n; i++) frameMaxD = Mathf.Max(frameMaxD, arr[i]);
            // each frame on its own scale: real smoke doesn't thin out as fast as the sim's dissipation
            float dScale = Mathf.Lerp(maxD, frameMaxD, 0.75f);
            var tex = new Texture3D(bx, bz, by, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, name = variant + "_" + f.ToString("00"),
            };
            var cols = new Color32[n];
            float topZ = 0f, rad = 0f;
            float thr = 0.08f * maxD;
            for (int k = 0; k < bz; k++)
                for (int j = 0; j < by; j++)
                    for (int i = 0; i < bx; i++)
                    {
                        int src = i + bx * (j + by * k);                  // Mantaflow: x fastest, then y, then z
                        float d = arr[src] / dScale, fl = arr[n + src] / maxF, te = arr[2 * n + src] / maxT;
                        // smoke stored with a gentle curve so thin wisps survive 8-bit quantisation
                        cols[i + bx * (k + bz * j)] = new Color32((byte)(Mathf.Sqrt(Mathf.Clamp01(d)) * 255f),
                            (byte)(Mathf.Clamp01(fl) * 255f), (byte)(Mathf.Clamp01(te) * 255f), 255);
                        if (arr[src] > thr || arr[n + src] > 0.08f * maxF)
                        {
                            topZ = Mathf.Max(topZ, (k + 1) * cellZ);
                            float x = (i + 0.5f) * cellX + bm.boundsMin[0], y = (j + 0.5f) * cellX + bm.boundsMin[1];
                            rad = Mathf.Max(rad, Mathf.Sqrt(x * x + y * y));
                        }
                    }
            tex.SetPixels32(cols);
            tex.Apply(false, false);
            string path = assetDir + "/f" + f.ToString("00") + ".asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(tex, path);
            outAssets.Add(path);
            meta.top[f] = topZ;
            meta.radius[f] = Mathf.Max(rad, cellX);
        }
        string metaPath = assetDir + "/" + variant + "_meta.json";
        File.WriteAllText(Path.Combine(Application.dataPath, "Sim", variant, variant + "_meta.json"), JsonUtility.ToJson(meta, true));
        AssetDatabase.ImportAsset(metaPath);
        outAssets.Add(metaPath);
        AssetDatabase.SaveAssets();
        Debug.Log(string.Format("[BDNukesBuild] imported sim '{0}': {1} frames {2}x{3}x{4}, max density {5:F2} flame {6:F2} temp {7:F2}",
            variant, frames, bx, bz, by, maxD, maxF, maxT));
        return outAssets.ToArray();
    }
}
