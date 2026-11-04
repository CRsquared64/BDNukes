using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BDNukes
{
    /// <summary>One crater left in the terrain by a surface or near-surface burst.</summary>
    internal class NukeCrater
    {
        public string body;
        public double lat, lon;
        public float radius;    // apparent crater radius, centre to rim crest (m)
        public float depth;     // floor depth below the original ground (m)
        public float rim;       // rim crest height above the original ground (m)
        public float yield;     // kt, for reference
        public int seed;

        // derived, in PQS space (the frame CelestialBody.TerrainAltitude hands to PQS.GetSurfaceHeight)
        internal Vector3d dir, east, north;
        internal double cosInfluence, influenceAngle;
        internal float ph1, ph2, ph3;

        /// <summary>Ejecta blanket reaches this many crater radii out from the centre.</summary>
        public const float EjectaReach = 3f;

        public void Prepare(CelestialBody b)
        {
            dir = b.GetRelSurfaceNVector(lat, lon).normalized;
            Vector3d pole = Math.Abs(dir.y) > 0.99 ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
            east = Vector3d.Cross(pole, dir).normalized;
            north = Vector3d.Cross(dir, east);
            influenceAngle = EjectaReach * radius / b.Radius;
            cosInfluence = Math.Cos(influenceAngle);
            var rng = new System.Random(seed);
            ph1 = (float)rng.NextDouble() * 6.283f;
            ph2 = (float)rng.NextDouble() * 6.283f;
            ph3 = (float)rng.NextDouble() * 6.283f;
        }

        /// <summary>
        /// Height change (m) at a PQS radial direction: a parabolic bowl from -depth at the centre to +rim at the
        /// (slightly ragged) rim crest, then the ejecta blanket thinning out to nothing at EjectaReach radii.
        /// Zero at the edge of the influence, so quads outside it never change and no seams open up.
        /// </summary>
        public double Offset(Vector3d v, double bodyRadius)
        {
            double c = Vector3d.Dot(v, dir);
            if (c <= cosInfluence) return 0d;
            double ang = Math.Acos(Math.Min(1d, c));
            double r = ang * bodyRadius;
            double az = Math.Atan2(Vector3d.Dot(v, east), Vector3d.Dot(v, north));
            double ragged = 1d + 0.05 * Math.Sin(5d * az + ph1) + 0.035 * Math.Sin(11d * az + ph2) + 0.02 * Math.Sin(23d * az + ph3);
            double x = r / (radius * ragged);
            if (x <= 1d) return -depth + (depth + rim) * x * x;
            double reach = EjectaReach / ragged;
            if (x >= reach) return 0d;
            double k = (reach - x) / (reach - 1d);
            return rim * k * k * k;
        }

        public ConfigNode Save()
        {
            var n = new ConfigNode("CRATER");
            n.AddValue("body", body);
            n.AddValue("lat", lat.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("lon", lon.ToString("R", CultureInfo.InvariantCulture));
            n.AddValue("radius", radius.ToString("0.##", CultureInfo.InvariantCulture));
            n.AddValue("depth", depth.ToString("0.##", CultureInfo.InvariantCulture));
            n.AddValue("rim", rim.ToString("0.##", CultureInfo.InvariantCulture));
            n.AddValue("yield", yield.ToString("0.####", CultureInfo.InvariantCulture));
            n.AddValue("seed", seed.ToString(CultureInfo.InvariantCulture));
            return n;
        }

        public static NukeCrater Load(ConfigNode n)
        {
            var c = new NukeCrater { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(c.body)) return null;
            if (!n.TryGetValue("lat", ref c.lat) || !n.TryGetValue("lon", ref c.lon) || !n.TryGetValue("radius", ref c.radius)) return null;
            n.TryGetValue("depth", ref c.depth);
            n.TryGetValue("rim", ref c.rim);
            n.TryGetValue("yield", ref c.yield);
            n.TryGetValue("seed", ref c.seed);
            return c.radius > 0f ? c : null;
        }
    }

    /// <summary>
    /// Adds the craters to the terrain height chain. It runs last, after every other height mod (PQSCity flattening
    /// included), and is offset from whatever ground was there, so it works on any terrain, Kopernicus or stock.
    /// Anything that asks the PQS for a height (TerrainAltitude, the scorch decal, the terrain colliders) sees it.
    /// </summary>
    internal class PQSMod_BDNukesCrater : PQSMod
    {
        public CelestialBody body;
        public List<NukeCrater> craters = new List<NukeCrater>();

        /// <summary>Built-up sites (the space centre, other PQS cities): craters leave their ground alone.</summary>
        public readonly List<Vector3d> siteDirs = new List<Vector3d>();
        public readonly List<double> siteCosIn = new List<double>(), siteCosOut = new List<double>();

        public override void OnVertexBuildHeight(PQS.VertexBuildData data)
        {
            int n = craters.Count;
            if (n == 0) return;
            double R = sphere.radius;
            Vector3d v = data.directionFromCenter;
            double h = 0d;
            for (int i = 0; i < n; i++)
                h += craters[i].Offset(v, R);
            if (h == 0d) return;
            // inside a protected site the crater fades out: buildings stay on their own ground, not buried under a
            // rim or left floating over a bowl
            for (int i = 0; i < siteDirs.Count; i++)
            {
                double c = Vector3d.Dot(v, siteDirs[i]);
                if (c <= siteCosOut[i]) continue;
                if (c >= siteCosIn[i]) return;
                double k = (c - siteCosOut[i]) / (siteCosIn[i] - siteCosOut[i]);
                h *= 1d - k * k * (3d - 2d * k);
            }
            data.vertHeight += h;
        }

        /// <summary>Finds the body's built-up sites: PQSCity (the space centre and its like) and PQSCity2 (Kerbal Konstructs, stock monuments).</summary>
        public void FindSites()
        {
            siteDirs.Clear(); siteCosIn.Clear(); siteCosOut.Clear();
            if (!NukeSettings.Current.protectSites || sphere == null) return;
            double R = sphere.radius;
            void Add(Vector3d dir, double inner, double outer)
            {
                if (dir.sqrMagnitude < 1e-12) return;
                siteDirs.Add(dir.normalized);
                siteCosIn.Add(Math.Cos(inner / R));
                siteCosOut.Add(Math.Cos(outer / R));
            }
            foreach (var city in sphere.GetComponentsInChildren<PQSCity>(true))
            {
                bool ksc = city.name == "KSC" || city.name.StartsWith("KSC");
                Add((Vector3d)city.repositionRadial, ksc ? 1300d : 250d, ksc ? 2000d : 450d);
            }
            foreach (var city in sphere.GetComponentsInChildren<PQSCity2>(true))
                Add(body.GetRelSurfaceNVector(city.lat, city.lon), 200d, 380d);
        }
    }

    /// <summary>
    /// The crater registry, the runtime PQSMod per body and the terrain rebuilds. The craters live in the save
    /// (NukeCraterScenario) and get added back whenever a game loads.
    /// </summary>
    internal static class NukeCraters
    {
        static readonly Dictionary<string, List<NukeCrater>> byBody = new Dictionary<string, List<NukeCrater>>();
        static readonly Dictionary<string, PQSMod_BDNukesCrater> mods = new Dictionary<string, PQSMod_BDNukesCrater>();
        // the craters each body's built terrain already has; any difference from the live list is what's stale
        static readonly Dictionary<string, List<NukeCrater>> applied = new Dictionary<string, List<NukeCrater>>();
        // rebuilds already scheduled (under a flash) per body: the stale check leaves those bodies alone
        static readonly Dictionary<string, int> pending = new Dictionary<string, int>();

        static readonly FieldInfo modsField = typeof(PQS).GetField("mods", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo modCountField = typeof(PQS).GetField("modCount", BindingFlags.NonPublic | BindingFlags.Instance);
        // tells every mod a quad is going away (Parallax drops its per-quad data) before it is rebuilt in place,
        // or Parallax's OnQuadBuilt throws on the second registration of the same quad
        static readonly MethodInfo quadDestroyMethod = typeof(PQS).GetMethod("Mod_OnQuadDestroy", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly object[] quadArg = new object[1];

        internal static string lastRebuild = "none yet";

        public static int Count
        {
            get
            {
                int n = 0;
                foreach (var l in byBody.Values) n += l.Count;
                return n;
            }
        }

        static List<NukeCrater> ListFor(string body)
        {
            if (!byBody.TryGetValue(body, out var l)) byBody[body] = l = new List<NukeCrater>();
            return l;
        }

        /// <summary>Craters added or removed since the body's terrain was last built (by seed).</summary>
        static List<NukeCrater> Changed(string body)
        {
            var now = ListFor(body);
            applied.TryGetValue(body, out var had);
            var diff = new List<NukeCrater>();
            var nowSeeds = new HashSet<int>();
            foreach (var c in now) nowSeeds.Add(c.seed);
            var hadSeeds = new HashSet<int>();
            if (had != null)
                foreach (var c in had)
                {
                    hadSeeds.Add(c.seed);
                    if (!nowSeeds.Contains(c.seed)) diff.Add(c);
                }
            foreach (var c in now) if (!hadSeeds.Contains(c.seed)) diff.Add(c);
            return diff;
        }

        /// <summary>
        /// Crater size for a burst: a contact burst makes a crater of ~52 m * W^0.294 radius (W in kt: ~125 m at
        /// 20 kt, ~400 m at 1 Mt, ~1.3 km at 50 Mt), a bit over real dry-soil figures so it reads at game scale.
        /// It shrinks into a broad, shallow blast depression as the burst rises. Small bursts stop cratering at
        /// about half a fireball radius up; big ones keep pressing a depression in from higher, up to ~2.2 fireball
        /// radii for megaton airbursts (a Tsar Bomba airburst at 4 km still leaves one).
        /// Lower gravity (the Mun, Minmus) lets the ejecta fly further: bigger craters.
        /// Returns null if this burst leaves no crater.
        /// </summary>
        public static NukeCrater Plan(CelestialBody body, double lat, double lon, float W, float R, float hb)
        {
            var s = NukeSettings.Current;
            if (!s.craters || body == null || body.pqsController == null) return null;
            float big = Mathf.Clamp01(Mathf.Log10(Mathf.Max(W, 1f)) / 4f);   // 0 at 1 kt .. 1 at 10 Mt
            float reach = R * Mathf.Lerp(0.5f, 2.2f, big * big);              // highest burst that still craters
            float contact = 1f - Mathf.Clamp01((hb - 0.03f * R) / (reach - 0.03f * R));
            if (contact <= 0.05f) return null;
            float g = (float)(body.GeeASL * 9.80665);
            float gScale = Mathf.Clamp(Mathf.Pow(9.81f / Mathf.Max(g, 0.05f), 0.15f), 0.8f, 1.7f);
            // an airburst's depression is wider than it is deep: the radius holds up, the depth falls away
            float radius = 52f * Mathf.Pow(W, 0.294f) * gScale * s.craterScale * Mathf.Lerp(0.6f, 1f, contact);
            float depthRatio = Mathf.Lerp(0.4f, 0.1f, big);
            float depth = radius * depthRatio * Mathf.Lerp(0.15f, 1f, contact * contact);
            if (radius < 2f) return null;
            return new NukeCrater
            {
                body = body.bodyName, lat = lat, lon = lon, radius = radius, depth = depth, rim = 0.25f * depth,
                yield = W, seed = UnityEngine.Random.Range(1, int.MaxValue),
            };
        }

        /// <summary>
        /// Adds a crater straight away, so heights sampled from now on (the scorch decal) already include it, and
        /// rebuilds the visible terrain after rebuildDelay seconds, under the flash.
        /// </summary>
        public static void Add(NukeCrater c, float rebuildDelay)
        {
            var body = FlightGlobals.GetBodyByName(c.body);
            if (body == null) return;
            var list = ListFor(c.body);
            c.Prepare(body);
            list.Add(c);
            int max = NukeSettings.Current.maxCraters;
            while (list.Count > max) list.RemoveAt(0);
            var mod = Ensure(body);
            if (mod == null) return;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BDNukes]: Crater on {0} at {1:F4},{2:F4}: radius {3:F0} m, depth {4:F0} m ({5:0.###} kt). Terrain vertex spacing here ~{6:F0} m.",
                c.body, c.lat, c.lon, c.radius, c.depth, c.yield, VertexSpacing(body.pqsController)));
            pending.TryGetValue(c.body, out int p);
            pending[c.body] = p + 1;
            NukeCraterScenario.ScheduleRebuild(body, c, rebuildDelay);
        }

        /// <summary>A scheduled rebuild has come due: rebuild whatever changed (a salvo's earlier rebuild may have covered it).</summary>
        public static void RebuildDue(CelestialBody body, NukeCrater c)
        {
            if (pending.TryGetValue(c.body, out int p)) pending[c.body] = Math.Max(0, p - 1);
            if (body == null || FlightGlobals.currentMainBody != body) return;
            Rebuild(body);
        }

        public static void ClearAll()
        {
            foreach (var l in byBody.Values) l.Clear();
            var body = FlightGlobals.currentMainBody;
            if (body != null) Rebuild(body);
        }

        /// <summary>The finest terrain mesh spacing on a body: a cube face split 2^maxLevel times, cacheRes segments per quad.</summary>
        public static double VertexSpacing(PQS pqs)
        {
            if (pqs == null) return 0d;
            return pqs.radius * (Math.PI / 2d) / (1 << Mathf.Clamp(pqs.maxLevel, 0, 30)) / Math.Max(1, PQS.cacheRes);
        }

        static PQSMod_BDNukesCrater Ensure(CelestialBody body)
        {
            var pqs = body.pqsController;
            if (pqs == null) return null;
            if (mods.TryGetValue(body.bodyName, out var mod) && mod != null && mod.sphere == pqs) return mod;
            if (modsField == null || modCountField == null)
            {
                Debug.LogError("[BDNukes]: Can't find PQS.mods; terrain craters are disabled.");
                return null;
            }
            var go = new GameObject("BDNukes_Craters");
            go.transform.SetParent(pqs.transform, false);
            mod = go.AddComponent<PQSMod_BDNukesCrater>();
            mod.sphere = pqs;
            mod.body = body;
            mod.craters = ListFor(body.bodyName);
            mod.modEnabled = true;
            mod.order = 99999999; // last in the height chain; also where SetupMods sorts it if it ever runs again
            mod.requirements = PQS.ModiferRequirements.Default;
            mod.FindSites();
            mods[body.bodyName] = mod;

            // splice it into the live mod list (SetupMods would re-run OnSetup on every mod of the body)
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

        /// <summary>
        /// Rebuilds the terrain so the built quads pick up a change to these craters.
        /// Partial (the default): the visible quads touching the craters are rebuilt on the spot, a few ms; the
        /// hidden ones (coarser parents, cached children) are marked unbuilt, and PQ.SetVisible rebuilds a quad that
        /// isn't built when it next comes into view, so the crater is there at every zoom level.
        /// Full: RebuildSphere, correct everywhere but a multi-second freeze (~3.3 s on Kerbin with Parallax).
        /// </summary>
        public static void Rebuild(CelestialBody body)
        {
            var pqs = body.pqsController;
            if (pqs == null || !pqs.isActive) return;
            var around = Changed(body.bodyName);
            if (around.Count == 0) return;
            if (ListFor(body.bodyName).Count > 0) Ensure(body);
            var sw = Stopwatch.StartNew();
            string how;
            if (NukeSettings.Current.craterPartialRebuild)
            {
                RebuildQuadsNear(pqs, around, out int built, out int deferred);
                how = "partial: " + built + " visible quads rebuilt, " + deferred + " hidden ones on next view";
                if (built + deferred == 0) Debug.LogWarning("[BDNukes]: Partial crater rebuild found no quads near the crater; it will appear as the terrain refreshes.");
            }
            else
            {
                pqs.RebuildSphere();
                how = "full RebuildSphere";
            }
            sw.Stop();
            applied[body.bodyName] = new List<NukeCrater>(ListFor(body.bodyName));
            lastRebuild = string.Format(CultureInfo.InvariantCulture, "{0}: {1:F0} ms ({2})", body.bodyName, sw.Elapsed.TotalMilliseconds, how);
            Debug.Log("[BDNukes]: Terrain rebuilt for craters on " + lastRebuild);
        }

        static bool Touches(PQ q, List<NukeCrater> around)
        {
            // a quad's half-diagonal, as an angle: a cube face is ~90 degrees across, halved per subdivision
            double half = (Math.PI / 4d) / (1 << Mathf.Clamp(q.subdivision, 0, 30)) * 1.6;
            Vector3d qd = q.positionPlanet.normalized;
            foreach (var c in around)
            {
                double ang = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(qd, c.dir))));
                if (ang <= half + c.influenceAngle) return true;
            }
            return false;
        }

        static void RebuildQuadsNear(PQS pqs, List<NukeCrater> around, out int built, out int deferred)
        {
            built = deferred = 0;
            if (pqs.quads == null) return;
            var stack = new Stack<PQ>();
            foreach (var q in pqs.quads) if (q != null) stack.Push(q);
            while (stack.Count > 0)
            {
                var q = stack.Pop();
                if (!Touches(q, around)) continue;
                // walk cached children too: a collapsed quad keeps them, with their old meshes, for re-subdividing
                if (q.subNodes != null)
                    foreach (var sub in q.subNodes) if (sub != null) stack.Push(sub);
                if (!q.isBuilt) continue;
                bool shown = !q.isSubdivided && q.isVisible && q.gameObject.activeInHierarchy;
                QuadDestroyed(pqs, q);
                if (!shown)
                {
                    q.isBuilt = false; // PQ.SetVisible rebuilds it when it comes back into view
                    deferred++;
                    continue;
                }
                q.isBuilt = false;
                bool ok = pqs.BuildQuad(q);
                q.isBuilt = true;
                if (!ok) continue;
                built++;
                if (q.meshCollider != null && q.meshCollider.sharedMesh != null)
                {
                    q.meshCollider.sharedMesh = null; // force PhysX to re-bake it
                    q.meshCollider.sharedMesh = q.mesh;
                }
                q.QueueForNormalUpdate();
                if (q.north != null) q.north.QueueForNormalUpdate();
                if (q.south != null) q.south.QueueForNormalUpdate();
                if (q.east != null) q.east.QueueForNormalUpdate();
                if (q.west != null) q.west.QueueForNormalUpdate();
            }
        }

        static void QuadDestroyed(PQS pqs, PQ q)
        {
            if (quadDestroyMethod == null) return;
            quadArg[0] = q;
            try { quadDestroyMethod.Invoke(pqs, quadArg); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Mod_OnQuadDestroy failed: " + (e.InnerException ?? e).Message); }
            quadArg[0] = null;
        }

        /// <summary>
        /// Brings the active body's built terrain up to date with the crater list (after a load or a revert), only
        /// around the craters that changed. Leaves it alone while a rebuild is waiting for a flash.
        /// </summary>
        public static void CheckStale()
        {
            var body = FlightGlobals.currentMainBody;
            if (body == null || body.pqsController == null || !body.pqsController.isActive) return;
            if (pending.TryGetValue(body.bodyName, out int p) && p > 0) return;
            Rebuild(body);
        }

        public static void Load(ConfigNode node)
        {
            foreach (var l in byBody.Values) l.Clear();
            pending.Clear();
            int n = 0;
            foreach (var cn in node.GetNodes("CRATER"))
            {
                var c = NukeCrater.Load(cn);
                var body = c != null ? FlightGlobals.GetBodyByName(c.body) : null;
                if (body == null) continue;
                c.Prepare(body);
                ListFor(c.body).Add(c);
                Ensure(body);
                n++;
            }
            if (n > 0) Debug.Log("[BDNukes]: Loaded " + n + " terrain craters.");
        }

        public static void Save(ConfigNode node)
        {
            foreach (var l in byBody.Values)
                foreach (var c in l) node.AddNode(c.Save());
        }

        public static string Status() =>
            Count + " craters. Finest terrain spacing here: ~" +
            (FlightGlobals.currentMainBody != null ? VertexSpacing(FlightGlobals.currentMainBody.pqsController).ToString("F0", CultureInfo.InvariantCulture) : "?") +
            " m. Last rebuild: " + lastRebuild;
    }

    /// <summary>Keeps the craters in the save and runs the delayed terrain rebuilds.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class NukeCraterScenario : ScenarioModule
    {
        static NukeCraterScenario instance;
        float nextStaleCheck;

        public override void OnAwake()
        {
            base.OnAwake();
            instance = this;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        public override void OnLoad(ConfigNode node) { NukeCraters.Load(node); NukePersistence.Load(node); }

        public override void OnSave(ConfigNode node) { NukeCraters.Save(node); NukePersistence.Save(node); }

        void Update()
        {
            NukePersistence.Tick();
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && HighLogic.LoadedScene != GameScenes.SPACECENTER) return;
            if (Time.unscaledTime < nextStaleCheck) return;
            nextStaleCheck = Time.unscaledTime + 1f;
            NukeCraters.CheckStale();
        }

        internal static void ScheduleRebuild(CelestialBody body, NukeCrater c, float delay)
        {
            if (instance != null) instance.StartCoroutine(instance.RebuildLater(body, c, delay));
            else NukeCraters.RebuildDue(body, c);
        }

        IEnumerator RebuildLater(CelestialBody body, NukeCrater c, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            NukeCraters.RebuildDue(body, c);
        }
    }
}
