using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A bomb bay: a hollow fuselage section with two belly doors, built from code like the other BDNukes parts (the
    /// part's MODEL is only a placeholder that is stripped at load). It works like a stock cargo bay: pair it with
    /// ModuleAnimateGeneric (the doors, animation "BombBayDoors") and ModuleCargoBay (shields what is inside while the
    /// doors are closed), put a weapon inside by its inner nodes or by surface-attaching it to the inside of the
    /// roof, and open the doors to drop it.
    ///
    /// The part's long axis is Y (it stacks with fuselages), the belly (the doors) is -Z and the roof +Z. A fuselage
    /// placed in the spaceplane hangar's default orientation has its belly down.
    ///
    /// The animation runs open (time 0) to closed (time 1) like the stock bays' (ModuleCargoBay.closedPosition = 1).
    /// It is made here as a legacy clip rotating the two door transforms about their hinges.
    ///
    /// Shapes: "round" (a cylinder of the given width) and "rect" (a rounded rectangle, width x height, corner radius).
    /// </summary>
    public class ModuleBombBay : PartModule
    {
        public const string RootName = "BDNukes_BombBay";
        const string DoorRight = "BombBayDoorR", DoorLeft = "BombBayDoorL";

        [KSPField] public string shape = "round";
        [KSPField] public float length = 2f;
        [KSPField] public float width = 1.25f;
        [KSPField] public float height = 1.25f;
        [KSPField] public float cornerRadius = 0.25f;
        [KSPField] public float wall = 0.06f;
        /// <summary>Round bays: how far round the belly each door reaches from the belly's centre line (degrees).</summary>
        [KSPField] public float doorAngle = 60f;
        /// <summary>Rect bays: how much of the flat belly's half width each door covers.</summary>
        [KSPField] public float doorFraction = 0.9f;
        [KSPField] public float openAngle = 105f;
        [KSPField] public float openTime = 2.5f;
        [KSPField] public float endRing = 0.05f;
        [KSPField] public string animationName = "BombBayDoors";
        /// <summary>
        /// The counterpart part's texture (a GameDatabase url) and the patch of it, in pixels from its top left
        /// (x, y, width, height), that skins the bay's outside; textureDensity is how many pixels of the patch
        /// cover a metre of the bay. The patch is tiled (mirrored) over the whole outside. If the texture is not
        /// installed the bay is painted with bodyColor. innerTexture / innerPatch / innerDensity skin the inside the
        /// same way (the outside's patch, darkened by innerTint, if left out).
        /// </summary>
        [KSPField] public string texture = "";
        [KSPField] public string texturePatch = "";
        [KSPField] public float textureDensity = 128f;
        [KSPField] public string innerTexture = "";
        [KSPField] public string innerPatch = "";
        [KSPField] public float innerDensity = 128f;
        [KSPField] public float innerTint = 0.45f;
        [KSPField] public string bodyColor = "0.55,0.57,0.56";
        [KSPField] public string innerColor = "0.22,0.23,0.24";
        [KSPField] public float shininess = 0.35f;

        const int RoundSteps = 72, CornerSteps = 8;
        static readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, AnimationClip> clipCache = new Dictionary<string, AnimationClip>();

        // ----------------------------------------------------------------------------------------
        // Part setup
        // ----------------------------------------------------------------------------------------

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            // the prefab gets the full MODULE node; instances only the saved fields
            if (node == null || !node.HasValue("shape")) return;
            Build(true);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Build(false);   // no-op when the prefab's model came along
            EnsureClip();
        }

        void Build(bool fromConfig)
        {
            if (part == null) return;
            Transform model = part.transform.Find("model");
            if (model == null) model = part.transform;
            Transform existing = model.Find(RootName);
            if (existing != null)
            {
                if (!fromConfig) return;
                DestroyImmediate(existing.gameObject);
            }
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true)) DestroyImmediate(r);
            foreach (var f in model.GetComponentsInChildren<MeshFilter>(true)) DestroyImmediate(f);

            int layer = model.gameObject.layer;
            var root = new GameObject(RootName) { layer = layer };
            root.transform.SetParent(model, false);

            Loop loop = MakeLoop();
            float half = length * 0.5f;
            string key = Key();
            Func<Material> plainOut = () => ModuleProceduralModel.FinMaterial(ParseColor(bodyColor, new Color(0.55f, 0.57f, 0.56f)), shininess);
            Func<Material> plainIn = () => ModuleProceduralModel.FinMaterial(ParseColor(innerColor, new Color(0.22f, 0.23f, 0.24f)), 0.15f);
            Material outer = SkinMaterial("out|" + key, texture, texturePatch, textureDensity, loop, Color.white, plainOut);
            Material inner = !string.IsNullOrEmpty(innerTexture)
                ? SkinMaterial("in|" + key, innerTexture, innerPatch, innerDensity, loop, Color.white, plainIn)
                : SkinMaterial("out|" + key, texture, texturePatch, textureDensity, loop, new Color(innerTint, innerTint, innerTint, 1f), plainIn,
                    "tint" + innerTint.ToString("R", CultureInfo.InvariantCulture));
            var mats = new[] { outer, inner };

            // the fixed hull: the roof and sides between the two door hinges, plus a short ring at each end all round
            var hull = new GameObject("Hull") { layer = layer };
            hull.transform.SetParent(root.transform, false);
            hull.AddComponent<MeshFilter>().sharedMesh = Cached("hull|" + key, () => BuildHull(loop, half));
            hull.AddComponent<MeshRenderer>().sharedMaterials = mats;

            // the doors, each hinged along a line down the belly's side
            float gap = 0.02f;
            float y0 = -half + endRing + gap, y1 = half - endRing - gap;
            Vector2 hingeR = loop.P[loop.HingeR], hingeL = loop.P[loop.HingeL];
            MakeDoor(root.transform, layer, DoorRight, hingeR, key, mats, () => BuildDoor(loop, 0, loop.HingeR, y0, y1, hingeR));
            MakeDoor(root.transform, layer, DoorLeft, hingeL, key, mats, () => BuildDoor(loop, loop.HingeL, loop.Count, y0, y1, hingeL));

            // colliders: the roof and sides as a few flat slabs (they must be convex; the belly is open)
            var cols = new GameObject("Colliders") { layer = layer };
            cols.transform.SetParent(root.transform, false);
            Collider first = null;
            int step = Mathf.Max(1, (loop.HingeL - loop.HingeR) / 9);
            for (int k = loop.HingeR; k < loop.HingeL; k += step)
            {
                int k2 = Mathf.Min(loop.HingeL, k + step);
                Vector2 a = loop.P[k % loop.Count], b = loop.P[k2 % loop.Count];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f) continue;
                Vector2 dir = d / len, n = new Vector2(dir.y, -dir.x);   // outward for this counter-clockwise run
                float t = Mathf.Max(0.04f, wall);
                var go = new GameObject("Slab" + k) { layer = layer };
                go.transform.SetParent(cols.transform, false);
                Vector2 c = (a + b) * 0.5f - n * (t * 0.5f);
                go.transform.localPosition = new Vector3(c.x, 0f, c.y);
                go.transform.localRotation = Quaternion.Euler(0f, Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg, 0f);
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(len * 1.02f, length - 0.02f, t);
                if (first == null) first = box;
            }

            // the doors' own pose: closed on the prefab (the icon), the animation opens them
            if (first != null) part.collider = first;
            AddClip(root);
            if (!fromConfig)
            {
                part.ResetModelMeshRenderersCache();
                part.ResetModelRenderersCache();
            }
        }

        void MakeDoor(Transform root, int layer, string name, Vector2 hinge, string key, Material[] mats, Func<Mesh> make)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(hinge.x, 0f, hinge.y);
            go.AddComponent<MeshFilter>().sharedMesh = Cached(name + "|" + key, make);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        }

        // ----------------------------------------------------------------------------------------
        // The doors' animation
        // ----------------------------------------------------------------------------------------

        void AddClip(GameObject root)
        {
            var anim = root.GetComponent<Animation>();
            if (anim == null) anim = root.AddComponent<Animation>();
            anim.playAutomatically = false;
            anim.cullingType = AnimationCullingType.AlwaysAnimate;
            if (anim.GetClip(animationName) == null) anim.AddClip(MakeClip(), animationName);
        }

        void EnsureClip()
        {
            if (part == null) return;
            Transform model = part.transform.Find("model");
            Transform root = (model != null ? model : part.transform).Find(RootName);
            if (root == null) return;
            var anim = root.GetComponent<Animation>();
            if (anim == null) { AddClip(root.gameObject); return; }
            if (anim.GetClip(animationName) == null) anim.AddClip(MakeClip(), animationName);
        }

        AnimationClip MakeClip()
        {
            string key = animationName + "|" + Key() + "|" + openAngle.ToString("R", CultureInfo.InvariantCulture) + "|" + openTime.ToString("R", CultureInfo.InvariantCulture);
            if (clipCache.TryGetValue(key, out var cached) && cached != null) return cached;
            Loop loop = MakeLoop();
            // the right door hangs down and outward when open: of the two turns, the one that takes its free edge
            // (the belly's centre line) lower
            Vector2 hinge = loop.P[loop.HingeR], free = loop.P[0] - hinge;
            float up = Rotate(free, openAngle).y, down = Rotate(free, -openAngle).y;
            float rightOpen = down < up ? -openAngle : openAngle;
            var clip = new AnimationClip { name = animationName, legacy = true, wrapMode = WrapMode.Once };
            // time 0 = open, time 1 (openTime) = closed, like the stock bays
            clip.SetCurve(DoorRight, typeof(Transform), "localEulerAnglesRaw.y", AnimationCurve.EaseInOut(0f, rightOpen, openTime, 0f));
            clip.SetCurve(DoorLeft, typeof(Transform), "localEulerAnglesRaw.y", AnimationCurve.EaseInOut(0f, -rightOpen, openTime, 0f));
            clipCache[key] = clip;
            return clip;
        }

        /// <summary>A rotation about the Y axis, the way Unity turns a point in the X-Z plane.</summary>
        static Vector2 Rotate(Vector2 v, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        // ----------------------------------------------------------------------------------------
        // Cross-section
        // ----------------------------------------------------------------------------------------

        /// <summary>The outline of the section in the X-Z plane (x across, z up), counter-clockwise from the belly's centre.</summary>
        class Loop
        {
            public readonly List<Vector2> P = new List<Vector2>();   // outer
            public readonly List<Vector2> Q = new List<Vector2>();   // inner
            public readonly List<Vector2> N = new List<Vector2>();   // outward normals
            public int HingeR, HingeL;
            public int Count => P.Count;
            public float[] Arc;           // distance round the outside from the belly's centre, 0..1, Count + 1 entries
            public float Perimeter;
        }

        Loop MakeLoop()
        {
            var loop = new Loop();
            if (shape == "rect") BuildRect(loop); else BuildRound(loop);
            int count = loop.P.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 d = loop.P[(i + 1) % count] - loop.P[(i + count - 1) % count];
                Vector2 n = new Vector2(d.y, -d.x).normalized;
                loop.N.Add(n);
                loop.Q.Add(loop.P[i] - n * wall);
            }
            loop.Arc = new float[count + 1];
            float run = 0f;
            for (int i = 0; i < count; i++)
            {
                loop.Arc[i] = run;
                run += (loop.P[(i + 1) % count] - loop.P[i]).magnitude;
            }
            loop.Perimeter = Mathf.Max(0.01f, run);
            for (int i = 0; i < count; i++) loop.Arc[i] /= loop.Perimeter;
            loop.Arc[count] = 1f;
            return loop;
        }

        void BuildRound(Loop loop)
        {
            float r = width * 0.5f;
            for (int i = 0; i < RoundSteps; i++)
            {
                float a = i * 2f * Mathf.PI / RoundSteps;
                loop.P.Add(new Vector2(r * Mathf.Sin(a), -r * Mathf.Cos(a)));
            }
            loop.HingeR = Mathf.Clamp(Mathf.RoundToInt(doorAngle / 360f * RoundSteps), 2, RoundSteps / 2 - 6);
            loop.HingeL = RoundSteps - loop.HingeR;
        }

        void BuildRect(Loop loop)
        {
            float a = width * 0.5f, b = height * 0.5f;
            float r = Mathf.Min(cornerRadius, a * 0.9f, b * 0.9f);
            float xh = Mathf.Clamp01(doorFraction) * (a - r);
            var p = loop.P;
            p.Add(new Vector2(0f, -b));                      // 0: the belly's centre
            p.Add(new Vector2(xh, -b)); loop.HingeR = 1;     // the right hinge
            p.Add(new Vector2(a - r, -b));
            Arc(p, new Vector2(a - r, -b + r), r, -90f, 0f);
            p.Add(new Vector2(a, 0f));
            Arc(p, new Vector2(a - r, b - r), r, 0f, 90f);
            p.Add(new Vector2(0f, b));
            Arc(p, new Vector2(-(a - r), b - r), r, 90f, 180f);
            p.Add(new Vector2(-a, 0f));
            Arc(p, new Vector2(-(a - r), -b + r), r, 180f, 270f);
            loop.HingeL = p.Count;                           // the left hinge
            p.Add(new Vector2(-xh, -b));
        }

        static void Arc(List<Vector2> p, Vector2 c, float r, float from, float to)
        {
            for (int k = 0; k <= CornerSteps; k++)
            {
                float a = Mathf.Lerp(from, to, k / (float)CornerSteps) * Mathf.Deg2Rad;
                Vector2 q = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                if (p.Count > 0 && (p[p.Count - 1] - q).sqrMagnitude < 1e-8f) continue;
                p.Add(q);
            }
        }

        // ----------------------------------------------------------------------------------------
        // Meshes: submesh 0 the outside (and edges), submesh 1 the inside
        // ----------------------------------------------------------------------------------------

        class MeshData
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> Nrm = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> Outside = new List<int>();
            public readonly List<int> Inside = new List<int>();
            public Vector2 Pivot;

            public Vector3 At(Vector2 xz, float y) => new Vector3(xz.x - Pivot.x, y, xz.y - Pivot.y);

            public void Quad(List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd,
                             Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                Nrm.Add(na); Nrm.Add(nb); Nrm.Add(nc); Nrm.Add(nd);
                UV.Add(ua); UV.Add(ub); UV.Add(uc); UV.Add(ud);
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }

            public Mesh Finish(string name)
            {
                var mesh = new Mesh { name = name, subMeshCount = 2 };
                mesh.SetVertices(V);
                mesh.SetNormals(Nrm);
                mesh.SetUVs(0, UV);
                // face every triangle along its normals, whichever way the strips were wound
                Face(Outside); Face(Inside);
                mesh.SetTriangles(Outside, 0);
                mesh.SetTriangles(Inside, 1);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                return mesh;
            }

            void Face(List<int> t)
            {
                for (int i = 0; i + 2 < t.Count; i += 3)
                {
                    Vector3 fn = Vector3.Cross(V[t[i + 1]] - V[t[i]], V[t[i + 2]] - V[t[i]]);
                    Vector3 avg = Nrm[t[i]] + Nrm[t[i + 1]] + Nrm[t[i + 2]];
                    if (Vector3.Dot(fn, avg) < 0f) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
                }
            }
        }

        /// <summary>The hull's shell over loop indexes i0..i1 (wrapping), between y0 and y1.</summary>
        void Strip(MeshData m, Loop loop, int i0, int i1, float y0, float y1, bool seams)
        {
            int cnt = loop.Count;
            // u runs round the outside and v down the length, whichever piece the face belongs to, so the skin
            // carries over from the hull to the doors
            float v0 = (y0 + length * 0.5f) / length, v1 = (y1 + length * 0.5f) / length;
            for (int k = i0; k < i1; k++)
            {
                int a = k % cnt, b = (k + 1) % cnt;
                float u0 = loop.Arc[k], u1 = loop.Arc[k + 1];
                Vector2 ua = new Vector2(u0, v0), ub = new Vector2(u1, v0), uc = new Vector2(u1, v1), ud = new Vector2(u0, v1);
                Vector3 nA = new Vector3(loop.N[a].x, 0f, loop.N[a].y), nB = new Vector3(loop.N[b].x, 0f, loop.N[b].y);
                m.Quad(m.Outside, m.At(loop.P[a], y0), m.At(loop.P[b], y0), m.At(loop.P[b], y1), m.At(loop.P[a], y1), nA, nB, nB, nA, ua, ub, uc, ud);
                m.Quad(m.Inside, m.At(loop.Q[a], y0), m.At(loop.Q[b], y0), m.At(loop.Q[b], y1), m.At(loop.Q[a], y1), -nA, -nB, -nB, -nA, ua, ub, uc, ud);
                // the edges of the shell at both ends of the strip
                m.Quad(m.Outside, m.At(loop.Q[a], y0), m.At(loop.Q[b], y0), m.At(loop.P[b], y0), m.At(loop.P[a], y0), Vector3.down, Vector3.down, Vector3.down, Vector3.down, ua, ub, uc, ud);
                m.Quad(m.Outside, m.At(loop.Q[a], y1), m.At(loop.Q[b], y1), m.At(loop.P[b], y1), m.At(loop.P[a], y1), Vector3.up, Vector3.up, Vector3.up, Vector3.up, ua, ub, uc, ud);
            }
            if (!seams) return;
            // the cut faces where the strip meets the next piece
            Seam(m, loop, i0, i0 + 1, y0, y1, true);
            Seam(m, loop, i1, i1 - 1, y0, y1, true);
        }

        static void Seam(MeshData m, Loop loop, int at, int towards, float y0, float y1, bool outwards)
        {
            int cnt = loop.Count;
            int a = ((at % cnt) + cnt) % cnt, b = ((towards % cnt) + cnt) % cnt;
            Vector2 t = (loop.P[a] - loop.P[b]).normalized;   // away from the strip
            Vector3 n = new Vector3(t.x, 0f, t.y);
            Vector2 ua = new Vector2(0f, y0), ub = new Vector2(1f, y0), uc = new Vector2(1f, y1), ud = new Vector2(0f, y1);
            m.Quad(m.Outside, m.At(loop.Q[a], y0), m.At(loop.P[a], y0), m.At(loop.P[a], y1), m.At(loop.Q[a], y1), n, n, n, n, ua, ub, uc, ud);
        }

        Mesh BuildHull(Loop loop, float half)
        {
            var m = new MeshData();
            Strip(m, loop, loop.HingeR, loop.HingeL, -half, half, true);
            // a ring at each end across the door sectors too, so the doors close against it
            Strip(m, loop, loop.HingeL, loop.Count, -half, -half + endRing, false);
            Strip(m, loop, 0, loop.HingeR, -half, -half + endRing, false);
            Strip(m, loop, loop.HingeL, loop.Count, half - endRing, half, false);
            Strip(m, loop, 0, loop.HingeR, half - endRing, half, false);
            return m.Finish("BDNukes bomb bay hull");
        }

        Mesh BuildDoor(Loop loop, int i0, int i1, float y0, float y1, Vector2 hinge)
        {
            var m = new MeshData { Pivot = hinge };
            Strip(m, loop, i0, i1, y0, y1, true);
            return m.Finish("BDNukes bomb bay door");
        }

        // ----------------------------------------------------------------------------------------

        // ----------------------------------------------------------------------------------------
        // Skins from the counterpart parts' own textures
        // ----------------------------------------------------------------------------------------

        static readonly Dictionary<string, Material> skinCache = new Dictionary<string, Material>();

        Material SkinMaterial(string key, string textureUrl, string patch, float density, Loop loop, Color tint, Func<Material> fallback, string tag = "")
        {
            string full = key + "|" + textureUrl + "|" + patch + "|" + density.ToString("R", CultureInfo.InvariantCulture) + "|" + tag;
            if (skinCache.TryGetValue(full, out var cached) && cached != null) return cached;
            Material mat = null;
            try
            {
                Texture2D tex = MakeSkin(textureUrl, patch, density, loop.Perimeter);
                if (tex != null)
                {
                    Shader sh = Shader.Find("KSP/Specular") ?? Shader.Find("KSP/Diffuse") ?? Shader.Find("Standard");
                    mat = new Material(sh) { mainTexture = tex };
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
                    if (mat.HasProperty("_SpecColor")) mat.SetColor("_SpecColor", new Color(0.3f, 0.3f, 0.3f));
                    if (mat.HasProperty("_Shininess")) mat.SetFloat("_Shininess", shininess);
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Bomb bay skin from " + textureUrl + " failed: " + e.Message); }
            if (mat == null) mat = fallback();
            skinCache[full] = mat;
            return mat;
        }

        /// <summary>
        /// The patch of the source texture, tiled (mirrored) into a texture of the bay's own size at the given density.
        /// The source may be unreadable (the texture loader drops its CPU copy), so it is copied on the GPU.
        /// </summary>
        Texture2D MakeSkin(string textureUrl, string patch, float density, float perimeter)
        {
            if (string.IsNullOrEmpty(textureUrl) || GameDatabase.Instance == null) return null;
            Texture src = GameDatabase.Instance.GetTexture(textureUrl, false);
            if (src == null) { Debug.LogWarning("[BDNukes]: Bomb bay texture not installed: " + textureUrl + "; painting it plain."); return null; }
            var ci = CultureInfo.InvariantCulture;
            var f = (patch ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float px, py, pw, ph;
            if (f.Length < 4 || !float.TryParse(f[0], NumberStyles.Float, ci, out px) || !float.TryParse(f[1], NumberStyles.Float, ci, out py) ||
                !float.TryParse(f[2], NumberStyles.Float, ci, out pw) || !float.TryParse(f[3], NumberStyles.Float, ci, out ph))
            { px = 0f; py = 0f; pw = src.width; ph = src.height; }
            // no bigger than 2048 on a side, at the density asked for or lower
            float d = Mathf.Min(density, 2048f / Mathf.Max(perimeter, length));
            int w = Mathf.Clamp(Mathf.RoundToInt(perimeter * d), 32, 2048), h = Mathf.Clamp(Mathf.RoundToInt(length * d), 32, 2048);
            int iw = Mathf.Max(8, Mathf.RoundToInt(pw * d / density)), ih = Mathf.Max(8, Mathf.RoundToInt(ph * d / density));

            var piece = RenderTexture.GetTemporary(iw, ih, 0, RenderTextureFormat.ARGB32);
            var big = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                // pixel y runs down from the top; Unity's v runs up from the bottom
                Graphics.Blit(src, piece, new Vector2(pw / src.width, ph / src.height), new Vector2(px / src.width, 1f - (py + ph) / src.height));
                piece.wrapMode = TextureWrapMode.Mirror;
                piece.filterMode = FilterMode.Bilinear;
                Graphics.Blit(piece, big, new Vector2(w / (float)iw, h / (float)ih), Vector2.zero);
                RenderTexture.active = big;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "BDNukesBay_" + System.IO.Path.GetFileName(textureUrl), wrapMode = TextureWrapMode.Repeat };
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(true, true);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(piece);
                RenderTexture.ReleaseTemporary(big);
            }
        }

        string Key()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Join("|", shape, length.ToString("R", ci), width.ToString("R", ci), height.ToString("R", ci),
                cornerRadius.ToString("R", ci), wall.ToString("R", ci), doorAngle.ToString("R", ci), doorFraction.ToString("R", ci),
                endRing.ToString("R", ci));
        }

        static Mesh Cached(string key, Func<Mesh> make)
        {
            if (meshCache.TryGetValue(key, out var m) && m != null) return m;
            m = make();
            meshCache[key] = m;
            return m;
        }

        static Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var f = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3) return fallback;
            var ci = CultureInfo.InvariantCulture;
            if (!float.TryParse(f[0], NumberStyles.Float, ci, out float r) || !float.TryParse(f[1], NumberStyles.Float, ci, out float g) ||
                !float.TryParse(f[2], NumberStyles.Float, ci, out float b)) return fallback;
            return new Color(r, g, b, 1f);
        }
    }
}
