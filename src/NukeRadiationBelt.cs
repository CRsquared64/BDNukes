using System;
using System.Collections.Generic;
using System.Globalization;
using BDArmory.Damage;
using BDArmory.Settings;
using BDArmory.Utils;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// One injection of fission electrons into a body's magnetic field: a torus around the rotation (= magnetic)
    /// axis, centred on the dipole shell L (equatorial radius of the field line, in body radii) through the burst.
    /// </summary>
    internal class NukeBeltShell
    {
        public string body;
        public double L;        // shell, in body radii
        public double spread;   // half-width in L (Gaussian sigma), body radii
        public double I0;       // Sv/h at the heart of the shell right after the burst
        public double ut0;      // universal time of the burst

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        static double Day => KSPUtil.dateTimeFormatter != null ? KSPUtil.dateTimeFormatter.Day : 21600d;

        /// <summary>Dose rate (Sv/h) at the heart of the shell: a quickly lost part plus a long half-life.</summary>
        public double Intensity(double ut)
        {
            if (ut < ut0) return 0d;
            var s = NukeSettings.Current;
            double t = ut - ut0, day = Day;
            double f = s.beltFastFraction;
            double fast = f * Math.Exp(-t / (s.beltFastDecayDays * day)) + (1d - f);
            return I0 * fast * Math.Pow(0.5d, t / (s.beltHalfLifeDays * day));
        }

        public ConfigNode Save()
        {
            var n = new ConfigNode("BELT");
            n.AddValue("body", body);
            n.AddValue("L", F(L)); n.AddValue("spread", F(spread));
            n.AddValue("I0", F(I0)); n.AddValue("ut0", F(ut0));
            return n;
        }

        public static NukeBeltShell Load(ConfigNode n)
        {
            var b = new NukeBeltShell { body = n.GetValue("body") };
            if (string.IsNullOrEmpty(b.body)) return null;
            n.TryGetValue("L", ref b.L); n.TryGetValue("spread", ref b.spread);
            n.TryGetValue("I0", ref b.I0); n.TryGetValue("ut0", ref b.ut0);
            if (b.L <= 1d || b.spread <= 0d || b.I0 <= 0d) return null;
            return b;
        }
    }

    /// <summary>
    /// High-altitude nuclear effects.
    ///
    /// Artificial radiation belt (Starfish Prime): a burst above the atmosphere of a magnetised body (beltBodies)
    /// traps its fission electrons on the field line through the burst. They drift round the body within minutes,
    /// so the belt is a torus around the magnetic (= rotation) axis: shell L = r / (R cos^2 lat) in body radii,
    /// filling the field lines r = L R cos^2(lat) of L +/- spread down to where they dip into the atmosphere.
    /// Its dose rate is proportional to yield and decays with a fast early loss and a long half-life; bursts stack.
    /// Every couple of seconds every vessel (loaded or not; unloaded ones by their orbit, sampled across the elapsed
    /// game time so time warp is covered) takes the dose: ModuleNukeBeltDose on its command parts and solar panels
    /// keeps the total, panels lose output and uncrewed probe cores eventually fail; crew get a shielded share fed
    /// into NukeRadiation.
    ///
    /// High-altitude EMP: a burst at 30-400 km over an atmosphere produces an E1 pulse over the ground circle within
    /// the horizon, radius sqrt((R+h)^2 - R^2). Loaded craft below 20 km inside it get BDArmory's soft EMP
    /// (ModuleDrainEC, as in the EMP fix), stronger towards the point under the burst.
    /// The artificial aurora of such bursts is drawn by NukeCinematic (SetupField), so it isn't repeated here.
    /// </summary>
    internal static class NukeRadiationBelt
    {
        internal const double MaxL = 10d;
        const float HempMinAlt = 30000f, HempMaxAlt = 400000f, HempCeiling = 20000f;
        const int MaxShells = 64;

        static readonly List<NukeBeltShell> shells = new List<NukeBeltShell>();
        internal static List<NukeBeltShell> Shells => shells;

        struct PendingEMP { public CelestialBody body; public double lat, lon, alt; public float W; public float fireTime; }
        static readonly List<PendingEMP> pendingEMP = new List<PendingEMP>();

        static double Now => Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0d;

        public static bool HasMagnetosphere(CelestialBody body)
        {
            if (body == null || body == Planetarium.fetch?.Sun) return false;
            var s = NukeSettings.Current;
            if (s.beltAllAtmospheres && body.atmosphere) return true;
            foreach (var name in s.beltBodies.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (NukeScale.NameMatches(name, body)) return true;
            return false;
        }

        /// <summary>Radius below which trapped electrons are lost: the top of the atmosphere (or just above the ground).</summary>
        internal static double CutoffRadius(CelestialBody body) =>
            body.Radius + (body.atmosphere ? body.atmosphereDepth : 0.01d * body.Radius);

        /// <summary>Called for every nuclear detonation in flight (BDArmory's NukeFX.CreateExplosion).</summary>
        public static void OnDetonation(CelestialBody body, Vector3 pos, float W, float delay)
        {
            try
            {
                if (body == null || W <= 0f) return;
                var s = NukeSettings.Current;
                double alt = body.GetAltitude(pos), lat = body.GetLatitude(pos), lon = body.GetLongitude(pos);

                if (s.hemp && body.atmosphere && alt >= HempMinAlt && alt <= HempMaxAlt)
                    pendingEMP.Add(new PendingEMP { body = body, lat = lat, lon = lon, alt = alt, W = W, fireTime = Time.time + Mathf.Max(0f, delay) });

                if (!s.radiationBelts || !HasMagnetosphere(body)) return;
                double minAlt = Math.Max(s.beltMinAltitude, body.atmosphere ? body.atmosphereDepth : 0d);
                if (alt < minAlt) return;
                double r = body.Radius + alt;
                double cl = Math.Cos(lat * Math.PI / 180d);
                double L = r / (body.Radius * Math.Max(1e-3d, cl * cl));
                if (L > MaxL || L * body.Radius > 0.5d * body.sphereOfInfluence) return; // open field lines: lost down the tail
                var shell = new NukeBeltShell
                {
                    body = body.bodyName,
                    L = L,
                    spread = 0.15d + 0.25d * (L - 1d),
                    I0 = s.beltDoseRate * W / 1000d,
                    ut0 = Now + Mathf.Max(0f, delay),
                };
                shells.Add(shell);
                if (shells.Count > MaxShells)
                {
                    // drop the weakest
                    double now = Now;
                    int weakest = 0;
                    for (int i = 1; i < shells.Count; i++)
                        if (shells[i].Intensity(now) < shells[weakest].Intensity(now)) weakest = i;
                    shells.RemoveAt(weakest);
                }
                double eqAlt = (L - 1d) * body.Radius / 1000d;
                ScreenMessages.PostScreenMessage(string.Format(CultureInfo.InvariantCulture,
                    "Fission electrons trapped in {0}'s magnetic field: artificial radiation belt at L = {1:0.00} ({2:0} km over the equator)",
                    body.bodyName, L, eqAlt), 8f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: {0} kt burst at {1:0} km injected a radiation belt around {2}: L = {3:0.00}, {4:0.00} Sv/h",
                    W, alt / 1000d, body.bodyName, L, shell.I0));
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Radiation belt / HEMP setup failed: " + e);
            }
        }

        public static bool HasShells(CelestialBody body)
        {
            if (body == null) return false;
            for (int i = 0; i < shells.Count; i++) if (shells[i].body == body.bodyName) return true;
            return false;
        }

        /// <summary>Belt dose rate (Sv/h, unshielded) at a world position near a body.</summary>
        public static double DoseRate(CelestialBody body, Vector3d worldPos, double ut)
        {
            if (body == null || shells.Count == 0) return 0d;
            Vector3d rel = worldPos - body.position;
            double r = rel.magnitude;
            double rCut = CutoffRadius(body);
            if (r <= rCut) return 0d;
            double latDeg = body.GetLatitude(worldPos);
            double cl = Math.Cos(latDeg * Math.PI / 180d);
            double Lp = r / (body.Radius * Math.Max(1e-3d, cl * cl));
            double fade = SmoothStep(rCut, rCut + 0.05d * body.Radius, r);
            // trapped electrons bounce between mirror points but spend most time near the equator
            double eq = 0.3d + 0.7d * cl * cl * cl;
            double rate = 0d;
            for (int i = 0; i < shells.Count; i++)
            {
                var sh = shells[i];
                if (sh.body != body.bodyName) continue;
                double x = (Lp - sh.L) / sh.spread;
                if (x * x > 18d) continue;
                rate += sh.Intensity(ut) * Math.Exp(-0.5d * x * x);
            }
            return rate * fade * eq;
        }

        static double SmoothStep(double a, double b, double x)
        {
            double t = Math.Max(0d, Math.Min(1d, (x - a) / (b - a)));
            return t * t * (3d - 2d * t);
        }

        /// <summary>Drops shells that have decayed away.</summary>
        internal static void Prune(double ut) => shells.RemoveAll(sh => sh.ut0 < ut && sh.Intensity(ut) < 1e-5d);

        public static void Load(ConfigNode node)
        {
            shells.Clear();
            pendingEMP.Clear();
            foreach (var n in node.GetNodes("BELT"))
            {
                var b = NukeBeltShell.Load(n);
                if (b != null) shells.Add(b);
            }
        }

        public static void Save(ConfigNode node)
        {
            Prune(Now);
            foreach (var b in shells) node.AddNode(b.Save());
        }

        // ---------------- high-altitude EMP ----------------

        internal static void TickEMP()
        {
            if (pendingEMP.Count == 0) return;
            if (!HighLogic.LoadedSceneIsFlight) { pendingEMP.Clear(); return; }
            for (int i = pendingEMP.Count - 1; i >= 0; i--)
            {
                if (Time.time < pendingEMP[i].fireTime) continue;
                var p = pendingEMP[i];
                pendingEMP.RemoveAt(i);
                try { ApplyHEMP(p); }
                catch (Exception e) { Debug.LogError("[BDNukes]: High-altitude EMP failed: " + e); }
            }
        }

        static void ApplyHEMP(PendingEMP p)
        {
            var body = p.body;
            if (body == null) return;
            double R = body.Radius;
            double footprint = Math.Sqrt((R + p.alt) * (R + p.alt) - R * R);
            Vector3d under = body.GetSurfaceNVector(p.lat, p.lon);
            float yieldK = Mathf.Clamp(Mathf.Pow(p.W / 100f, 0.2f), 0.5f, 1.5f); // E1 saturates: weak yield dependence
            int hit = 0;
            var vessels = FlightGlobals.VesselsLoaded;
            for (int i = 0; i < vessels.Count; i++)
            {
                var v = vessels[i];
                if (v == null || v.packed || v.HoldPhysics || v.mainBody != body || v.rootPart == null) continue;
                if (v.altitude > HempCeiling) continue;
                if (VesselModuleRegistry.IgnoredVesselTypes.Contains(v.vesselType)) continue;
                Vector3d n = body.GetSurfaceNVector(v.latitude, v.longitude);
                double ground = R * Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(under, n))));
                if (ground > footprint) continue;
                float k = Mathf.Lerp(3f, 1f, (float)(ground / footprint)) * yieldK;
                float damage = 100f * BDArmorySettings.DMG_MULTIPLIER * k;
                var drain = v.rootPart.FindModuleImplementing<ModuleDrainEC>();
                if (drain == null) drain = (ModuleDrainEC)v.rootPart.AddModule("ModuleDrainEC");
                if (drain == null) continue;
                if (drain.incomingDamage <= 0f) drain.softEMP = true;   // never downgrade a pending hard EMP
                drain.incomingDamage = Mathf.Max(drain.incomingDamage, damage);
                hit++;
                if (BDArmorySettings.DEBUG_DAMAGE)
                    Debug.Log($"[BDNukes]: High-altitude EMP hits {v.vesselName} {ground / 1000d:0} km from ground zero: {damage:0.0}");
            }
            if (hit > 0 && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mainBody == body)
                ScreenMessages.PostScreenMessage("High-altitude EMP: electronics disrupted across " + (footprint / 1000d).ToString("0", CultureInfo.InvariantCulture) + " km", 5f, ScreenMessageStyle.UPPER_CENTER);
        }

        // ---------------- vessel effects ----------------

        static double lastUT = -1d;
        static readonly HashSet<Guid> warned = new HashSet<Guid>();
        static readonly HashSet<Guid> warnedOnce = new HashSet<Guid>();
        static readonly List<ProtoCrewMember> crewCopy = new List<ProtoCrewMember>();

        internal static void ResetClock() => lastUT = -1d;

        internal static void TickVessels()
        {
            double now = Now;
            if (lastUT < 0d || now < lastUT) { lastUT = now; return; }
            double dt = now - lastUT;
            lastUT = now;
            Prune(now);
            var all = FlightGlobals.Vessels;
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                var v = all[i];
                if (v == null || v.state == Vessel.State.DEAD) continue;
                try
                {
                    if (v.loaded) TickLoaded(v, now, dt);
                    else if (dt > 0d && shells.Count > 0) TickUnloaded(v, now, dt);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[BDNukes]: Radiation belt tick failed for " + v.vesselName + ": " + e.Message);
                }
            }
        }

        static bool Skip(Vessel v) =>
            v.vesselType == VesselType.Debris || v.vesselType == VesselType.SpaceObject || v.vesselType == VesselType.Flag
            || v.vesselType == VesselType.Unknown;

        /// <summary>Dose (Sv, unshielded) over the last dt seconds of game time, sampled along the orbit, and the rate now.</summary>
        static double Exposure(Vessel v, double now, double dt, out double rateNow)
        {
            rateNow = 0d;
            if (v.LandedOrSplashed || v.situation == Vessel.Situations.PRELAUNCH || v.orbit == null) return 0d;
            var body = v.orbit.referenceBody;
            if (!HasShells(body)) return 0d;
            rateNow = DoseRate(body, v.loaded ? v.CoMD : v.orbit.getPositionAtUT(now), now);
            if (dt <= 0d) return 0d;
            int n = (int)Math.Min(48d, Math.Max(1d, Math.Ceiling(dt / 120d)));
            if (n == 1) return rateNow * dt / 3600d;
            double sum = 0d, step = dt / n;
            for (int k = 0; k < n; k++)
            {
                double t = now - dt + (k + UnityEngine.Random.value) * step;   // jittered so high warp cannot lock onto one orbital phase
                sum += DoseRate(body, v.orbit.getPositionAtUT(t), t);
            }
            return sum * step / 3600d;
        }

        static void TickLoaded(Vessel v, double now, double dt)
        {
            var s = NukeSettings.Current;
            double rate = 0d, dose = 0d;
            if (!Skip(v)) dose = Exposure(v, now, dt, out rate);
            bool active = v == FlightGlobals.ActiveVessel;
            Warn(v, rate, active);
            float shield = v.isEVA ? 1f : s.beltCrewShielding;
            for (int pi = 0; pi < v.parts.Count; pi++)
            {
                var part = v.parts[pi];
                var m = part.Modules.GetModule<ModuleNukeBeltDose>();
                if (m != null) m.Tick((float)dose, (float)rate);
                var crew = part.protoModuleCrew;
                if (crew == null || crew.Count == 0) continue;
                crewCopy.Clear();
                crewCopy.AddRange(crew);
                // also checks doses that went lethal while the vessel was unloaded
                for (int ci = 0; ci < crewCopy.Count; ci++)
                    NukeRadiation.AddDose(part, crewCopy[ci], (float)dose * shield, active);
            }
            crewCopy.Clear();
        }

        static void TickUnloaded(Vessel v, double now, double dt)
        {
            if (Skip(v) || v.protoVessel == null) return;
            double dose = Exposure(v, now, dt, out double rate);
            Warn(v, rate, false);
            if (dose <= 0d) return;
            var s = NukeSettings.Current;
            foreach (var pps in v.protoVessel.protoPartSnapshots)
            {
                for (int mi = 0; mi < pps.modules.Count; mi++)
                {
                    var pm = pps.modules[mi];
                    if (pm.moduleName != nameof(ModuleNukeBeltDose) || pm.moduleValues == null) continue;
                    var node = pm.moduleValues;
                    float d = 0f;
                    bool failed = false;
                    node.TryGetValue("beltDose", ref d);
                    node.TryGetValue("radiationFailed", ref failed);
                    d += (float)dose;
                    node.SetValue("beltDose", d.ToString("R", CultureInfo.InvariantCulture), true);
                    if (!failed && s.beltDamage && IsProbe(pps) && d >= ModuleNukeBeltDose.FailThreshold(pps.flightID))
                    {
                        node.SetValue("radiationFailed", "True", true);
                        ScreenMessages.PostScreenMessage($"{v.vesselName}: {pps.partInfo?.title ?? "probe core"} has failed from radiation damage", 6f, ScreenMessageStyle.UPPER_LEFT);
                        Debug.Log($"[BDNukes]: {v.vesselName}: probe core failed from radiation ({d:0.0} Sv)");
                    }
                }
                float shield = v.isEVA ? 1f : s.beltCrewShielding;
                for (int ci = 0; ci < pps.protoModuleCrew.Count; ci++)
                    NukeRadiation.AddDose(null, pps.protoModuleCrew[ci], (float)dose * shield, false);
            }
        }

        static bool IsProbe(ProtoPartSnapshot pps)
        {
            var cmd = pps.partInfo?.partPrefab != null ? pps.partInfo.partPrefab.FindModuleImplementing<ModuleCommand>() : null;
            return cmd != null && cmd.minimumCrew == 0;
        }

        static void Warn(Vessel v, double rate, bool active)
        {
            if (rate < 0.01d) { warned.Remove(v.id); return; }
            double threshold = active ? 0.05d : 0.5d;
            if (rate < threshold || warned.Contains(v.id)) return;
            warned.Add(v.id);
            if (!active && !warnedOnce.Add(v.id)) return; // other craft: once a session
            ScreenMessages.PostScreenMessage($"{v.vesselName} has entered an artificial radiation belt: {NukeRadiation.FormatRate((float)rate)}",
                6f, active ? ScreenMessageStyle.UPPER_CENTER : ScreenMessageStyle.UPPER_LEFT);
        }

        /// <summary>
        /// A vessel whose every command part has failed from radiation and has no crew is dead: BDArmory's hard EMP
        /// (ModuleDrainEC, the same mechanism as the EMP fix) shuts it down for good.
        /// </summary>
        internal static void BrickIfDead(Vessel v)
        {
            if (v == null || !v.loaded || v.rootPart == null || v.GetCrewCount() > 0) return;
            bool anyCommand = false;
            for (int i = 0; i < v.parts.Count; i++)
            {
                var p = v.parts[i];
                if (p.FindModuleImplementing<ModuleCommand>() == null) continue;
                anyCommand = true;
                var m = p.Modules.GetModule<ModuleNukeBeltDose>();
                if (m == null || !m.radiationFailed) return;
            }
            if (!anyCommand || VesselModuleRegistry.IgnoredVesselTypes.Contains(v.vesselType)) return;
            var drain = v.rootPart.FindModuleImplementing<ModuleDrainEC>();
            if (drain == null) drain = (ModuleDrainEC)v.rootPart.AddModule("ModuleDrainEC");
            if (drain == null) return;
            drain.softEMP = false;
            drain.incomingDamage = Mathf.Max(drain.incomingDamage, 1e9f);
        }
    }

    /// <summary>
    /// Keeps the radiation belts in the save, runs the belt dose and high-altitude EMP, and draws the belts in map view.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class NukeBeltScenario : ScenarioModule
    {
        const float Tick = 2f;
        float nextTick;
        Material lineMat;

        public override void OnLoad(ConfigNode node)
        {
            NukeRadiationBelt.Load(node);
            NukeRadiationBelt.ResetClock();
        }

        public override void OnSave(ConfigNode node) => NukeRadiationBelt.Save(node);

        void Start()
        {
            NukeRadiationBelt.ResetClock();
            Camera.onPostRender += DrawBelts;
        }

        void OnDestroy()
        {
            Camera.onPostRender -= DrawBelts;
            if (lineMat != null) Destroy(lineMat);
        }

        void Update()
        {
            NukeRadiationBelt.TickEMP();
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + Tick;
            if (HighLogic.LoadedSceneIsFlight && !FlightGlobals.ready) return;
            NukeRadiationBelt.TickVessels();
        }

        // ---------------- map view ----------------

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

        static Color BeltColor(double intensity, float alpha)
        {
            // blue-green when faint, through yellow, to red-magenta when deadly
            float t = Mathf.InverseLerp(-2.5f, 1f, (float)Math.Log10(Math.Max(1e-6d, intensity)));
            Color c = t < 0.5f ? Color.Lerp(new Color(0.2f, 0.8f, 1f), new Color(1f, 0.9f, 0.2f), t * 2f)
                               : Color.Lerp(new Color(1f, 0.9f, 0.2f), new Color(1f, 0.15f, 0.45f), (t - 0.5f) * 2f);
            c.a = alpha * Mathf.Lerp(0.35f, 1f, t);
            return c;
        }

        static float drawWarnAt;

        void DrawBelts(Camera cam)
        {
            try
            {
                if (!NukeSettings.Current.showRadiationBelts || NukeRadiationBelt.Shells.Count == 0) return;
                if (cam == null || cam != PlanetariumCamera.Camera) return;
                if (HighLogic.LoadedScene == GameScenes.FLIGHT) { if (!MapView.MapIsEnabled) return; }
                else if (HighLogic.LoadedScene != GameScenes.TRACKSTATION) return;
                var mat = LineMaterial();
                if (mat == null || FlightGlobals.Bodies == null) return;
                double now = Planetarium.GetUniversalTime();

                mat.SetPass(0);
                GL.PushMatrix();
                GL.LoadProjectionMatrix(cam.projectionMatrix);
                GL.modelview = cam.worldToCameraMatrix;
                GL.Begin(GL.LINES);
                foreach (var sh in NukeRadiationBelt.Shells)
                {
                    double I = sh.Intensity(now);
                    if (I <= 0d) continue;
                    var body = FlightGlobals.GetBodyByName(sh.body);
                    if (body == null) continue;
                    DrawShell(body, sh, I);
                }
                GL.End();
                GL.PopMatrix();
            }
            catch (Exception e)
            {
                try { GL.End(); } catch { } try { GL.PopMatrix(); } catch { }
                if (Time.unscaledTime > drawWarnAt) { drawWarnAt = Time.unscaledTime + 30f; Debug.LogWarning("[BDNukes]: Couldn't draw the radiation belts: " + e.Message); }
            }
        }

        static void DrawShell(CelestialBody body, NukeBeltShell sh, double I)
        {
            Vector3d c = body.position;
            Vector3d axis = (body.GetWorldSurfacePosition(90d, 0d, 0d) - c).normalized;
            Vector3d e0 = (body.GetWorldSurfacePosition(0d, 0d, 0d) - c).normalized;
            Vector3d e1 = Vector3d.Cross(axis, e0).normalized;
            double R = body.Radius, rCut = NukeRadiationBelt.CutoffRadius(body);

            for (int band = -1; band <= 1; band++)
            {
                double L = sh.L + band * sh.spread;
                if (L * R <= rCut * 1.001d) continue;
                float alpha = band == 0 ? 0.35f : 0.14f;
                GL.Color(BeltColor(I * (band == 0 ? 1d : 0.6d), alpha));
                // the field lines the belt fills, down to where they meet the atmosphere
                double latMax = Math.Acos(Math.Sqrt(rCut / (L * R)));
                int lines = band == 0 ? 24 : 12;
                const int segs = 32;
                for (int li = 0; li < lines; li++)
                {
                    double lon = (li + (band == 0 ? 0d : 0.5d)) * 2d * Math.PI / lines;
                    Vector3d dir = Math.Cos(lon) * e0 + Math.Sin(lon) * e1;
                    Vector3 prev = Point(c, dir, axis, L * R, -latMax);
                    for (int k = 1; k <= segs; k++)
                    {
                        Vector3 p = Point(c, dir, axis, L * R, -latMax + 2d * latMax * k / segs);
                        GL.Vertex(prev); GL.Vertex(p);
                        prev = p;
                    }
                }
                // the equatorial ring
                const int ring = 96;
                Vector3 rp = Point(c, e0, axis, L * R, 0d);
                for (int k = 1; k <= ring; k++)
                {
                    double lon = 2d * Math.PI * k / ring;
                    Vector3 p = Point(c, Math.Cos(lon) * e0 + Math.Sin(lon) * e1, axis, L * R, 0d);
                    GL.Vertex(rp); GL.Vertex(p);
                    rp = p;
                }
            }
        }

        /// <summary>Scaled-space point on the dipole field line r = L R cos^2(lat) at a given longitude direction.</summary>
        static Vector3 Point(Vector3d centre, Vector3d dir, Vector3d axis, double LR, double lat)
        {
            double cl = Math.Cos(lat);
            double r = LR * cl * cl;
            Vector3d world = centre + r * (cl * dir + Math.Sin(lat) * axis);
            return ScaledSpace.LocalToScaledSpace(world);
        }
    }

    /// <summary>
    /// Added by ModuleManager to command parts and solar panels. Keeps the part's accumulated radiation-belt dose
    /// (saved with the vessel, and updated in the save for unloaded vessels), degrades solar panel output with it,
    /// and makes an uncrewed probe core fail for good past its (per-part) failure dose.
    /// </summary>
    public class ModuleNukeBeltDose : PartModule
    {
        [KSPField(isPersistant = true)] public float beltDose;
        [KSPField(isPersistant = true)] public bool radiationFailed;
        [KSPField(isPersistant = true)] public float panelBaseEfficiency = -1f;

        [KSPField(guiActive = true, guiName = "Belt radiation dose")] public string doseReadout = "0 mSv";
        [KSPField(guiActive = false, guiName = "Belt dose rate")] public string rateReadout = "-";
        [KSPField(guiActive = false, guiName = "Panel output (radiation)")] public string panelReadout = "100%";
        [KSPField(guiActive = false, guiName = "Electronics")] public string statusReadout = "OK";

        ModuleCommand cmd;
        ModuleDeployableSolarPanel panel;
        bool failureApplied;

        bool IsProbe => cmd != null && cmd.minimumCrew == 0;

        internal static float FailThreshold(uint flightID)
        {
            uint h = flightID * 2654435761u;
            float u = ((h >> 8) & 0xFFFFFF) / 16777216f;
            return NukeSettings.Current.beltFailDose * (0.7f + 0.6f * u);
        }

        static float PanelFactor(float dose)
        {
            var s = NukeSettings.Current;
            if (!s.beltDamage) return 1f;
            return Mathf.Max(s.beltPanelMinEfficiency, Mathf.Exp(-dose / s.beltPanelDose));
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight) return;
            cmd = part.FindModuleImplementing<ModuleCommand>();
            panel = part.FindModuleImplementing<ModuleDeployableSolarPanel>();
            Fields[nameof(doseReadout)].guiActive = cmd != null || panel != null;
            Fields[nameof(rateReadout)].guiActive = IsProbe;
            Fields[nameof(statusReadout)].guiActive = IsProbe;
            Fields[nameof(panelReadout)].guiActive = panel != null;
            if (panel != null && panelBaseEfficiency < 0f) panelBaseEfficiency = panel.efficiencyMult;
            Refresh(0f);
        }

        /// <summary>Called every couple of seconds by the belt tick for loaded vessels.</summary>
        internal void Tick(float dose, float rate)
        {
            if (dose > 0f) beltDose += dose;
            Refresh(rate);
        }

        void Refresh(float rate)
        {
            var s = NukeSettings.Current;
            doseReadout = NukeRadiation.FormatDose(beltDose);
            rateReadout = rate > 1e-5f ? NukeRadiation.FormatRate(rate) : "-";
            if (panel != null && panelBaseEfficiency >= 0f)
            {
                float f = PanelFactor(beltDose);
                panel.efficiencyMult = panelBaseEfficiency * f;
                panelReadout = (f * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            }
            if (!IsProbe) return;
            if (!radiationFailed && s.beltDamage && beltDose >= FailThreshold(part.flightID))
            {
                radiationFailed = true;
                ScreenMessages.PostScreenMessage($"{vessel?.vesselName}: {part.partInfo?.title ?? part.name} has failed from radiation damage", 6f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log($"[BDNukes]: {vessel?.vesselName}: probe core failed from radiation ({beltDose:0.0} Sv)");
            }
            statusReadout = radiationFailed ? (s.beltDamage ? "FAILED (radiation)" : "failed (damage off)")
                : beltDose > 0.5f * FailThreshold(part.flightID) && s.beltDamage ? "degraded" : "OK";
            if (radiationFailed && s.beltDamage && !failureApplied && vessel != null && !vessel.packed) ApplyFailure();
        }

        void ApplyFailure()
        {
            failureApplied = true;
            try
            {
                cmd.isEnabled = false;
                cmd.enabled = false;
                part.isControlSource = Vessel.ControlLevel.NONE;
                NukeRadiationBelt.BrickIfDead(vessel);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Couldn't disable a radiation-failed probe core: " + e.Message);
            }
        }

        public override string GetInfo() => "";
    }
}
