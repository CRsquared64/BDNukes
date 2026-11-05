using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// The fallout a ground or low burst leaves on the ground: a cigar-shaped hot zone stretching downwind from
    /// ground zero (setting falloutFootprint). It lives in the save with the universal time of the burst, shows as a
    /// faint brownish-grey tint draped on the terrain that fades over footprintDays game days, and gives a slow dose
    /// (through NukeRadiation) to kerbals inside it once the airborne fallout field has gone.
    /// </summary>
    internal class NukeFootprintRecord
    {
        public string body;
        public double lat, lon, ut;
        public float W, falloutK, heading, length, width, life;
        public int seed;

        internal NukeFootprintDecal visual;

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("FOOTPRINT");
            n.AddValue("body", body);
            n.AddValue("lat", F(lat)); n.AddValue("lon", F(lon)); n.AddValue("ut", F(ut));
            n.AddValue("W", F(W)); n.AddValue("falloutK", F(falloutK)); n.AddValue("heading", F(heading));
            n.AddValue("length", F(length)); n.AddValue("width", F(width)); n.AddValue("life", F(life));
            n.AddValue("seed", seed.ToString(CultureInfo.InvariantCulture));
            return n;
        }

        public static NukeFootprintRecord Load(ConfigNode n)
        {
            var r = new NukeFootprintRecord { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(r.body)) return null;
            if (!n.TryGetValue("lat", ref r.lat) || !n.TryGetValue("lon", ref r.lon) || !n.TryGetValue("ut", ref r.ut)) return null;
            n.TryGetValue("W", ref r.W); n.TryGetValue("falloutK", ref r.falloutK); n.TryGetValue("heading", ref r.heading);
            n.TryGetValue("length", ref r.length); n.TryGetValue("width", ref r.width); n.TryGetValue("life", ref r.life);
            n.TryGetValue("seed", ref r.seed);
            return r.length > 10f && r.width > 1f ? r : null;
        }

        /// <summary>Ground-zero-relative east/north (m) of a lat/lon, flat-earth about ground zero (fine for a few hundred km).</summary>
        public void Local(CelestialBody b, double la, double lo, out double east, out double north)
        {
            double dLon = lo - lon;
            while (dLon > 180d) dLon -= 360d;
            while (dLon < -180d) dLon += 360d;
            east = dLon * Mathf.Deg2Rad * b.Radius * System.Math.Cos(lat * Mathf.Deg2Rad);
            north = (la - lat) * Mathf.Deg2Rad * b.Radius;
        }

        /// <summary>
        /// Contamination (0..1) at a ground-zero-relative (east, north): an ellipse from a little upwind of ground
        /// zero to `length` downwind, hottest along its axis and close in, plus the ground-zero hot spot.
        /// </summary>
        public float Shape(double east, double north)
        {
            float h = heading * Mathf.Deg2Rad;
            float sh = Mathf.Sin(h), ch = Mathf.Cos(h);
            float along = (float)(east * sh + north * ch);
            float across = (float)(east * ch - north * sh);
            float c = 0.44f * length, a = 0.56f * length, b = 0.5f * width;
            float u = (along - c) / a, v = across / b;
            float e = u * u + v * v;
            float gzR = 0.6f * b;
            float gz = Mathf.Exp(-(along * along + across * across) / (gzR * gzR));
            if (e >= 1f) return Mathf.Clamp01(0.5f * gz);
            float k = Mathf.Clamp01((e - 0.35f) / 0.65f);
            float core = 1f - k * k * (3f - 2f * k);
            float axial = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(along / length));
            return Mathf.Clamp01(core * axial + 0.5f * gz);
        }
    }

    internal static class NukeFootprint
    {
        static readonly List<NukeFootprintRecord> records = new List<NukeFootprintRecord>();
        const int MaxRecords = 64;
        const float RefTime = 60f;

        static double Now => NukePersistence.Now;

        /// <summary>Seconds in a game day (6 h on a Kerbin calendar).</summary>
        static double DaySeconds => KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : 21600d;

        public static double Lifespan => NukeSettings.Current.footprintDays * DaySeconds;

        public static bool AnyOn(CelestialBody b)
        {
            if (b == null) return false;
            for (int i = 0; i < records.Count; i++) if (records[i].body == b.bodyName) return true;
            return false;
        }

        public static void Add(CelestialBody body, double lat, double lon, float W, float R, float falloutK, float windHeading, NukeWind wind, float delay)
        {
            var s = NukeSettings.Current;
            float life = NukeFallout.Lifetime(s, W);
            // the plume goes where the wind aloft carries it, veer and all, over the time the fallout comes down
            float heading = windHeading;
            if (wind != null)
            {
                Vector3 d = wind.At(0.45f * life, 0.6f);
                if (d.sqrMagnitude > 1f) heading = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            }
            // ~3 km * sqrt(kt) for the hot zone, scaled down for Kerbin like the rest; dirtier bursts reach further
            float length = Mathf.Clamp(0.45f * 3000f * Mathf.Sqrt(Mathf.Max(W, 0.01f)) * s.sizeScale * Mathf.Sqrt(Mathf.Clamp01(falloutK)), 800f, 300000f);
            float width = Mathf.Max(length / 8f, 2.5f * R);
            var r = new NukeFootprintRecord
            {
                body = body.bodyName, lat = lat, lon = lon, ut = Now + delay, W = W, falloutK = falloutK,
                heading = heading, length = length, width = width, life = life, seed = Random.Range(1, int.MaxValue),
            };
            records.Add(r);
            while (records.Count > MaxRecords) { Drop(records[0]); records.RemoveAt(0); }
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Fallout footprint on {0}: {1:F1} x {2:F1} km toward {3:F0} deg.",
                r.body, length / 1000f, width / 1000f, Mathf.Repeat(heading, 360f)));
        }

        /// <summary>Visual strength (0..1) at an age: settles out over the fallout's life, then fades over footprintDays.</summary>
        internal static float Visibility(NukeFootprintRecord r, double age)
        {
            if (age <= 0d) return 0f;
            float ramp = Mathf.Clamp01((float)((age - 0.15 * r.life) / (0.75 * r.life)));
            ramp = ramp * ramp * (3f - 2f * ramp);
            float fade = Mathf.Clamp01((float)(1d - age / System.Math.Max(1d, Lifespan)));
            return ramp * Mathf.Pow(fade, 1.5f);
        }

        /// <summary>
        /// Ground-shine dose rate (Sv/h) from every footprint at a vessel. Picks up where the airborne fallout field
        /// leaves off (cross-faded over its last quarter) and keeps the same t^-1.2 decay, on universal time.
        /// </summary>
        public static float DoseRate(Vessel v, float heightAboveGround)
        {
            var s = NukeSettings.Current;
            if (!s.falloutFootprint || v == null || v.mainBody == null || records.Count == 0) return 0f;
            var b = v.mainBody;
            double now = Now, span = Lifespan;
            float rate = 0f;
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.body != b.bodyName) continue;
                double age = now - r.ut;
                if (age <= 0.75 * r.life || age >= span) continue;
                r.Local(b, v.latitude, v.longitude, out double e, out double n);
                float shape = r.Shape(e, n);
                if (shape <= 1e-4f) continue;
                float x = Mathf.Clamp01((float)((age - 0.75 * r.life) / (0.25 * r.life)));
                float handover = x * x * (3f - 2f * x);
                float peak = s.falloutDoseRate * Mathf.Pow(Mathf.Max(r.W, 0.01f), 0.25f) * r.falloutK;
                float decay = Mathf.Pow((float)(System.Math.Max(age, RefTime) / RefTime), -1.2f);
                float tail = 1f - Mathf.Clamp01((float)((age - 0.8 * span) / (0.2 * span)));
                rate += peak * 0.6f * decay * shape * handover * tail;
            }
            return rate * s.footprintDose * Mathf.Exp(-Mathf.Max(0f, heightAboveGround) / 350f);
        }

        /// <summary>Once a second: drops expired footprints and builds or removes the terrain tints near the active vessel.</summary>
        public static void Tick()
        {
            double now = Now, span = Lifespan;
            for (int i = records.Count - 1; i >= 0; i--)
                if (now - records[i].ut >= span) { Drop(records[i]); records.RemoveAt(i); }
            var s = NukeSettings.Current;
            bool show = s.falloutFootprint && HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready;
            var body = FlightGlobals.currentMainBody;
            var av = FlightGlobals.ActiveVessel;
            foreach (var r in records)
            {
                bool want = false;
                if (show && body != null && av != null && r.body == body.bodyName && Visibility(r, now - r.ut) > 0.003f)
                {
                    r.Local(body, av.latitude, av.longitude, out double e, out double n);
                    double d = System.Math.Sqrt(e * e + n * n);
                    double reach = r.length + 50000d + System.Math.Max(0d, av.altitude) * 2d;
                    want = d < (r.visual != null ? reach * 1.3 : reach);
                }
                if (want && r.visual == null) r.visual = NukeFootprintDecal.Create(body, r);
                else if (!want && r.visual != null) Drop(r);
            }
        }

        static void Drop(NukeFootprintRecord r)
        {
            if (r.visual != null) Object.Destroy(r.visual.gameObject);
            r.visual = null;
        }

        public static void DropVisuals() { foreach (var r in records) Drop(r); }

        public static void Load(ConfigNode node)
        {
            DropVisuals();
            records.Clear();
            double now = Now, span = Lifespan;
            foreach (var cn in node.GetNodes("FOOTPRINT"))
            {
                var r = NukeFootprintRecord.Load(cn);
                if (r != null && now - r.ut < span) records.Add(r);
            }
        }

        public static void Save(ConfigNode node)
        {
            foreach (var r in records) node.AddNode(r.Save());
        }

        public static int Count => records.Count;
    }

    /// <summary>The footprint's terrain tint: a grid draped over PQS height samples, drawn with the scorch decal's material.</summary>
    internal class NukeFootprintDecal : MonoBehaviour
    {
        CelestialBody body;
        NukeFootprintRecord r;
        Material mat;
        MeshRenderer rend;
        Transform decal;

        public static NukeFootprintDecal Create(CelestialBody body, NukeFootprintRecord r)
        {
            var go = new GameObject("BDNukes_FalloutFootprint");
            var d = go.AddComponent<NukeFootprintDecal>();
            d.body = body;
            d.r = r;
            d.Build();
            return d;
        }

        void Anchor()
        {
            Vector3 pos = body.GetWorldSurfacePosition(r.lat, r.lon, 0d);
            transform.position = pos;
            Vector3 up = (pos - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane(((Vector3)(body.GetWorldSurfacePosition(90d, 0d, 0d) - body.position)).normalized, up);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, up);
            transform.rotation = Quaternion.LookRotation(north.normalized, up);
        }

        Vector3 GroundPoint(float x, float z, out bool water)
        {
            Vector3 w = transform.TransformPoint(new Vector3(x, 0f, z));
            double la = body.GetLatitude(w), lo = body.GetLongitude(w);
            // TerrainAltitude: PQS.GetSurfaceHeight under it, craters included
            double g = body.pqsController != null ? body.TerrainAltitude(la, lo) : 0d;
            water = body.ocean && g < 0d;
            if (water) g = 0d;
            return transform.InverseTransformPoint(body.GetWorldSurfacePosition(la, lo, g));
        }

        void Build()
        {
            Anchor();
            // vertex spacing ~250 m along and across (48 x 16 at the least), so the tint hugs the hills
            int nu = Mathf.Clamp(Mathf.CeilToInt(r.length * 1.12f / 250f), 48, 128);
            int nv = Mathf.Clamp(Mathf.CeilToInt(r.width / 250f), 16, 40);
            float h = r.heading * Mathf.Deg2Rad;
            Vector2 ax = new Vector2(Mathf.Sin(h), Mathf.Cos(h)), side = new Vector2(Mathf.Cos(h), -Mathf.Sin(h));
            float u0 = -0.12f * r.length, u1 = r.length, b = 0.5f * r.width;
            var verts = new Vector3[nu * nv];
            var uvs = new Vector2[nu * nv];
            var cols = new Color[nu * nv];
            var rng = new System.Random(r.seed);
            float ph = (float)rng.NextDouble() * 6.3f;
            float tiles = Mathf.Max(3f, r.length / 1500f);
            Color tint = new Color(0.42f, 0.37f, 0.30f);
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    float along = Mathf.Lerp(u0, u1, i / (float)(nu - 1));
                    float across = Mathf.Lerp(-b, b, j / (float)(nv - 1));
                    // a bit of waviness so the edge isn't a perfect ellipse
                    across *= 1f + 0.12f * Mathf.Sin(along / r.length * 9f + ph);
                    Vector2 p = ax * along + side * across;
                    int k = i * nv + j;
                    verts[k] = GroundPoint(p.x, p.y, out bool water);
                    uvs[k] = new Vector2(along / r.length * tiles, across / r.length * tiles);
                    float a = water || i == 0 || j == 0 || i == nu - 1 || j == nv - 1 ? 0f : r.Shape(p.x, p.y);
                    Color c = tint;
                    c.a = 0.45f * a;
                    cols[k] = c;
                }
            var tris = new int[(nu - 1) * (nv - 1) * 6];
            int t = 0;
            for (int i = 0; i < nu - 1; i++)
                for (int j = 0; j < nv - 1; j++)
                {
                    int a0 = i * nv + j, a1 = a0 + 1, b0 = a0 + nv, b1 = b0 + 1;
                    tris[t++] = a0; tris[t++] = a1; tris[t++] = b0;
                    tris[t++] = a1; tris[t++] = b1; tris[t++] = b0;
                }
            var mesh = new Mesh { name = "BDNukesFootprint" };
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors = cols;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            var go = new GameObject("Tint");
            go.transform.SetParent(transform, false);
            decal = go.transform;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            rend = go.AddComponent<MeshRenderer>();
            mat = NukeAssets.MakeDecalMaterial(false);
            if (mat.HasProperty("_InvFade")) mat.SetFloat("_InvFade", 5f);
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            rend.enabled = false;
        }

        void OnDestroy()
        {
            if (mat != null) Destroy(mat);
            if (decal != null)
            {
                var mf = decal.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            }
        }

        void LateUpdate()
        {
            if (body == null || FlightGlobals.currentMainBody != body || r == null) { rend.enabled = false; return; }
            float vis = NukeFootprint.Visibility(r, NukePersistence.Now - r.ut);
            bool on = vis > 0.003f;
            if (rend.enabled != on) rend.enabled = on;
            if (!on) return;
            Anchor();
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float camDist = cam != null ? (cam.transform.position - transform.position).magnitude : 1e5f;
            // lift it clear of the terrain's own LOD error, which grows with distance
            decal.localPosition = new Vector3(0f, Mathf.Clamp(2f + 0.004f * camDist, 2f, 600f), 0f);
            Vector3 sunW = Planetarium.fetch != null && Planetarium.fetch.Sun != null
                ? ((Vector3)(Planetarium.fetch.Sun.position) - transform.position).normalized
                : Vector3.up;
            float daylight = Mathf.Clamp01(Vector3.Dot(sunW, transform.up) * 3f + 0.3f);
            float light = 0.5f * (0.3f + 0.7f * daylight);
            mat.SetColor("_TintColor", new Color(light, light, light, 0.5f * vis));
        }
    }
}
