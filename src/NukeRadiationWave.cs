using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>One expanding radiation front from a burst in space.</summary>
    internal class NukeWaveFront
    {
        public string body;          // body whose SOI the burst was in (the origin is fixed where that body was at ut0)
        public Vector3d offset;      // burst position relative to that body at ut0 (world axes; small next to the front)
        public double ut0;           // universal time of the burst
        public float W;              // kt
        public double speed;         // m/s
        public double pulse;         // s the front takes to pass a point
        public double maxRadius;     // m; dropped once it gets this far
        public readonly HashSet<Guid> announced = new HashSet<Guid>();

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public double Radius(double ut) => Math.Max(0d, ut - ut0) * speed;

        /// <summary>The burst point now, in the current world frame (bodies' positions at ut0 via their orbits).</summary>
        public Vector3d Origin()
        {
            var b = FlightGlobals.GetBodyByName(body);
            if (b == null) return Vector3d.zero;
            return b.getTruePositionAtUT(ut0) + offset;
        }

        public ConfigNode Save()
        {
            var n = new ConfigNode("WAVE");
            n.AddValue("body", body);
            n.AddValue("offset", ConfigNode.WriteVector(offset));
            n.AddValue("ut0", F(ut0));
            n.AddValue("W", F(W));
            n.AddValue("speed", F(speed));
            n.AddValue("pulse", F(pulse));
            n.AddValue("maxRadius", F(maxRadius));
            return n;
        }

        public static NukeWaveFront Load(ConfigNode n)
        {
            var w = new NukeWaveFront { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(w.body)) return null;
            n.TryGetValue("offset", ref w.offset);
            n.TryGetValue("ut0", ref w.ut0);
            n.TryGetValue("W", ref w.W);
            n.TryGetValue("speed", ref w.speed);
            n.TryGetValue("pulse", ref w.pulse);
            n.TryGetValue("maxRadius", ref w.maxRadius);
            if (w.speed <= 0d || w.W <= 0f) return null;
            return w;
        }
    }

    /// <summary>
    /// Radiation fronts from bursts in space. In vacuum nothing absorbs a burst's prompt radiation and debris, so it
    /// spreads out as a sphere across the whole system. Each front expands at waveSpeed (slower than light, so you can
    /// watch it go: it's drawn in map view and the tracking station) and gives a short pulse of dose to every craft it
    /// passes, falling off as 1/r^2 from the burst. A planet between the burst and a craft shades it, and an atmosphere
    /// shields craft inside it. Time warp drops to 1x just before a front reaches the active vessel, so you hear the
    /// dosimeter pick it up. Fronts are saved with the game.
    /// </summary>
    internal static class NukeRadiationWave
    {
        const double Gm = 1e9;
        const int MaxFronts = 16;

        static readonly List<NukeWaveFront> fronts = new List<NukeWaveFront>();
        internal static IList<NukeWaveFront> Fronts => fronts;

        /// <summary>Dose rate (Sv/h) at the active vessel from passing fronts, for the dosimeter.</summary>
        public static float ActiveRate { get; private set; }

        static double lastUT = -1d;
        static Guid lastActive;

        static double Now => Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0d;

        internal static void ResetClock() => lastUT = -1d;

        public static void OnDetonation(CelestialBody body, Vector3 pos, float W, float delay)
        {
            try
            {
                var s = NukeSettings.Current;
                if (!s.radiationWaves || body == null || W < s.waveMinYield) return;
                double alt = body.GetAltitude(pos);
                if (body.atmosphere && alt < body.atmosphereDepth) return;   // the air takes it up
                var sun = FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0 ? FlightGlobals.Bodies[0] : null;
                double reach = 150d * Gm;
                if (sun != null && sun.sphereOfInfluence > 0d && !double.IsInfinity(sun.sphereOfInfluence))
                    reach = Math.Min(reach, sun.sphereOfInfluence);
                var w = new NukeWaveFront
                {
                    body = body.bodyName,
                    offset = (Vector3d)pos - body.position,
                    ut0 = Now + Mathf.Max(0f, delay),
                    W = W,
                    speed = Math.Max(1e3d, s.waveSpeed * 1000d),
                    pulse = Math.Max(5d, s.wavePulse),
                    maxRadius = reach,
                };
                fronts.Add(w);
                while (fronts.Count > MaxFronts) fronts.RemoveAt(0);
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[BDNukes]: {0} kt burst in space near {1}: radiation front expanding at {2:0} km/s", W, body.bodyName, w.speed / 1000d));
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Radiation front setup failed: " + e);
            }
        }

        /// <summary>Peak dose rate (Sv/h) of a front at distance r from its burst.</summary>
        static double PeakRate(NukeWaveFront w, double r)
        {
            double rg = Math.Max(r, NukeSettings.Current.waveMinDistance * 1000d) / Gm;
            return NukeSettings.Current.waveDoseRate * (w.W / 1000d) / (rg * rg);
        }

        /// <summary>
        /// Shielding at a point: 0 when the body it's near sits between it and the burst, reduced inside an atmosphere
        /// (to waveSurfaceShielding on the ground).
        /// </summary>
        static double Shielding(CelestialBody body, Vector3d p, Vector3d origin)
        {
            if (body == null) return 1d;
            Vector3d c = body.getTruePositionAtUT(Now);
            // line of sight from p to the burst against the body's sphere
            Vector3d d = origin - p;
            double len = d.magnitude;
            if (len > 1d)
            {
                d /= len;
                double t = Vector3d.Dot(c - p, d);
                if (t > 0d && t < len && (p + d * t - c).magnitude < body.Radius) return 0d;
            }
            if (!body.atmosphere) return 1d;
            double alt = (p - c).magnitude - body.Radius;
            if (alt >= body.atmosphereDepth) return 1d;
            double f = Math.Max(0d, alt) / body.atmosphereDepth;
            return NukeSettings.Current.waveSurfaceShielding + (1d - NukeSettings.Current.waveSurfaceShielding) * f * f;
        }

        /// <summary>Position of a vessel in the frame the fronts use (its body's orbit position plus its offset).</summary>
        static Vector3d VesselPos(Vessel v, double ut)
        {
            var body = v.mainBody;
            if (v.loaded || v.orbit == null || v.LandedOrSplashed)
                return body.getTruePositionAtUT(ut) + (v.GetWorldPos3D() - body.position);
            return v.orbit.getTruePositionAtUT(ut);
        }

        /// <summary>Fraction of the front's pulse that passes over [t0, t1] for a point at distance r (a smooth hump).</summary>
        static double PulseFraction(NukeWaveFront w, double r, double t0, double t1)
        {
            double a = w.ut0 + r / w.speed;   // the front arrives
            double lo = Math.Max(t0, a), hi = Math.Min(t1, a + w.pulse);
            if (hi <= lo) return 0d;
            // integral of sin^2(pi x) over [x0, x1], normalised to 1 over the whole pulse
            double x0 = (lo - a) / w.pulse, x1 = (hi - a) / w.pulse;
            double I(double x) => x - Math.Sin(2d * Math.PI * x) / (2d * Math.PI);
            return I(x1) - I(x0);
        }

        static double RateNow(NukeWaveFront w, double r, double now)
        {
            double x = (now - (w.ut0 + r / w.speed)) / w.pulse;
            if (x <= 0d || x >= 1d) return 0d;
            double sn = Math.Sin(Math.PI * x);
            return 2d * PeakRate(w, r) * sn * sn;   // mean over the pulse = PeakRate
        }

        /// <summary>Called a few times a second (any scene with the scenario): doses, the dosimeter and time warp.</summary>
        internal static void Tick()
        {
            double now = Now;
            if (lastUT < 0d || now < lastUT) { lastUT = now; ActiveRate = 0f; return; }
            double dt = now - lastUT;
            double t0 = lastUT;
            lastUT = now;
            fronts.RemoveAll(w => w.Radius(now) > w.maxRadius + w.speed * w.pulse);
            ActiveRate = 0f;
            if (fronts.Count == 0 || !HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready) return;

            var s = NukeSettings.Current;
            var active = FlightGlobals.ActiveVessel;
            if (active != null && active.id != lastActive) { lastActive = active.id; }

            var vessels = FlightGlobals.VesselsLoaded;
            foreach (var w in fronts)
            {
                if (now < w.ut0) continue;
                Vector3d origin = w.Origin();
                for (int i = 0; i < vessels.Count; i++)
                {
                    var v = vessels[i];
                    if (v == null || v.state == Vessel.State.DEAD || v.mainBody == null) continue;
                    Vector3d p = VesselPos(v, now);
                    double r = (p - origin).magnitude;
                    double shield = Shielding(v.mainBody, p, origin);
                    bool isActive = v == active;

                    if (isActive)
                    {
                        ActiveRate += (float)(RateNow(w, r, now) * shield);
                        Approach(w, v, r, now, shield);
                    }
                    if (dt <= 0d || shield <= 0d) continue;
                    double frac = PulseFraction(w, r, t0, now);
                    if (frac <= 0d) continue;
                    double dose = PeakRate(w, r) * w.pulse / 3600d * frac * shield;
                    if (dose <= 0d) continue;
                    for (int pi = 0; pi < v.parts.Count; pi++)
                    {
                        var part = v.parts[pi];
                        var crew = part.protoModuleCrew;
                        if (crew == null) continue;
                        for (int ci = crew.Count - 1; ci >= 0; ci--)
                            NukeRadiation.AddDose(part, crew[ci], (float)dose, isActive);
                    }
                }
            }
        }

        /// <summary>Warns, and drops out of time warp, as a front is about to reach the active vessel.</summary>
        static void Approach(NukeWaveFront w, Vessel v, double r, double now, double shield)
        {
            double arrive = w.ut0 + r / w.speed;
            double left = arrive - now;
            if (left < -w.pulse || w.announced.Contains(v.id)) return;
            double peak = PeakRate(w, r) * shield;
            if (peak < 0.002d) { w.announced.Add(v.id); return; }   // too faint to matter
            float warp = TimeWarp.fetch != null ? TimeWarp.CurrentRate : 1f;
            // look ahead by a few real seconds of the current warp rate
            if (left > Math.Max(30d, 6d * warp)) return;
            w.announced.Add(v.id);
            if (warp > 1f && NukeSettings.Current.waveStopWarp) TimeWarp.SetRate(0, true);
            string near = w.body;
            ScreenMessages.PostScreenMessage(string.Format(CultureInfo.InvariantCulture,
                "Radiation front from the {0} burst near {1} arriving: {2} at peak{3}",
                FormatYield(w.W), near, NukeRadiation.FormatRate((float)(2d * peak)), shield < 1d ? " (shielded)" : ""),
                8f, ScreenMessageStyle.UPPER_CENTER);
        }

        static string FormatYield(float W) => W >= 1000f ? (W / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + " Mt"
                                                         : W.ToString("0.#", CultureInfo.InvariantCulture) + " kt";

        public static void Load(ConfigNode node)
        {
            fronts.Clear();
            foreach (var n in node.GetNodes("WAVE"))
            {
                var w = NukeWaveFront.Load(n);
                if (w != null) fronts.Add(w);
            }
            lastUT = -1d;
        }

        public static void Save(ConfigNode node)
        {
            double now = Now;
            foreach (var w in fronts)
                if (w.Radius(now) <= w.maxRadius + w.speed * w.pulse) node.AddNode(w.Save());
        }
    }

    /// <summary>Saves the radiation fronts, runs their tick and draws them in map view and the tracking station.</summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class NukeWaveScenario : ScenarioModule
    {
        float nextTick;
        Material lineMat;

        public override void OnLoad(ConfigNode node) => NukeRadiationWave.Load(node);
        public override void OnSave(ConfigNode node) => NukeRadiationWave.Save(node);

        void Start()
        {
            NukeRadiationWave.ResetClock();
            Camera.onPostRender += Draw;
        }

        void OnDestroy()
        {
            Camera.onPostRender -= Draw;
            if (lineMat != null) Destroy(lineMat);
        }

        void Update()
        {
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.25f;
            try { NukeRadiationWave.Tick(); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Radiation front tick failed: " + e.Message); }
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
            if (lineMat.HasProperty("_ZTest")) lineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            return lineMat;
        }

        void Draw(Camera cam)
        {
            try
            {
                var s = NukeSettings.Current;
                if (!s.showRadiationWaves || NukeRadiationWave.Fronts.Count == 0) return;
                if (cam == null || cam != PlanetariumCamera.Camera) return;
                if (HighLogic.LoadedScene == GameScenes.FLIGHT) { if (!MapView.MapIsEnabled) return; }
                else if (HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
                var mat = LineMaterial();
                if (mat == null) return;
                double now = Planetarium.GetUniversalTime();

                // the plane of the planets' orbits (the home world's orbit), plus two upright circles to show the sphere
                var home = FlightGlobals.GetHomeBody();
                Vector3d n = home != null && home.orbit != null ? home.orbit.GetOrbitNormal().xzy.normalized : Vector3d.up;
                Vector3d e1 = Vector3d.Cross(n, Math.Abs(n.y) < 0.9d ? Vector3d.up : Vector3d.right).normalized;
                Vector3d e2 = Vector3d.Cross(n, e1).normalized;

                mat.SetPass(0);
                GL.PushMatrix();
                GL.LoadProjectionMatrix(cam.projectionMatrix);
                GL.modelview = cam.worldToCameraMatrix;
                GL.Begin(GL.LINES);
                foreach (var w in NukeRadiationWave.Fronts)
                {
                    double R = w.Radius(now);
                    if (R <= 0d) continue;
                    Vector3d o = w.Origin();
                    // fade as it spreads (1/r, gentler than the dose so a faint front is still visible)
                    float a = Mathf.Clamp01((float)(0.9d / Math.Max(1d, R / 5e9d)));
                    a *= Mathf.Clamp01((float)((w.maxRadius + w.speed * w.pulse - R) / (0.1d * w.maxRadius)));
                    var hot = new Color(1f, 0.85f, 0.25f, a);
                    var cool = new Color(0.3f, 1f, 0.5f, a * 0.45f);
                    Circle(o, e1, e2, R, hot, 180);                       // in the plane of the planets
                    Circle(o, e1, n, R, cool, 120);                       // two upright great circles
                    Circle(o, e2, n, R, cool, 120);
                    // the trailing edge of the pulse, faintly
                    double Rt = R - w.speed * w.pulse;
                    if (Rt > 0d) Circle(o, e1, e2, Rt, new Color(1f, 0.5f, 0.15f, a * 0.35f), 180);
                }
                GL.End();
                GL.PopMatrix();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Couldn't draw the radiation fronts: " + e.Message);
                NukeSettings.Current.showRadiationWaves = false;
            }
        }

        static void Circle(Vector3d o, Vector3d a, Vector3d b, double R, Color c, int seg)
        {
            GL.Color(c);
            Vector3 prev = ScaledSpace.LocalToScaledSpace(o + a * R);
            for (int i = 1; i <= seg; i++)
            {
                double t = 2d * Math.PI * i / seg;
                Vector3 p = ScaledSpace.LocalToScaledSpace(o + (a * Math.Cos(t) + b * Math.Sin(t)) * R);
                GL.Vertex(prev); GL.Vertex(p);
                prev = p;
            }
        }
    }
}
