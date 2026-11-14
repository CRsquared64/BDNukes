using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Config-driven procedural part model: a lathed body from a PROFILE, painted BANDs, FINS sets (with optional
    /// box/ring shrouds joining the tips), empty named TRANSFORMs for BDArmory modules, a painted texture and a
    /// collider. The part's MODEL is only a placeholder whose renderers and colliders are stripped.
    ///
    /// The model is built on the part prefab in OnLoad, so the editor icon, drag cubes and every spawned copy get it.
    /// Instances normally inherit the built model from the prefab; OnStart rebuilds it only if it's missing.
    /// Meshes, textures and materials are cached per distinct parameter set, so identical parts share them.
    ///
    /// Hierarchy (all under the part's "model" transform so part.FindModelTransform finds the transforms):
    ///   model / BDNukes_ProceduralModel / Geometry (rotated for axis = Y) / Body, Fins0, Fins1...
    ///   model / BDNukes_ProceduralModel / &lt;TRANSFORM name&gt;   (part-local position and orientation)
    /// </summary>
    public class ModuleProceduralModel : PartModule
    {
        public const string RootName = "BDNukes_ProceduralModel";
        const string GeometryName = "Geometry";

        // ----------------------------------------------------------------------------------------
        // Parsed configuration
        // ----------------------------------------------------------------------------------------

        class BandSpec
        {
            public float from, to;
            public Color color;
        }

        class FinSpec
        {
            public int count = 4;
            public float roll, rootFront, rootBack, tipFront, tipBack, span, thickness = 0.03f, shroudChord;
            public Color color;
            public string shroud = "none";
            public string parent;   // optional TRANSFORM the fin set rides on (a dropped stage takes its fins along)
        }

        class TransformSpec
        {
            public string name, parent;
            public Vector3 position, forward, up;
            public bool hasUp;
            // BDArmory exhaust prefabs (and KSP particle FX) stream along the transform's local +Y, with the
            // identity rotation BDArmory gives them. So a plume transform's +Y is the exhaust direction ("forward"
            // in the config) and its +Z a perpendicular, exactly like BDArmory's own missile models.
            public bool plume;
            // optional: the lathed body between these distances from the nose is built as its own mesh under this
            // transform, so when BDArmory drops it as a booster (boostTransformName) the stage visibly falls away
            public bool detach;
            public float detachFrom, detachTo;
        }

        class Spec
        {
            public bool axisY;
            public float length;
            public Color bodyColor;
            public float shininess;
            public bool panelLines;
            public float seamSpacing;
            public string collider;
            public int segments;
            public Vector2[] profile;
            public float maxRadius;
            public readonly List<BandSpec> bands = new List<BandSpec>();
            public readonly List<FinSpec> fins = new List<FinSpec>();
            public readonly List<TransformSpec> transforms = new List<TransformSpec>();
            public string key;
        }

        static readonly Dictionary<string, Spec> specs = new Dictionary<string, Spec>();
        static readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();

        // Serialized so Unity copies it when KSP instantiates parts from the prefab.
        [SerializeField] string specKey;
        [SerializeField] string builtKey;
        Spec spec;

        /// <summary>True when this part's nose points along +Y (vertical/stack parts) rather than +Z.</summary>
        public bool AxisIsY => GetSpec()?.axisY ?? false;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            // The prefab gets the full MODULE node; instances get only the saved persistent fields (no PROFILE).
            if (!node.HasNode("PROFILE")) return;
            var parsed = Parse(node, part != null && part.partInfo != null ? part.partInfo.name : part?.name);
            if (parsed == null) return;
            spec = parsed;
            specKey = parsed.key;
            specs[parsed.key] = parsed;
            Build(true);
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Build(false); // no-op if the prefab already carried it over
        }

        Spec GetSpec()
        {
            if (spec != null) return spec;
            if (!string.IsNullOrEmpty(specKey) && specs.TryGetValue(specKey, out var s)) return spec = s;

            // Fall back to the prefab's module at the same index, then to the part config itself.
            int index = 0;
            foreach (PartModule pm in part.Modules)
            {
                if (pm == this) break;
                if (pm is ModuleProceduralModel) index++;
            }
            Part prefab = part.partInfo?.partPrefab;
            if (prefab != null && prefab != part)
            {
                int i = 0;
                foreach (PartModule pm in prefab.Modules)
                {
                    if (!(pm is ModuleProceduralModel pmm)) continue;
                    if (i++ != index) continue;
                    if (pmm.spec != null) return spec = pmm.spec;
                    if (!string.IsNullOrEmpty(pmm.specKey) && specs.TryGetValue(pmm.specKey, out s)) return spec = s;
                }
            }
            ConfigNode cfg = part.partInfo?.partConfig;
            if (cfg != null)
            {
                int i = 0;
                foreach (ConfigNode mn in cfg.GetNodes("MODULE"))
                {
                    if (mn.GetValue("name") != nameof(ModuleProceduralModel)) continue;
                    if (i++ != index) continue;
                    spec = Parse(mn, part.partInfo.name);
                    if (spec != null) { specKey = spec.key; specs[spec.key] = spec; }
                    return spec;
                }
            }
            return null;
        }

        // ----------------------------------------------------------------------------------------
        // Parsing
        // ----------------------------------------------------------------------------------------

        static readonly char[] Separators = { ',', ' ', '\t' };

        static float[] Floats(string s)
        {
            if (string.IsNullOrEmpty(s)) return new float[0];
            var parts = s.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var r = new List<float>(parts.Length);
            foreach (var p in parts)
                if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) r.Add(f);
            return r.ToArray();
        }

        static float F(ConfigNode n, string name, float def)
        {
            string v = n.GetValue(name);
            return v != null && float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : def;
        }

        static int I(ConfigNode n, string name, int def)
        {
            string v = n.GetValue(name);
            if (v == null) return def;
            return float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? Mathf.RoundToInt(f) : def;
        }

        static bool B(ConfigNode n, string name, bool def)
        {
            string v = n.GetValue(name);
            return v != null && bool.TryParse(v.Trim(), out bool b) ? b : def;
        }

        static Color C(string s, Color def)
        {
            var f = Floats(s);
            if (f.Length < 3) return def;
            return new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
        }

        static Vector3 V(string s, Vector3 def)
        {
            var f = Floats(s);
            return f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : def;
        }

        static string K(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        static string K(Color c) => K(c.r) + "," + K(c.g) + "," + K(c.b);
        static string K(Vector3 v) => K(v.x) + "," + K(v.y) + "," + K(v.z);

        static Spec Parse(ConfigNode node, string partName)
        {
            var s = new Spec
            {
                axisY = string.Equals(node.GetValue("axis")?.Trim(), "Y", StringComparison.OrdinalIgnoreCase),
                length = F(node, "length", 0f),
                bodyColor = C(node.GetValue("bodyColor"), new Color(0.7f, 0.7f, 0.68f)),
                shininess = F(node, "shininess", 0.3f),
                panelLines = B(node, "panelLines", true),
                seamSpacing = Mathf.Max(0f, F(node, "seamSpacing", 0f)),
                collider = (node.GetValue("collider") ?? "capsule").Trim().ToLowerInvariant(),
                segments = Mathf.Clamp(I(node, "segments", 48), 8, 128),
            };

            var pts = new List<Vector2>();
            var profileNode = node.GetNode("PROFILE");
            if (profileNode != null)
            {
                foreach (string p in profileNode.GetValues("point"))
                {
                    var f = Floats(p);
                    if (f.Length >= 2) pts.Add(new Vector2(f[0], Mathf.Max(0f, f[1])));
                }
            }
            if (pts.Count < 2)
            {
                Debug.LogError("[BDNukes]: ModuleProceduralModel on " + partName + " needs a PROFILE with at least two points.");
                return null;
            }
            s.profile = pts.ToArray();
            if (s.length <= 0f) s.length = pts[pts.Count - 1].x;
            foreach (var p in pts) s.maxRadius = Mathf.Max(s.maxRadius, p.y);

            foreach (ConfigNode bn in node.GetNodes("BAND"))
            {
                var b = new BandSpec { from = F(bn, "from", 0f), to = F(bn, "to", 0f), color = C(bn.GetValue("color"), s.bodyColor) };
                if (b.to < b.from) { float t = b.to; b.to = b.from; b.from = t; }
                s.bands.Add(b);
            }

            foreach (ConfigNode fnode in node.GetNodes("FINS"))
            {
                var f = new FinSpec
                {
                    count = Mathf.Clamp(I(fnode, "count", 4), 1, 32),
                    roll = F(fnode, "roll", 0f),
                    rootFront = F(fnode, "rootFront", 0f),
                    rootBack = F(fnode, "rootBack", 0f),
                    tipFront = F(fnode, "tipFront", 0f),
                    tipBack = F(fnode, "tipBack", 0f),
                    span = F(fnode, "span", s.maxRadius * 2f),
                    thickness = Mathf.Max(0.001f, F(fnode, "thickness", 0.03f)),
                    color = C(fnode.GetValue("color"), s.bodyColor * 0.8f),
                    shroud = (fnode.GetValue("shroud") ?? "none").Trim().ToLowerInvariant(),
                };
                f.shroudChord = F(fnode, "shroudChord", Mathf.Abs(f.tipBack - f.tipFront));
                f.parent = fnode.GetValue("parent")?.Trim();
                f.color.a = 1f;
                s.fins.Add(f);
            }

            Vector3 noseDir = s.axisY ? Vector3.up : Vector3.forward;
            foreach (ConfigNode tn in node.GetNodes("TRANSFORM"))
            {
                string name = tn.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                var t = new TransformSpec
                {
                    name = name.Trim(),
                    position = V(tn.GetValue("position"), Vector3.zero),
                    forward = V(tn.GetValue("forward"), noseDir),
                };
                if (t.forward.sqrMagnitude < 1e-8f) t.forward = noseDir;
                if (tn.HasValue("up")) { t.up = V(tn.GetValue("up"), Vector3.up); t.hasUp = t.up.sqrMagnitude > 1e-8f; }
                string lname = t.name.ToLowerInvariant();
                t.plume = B(tn, "plume", lname.Contains("exhaust"));
                t.parent = tn.GetValue("parent")?.Trim();
                var dr = Floats(tn.GetValue("detach"));
                if (dr.Length >= 2 && Mathf.Abs(dr[1] - dr[0]) > 1e-4f)
                {
                    t.detach = true;
                    t.detachFrom = Mathf.Min(dr[0], dr[1]);
                    t.detachTo = Mathf.Max(dr[0], dr[1]);
                }
                s.transforms.Add(t);
            }

            // Canonical key: identical configs share meshes, textures and materials.
            var sb = new StringBuilder();
            sb.Append(s.axisY ? "Y" : "Z").Append('|').Append(K(s.length)).Append('|').Append(K(s.bodyColor)).Append('|')
              .Append(K(s.shininess)).Append('|').Append(s.panelLines).Append('|').Append(K(s.seamSpacing)).Append('|')
              .Append(s.collider).Append('|').Append(s.segments).Append("|P");
            foreach (var p in s.profile) sb.Append(K(p.x)).Append(':').Append(K(p.y)).Append(';');
            sb.Append("|B");
            foreach (var b in s.bands) sb.Append(K(b.from)).Append(':').Append(K(b.to)).Append(':').Append(K(b.color)).Append(';');
            sb.Append("|F");
            foreach (var f in s.fins)
                sb.Append(f.count).Append(':').Append(K(f.roll)).Append(':').Append(K(f.rootFront)).Append(':').Append(K(f.rootBack)).Append(':')
                  .Append(K(f.tipFront)).Append(':').Append(K(f.tipBack)).Append(':').Append(K(f.span)).Append(':').Append(K(f.thickness)).Append(':')
                  .Append(K(f.color)).Append(':').Append(f.shroud).Append(':').Append(K(f.shroudChord)).Append(':').Append(f.parent ?? "").Append(';');
            sb.Append("|T");
            foreach (var t in s.transforms)
                sb.Append(t.name).Append(':').Append(K(t.position)).Append(':').Append(K(t.forward)).Append(':').Append(t.hasUp ? K(t.up) : "-")
                  .Append(':').Append(t.plume).Append(':').Append(t.parent ?? "").Append(':')
                  .Append(t.detach ? K(t.detachFrom) + "-" + K(t.detachTo) : "-").Append(';');
            s.key = sb.ToString();
            return s;
        }

        // ----------------------------------------------------------------------------------------
        // Building
        // ----------------------------------------------------------------------------------------

        void Build(bool fromConfig)
        {
            var s = GetSpec();
            if (s == null || part == null) return;

            Transform model = part.transform.Find("model");
            if (model == null) model = part.transform;
            Transform existing = model.Find(RootName);
            if (existing != null)
            {
                // Instances inherit the prefab's model; only a changed config on the prefab replaces it.
                if (!fromConfig || builtKey == s.key) return;
                DestroyImmediate(existing.gameObject);
            }

            StripPlaceholder(model);

            int layer = model.gameObject.layer;
            var root = new GameObject(RootName) { layer = layer };
            root.transform.SetParent(model, false);

            var geo = new GameObject(GeometryName) { layer = layer };
            geo.transform.SetParent(root.transform, false);
            // Geometry is built nose-along-+Z; for axis Y rotate +Z onto +Y (and +Y onto -Z).
            if (s.axisY) geo.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);

            var body = new GameObject("Body") { layer = layer };
            body.transform.SetParent(geo.transform, false);
            var cut = new List<Vector2>();
            foreach (var t in s.transforms) if (t.detach) cut.Add(new Vector2(t.detachFrom, t.detachTo));
            Mesh bodyMesh = CachedMesh("body|" + s.key, () => cut.Count == 0 ? BuildLathe(s) : BuildLatheRanges(s, Complement(cut, s.profile[0].x, s.profile[s.profile.Length - 1].x)));
            body.AddComponent<MeshFilter>().sharedMesh = bodyMesh;
            body.AddComponent<MeshRenderer>().sharedMaterial = BodyMaterial(s);
            Bounds bounds = bodyMesh.bounds;

            var finObjects = new List<GameObject>();
            for (int i = 0; i < s.fins.Count; i++)
            {
                var fs = s.fins[i];
                var fins = new GameObject("Fins" + i) { layer = layer };
                finObjects.Add(fins);
                fins.transform.SetParent(geo.transform, false);
                Mesh finMesh = CachedMesh("fins" + i + "|" + s.key, () => BuildFins(s, fs));
                fins.AddComponent<MeshFilter>().sharedMesh = finMesh;
                fins.AddComponent<MeshRenderer>().sharedMaterial = FinMaterial(fs.color, s.shininess);
                bounds.Encapsulate(finMesh.bounds);
            }

            Collider col = null;
            if (s.collider == "capsule")
            {
                float x0 = s.profile[0].x, x1 = s.profile[s.profile.Length - 1].x;
                var cap = geo.AddComponent<CapsuleCollider>();
                cap.direction = 2;
                cap.radius = Mathf.Max(0.01f, s.maxRadius);
                cap.height = Mathf.Max(Mathf.Abs(x1 - x0), cap.radius * 2f);
                cap.center = new Vector3(0f, 0f, s.length / 2f - (x0 + x1) / 2f);
                col = cap;
            }
            else if (s.collider == "box")
            {
                var box = geo.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = Vector3.Max(bounds.size, Vector3.one * 0.01f);
                col = box;
            }

            var made = new List<KeyValuePair<TransformSpec, Transform>>();
            foreach (var t in s.transforms)
            {
                var go = new GameObject(t.name) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = t.position;
                Vector3 fwd = t.forward.normalized;
                if (t.plume)
                {
                    // +Y along the exhaust, +Z perpendicular (the "up" hint if given, else the part's side)
                    Vector3 side = t.hasUp ? t.up.normalized : (s.axisY ? Vector3.forward : Vector3.up);
                    side = Vector3.ProjectOnPlane(side, fwd);
                    if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.right, fwd);
                    go.transform.localRotation = Quaternion.LookRotation(side.normalized, fwd);
                }
                else
                {
                    Vector3 up = t.hasUp ? t.up : Vector3.up;
                    if (Mathf.Abs(Vector3.Dot(fwd, up.normalized)) > 0.999f) up = Mathf.Abs(fwd.z) < 0.9f ? Vector3.forward : Vector3.up;
                    go.transform.localRotation = Quaternion.LookRotation(fwd, up);
                }
                if (t.detach)
                {
                    // the stage's own slice of the body, placed exactly where it was, riding on this transform
                    var sg = new GameObject("StageBody") { layer = layer };
                    sg.transform.SetParent(geo.transform, false);
                    var ts = t;
                    Mesh sm = CachedMesh("stage|" + t.name + "|" + K(t.detachFrom) + "|" + s.key,
                        () => BuildLatheRanges(s, new List<Vector2> { new Vector2(ts.detachFrom, ts.detachTo) }));
                    sg.AddComponent<MeshFilter>().sharedMesh = sm;
                    sg.AddComponent<MeshRenderer>().sharedMaterial = BodyMaterial(s);
                    sg.transform.SetParent(go.transform, true);
                }
                made.Add(new KeyValuePair<TransformSpec, Transform>(t, go.transform));
            }
            // nest transforms under their parent (keeping where they are), so a dropped stage takes its nozzles along
            foreach (var kv in made)
            {
                if (string.IsNullOrEmpty(kv.Key.parent)) continue;
                Transform p = null;
                foreach (var o in made) if (o.Key.name == kv.Key.parent && o.Value != kv.Value) { p = o.Value; break; }
                if (p != null) kv.Value.SetParent(p, true);
                else Debug.LogWarning("[BDNukes]: TRANSFORM " + kv.Key.name + " names a missing parent " + kv.Key.parent);
            }
            for (int i = 0; i < s.fins.Count; i++)
            {
                if (string.IsNullOrEmpty(s.fins[i].parent)) continue;
                Transform p = null;
                foreach (var o in made) if (o.Key.name == s.fins[i].parent) { p = o.Value; break; }
                if (p != null) finObjects[i].transform.SetParent(p, true);
            }

            builtKey = s.key;
            if (col != null) part.collider = col;
            if (!fromConfig)
            {
                part.ResetModelMeshRenderersCache();
                part.ResetModelRenderersCache();
            }
        }

        static void StripPlaceholder(Transform model)
        {
            foreach (var c in model.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true)) DestroyImmediate(r);
            foreach (var f in model.GetComponentsInChildren<MeshFilter>(true)) DestroyImmediate(f);
        }

        static Mesh CachedMesh(string key, Func<Mesh> make)
        {
            if (meshCache.TryGetValue(key, out var m) && m != null) return m;
            m = make();
            meshCache[key] = m;
            return m;
        }

        // ----------------------------------------------------------------------------------------
        // Geometry (nose at +Z = +length/2, tail at -length/2)
        // ----------------------------------------------------------------------------------------

        static Mesh BuildLathe(Spec s)
        {
            var profile = s.profile;
            int rings = profile.Length, segs = s.segments;
            float half = s.length / 2f;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            for (int i = 0; i < rings; i++)
            {
                Vector2 prev = profile[Mathf.Max(0, i - 1)], next = profile[Mathf.Min(rings - 1, i + 1)];
                // profile tangent in (distance-from-nose, r); the outward normal is perpendicular to it
                float dz = next.x - prev.x, dr = next.y - prev.y;
                Vector2 n2 = new Vector2(-dr, dz);
                n2 = n2.sqrMagnitude > 1e-12f ? n2.normalized : new Vector2(0f, 1f);
                float z = half - profile[i].x;
                for (int k = 0; k <= segs; k++)
                {
                    float a = k / (float)segs * 2f * Mathf.PI;
                    float cx = Mathf.Cos(a), cy = Mathf.Sin(a);
                    verts.Add(new Vector3(cx * profile[i].y, cy * profile[i].y, z));
                    // n2.x is along "distance from nose"; +Z points the other way (towards the nose)
                    norms.Add(new Vector3(cx * n2.y, cy * n2.y, -n2.x).normalized);
                    uvs.Add(new Vector2(k / (float)segs, profile[i].x / s.length));
                }
            }
            int stride = segs + 1;
            for (int i = 0; i < rings - 1; i++)
                for (int k = 0; k < segs; k++)
                {
                    int a = i * stride + k, b = a + 1, c = a + stride, d = c + 1;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }

            // Flat caps where the profile doesn't close to a point.
            AddCap(verts, norms, uvs, tris, profile[0], half, segs, s.length, true);
            AddCap(verts, norms, uvs, tris, profile[rings - 1], half, segs, s.length, false);

            return FinishMesh("ProceduralBody", verts, norms, uvs, tris);
        }

        /// <summary>The parts of [x0, x1] not covered by any of the cut ranges.</summary>
        static List<Vector2> Complement(List<Vector2> cuts, float x0, float x1)
        {
            cuts.Sort((a, b) => a.x.CompareTo(b.x));
            var keep = new List<Vector2>();
            float at = x0;
            foreach (var c in cuts)
            {
                if (c.x > at + 1e-4f) keep.Add(new Vector2(at, Mathf.Min(c.x, x1)));
                at = Mathf.Max(at, c.y);
            }
            if (at < x1 - 1e-4f) keep.Add(new Vector2(at, x1));
            return keep;
        }

        /// <summary>A lathe of only the given [from, to] slices of the profile, each capped where it's cut.</summary>
        static Mesh BuildLatheRanges(Spec s, List<Vector2> ranges)
        {
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
            foreach (var r in ranges)
            {
                var pts = new List<Vector2> { new Vector2(r.x, RadiusAt(s.profile, r.x)) };
                foreach (var p in s.profile) if (p.x > r.x + 1e-4f && p.x < r.y - 1e-4f) pts.Add(p);
                pts.Add(new Vector2(r.y, RadiusAt(s.profile, r.y)));
                var sub = new Spec { length = s.length, segments = s.segments, profile = pts.ToArray() };
                var m = BuildLathe(sub);
                int baseIndex = verts.Count;
                verts.AddRange(m.vertices); norms.AddRange(m.normals); uvs.AddRange(m.uv);
                foreach (int i in m.triangles) tris.Add(baseIndex + i);
                UnityEngine.Object.DestroyImmediate(m);
            }
            return FinishMesh("ProceduralBody", verts, norms, uvs, tris);
        }

        static void AddCap(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, Vector2 p, float half, int segs, float length, bool front)
        {
            if (p.y < 0.001f) return;
            float z = half - p.x;
            Vector3 normal = front ? Vector3.forward : Vector3.back;
            float vCoord = p.x / length;
            int centre = v.Count;
            v.Add(new Vector3(0f, 0f, z)); n.Add(normal); uv.Add(new Vector2(0.5f, vCoord));
            for (int k = 0; k <= segs; k++)
            {
                float a = k / (float)segs * 2f * Mathf.PI;
                v.Add(new Vector3(Mathf.Cos(a) * p.y, Mathf.Sin(a) * p.y, z)); n.Add(normal); uv.Add(new Vector2(k / (float)segs, vCoord));
            }
            for (int k = 0; k < segs; k++) { t.Add(centre); t.Add(centre + 1 + k); t.Add(centre + 2 + k); }
        }

        static Mesh FinishMesh(string name, List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
        {
            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
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

        static float RadiusAt(Vector2[] profile, float fromNose)
        {
            if (fromNose <= profile[0].x) return profile[0].y;
            for (int i = 1; i < profile.Length; i++)
            {
                if (fromNose <= profile[i].x)
                {
                    float k = Mathf.InverseLerp(profile[i - 1].x, profile[i].x, fromNose);
                    return Mathf.Lerp(profile[i - 1].y, profile[i].y, k);
                }
            }
            return profile[profile.Length - 1].y;
        }

        /// <summary>One fin set: trapezoidal flat-shaded prisms, plus an optional box or ring shroud joining the tips.</summary>
        static Mesh BuildFins(Spec s, FinSpec f)
        {
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float halfLen = s.length / 2f;
            float half = f.thickness / 2f;

            for (int i = 0; i < f.count; i++)
            {
                float ang = (f.roll + 360f / f.count * i) * Mathf.Deg2Rad;
                Vector3 radial = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                Vector3 side = new Vector3(-Mathf.Sin(ang), Mathf.Cos(ang), 0f) * half;

                Vector3 P(float fromNose, float r) => radial * r + new Vector3(0f, 0f, halfLen - fromNose);
                float sinkF = Mathf.Min(0.02f, RadiusAt(s.profile, f.rootFront) * 0.1f);
                float sinkB = Mathf.Min(0.02f, RadiusAt(s.profile, f.rootBack) * 0.1f);
                Vector3 rf = P(f.rootFront, Mathf.Max(0f, RadiusAt(s.profile, f.rootFront) - sinkF));
                Vector3 rb = P(f.rootBack, Mathf.Max(0f, RadiusAt(s.profile, f.rootBack) - sinkB));
                Vector3 tf = P(f.tipFront, f.span), tb = P(f.tipBack, f.span);

                // two broad faces
                Quad(verts, norms, uvs, tris, rf + side, tf + side, tb + side, rb + side);
                Quad(verts, norms, uvs, tris, rb - side, tb - side, tf - side, rf - side);
                // edges
                Quad(verts, norms, uvs, tris, rf - side, tf - side, tf + side, rf + side); // leading edge
                Quad(verts, norms, uvs, tris, tf - side, tb - side, tb + side, tf + side); // tip
                Quad(verts, norms, uvs, tris, tb - side, rb - side, rb + side, tb + side); // trailing edge
            }

            if (f.shroud == "box" || f.shroud == "ring")
            {
                float zBack = halfLen - f.tipBack;
                float zFront = zBack + Mathf.Max(0.01f, f.shroudChord);
                if (f.shroud == "box" && f.count >= 3) BoxShroud(verts, norms, uvs, tris, f, zFront, zBack, half);
                else RingShroud(verts, norms, uvs, tris, f.span, zFront, zBack, half, s.segments);
            }

            return FinishMesh("ProceduralFins", verts, norms, uvs, tris);
        }

        /// <summary>Flat plates between consecutive fin tips (a square box tail for four fins).</summary>
        static void BoxShroud(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, FinSpec f, float zFront, float zBack, float half)
        {
            for (int i = 0; i < f.count; i++)
            {
                float a0 = (f.roll + 360f / f.count * i) * Mathf.Deg2Rad, a1 = (f.roll + 360f / f.count * (i + 1)) * Mathf.Deg2Rad;
                Vector3 p0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f) * f.span;
                Vector3 p1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f) * f.span;
                Vector3 along = (p1 - p0).normalized;
                Vector3 outward = ((p0 + p1) * 0.5f).normalized * half;
                // extend each plate by half the thickness so the corners close
                p0 -= along * half; p1 += along * half;
                Vector3 zf = new Vector3(0f, 0f, zFront), zb = new Vector3(0f, 0f, zBack);
                Vector3 o0f = p0 + outward + zf, o1f = p1 + outward + zf, o1b = p1 + outward + zb, o0b = p0 + outward + zb;
                Vector3 i0f = p0 - outward + zf, i1f = p1 - outward + zf, i1b = p1 - outward + zb, i0b = p0 - outward + zb;
                Quad(v, n, uv, t, o0f, o1f, o1b, o0b);   // outer
                Quad(v, n, uv, t, i0b, i1b, i1f, i0f);   // inner
                Quad(v, n, uv, t, i0f, i1f, o1f, o0f);   // front rim
                Quad(v, n, uv, t, o0b, o1b, i1b, i0b);   // back rim
            }
        }

        /// <summary>A thin cylindrical ring through the fin tips.</summary>
        static void RingShroud(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, float r, float zFront, float zBack, float half, int segs)
        {
            float ro = r + half, ri = Mathf.Max(0.001f, r - half);
            // outer and inner surfaces, smooth-shaded
            for (int pass = 0; pass < 2; pass++)
            {
                float rr = pass == 0 ? ro : ri;
                float sign = pass == 0 ? 1f : -1f;
                int start = v.Count;
                for (int k = 0; k <= segs; k++)
                {
                    float a = k / (float)segs * 2f * Mathf.PI;
                    Vector3 radial = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(radial * rr + new Vector3(0f, 0f, zFront)); n.Add(radial * sign); uv.Add(new Vector2(k / (float)segs, 0f));
                    v.Add(radial * rr + new Vector3(0f, 0f, zBack)); n.Add(radial * sign); uv.Add(new Vector2(k / (float)segs, 1f));
                }
                for (int k = 0; k < segs; k++)
                {
                    int a = start + k * 2, b = a + 1, c = a + 2, d = a + 3;
                    t.Add(a); t.Add(b); t.Add(c);
                    t.Add(b); t.Add(d); t.Add(c);
                }
            }
            // front and back rims, flat-shaded
            foreach (float z in new[] { zFront, zBack })
            {
                Vector3 normal = z == zFront ? Vector3.forward : Vector3.back;
                int start = v.Count;
                for (int k = 0; k <= segs; k++)
                {
                    float a = k / (float)segs * 2f * Mathf.PI;
                    Vector3 radial = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(radial * ro + new Vector3(0f, 0f, z)); n.Add(normal); uv.Add(new Vector2(k / (float)segs, 0f));
                    v.Add(radial * ri + new Vector3(0f, 0f, z)); n.Add(normal); uv.Add(new Vector2(k / (float)segs, 0.05f));
                }
                for (int k = 0; k < segs; k++)
                {
                    int a = start + k * 2, b = a + 1, c = a + 2, d = a + 3;
                    t.Add(a); t.Add(b); t.Add(c);
                    t.Add(b); t.Add(d); t.Add(c);
                }
            }
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
        // Materials and textures. U runs around the circumference, V from nose (0) to tail (1).
        // ----------------------------------------------------------------------------------------

        static Material MakeMaterial(Texture2D tex, float shininess)
        {
            Shader sh = Shader.Find("KSP/Specular") ?? Shader.Find("KSP/Diffuse") ?? Shader.Find("Standard");
            var m = new Material(sh) { mainTexture = tex };
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.35f, 0.35f, 0.35f));
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", shininess);
            return m;
        }

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1442695041;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = xf * xf * (3f - 2f * xf), v = yf * yf * (3f - 2f * yf);
            return Mathf.Lerp(Mathf.Lerp(Hash(xi, yi, seed), Hash(xi + 1, yi, seed), u),
                              Mathf.Lerp(Hash(xi, yi + 1, seed), Hash(xi + 1, yi + 1, seed), u), v);
        }

        static Material BodyMaterial(Spec s)
        {
            float circumference = 2f * Mathf.PI * Mathf.Max(0.01f, s.maxRadius);
            int w = Mathf.NextPowerOfTwo(Mathf.Clamp(Mathf.RoundToInt(circumference * 96f), 128, 1024));
            int h = Mathf.NextPowerOfTwo(Mathf.Clamp(Mathf.RoundToInt(s.length * 96f), 128, 1024));

            // Longitudinal panel lines only on the near-full-diameter section of the body.
            float panelFrom = s.length, panelTo = 0f;
            foreach (var p in s.profile)
                if (p.y >= s.maxRadius * 0.85f) { panelFrom = Mathf.Min(panelFrom, p.x); panelTo = Mathf.Max(panelTo, p.x); }

            var sb = new StringBuilder("body|");
            sb.Append(w).Append('x').Append(h).Append('|').Append(K(s.length)).Append('|').Append(K(s.maxRadius)).Append('|')
              .Append(K(s.bodyColor)).Append('|').Append(K(s.shininess)).Append('|').Append(s.panelLines).Append('|')
              .Append(K(s.seamSpacing)).Append('|').Append(K(panelFrom)).Append('|').Append(K(panelTo)).Append('|');
            foreach (var b in s.bands) sb.Append(K(b.from)).Append(':').Append(K(b.to)).Append(':').Append(K(b.color)).Append(';');
            string key = sb.ToString();
            if (materialCache.TryGetValue(key, out var mat) && mat != null) return mat;

            var px = new Color[w * h];
            float metresPerU = circumference;          // arc length around the body per unit U
            float pxPerMetreV = h / s.length;
            float pxPerMetreU = w / circumference;
            float seamHalf = Mathf.Max(0.75f, 0.012f * pxPerMetreV);
            float panelHalf = Mathf.Max(0.6f, 0.01f * pxPerMetreU);
            float rivetOffset = 0.05f, rivetPitch = 0.07f, rivetSize = 0.012f;

            var seams = new List<float>();
            if (s.seamSpacing > 0f)
                for (float d = s.seamSpacing; d < s.length - 0.01f; d += s.seamSpacing) seams.Add(d);

            for (int y = 0; y < h; y++)
            {
                float v = y / (float)(h - 1);
                float fromNose = v * s.length;
                Color baseCol = s.bodyColor;
                foreach (var b in s.bands)
                    if (fromNose >= b.from && fromNose <= b.to) baseCol = b.color;

                for (int x = 0; x < w; x++)
                {
                    float u = x / (float)w;
                    float arc = u * metresPerU;
                    Color c = baseCol;

                    // subtle weathering: streaks along the flow, blotches and fine grain
                    float grime = ValueNoise(arc / 0.5f, fromNose / 1.6f, 3) * 0.06f
                                + ValueNoise(arc / 0.08f, fromNose / 0.12f, 5) * 0.03f
                                + Hash(x, y, 7) * 0.02f;
                    c *= 0.95f + grime;

                    // circumferential seams with rivet rows either side
                    foreach (float sv in seams)
                    {
                        float dm = Mathf.Abs(fromNose - sv);
                        if (dm * pxPerMetreV < seamHalf) c *= 0.55f;
                        else if (s.panelLines && Mathf.Abs(dm - rivetOffset) < rivetSize && Mathf.Repeat(arc, rivetPitch) < rivetSize * 1.5f) c *= 0.75f;
                    }

                    // longitudinal panel lines every 45 degrees
                    if (s.panelLines && fromNose > panelFrom && fromNose < panelTo)
                    {
                        float du = Mathf.Abs(Mathf.Repeat(u * 8f + 0.5f, 1f) - 0.5f) * w / 8f;
                        if (du < panelHalf) c *= 0.62f;
                    }

                    c.a = 1f;
                    px[y * w + x] = c;
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4, name = "BDNukesProcBody" };
            tex.SetPixels(px);
            tex.Apply(true, true);
            mat = MakeMaterial(tex, s.shininess);
            materialCache[key] = mat;
            return mat;
        }

        internal static Material FinMaterial(Color color, float shininess)
        {
            string key = "fin|" + K(color) + "|" + K(shininess);
            if (materialCache.TryGetValue(key, out var mat) && mat != null) return mat;
            const int size = 128;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float g = 0.95f + ValueNoise(x / 16f, y / 16f, 11) * 0.08f + Hash(x, y, 13) * 0.04f;
                    if (x < 3 || y < 3 || x > size - 4 || y > size - 4) g *= 0.7f;
                    Color c = color * g;
                    c.a = 1f;
                    px[y * size + x] = c;
                }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "BDNukesProcFin" };
            tex.SetPixels(px);
            tex.Apply(true, true);
            mat = MakeMaterial(tex, shininess);
            materialCache[key] = mat;
            return mat;
        }
    }
}
