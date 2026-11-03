using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Procedurally builds the AN602 "Tsar Bomba" casing: 8 m long, 2.1 m diameter. It has a blunt rounded nose,
    /// a long cylindrical body, a boat-tail with the parachute container at the rear, and four large tail fins.
    ///
    /// The part config points MODEL at a tiny placeholder mesh. The mesh is built on the part prefab during
    /// loading, so the editor icon, drag cubes and every spawned copy get the real shape.
    /// BDArmory convention: nose along +Z, attached by its +Y side.
    /// </summary>
    public class ModuleTsarBombaModel : PartModule
    {
        const string RootName = "BDNukes_TsarBomba";
        const float Length = 8f;
        const float Radius = 1.05f;
        const int Segments = 56;

        // Profile from nose (z = +Length/2) to tail, as (distance from nose, radius).
        static readonly Vector2[] Profile =
        {
            new Vector2(0.00f, 0.00f),
            new Vector2(0.04f, 0.30f),
            new Vector2(0.14f, 0.55f),
            new Vector2(0.30f, 0.76f),
            new Vector2(0.52f, 0.91f),
            new Vector2(0.80f, 1.00f),
            new Vector2(1.15f, 1.045f),
            new Vector2(1.55f, 1.05f),
            new Vector2(1.58f, 1.065f), // forward joint band
            new Vector2(1.66f, 1.065f),
            new Vector2(1.69f, 1.05f),
            new Vector2(5.30f, 1.05f),
            new Vector2(5.33f, 1.065f), // aft joint band
            new Vector2(5.41f, 1.065f),
            new Vector2(5.44f, 1.05f),
            new Vector2(5.80f, 1.03f),
            new Vector2(6.40f, 0.93f),
            new Vector2(7.00f, 0.78f),
            new Vector2(7.30f, 0.72f),
            new Vector2(7.32f, 0.66f),  // step to parachute container
            new Vector2(7.92f, 0.64f),
            new Vector2(7.96f, 0.58f),
            new Vector2(8.00f, 0.00f),
        };

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            Build();
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Build(); // no-op if the prefab already carried it over
        }

        void Build()
        {
            Transform model = part.transform.Find("model");
            if (model == null) model = part.transform;
            if (model.Find(RootName) != null) return;

            StripPlaceholder(model);

            var root = new GameObject(RootName);
            root.transform.SetParent(model, false);

            var bodyMat = MakeMaterial(MakeBodyTexture(), 0.35f);
            var finMat = MakeMaterial(MakeFinTexture(), 0.25f);

            var body = new GameObject("Casing");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = BuildLathe();
            body.AddComponent<MeshRenderer>().sharedMaterial = bodyMat;

            var fins = new GameObject("Fins");
            fins.transform.SetParent(root.transform, false);
            fins.AddComponent<MeshFilter>().sharedMesh = BuildFins();
            fins.AddComponent<MeshRenderer>().sharedMaterial = finMat;

            var col = root.AddComponent<CapsuleCollider>();
            col.direction = 2;
            col.radius = Radius;
            col.height = Length;
            col.center = Vector3.zero;

            var finCol = fins.AddComponent<BoxCollider>();
            finCol.center = new Vector3(0f, 0f, -Length / 2f + 0.75f);
            finCol.size = new Vector3(3.3f, 3.3f, 1.2f);
        }

        static void StripPlaceholder(Transform model)
        {
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true)) Object.DestroyImmediate(r);
            foreach (var f in model.GetComponentsInChildren<MeshFilter>(true)) Object.DestroyImmediate(f);
        }

        static Material MakeMaterial(Texture2D tex, float shininess)
        {
            Shader sh = Shader.Find("KSP/Specular") ?? Shader.Find("KSP/Diffuse") ?? Shader.Find("Standard");
            var m = new Material(sh) { mainTexture = tex };
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.35f, 0.35f, 0.35f));
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
            return m;
        }

        // ----------------------------------------------------------------------------------------
        // Geometry
        // ----------------------------------------------------------------------------------------

        static Mesh BuildLathe()
        {
            int rings = Profile.Length;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int i = 0; i < rings; i++)
            {
                Vector2 prev = Profile[Mathf.Max(0, i - 1)], next = Profile[Mathf.Min(rings - 1, i + 1)];
                // profile tangent in (z-from-nose, r); outward normal is perpendicular to it
                float dz = next.x - prev.x, dr = next.y - prev.y;
                Vector2 n2 = new Vector2(-dr, dz).normalized; // (axial component along nose-direction, radial)
                float z = Length / 2f - Profile[i].x;
                for (int s = 0; s <= Segments; s++)
                {
                    float a = s / (float)Segments * 2f * Mathf.PI;
                    float cx = Mathf.Cos(a), cy = Mathf.Sin(a);
                    verts.Add(new Vector3(cx * Profile[i].y, cy * Profile[i].y, z));
                    // n2.x is along "distance from nose"; +Z points the other way (towards the nose)
                    norms.Add(new Vector3(cx * n2.y, cy * n2.y, -n2.x).normalized);
                    uvs.Add(new Vector2(s / (float)Segments, Profile[i].x / Length));
                }
            }
            int stride = Segments + 1;
            for (int i = 0; i < rings - 1; i++)
            {
                for (int s = 0; s < Segments; s++)
                {
                    int a = i * stride + s, b = a + 1, c = a + stride, d = c + 1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            }
            var mesh = new Mesh { name = "TsarBombaCasing" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            FixWinding(mesh);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>Ensure triangles face along their normals (outward), whatever the profile direction.</summary>
        static void FixWinding(Mesh mesh)
        {
            var v = mesh.vertices; var n = mesh.normals; var t = mesh.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                Vector3 avg = n[t[i]] + n[t[i + 1]] + n[t[i + 2]];
                if (Vector3.Dot(fn, avg) < 0f) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
            }
            mesh.triangles = t;
        }

        static float RadiusAt(float fromNose)
        {
            for (int i = 1; i < Profile.Length; i++)
            {
                if (fromNose <= Profile[i].x)
                {
                    float k = Mathf.InverseLerp(Profile[i - 1].x, Profile[i].x, fromNose);
                    return Mathf.Lerp(Profile[i - 1].y, Profile[i].y, k);
                }
            }
            return 0f;
        }

        /// <summary>Four cruciform trapezoidal fins on the boat-tail, each a thin flat-shaded prism.</summary>
        static Mesh BuildFins()
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            const float rootFront = 5.75f, rootBack = 7.45f;   // along the body, from the nose
            const float tipFront = 6.75f, tipBack = 7.75f;
            const float span = 1.70f;                          // tip radius from the axis
            const float half = 0.035f;                         // half thickness

            for (int f = 0; f < 4; f++)
            {
                float ang = (45f + 90f * f) * Mathf.Deg2Rad;
                Vector3 radial = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                Vector3 side = new Vector3(-Mathf.Sin(ang), Mathf.Cos(ang), 0f) * half;

                Vector3 P(float fromNose, float r) => radial * r + new Vector3(0f, 0f, Length / 2f - fromNose);
                Vector3 rf = P(rootFront, RadiusAt(rootFront) - 0.02f), rb = P(rootBack, RadiusAt(rootBack) - 0.02f);
                Vector3 tf = P(tipFront, span), tb = P(tipBack, span);

                // two broad faces
                Quad(verts, norms, uvs, tris, rf + side, tf + side, tb + side, rb + side);
                Quad(verts, norms, uvs, tris, rb - side, tb - side, tf - side, rf - side);
                // edges
                Quad(verts, norms, uvs, tris, rf - side, tf - side, tf + side, rf + side); // leading edge
                Quad(verts, norms, uvs, tris, tf - side, tb - side, tb + side, tf + side); // tip
                Quad(verts, norms, uvs, tris, tb - side, rb - side, rb + side, tb + side); // trailing edge
            }
            var mesh = new Mesh { name = "TsarBombaFins" };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            FixWinding(mesh);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static void Quad(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            Vector3 normal = Vector3.Cross(b - a, d - a).normalized;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            for (int k = 0; k < 4; k++) n.Add(normal);
            uv.Add(new Vector2(0f, 0f)); uv.Add(new Vector2(1f, 0f)); uv.Add(new Vector2(1f, 1f)); uv.Add(new Vector2(0f, 1f));
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        // ----------------------------------------------------------------------------------------
        // Textures: weathered light-grey casing with panel seams, rivet lines and a darker tail section.
        // U runs around the circumference, V from nose (0) to tail (1).
        // ----------------------------------------------------------------------------------------

        static Texture2D bodyTex, finTex;

        static float Noise(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        static Texture2D MakeBodyTexture()
        {
            if (bodyTex != null) return bodyTex;
            const int w = 1024, h = 512;
            var px = new Color[w * h];
            Color baseCol = new Color(0.66f, 0.68f, 0.64f);
            Color tailCol = new Color(0.42f, 0.44f, 0.42f);
            float[] seams = { 1.62f / Length, 3.5f / Length, 5.37f / Length, 7.31f / Length };
            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                float fromNose = v * Length;
                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w;
                    Color c = fromNose > 7.31f ? tailCol : baseCol;
                    // nose cap slightly darker and more weathered
                    if (fromNose < 0.8f) c *= 0.92f;
                    // large-scale weathering and fine grain
                    float grime = Noise(x / 64, y / 32, 3) * 0.06f + Noise(x / 8, y / 4, 5) * 0.03f + Noise(x, y, 7) * 0.02f;
                    c *= 0.95f + grime;
                    // circumferential seams
                    foreach (float s in seams)
                    {
                        float dv = Mathf.Abs(v - s) * h;
                        if (dv < 1.5f) c *= 0.55f;
                        else if (dv > 5f && dv < 6f && x % 12 < 3) c *= 0.75f; // rivet rows beside each seam
                    }
                    // longitudinal panel seams every 45 degrees on the body section
                    float du = Mathf.Abs(Mathf.Repeat(u * 8f + 0.5f, 1f) - 0.5f) * w / 8f;
                    if (fromNose > 1.7f && fromNose < 7.3f && du < 1.2f) c *= 0.62f;
                    // lifting lugs / hatch outline on top centre
                    if (fromNose > 3.0f && fromNose < 3.8f && Mathf.Abs(u - 0.25f) * w < 40f &&
                        (Mathf.Abs(fromNose - 3.0f) * h / Length < 1.5f || Mathf.Abs(fromNose - 3.8f) * h / Length < 1.5f ||
                         Mathf.Abs(Mathf.Abs(u - 0.25f) * w - 40f) < 1.5f))
                        c *= 0.5f;
                    // red identification band ahead of the aft joint
                    if (fromNose > 4.9f && fromNose < 5.05f) c = Color.Lerp(c, new Color(0.62f, 0.12f, 0.1f), 0.85f);
                    c.a = 1f;
                    px[y * w + x] = c;
                }
            }
            bodyTex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            bodyTex.SetPixels(px);
            bodyTex.Apply(true, true);
            return bodyTex;
        }

        static Texture2D MakeFinTexture()
        {
            if (finTex != null) return finTex;
            const int s = 128;
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float g = 0.45f + Noise(x / 8, y / 8, 11) * 0.05f + Noise(x, y, 13) * 0.03f;
                    if (x < 3 || y < 3 || x > s - 4 || y > s - 4) g *= 0.7f;
                    px[y * s + x] = new Color(g, g * 1.02f, g * 0.98f, 1f);
                }
            finTex = new Texture2D(s, s, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            finTex.SetPixels(px);
            finTex.Apply(true, true);
            return finTex;
        }
    }
}
