using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>One detected detonation: where, when and how big, kept for the alert window and the map markers.</summary>
    internal class NukeBurstRecord
    {
        public string body;
        public double lat, lon, alt;   // alt above sea level
        public Vector3d offset;        // position relative to the body at the time (world axes), for bursts in space
        public double ut;
        public float W;
        public bool space;             // above the atmosphere (or no atmosphere)
        public double agl = -1d;       // height above the ground at the burst (-1: unknown, older saves)

        /// <summary>On or near the ground (including airless worlds), as opposed to up in the sky or out in space.</summary>
        public bool NearGround => agl >= 0d ? agl < 5d * 70d * Math.Pow(Math.Max(W, 0.01f), 0.4d) : !space || alt < 50d;

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("BURST");
            n.AddValue("body", body);
            n.AddValue("lat", F(lat)); n.AddValue("lon", F(lon)); n.AddValue("alt", F(alt));
            n.AddValue("offset", ConfigNode.WriteVector(offset));
            n.AddValue("ut", F(ut)); n.AddValue("W", F(W)); n.AddValue("space", space); n.AddValue("agl", F(agl));
            return n;
        }

        public static NukeBurstRecord Load(ConfigNode n)
        {
            var r = new NukeBurstRecord { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(r.body)) return null;
            n.TryGetValue("lat", ref r.lat); n.TryGetValue("lon", ref r.lon); n.TryGetValue("alt", ref r.alt);
            n.TryGetValue("offset", ref r.offset);
            n.TryGetValue("ut", ref r.ut); n.TryGetValue("W", ref r.W); n.TryGetValue("space", ref r.space);
            if (!n.TryGetValue("agl", ref r.agl)) r.agl = -1d;
            return r;
        }

        /// <summary>World position of the burst point now: on the surface frame for bursts in the air, fixed in space otherwise.</summary>
        public Vector3d WorldPos(CelestialBody b)
        {
            if (space) return b.getTruePositionAtUT(ut) + offset - b.getTruePositionAtUT(Planetarium.GetUniversalTime()) + b.position;
            return b.GetWorldSurfacePosition(lat, lon, Math.Max(alt, 0d) + 0.002d * b.Radius);
        }
    }

    /// <summary>
    /// Nuclear detonation alert. Every burst anywhere in the system raises an alarm and a warning window: yield, where it
    /// was, how far from you, and when its radiation front reaches you and each planet. Map view and the tracking station
    /// then show every radiation overlay: burst sites, fallout footprints, radiation belts and expanding fronts.
    /// </summary>
    internal static class NukeAlert
    {
        const int MaxRecords = 32;
        static readonly List<NukeBurstRecord> records = new List<NukeBurstRecord>();
        internal static IList<NukeBurstRecord> Records => records;

        /// <summary>The detonation the alert window is showing, or null.</summary>
        internal static NukeBurstRecord Current;
        internal static int PendingAlarms;
        internal static float AlertStarted;

        static double Now => Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0d;
        static double DaySeconds => KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : 21600d;

        public static void OnDetonation(CelestialBody body, Vector3 pos, float W, float delay)
        {
            try
            {
                var s = NukeSettings.Current;
                // every burst is recorded (and saved with the game): the long-range view and the map markers use the
                // records too, so they're kept whether or not the alarm is on
                if (body == null || W <= 0f) return;
                double alt = body.GetAltitude(pos);
                double ut = Now + Mathf.Max(0f, delay);
                // MIRV warheads and salvos: one alert per salvo (same body, within 30 s), keeping the biggest yield
                for (int i = records.Count - 1; i >= 0; i--)
                {
                    var o = records[i];
                    if (o.body == body.bodyName && Math.Abs(o.ut - ut) < 30d && (o.WorldPos(body) - (Vector3d)pos).magnitude < 50000d)
                    {
                        if (W > o.W) o.W = W;
                        return;   // a salvo is one burst, for the alarm and the long-range view alike
                    }
                }
                var r = new NukeBurstRecord
                {
                    body = body.bodyName,
                    lat = body.GetLatitude(pos), lon = body.GetLongitude(pos), alt = alt,
                    offset = (Vector3d)pos - body.position,
                    ut = ut, W = W,
                    space = !body.atmosphere || alt >= body.atmosphereDepth,
                };
                try { r.agl = Math.Max(0d, alt - Math.Max(body.ocean ? 0d : double.MinValue, body.TerrainAltitude(r.lat, r.lon, true))); }
                catch (Exception) { r.agl = -1d; }
                records.Add(r);
                while (records.Count > MaxRecords) records.RemoveAt(0);
                if (!s.blastAlert) return;
                Current = r;
                AlertStarted = Time.unscaledTime;
                // every radiation overlay shows on the map from now on
                s.showRadiationOverlays = s.showRadiationBelts = s.showRadiationWaves = true;
                if (s.blastAlertSound) PendingAlarms = 3;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Detonation alert failed: " + e.Message);
            }
        }

        /// <summary>How long a burst site stays marked on the map.</summary>
        internal static double MarkerLife(NukeBurstRecord r) => (r.space ? 1d : 3d) * Math.Max(0.1d, NukeSettings.Current.blastMarkerDays) * DaySeconds;

        internal static void Prune()
        {
            double now = Now;
            // keep a burst at least until its light (travelling with the radiation front) could have crossed the
            // whole system, so the long-range view can still show it arriving
            double crossing = 3e11d * NukeScale.SystemSize / NukeScale.WaveSpeed + 600d;
            records.RemoveAll(r => now - r.ut > Math.Max(MarkerLife(r), crossing));
            if (Current != null && !records.Contains(Current)) Current = null;
        }

        public static void Load(ConfigNode node)
        {
            records.Clear();
            Current = null;
            foreach (var n in node.GetNodes("BURST"))
            {
                var r = NukeBurstRecord.Load(n);
                if (r != null) records.Add(r);
            }
        }

        public static void Save(ConfigNode node)
        {
            Prune();
            foreach (var r in records) node.AddNode(r.Save());
        }

        internal static string Yield(float W) => W >= 1000f ? (W / 1000f).ToString("0.##", CultureInfo.InvariantCulture) + " Mt"
                                                            : W.ToString(W < 1f ? "0.###" : "0.#", CultureInfo.InvariantCulture) + " kt";

        internal static string Distance(double m)
        {
            if (m >= 1e9d) return (m / 1e9d).ToString("0.##", CultureInfo.InvariantCulture) + " Gm";
            if (m >= 1e6d) return (m / 1e6d).ToString("0.##", CultureInfo.InvariantCulture) + " Mm";
            if (m >= 1e4d) return (m / 1e3d).ToString("0", CultureInfo.InvariantCulture) + " km";
            return (m / 1e3d).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        }

        internal static string Where(NukeBurstRecord r)
        {
            string ll = string.Format(CultureInfo.InvariantCulture, "{0:0.00}°{1} {2:0.00}°{3}",
                Math.Abs(r.lat), r.lat >= 0 ? "N" : "S", Math.Abs(r.lon), r.lon >= 0 ? "E" : "W");
            var b = FlightGlobals.GetBodyByName(r.body);
            if (b != null && b == FlightGlobals.Bodies[0] && r.space) return "in solar orbit, " + Distance(r.offset.magnitude) + " from " + r.body;
            if (r.space) return Distance(Math.Max(0d, r.alt)) + " above " + r.body + " (" + ll + ")";
            if (r.alt < 50d) return "on the surface of " + r.body + " at " + ll;
            return Distance(r.alt) + " above " + r.body + " at " + ll;
        }
    }

    /// <summary>Alarm, alert window and the map view radiation overlays.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class NukeAlertScenario : ScenarioModule
    {
        const int WindowId = 0x4E554B41; // "NUKA"
        Rect window = new Rect(0, 60, 440, 10);
        bool placed;
        AudioSource audio;
        AudioClip alarm;
        float nextAlarm;
        Material lineMat;
        Texture2D ring, dot;
        GUIStyle title, body, small;
        float nextEta;
        readonly List<string> etaLines = new List<string>();

        public override void OnLoad(ConfigNode node) => NukeAlert.Load(node);
        public override void OnSave(ConfigNode node) => NukeAlert.Save(node);

        void Start()
        {
            Camera.onPostRender += DrawMap;
            var go = new GameObject("BDNukes_Alarm");
            go.transform.SetParent(transform, false);
            audio = go.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f;
            if (GameDatabase.Instance != null) alarm = GameDatabase.Instance.GetAudioClip("BDArmory/Sounds/warning");
        }

        void OnDestroy()
        {
            Camera.onPostRender -= DrawMap;
            if (lineMat != null) Destroy(lineMat);
            if (ring != null) Destroy(ring);
            if (dot != null) Destroy(dot);
        }

        void Update()
        {
            if (NukeAlert.PendingAlarms > 0 && Time.unscaledTime >= nextAlarm && alarm != null && audio != null)
            {
                NukeAlert.PendingAlarms--;
                nextAlarm = Time.unscaledTime + Mathf.Max(0.6f, alarm.length + 0.15f);
                audio.PlayOneShot(alarm, 0.8f * NukeSettings.Current.volume * GameSettings.UI_VOLUME);
            }
            else if (alarm == null) NukeAlert.PendingAlarms = 0;
        }

        // ---------------- alert window ----------------

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(HighLogic.Skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            title.normal.textColor = new Color(1f, 0.35f, 0.25f);
            body = new GUIStyle(HighLogic.Skin.label) { fontSize = 13, wordWrap = true };
            body.normal.textColor = new Color(1f, 0.92f, 0.8f);
            small = new GUIStyle(HighLogic.Skin.label) { fontSize = 12, wordWrap = true };
            small.normal.textColor = new Color(0.85f, 0.95f, 0.85f);
        }

        void OnGUI()
        {
            DrawMarkers();
            var r = NukeAlert.Current;
            if (r == null || !NukeSettings.Current.blastAlert) return;
            Styles();
            if (!placed) { window.x = (Screen.width - window.width) * 0.5f; placed = true; }
            GUI.skin = HighLogic.Skin;
            // flash the frame red for the first few seconds
            float since = Time.unscaledTime - NukeAlert.AlertStarted;
            var old = GUI.color;
            if (since < 6f && Mathf.Repeat(since, 0.6f) < 0.3f) GUI.color = new Color(1f, 0.45f, 0.4f);
            window = GUILayout.Window(WindowId, window, WindowContents, "", GUILayout.Width(440));
            GUI.color = old;
        }

        void WindowContents(int id)
        {
            var r = NukeAlert.Current;
            if (r == null) return;
            double now = Planetarium.GetUniversalTime();
            GUILayout.Label("NUCLEAR DETONATION DETECTED", title);
            GUILayout.Label(NukeAlert.Yield(r.W) + " " + NukeAlert.Where(r), body);
            GUILayout.Label("Detonation " + KSPUtil.PrintTime(Math.Max(0d, now - r.ut), 3, false) + " ago", small);

            if (Time.unscaledTime >= nextEta) { nextEta = Time.unscaledTime + 0.5f; Eta(r, now); }
            for (int i = 0; i < etaLines.Count; i++) GUILayout.Label(etaLines[i], small);

            GUILayout.BeginHorizontal();
            if (HighLogic.LoadedSceneIsFlight && GUILayout.Button(MapView.MapIsEnabled ? "Close map" : "Show on map"))
            {
                if (MapView.MapIsEnabled) MapView.ExitMapView();
                else { MapView.EnterMapView(); Focus(r); }
            }
            else if (HighLogic.LoadedScene == GameScenes.TRACKSTATION && GUILayout.Button("Focus")) Focus(r);
            if (NukeAlert.Records.Count > 1 && GUILayout.Button("Previous"))
            {
                int i = NukeAlert.Records.IndexOf(r);
                NukeAlert.Current = NukeAlert.Records[(i - 1 + NukeAlert.Records.Count) % NukeAlert.Records.Count];
                nextEta = 0f;
            }
            if (GUILayout.Button("Dismiss")) NukeAlert.Current = null;
            GUILayout.EndHorizontal();
            GUI.DragWindow();
        }

        static void Focus(NukeBurstRecord r)
        {
            var b = FlightGlobals.GetBodyByName(r.body);
            if (b == null || PlanetariumCamera.fetch == null) return;
            var mo = b.MapObject;
            if (mo != null) PlanetariumCamera.fetch.SetTarget(mo);
        }

        /// <summary>Distance to the burst and the radiation front's arrival at you and at each planet.</summary>
        void Eta(NukeBurstRecord r, double now)
        {
            etaLines.Clear();
            var b = FlightGlobals.GetBodyByName(r.body);
            if (b == null) return;
            Vector3d gz = b.getTruePositionAtUT(r.ut) + r.offset;   // where it went off, fixed in space

            var v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v != null && v.mainBody != null)
            {
                Vector3d p = v.mainBody.getTruePositionAtUT(now) + (v.GetWorldPos3D() - v.mainBody.position);
                double d = r.space ? (p - gz).magnitude : (v.GetWorldPos3D() - b.GetWorldSurfacePosition(r.lat, r.lon, r.alt)).magnitude;
                etaLines.Add("Distance from " + v.vesselName + ": " + NukeAlert.Distance(d));
            }

            NukeWaveFront front = null;
            foreach (var w in NukeRadiationWave.Fronts)
                if (w.body == r.body && Math.Abs(w.ut0 - r.ut) < 1d) { front = w; break; }
            if (front == null)
            {
                if (!r.space) etaLines.Add("Atmospheric burst: watch for fallout downwind of ground zero.");
                if (NukeRadiationBelt.HasShells(b) && r.space) etaLines.Add("Trapped radiation: a belt is forming around " + r.body + ".");
                return;
            }
            Vector3d o = front.Origin();
            double R = front.Radius(now);
            if (v != null && v.mainBody != null)
            {
                Vector3d p = v.mainBody.getTruePositionAtUT(now) + (v.GetWorldPos3D() - v.mainBody.position);
                etaLines.Add("Radiation front at you: " + When((p - o).magnitude, R, front));
            }
            // the planets, nearest first
            var list = new List<KeyValuePair<double, CelestialBody>>();
            foreach (var cb in FlightGlobals.Bodies)
            {
                if (cb == null || cb == FlightGlobals.Bodies[0]) continue;
                if (cb.referenceBody != FlightGlobals.Bodies[0] && cb.bodyName != r.body) continue; // planets, plus the burst's own body
                list.Add(new KeyValuePair<double, CelestialBody>((cb.getTruePositionAtUT(now) - o).magnitude, cb));
            }
            list.Sort((a, c) => a.Key.CompareTo(c.Key));
            for (int i = 0; i < list.Count && i < 6; i++)
                etaLines.Add("   " + list[i].Value.bodyName + ": " + When(list[i].Key, R, front));
            if (NukeRadiationBelt.HasShells(b)) etaLines.Add("Trapped radiation: a belt is forming around " + r.body + ".");
        }

        static string When(double dist, double R, NukeWaveFront w)
        {
            if (dist <= R - w.speed * w.pulse) return "passed";
            if (dist <= R) return "PASSING NOW";
            return "in " + KSPUtil.PrintTime((dist - R) / w.speed, 3, false) + " (" + NukeAlert.Distance(dist) + ")";
        }

        // ---------------- map overlays ----------------

        static bool MapShowing(Camera cam)
        {
            if (cam == null || cam != PlanetariumCamera.Camera) return false;
            if (HighLogic.LoadedScene == GameScenes.FLIGHT) return MapView.MapIsEnabled;
            return HighLogic.LoadedScene == GameScenes.TRACKSTATION;
        }

        Material LineMaterial()
        {
            if (lineMat != null) return lineMat;
            var sh = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
            if (sh == null) return null;
            lineMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            if (lineMat.HasProperty("_SrcBlend")) lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (lineMat.HasProperty("_DstBlend")) lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (lineMat.HasProperty("_Cull")) lineMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            if (lineMat.HasProperty("_ZWrite")) lineMat.SetInt("_ZWrite", 0);
            if (lineMat.HasProperty("_ZTest")) lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            return lineMat;
        }

        /// <summary>Fallout footprints as outlines on the ground, in map view and the tracking station.</summary>
        static float drawWarnAt;

        void DrawMap(Camera cam)
        {
            try
            {
                if (!NukeSettings.Current.showRadiationOverlays || !MapShowing(cam)) return;
                var fps = NukeFootprint.Records;
                if (fps.Count == 0) return;
                var mat = LineMaterial();
                if (mat == null) return;
                double now = Planetarium.GetUniversalTime();
                mat.SetPass(0);
                GL.PushMatrix();
                GL.LoadProjectionMatrix(cam.projectionMatrix);
                GL.modelview = cam.worldToCameraMatrix;
                GL.Begin(GL.LINES);
                for (int i = 0; i < fps.Count; i++)
                {
                    var f = fps[i];
                    var b = FlightGlobals.GetBodyByName(f.body);
                    if (b == null) continue;
                    // shown from the moment it starts settling, fading as it decays
                    float vis = Mathf.Max(NukeFootprint.Visibility(f, now - f.ut), now > f.ut ? 0.25f : 0f);
                    vis *= Mathf.Clamp01((float)(1d - (now - f.ut) / Math.Max(1d, NukeFootprint.Lifespan)));
                    if (vis <= 0.01f) continue;
                    Ellipse(b, f, 1f, new Color(1f, 0.75f, 0.2f, 0.9f * vis));
                    Ellipse(b, f, 0.55f, new Color(1f, 0.35f, 0.15f, 0.9f * vis));   // the hot core
                }
                GL.End();
                GL.PopMatrix();
            }
            catch (Exception e)
            {
                try { GL.End(); } catch { } try { GL.PopMatrix(); } catch { }
                if (Time.unscaledTime > drawWarnAt) { drawWarnAt = Time.unscaledTime + 30f; Debug.LogWarning("[BDNukes]: Couldn't draw the fallout overlay: " + e.Message); }
            }
        }

        static void Ellipse(CelestialBody b, NukeFootprintRecord f, float scale, Color c)
        {
            GL.Color(c);
            float h = f.heading * Mathf.Deg2Rad;
            double sh = Math.Sin(h), ch = Math.Cos(h);
            double cAlong = 0.44d * f.length, a = 0.56d * f.length * scale, bb = 0.5d * f.width * scale;
            double lift = 0.003d * b.Radius;   // keeps it off the scaled planet's surface
            const int seg = 64;
            Vector3 prev = Vector3.zero;
            for (int i = 0; i <= seg; i++)
            {
                double t = 2d * Math.PI * i / seg;
                double along = cAlong + a * Math.Cos(t), across = bb * Math.Sin(t);
                double east = along * sh + across * ch, north = along * ch - across * sh;
                double la = f.lat + north / b.Radius * Mathf.Rad2Deg;
                double lo = f.lon + east / (b.Radius * Math.Max(0.01d, Math.Cos(f.lat * Mathf.Deg2Rad))) * Mathf.Rad2Deg;
                Vector3 p = ScaledSpace.LocalToScaledSpace(b.GetWorldSurfacePosition(la, lo, lift));
                if (i > 0) { GL.Vertex(prev); GL.Vertex(p); }
                prev = p;
            }
        }

        Texture2D MakeRing(int size, float inner, float outer)
        {
            var t = new Texture2D(size, size, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float a = Mathf.Clamp01((d - inner) * 8f) * Mathf.Clamp01((outer - d) * 8f);
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            t.Apply();
            return t;
        }

        /// <summary>Burst sites as pulsing markers with their yield, on the map.</summary>
        void DrawMarkers()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!NukeSettings.Current.showRadiationOverlays || NukeAlert.Records.Count == 0) return;
            var cam = PlanetariumCamera.Camera;
            if (cam == null || !MapShowing(cam)) return;
            if (ring == null) ring = MakeRing(64, 0.72f, 0.98f);
            if (dot == null) dot = MakeRing(32, -1f, 0.6f);
            Styles();
            double now = Planetarium.GetUniversalTime();
            NukeAlert.Prune();
            foreach (var r in NukeAlert.Records)
            {
                var b = FlightGlobals.GetBodyByName(r.body);
                if (b == null) continue;
                Vector3d wp = r.WorldPos(b);
                Vector3 sp = ScaledSpace.LocalToScaledSpace(wp);
                // hidden behind its planet? (the camera-to-marker segment passes through the scaled planet)
                Vector3 c = ScaledSpace.LocalToScaledSpace(b.position);
                float rs = (float)(b.Radius / ScaledSpace.ScaleFactor) * 0.995f;
                Vector3 camPos = cam.transform.position, seg = sp - camPos;
                float len = seg.magnitude;
                if (len > 1e-4f)
                {
                    Vector3 dir = seg / len;
                    float t = Vector3.Dot(c - camPos, dir);
                    if (t > 0f && t < len && (camPos + dir * t - c).magnitude < rs)
                    {
                        // the near intersection is before the marker: occluded
                        float half = Mathf.Sqrt(Mathf.Max(0f, rs * rs - (camPos + dir * t - c).sqrMagnitude));
                        if (t - half < len - 1e-3f * rs) continue;
                    }
                }
                Vector3 s = cam.WorldToScreenPoint(sp);
                if (s.z <= 0f) continue;
                float age = (float)(now - r.ut);
                float life = (float)NukeAlert.MarkerLife(r);
                float fade = Mathf.Clamp01(1f - age / Mathf.Max(1f, life));
                bool fresh = age < 600f;
                float pulse = fresh ? Mathf.Repeat(Time.unscaledTime, 1.2f) / 1.2f : 0f;
                float size = Mathf.Lerp(14f, 26f, Mathf.InverseLerp(0f, 4f, Mathf.Log10(Mathf.Max(1f, r.W * 10f))));
                Vector2 gp = new Vector2(s.x, Screen.height - s.y);
                var old = GUI.color;
                var col = r.space ? new Color(0.6f, 0.9f, 1f) : new Color(1f, 0.55f, 0.2f);
                GUI.color = new Color(col.r, col.g, col.b, 0.9f * Mathf.Max(fade, 0.3f));
                GUI.DrawTexture(new Rect(gp.x - size * 0.5f, gp.y - size * 0.5f, size, size), ring);
                GUI.DrawTexture(new Rect(gp.x - 3f, gp.y - 3f, 6f, 6f), dot);
                if (fresh)
                {
                    float ps = size * (1f + 2.5f * pulse);
                    GUI.color = new Color(col.r, col.g, col.b, 0.8f * (1f - pulse));
                    GUI.DrawTexture(new Rect(gp.x - ps * 0.5f, gp.y - ps * 0.5f, ps, ps), ring);
                }
                GUI.color = old;
                small.normal.textColor = col;
                GUI.Label(new Rect(gp.x + size * 0.5f + 2f, gp.y - 9f, 220f, 20f),
                    "NUC " + NukeAlert.Yield(r.W) + (age < 86400f ? "  T+" + KSPUtil.PrintTime(age, 2, false) : ""), small);
                small.normal.textColor = new Color(0.85f, 0.95f, 0.85f);
            }
        }
    }
}
