using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace BDNukes
{
    /// <summary>A place where a blast flattened the trees (setting flattenTrees). Kept in the save forever.</summary>
    internal class NukeFlattenSite
    {
        public string body;
        public double lat, lon;
        public float killRadius;    // trees and bushes go down out to here (m), ~5 psi
        public float craterRadius;  // everything (grass, rocks too) goes in the quads around the crater; 0 = no crater
        public int seed;

        internal Vector3d dir;
        internal double killAngle, craterAngle;
        internal Component treeHolder, allHolder;   // Parallax PQSMod_MapDecalVertexRemoveScatter instances (data only)

        public void Prepare(CelestialBody b)
        {
            dir = b.GetRelSurfaceNVector(lat, lon).normalized;
            killAngle = killRadius / b.Radius;
            craterAngle = craterRadius / b.Radius;
        }

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("FLATTEN");
            n.AddValue("body", body);
            n.AddValue("lat", F(lat));
            n.AddValue("lon", F(lon));
            n.AddValue("killRadius", killRadius.ToString("0.#", CultureInfo.InvariantCulture));
            n.AddValue("craterRadius", craterRadius.ToString("0.#", CultureInfo.InvariantCulture));
            n.AddValue("seed", seed.ToString(CultureInfo.InvariantCulture));
            return n;
        }

        public static NukeFlattenSite Load(ConfigNode n)
        {
            var f = new NukeFlattenSite { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(f.body)) return null;
            if (!n.TryGetValue("lat", ref f.lat) || !n.TryGetValue("lon", ref f.lon) || !n.TryGetValue("killRadius", ref f.killRadius)) return null;
            n.TryGetValue("craterRadius", ref f.craterRadius);
            n.TryGetValue("seed", ref f.seed);
            return f.killRadius > 1f ? f : null;
        }
    }

    /// <summary>
    /// Hands each terrain quad near a flattened site to Parallax's scatter mask. Parallax (Continued) looks a quad up
    /// in PQSMod_MapDecalVertexRemoveScatter.maskedScattersPerQuad when it builds the quad's scatters: if there is an
    /// entry, the scatters named in its blockedScatters read its colorMap (green 0 = removed) over a square of
    /// +-radius around its normalisedPosition. Only one entry per quad, so each quad gets the biggest site touching it.
    /// The entries are added in OnQuadPreBuild and dropped in OnQuadDestroy, like Parallax's own mod does.
    /// </summary>
    internal class PQSMod_BDNukesScatterMask : PQSMod
    {
        public CelestialBody body;

        public override void OnQuadPreBuild(PQ quad) { NukeScatterFlatten.Assign(this, quad); }

        public override void OnQuadDestroy(PQ quad) { NukeScatterFlatten.Release(quad); }
    }

    /// <summary>
    /// Tree flattening through Parallax: soft (reflection only), and does nothing if Parallax isn't installed or its
    /// scatter-mask API isn't the one this was written against (ParallaxContinued 2.x: PQSMod_MapDecalVertexRemoveScatter,
    /// ScatterComponent.OnQuadDestroyed/OnQuadVisibleBuilt, ConfigLoader.parallaxScatterBodies).
    /// </summary>
    internal static class NukeScatterFlatten
    {
        static readonly List<NukeFlattenSite> sites = new List<NukeFlattenSite>();
        static readonly Dictionary<string, PQSMod_BDNukesScatterMask> mods = new Dictionary<string, PQSMod_BDNukesScatterMask>();
        static readonly Dictionary<string, List<string>> treeNames = new Dictionary<string, List<string>>(), allNames = new Dictionary<string, List<string>>();
        const int MaxSites = 200;

        // ---- Parallax reflection ----
        static bool resolved, available;
        static Type maskType;
        static IDictionary maskedPerQuad, scatterQuadData, scatterBodies;
        static FieldInfo fBlocked, fRadius, fPosition, fAngle, fColorMap, fNormPos, fRot, fFastScatters, fScatterName;
        static MethodInfo mQuadDestroyed, mQuadVisibleBuilt;
        static readonly object[] arg = new object[1];
        static Texture2D maskTex;
        static GameObject holderRoot;

        static readonly FieldInfo modsField = typeof(PQS).GetField("mods", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo modCountField = typeof(PQS).GetField("modCount", BindingFlags.NonPublic | BindingFlags.Instance);

        // names that count as trees and bushes: these go down out to the kill radius, the rest only near the crater
        static readonly string[] TreeWords = { "tree", "pine", "oak", "palm", "birch", "spruce", "fir", "conifer", "baobab", "bush", "shrub", "forest", "fern", "cact", "flora", "mushroom", "reed", "weed" };

        public static int Count => sites.Count;
        public static bool Available { get { Resolve(); return available; } }

        static Type FindType(string name)
        {
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                if (la?.assembly == null) continue;
                try
                {
                    var t = la.assembly.GetType(name, false);
                    if (t != null) return t;
                }
                catch (Exception) { }
            }
            return null;
        }

        static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                maskType = FindType("Parallax.PQS_Mods.PQSMod_MapDecalVertexRemoveScatter");
                var compType = FindType("Parallax.ScatterComponent");
                var loaderType = FindType("Parallax.ConfigLoader");
                var bodyType = FindType("Parallax.ParallaxScatterBody");
                var scatterType = FindType("Parallax.Scatter");
                if (maskType == null || compType == null || loaderType == null || bodyType == null || scatterType == null) return; // no Parallax
                const BindingFlags I = BindingFlags.Public | BindingFlags.Instance, S = BindingFlags.Public | BindingFlags.Static;
                maskedPerQuad = maskType.GetField("maskedScattersPerQuad", S)?.GetValue(null) as IDictionary;
                fBlocked = maskType.GetField("blockedScatters", I);
                fRadius = maskType.GetField("radius", I);
                fPosition = maskType.GetField("position", I);
                fAngle = maskType.GetField("angle", I);
                fColorMap = maskType.GetField("colorMap", I);
                fNormPos = maskType.GetField("normalisedPosition", I);
                fRot = maskType.GetField("rot", I);
                scatterQuadData = compType.GetField("scatterQuadData", S)?.GetValue(null) as IDictionary;
                mQuadDestroyed = compType.GetMethod("OnQuadDestroyed", S, null, new[] { typeof(PQ) }, null);
                mQuadVisibleBuilt = compType.GetMethod("OnQuadVisibleBuilt", S, null, new[] { typeof(PQ) }, null);
                scatterBodies = loaderType.GetField("parallaxScatterBodies", S)?.GetValue(null) as IDictionary;
                fFastScatters = bodyType.GetField("fastScatters", I);
                fScatterName = scatterType.GetField("scatterName", I);
                available = maskedPerQuad != null && fBlocked != null && fBlocked.FieldType == typeof(List<string>) && fRadius != null
                    && fColorMap != null && fNormPos != null && fRot != null && scatterQuadData != null && mQuadDestroyed != null
                    && mQuadVisibleBuilt != null && scatterBodies != null && fFastScatters != null && fScatterName != null
                    && modsField != null && modCountField != null;
                Debug.Log(available
                    ? "[BDNukes]: Parallax scatter masking found: blasts will flatten trees."
                    : "[BDNukes]: Parallax is installed but its scatter-mask API isn't the expected one; tree flattening is off.");
            }
            catch (Exception e)
            {
                available = false;
                Debug.LogWarning("[BDNukes]: Parallax scatter masking unavailable: " + e.Message);
            }
        }

        /// <summary>The shared mask: green 0 (scatters removed) inside a slightly ragged disk of radius ~0.5 in uv, white outside.</summary>
        static Texture2D MaskTexture()
        {
            if (maskTex != null) return maskTex;
            const int N = 256;
            maskTex = new Texture2D(N, N, TextureFormat.RGBA32, false, true) { name = "BDNukesTreeMask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N - 0.5f, v = (y + 0.5f) / N - 0.5f;
                    float r = Mathf.Sqrt(u * u + v * v) * 2f;      // 1 at the disk edge
                    float a = Mathf.Atan2(v, u);
                    // ragged edge: hills shelter some stands, the shock fells others a bit further out
                    float edge = 0.9f + 0.035f * Mathf.Sin(5f * a + 1.3f) + 0.025f * Mathf.Sin(11f * a + 0.4f) + 0.015f * Mathf.Sin(23f * a + 2.2f);
                    // a fringe where the forest thins out rather than a hard line
                    float keep = Mathf.Clamp01((r - edge) / 0.08f);
                    float hash = Mathf.Repeat(Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.547f, 1f);
                    byte g = keep >= 1f || (keep > 0f && hash < keep) ? (byte)255 : (byte)0;
                    if (x == 0 || y == 0 || x == N - 1 || y == N - 1) g = 255;
                    px[y * N + x] = new Color32(g, g, g, 255);
                }
            maskTex.SetPixels32(px);
            maskTex.Apply(false, false);
            UnityEngine.Object.DontDestroyOnLoad(maskTex);
            return maskTex;
        }

        /// <summary>The body's Parallax scatter names, split into trees/bushes and everything.</summary>
        static bool Names(PQS pqs, out List<string> trees, out List<string> all)
        {
            trees = all = null;
            if (pqs == null) return false;
            if (allNames.TryGetValue(pqs.name, out all)) { trees = treeNames[pqs.name]; return all.Count > 0; }
            all = new List<string>();
            trees = new List<string>();
            try
            {
                if (scatterBodies.Contains(pqs.name))
                {
                    var pb = scatterBodies[pqs.name];
                    if (fFastScatters.GetValue(pb) is Array arr)
                        foreach (var sc in arr)
                        {
                            if (sc == null) continue;
                            var nm = fScatterName.GetValue(sc) as string;
                            if (string.IsNullOrEmpty(nm) || all.Contains(nm)) continue;
                            all.Add(nm);
                            string low = nm.ToLowerInvariant();
                            foreach (var w in TreeWords)
                                if (low.Contains(w)) { trees.Add(nm); break; }
                        }
                }
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Couldn't read Parallax scatters for " + pqs.name + ": " + e.Message); }
            // a body with no tree-like scatters (rocks, crystals): nothing goes down outside the crater
            allNames[pqs.name] = all;
            treeNames[pqs.name] = trees;
            if (all.Count > 0)
                Debug.Log("[BDNukes]: Parallax scatters on " + pqs.name + ": " + all.Count + " (" + trees.Count + " flattened by blasts out to the kill radius: " + string.Join(", ", trees.ToArray()) + "; the rest only over craters)");
            return all.Count > 0;
        }

        static Component MakeHolder(PQS pqs, NukeFlattenSite f, List<string> blocked)
        {
            if (holderRoot == null)
            {
                // inactive and outside every PQS: Unity never wakes these components and SetupMods never finds them
                holderRoot = new GameObject("BDNukes_ScatterMasks");
                holderRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holderRoot);
            }
            var go = new GameObject("Mask_" + f.seed);
            go.transform.SetParent(holderRoot.transform, false);
            var h = go.AddComponent(maskType);
            ((PQSMod)h).sphere = pqs;
            ((PQSMod)h).modEnabled = false;
            Vector3d n = f.dir;
            // the mask square spans +-radius: a little over the kill radius so the ragged edge fits inside it
            fBlocked.SetValue(h, new List<string>(blocked));
            fRadius.SetValue(h, (double)f.killRadius * 1.12);
            fPosition?.SetValue(h, (Vector3)n);
            fAngle?.SetValue(h, 0f);
            fColorMap.SetValue(h, MaskTexture());
            fNormPos.SetValue(h, n);
            // as PQSMod_MapDecalVertexRemoveScatter.OnSetup builds it
            fRot.SetValue(h, Quaternion.AngleAxis(0f, Vector3.up) * Quaternion.FromToRotation((Vector3)n, Vector3.up));
            return h;
        }

        static bool Ready(NukeFlattenSite f, PQS pqs)
        {
            if (f.treeHolder != null || f.allHolder != null) return true;
            if (!Names(pqs, out var trees, out var all)) return false;
            if (trees.Count > 0) f.treeHolder = MakeHolder(pqs, f, trees);
            if (f.craterRadius > 0f) f.allHolder = MakeHolder(pqs, f, all);
            return f.treeHolder != null || f.allHolder != null;
        }

        static double HalfAngle(PQ q) => (Math.PI / 4d) / (1 << Mathf.Clamp(q.subdivision, 0, 30)) * 1.6;

        /// <summary>The mask holder a quad should have: from the biggest site touching it; null if none.</summary>
        static Component HolderFor(PQS pqs, string bodyName, PQ q)
        {
            if (sites.Count == 0) return null;
            Vector3d qd = q.positionPlanet.normalized;
            double half = HalfAngle(q);
            NukeFlattenSite best = null;
            double bestDist = 0d;
            for (int i = 0; i < sites.Count; i++)
            {
                var f = sites[i];
                if (f.body != bodyName) continue;
                double ang = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(qd, f.dir))));
                if (ang > half + f.killAngle) continue;
                if (best == null || f.killRadius > best.killRadius) { best = f; bestDist = ang; }
            }
            if (best == null || !Ready(best, pqs)) return null;
            // a quad over the crater loses everything; elsewhere only the trees and bushes go
            if (best.allHolder != null && bestDist <= half + 1.2 * best.craterAngle) return best.allHolder;
            return best.treeHolder; // null on a body with no trees: those quads keep their scatters
        }

        static bool IsMine(object holder)
        {
            if (holder == null) return false;
            for (int i = 0; i < sites.Count; i++)
                if (ReferenceEquals(sites[i].treeHolder, holder) || ReferenceEquals(sites[i].allHolder, holder)) return true;
            return false;
        }

        internal static void Assign(PQSMod_BDNukesScatterMask mod, PQ quad)
        {
            if (!available || quad == null || mod.body == null) return;
            try
            {
                if (maskedPerQuad.Contains(quad)) return; // Parallax's own mask (the space centre) keeps its quads
                var h = HolderFor(mod.sphere, mod.body.bodyName, quad);
                if (h != null) maskedPerQuad[quad] = h;
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Scatter mask assign failed: " + e.Message); }
        }

        internal static void Release(PQ quad)
        {
            if (!available || quad == null) return;
            try
            {
                if (maskedPerQuad.Contains(quad) && IsMine(maskedPerQuad[quad])) maskedPerQuad.Remove(quad);
            }
            catch (Exception) { }
        }

        static PQSMod_BDNukesScatterMask Ensure(CelestialBody body)
        {
            var pqs = body.pqsController;
            if (pqs == null) return null;
            if (mods.TryGetValue(body.bodyName, out var mod) && mod != null && mod.sphere == pqs) return mod;
            var go = new GameObject("BDNukes_ScatterMask");
            go.transform.SetParent(pqs.transform, false);
            mod = go.AddComponent<PQSMod_BDNukesScatterMask>();
            mod.sphere = pqs;
            mod.body = body;
            mod.modEnabled = true;
            mod.order = 99999998; // after Parallax's own scatter masks, so the space centre's mask takes its quads first
            mod.requirements = PQS.ModiferRequirements.Default;
            mods[body.bodyName] = mod;
            var arr = (PQSMod[])modsField.GetValue(pqs) ?? new PQSMod[0];
            if (Array.IndexOf(arr, mod) < 0)
            {
                var grown = new PQSMod[arr.Length + 1];
                Array.Copy(arr, grown, arr.Length);
                grown[arr.Length] = mod;
                modsField.SetValue(pqs, grown);
                modCountField.SetValue(pqs, grown.Length);
            }
            return mod;
        }

        /// <summary>Kill radius of the blast for trees (~5 psi): ~0.9 km * W^(1/3) for an airburst, Kerbin-scaled.</summary>
        public static float KillRadius(float W, float hb, float sizeScale)
        {
            float r = 0.45f * 900f * Mathf.Pow(Mathf.Max(W, 0.001f), 1f / 3f) * sizeScale;
            // the 5 psi ring on the ground: a burst too high up reaches less of it
            float g = (1.3f * r) * (1.3f * r) - hb * hb;
            return g <= 0f ? 0f : Mathf.Min(r, Mathf.Sqrt(g));
        }

        /// <summary>Records a blast's flattened area; the trees come down as the shock gets there (after `delay` s).</summary>
        public static void Add(CelestialBody body, double lat, double lon, float killRadius, float craterRadius, float delay)
        {
            if (!NukeSettings.Current.flattenTrees || body == null || body.pqsController == null || killRadius < 20f) return;
            Resolve();
            if (!available) return;
            var f = new NukeFlattenSite
            {
                body = body.bodyName, lat = lat, lon = lon, killRadius = killRadius, craterRadius = craterRadius,
                seed = UnityEngine.Random.Range(1, int.MaxValue),
            };
            f.Prepare(body);
            sites.Add(f);
            while (sites.Count > MaxSites) { DropHolders(sites[0]); sites.RemoveAt(0); }   // releases its Parallax masks too
            Ensure(body);
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Trees flattened on {0} at {1:F4},{2:F4} out to {3:F0} m (crater {4:F0} m).",
                f.body, lat, lon, killRadius, craterRadius));
            NukeWorldScenario.Schedule(delay, () => Refresh(body, f));
        }

        /// <summary>
        /// Re-masks the built quads around a site and rebuilds the scatters of the ones on show (the hidden ones pick
        /// the mask up when Parallax next builds their scatters). Also redoes scatters over a crater rebuild: Parallax
        /// copies a quad's mesh when it builds its scatters, so after a crater reshapes the ground they would float.
        /// </summary>
        public static void Refresh(CelestialBody body, NukeFlattenSite only, NukeCrater onlyCrater = null)
        {
            if (!available || body == null || body.pqsController == null || !body.pqsController.isActive) return;
            if (FlightGlobals.currentMainBody != body) return;
            var pqs = body.pqsController;
            if (pqs.quads == null) return;
            var mod = sites.Count > 0 ? Ensure(body) : null;
            var stack = new Stack<PQ>();
            foreach (var q in pqs.quads) if (q != null) stack.Push(q);
            int masked = 0, rebuilt = 0;
            var crs = NukeCraters.For(body.bodyName);
            if (onlyCrater != null) crs = new List<NukeCrater> { onlyCrater };
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                if (!Touches(q, body.bodyName, only, crs, onlyCrater != null)) continue;
                if (q.subNodes != null) foreach (var sub in q.subNodes) if (sub != null) stack.Push(sub);
                if (!q.isBuilt) continue;
                try
                {
                    if (mod != null)
                    {
                        object cur = maskedPerQuad.Contains(q) ? maskedPerQuad[q] : null;
                        if (cur == null || IsMine(cur))
                        {
                            var h = HolderFor(pqs, body.bodyName, q);
                            if (h != null && !ReferenceEquals(h, cur)) { maskedPerQuad[q] = h; masked++; }
                        }
                    }
                    if (scatterQuadData.Contains(q))
                    {
                        arg[0] = q;
                        mQuadDestroyed.Invoke(null, arg);
                        mQuadVisibleBuilt.Invoke(null, arg);
                        arg[0] = null;
                        rebuilt++;
                    }
                }
                catch (Exception e)
                {
                    arg[0] = null;
                    Debug.LogWarning("[BDNukes]: Scatter refresh failed on a quad: " + (e.InnerException ?? e).Message);
                }
            }
            if (masked + rebuilt > 0) Debug.Log("[BDNukes]: Scatter mask refresh on " + body.bodyName + ": " + masked + " quads masked, " + rebuilt + " scatter sets rebuilt.");
        }

        static bool Touches(PQ q, string bodyName, NukeFlattenSite only, List<NukeCrater> craters, bool cratersOnly = false)
        {
            double half = HalfAngle(q);
            Vector3d qd = q.positionPlanet.normalized;
            bool Near(Vector3d d, double a) => Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(qd, d)))) <= half + a;
            if (only != null) return Near(only.dir, only.killAngle);
            if (!cratersOnly)
            for (int i = 0; i < sites.Count; i++)
                if (sites[i].body == bodyName && Near(sites[i].dir, sites[i].killAngle)) return true;
            if (craters != null)
                for (int i = 0; i < craters.Count; i++)
                    if (Near(craters[i].dir, craters[i].influenceAngle)) return true;
            return false;
        }

        // ---- after a load, and after the craters' own terrain rebuilds ----
        static string seenRebuild;
        static string checkedBody;

        public static void Tick()
        {
            var body = FlightGlobals.currentMainBody;
            if (body == null || body.pqsController == null || !body.pqsController.isActive) return;
            if (!(HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.SPACECENTER)) return;
            Resolve();
            if (!available) return;
            bool any = false;
            for (int i = 0; i < sites.Count; i++) if (sites[i].body == body.bodyName) { any = true; break; }
            if (checkedBody != body.bodyName)
            {
                // a scene or body change: the quads built before the mod was there need their masks now
                checkedBody = body.bodyName;
                seenRebuild = NukeCraters.lastRebuild;
                if (any) Refresh(body, null);
                return;
            }
            if (seenRebuild != NukeCraters.lastRebuild)
            {
                seenRebuild = NukeCraters.lastRebuild;
                // only the ground around the crater that was just dug changed
                var c = NukeCraters.lastRebuildCrater;
                if (c != null && c.body == body.bodyName) Refresh(body, null, c);
                else Refresh(body, null);
            }
        }

        public static void Load(ConfigNode node)
        {
            foreach (var f in sites) DropHolders(f);
            sites.Clear();
            checkedBody = null;
            foreach (var cn in node.GetNodes("FLATTEN"))
            {
                var f = NukeFlattenSite.Load(cn);
                var body = f != null ? FlightGlobals.GetBodyByName(f.body) : null;
                if (body == null) continue;
                f.Prepare(body);
                sites.Add(f);
            }
            while (sites.Count > MaxSites) sites.RemoveAt(0);   // nothing is masked yet, so no holders to drop
            if (sites.Count == 0) return;
            Resolve();
            if (!available) return;
            foreach (var f in sites)
            {
                var b = FlightGlobals.GetBodyByName(f.body);
                if (b != null) Ensure(b);
            }
            Debug.Log("[BDNukes]: Loaded " + sites.Count + " flattened-forest sites.");
        }

        static void DropHolders(NukeFlattenSite f)
        {
            if (maskedPerQuad != null)
            {
                // a quad still pointing at a holder that is going away: let it go
                var stale = new List<object>();
                foreach (DictionaryEntry e in maskedPerQuad)
                    if (ReferenceEquals(e.Value, f.treeHolder) || ReferenceEquals(e.Value, f.allHolder)) stale.Add(e.Key);
                foreach (var k in stale) maskedPerQuad.Remove(k);
            }
            if (f.treeHolder != null) UnityEngine.Object.Destroy(f.treeHolder.gameObject);
            if (f.allHolder != null) UnityEngine.Object.Destroy(f.allHolder.gameObject);
            f.treeHolder = f.allHolder = null;
        }

        public static void Save(ConfigNode node)
        {
            foreach (var f in sites) node.AddNode(f.Save());
        }

    }
}
