using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BDArmory.Weapons.Missiles;
using KSP.UI.Screens;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// The strike planner: a toolbar window in flight that lists the strike weapons on the active vessel (BDArmory
    /// GPS ballistic missiles, and BDNukes' orbital and interplanetary strike vehicles), lets you pick a target
    /// (cities and bases from Kerbal Konstructs, the stock launch sites, a click on the map or the ground, typed
    /// coordinates, or the point under a satellite), checks it can be reached, and launches. A launched weapon can
    /// be followed in by the camera; afterwards the window takes you back to the vessel it came from.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeStrikePlanner : MonoBehaviour
    {
        const string LockId = "BDNukesStrikePlanner";
        static NukeStrikePlanner instance;

        ApplicationLauncherButton button;
        Texture2D icon;
        bool show, locked, uiHidden;
        Rect rect = new Rect(Screen.width * 0.5f - 210f, 120f, 420f, 100f);
        readonly int windowId = "BDNukesStrikePlanner".GetHashCode();
        Vector2 listScroll;

        // weapons
        class Weapon
        {
            public MissileLauncher icbm;
            public IStrikeWeapon vehicle;
            public string Title => icbm != null ? icbm.part.partInfo.title : vehicle.StrikePart.partInfo.title;
            public Part Part => icbm != null ? icbm.part : vehicle.StrikePart;
        }
        readonly List<Weapon> weapons = new List<Weapon>();
        int weaponIndex;
        float nextScan;

        // targets
        CelestialBody body;
        StrikeTarget target;
        bool picking;
        string latText = "0", lonText = "0", heightText = "", filter = "";
        bool follow = true;
        float confirmUntil;
        string assessment = "";
        bool reachable;
        float nextAssess;

        struct Poi { public string name; public CelestialBody body; public double lat, lon; }
        readonly List<Poi> pois = new List<Poi>();
        CelestialBody poisFor;

        void Awake() { instance = this; }

        void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(RemoveButton);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
            if (ApplicationLauncher.Ready) AddButton();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherUnreadifying.Remove(RemoveButton);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            RemoveButton(GameScenes.FLIGHT);
            SetLock(false);
            if (icon != null) Destroy(icon);
            if (instance == this) instance = null;
        }

        void OnHideUI() { uiHidden = true; }
        void OnShowUI() { uiHidden = false; }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            if (icon == null) icon = NukeAssets.MakeTrefoilIcon(38, new Color(0.95f, 0.35f, 0.25f, 1f));
            button = ApplicationLauncher.Instance.AddModApplication(() => show = true, () => { show = false; picking = false; },
                null, null, null, null, ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW, icon);
        }

        void RemoveButton(GameScenes scene)
        {
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        void SetLock(bool on)
        {
            if (on == locked) return;
            locked = on;
            if (on) InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockId);
            else InputLockManager.RemoveControlLock(LockId);
        }

        // ------------------------------------------------------------------------------------------------
        // Weapons and the bodies each can reach
        // ------------------------------------------------------------------------------------------------

        void ScanWeapons()
        {
            var keep = weaponIndex < weapons.Count ? weapons[weaponIndex].Part : null;
            weapons.Clear();
            var v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            foreach (var p in v.parts)
            {
                var ml = p.FindModuleImplementing<MissileLauncher>();
                if (ml != null && NukeStrike.IsIcbm(ml) && !ml.HasFired && ml.MissileState == MissileBase.MissileStates.Idle)
                    weapons.Add(new Weapon { icbm = ml });
                foreach (PartModule module in p.Modules)
                    if (module is IStrikeWeapon sv) weapons.Add(new Weapon { vehicle = sv });
            }
            weaponIndex = Math.Max(0, weapons.FindIndex(w => w.Part == keep));
        }

        Weapon Current => weaponIndex < weapons.Count ? weapons[weaponIndex] : null;

        List<CelestialBody> Bodies(Weapon w)
        {
            var list = new List<CelestialBody>();
            var v = FlightGlobals.ActiveVessel;
            if (w == null || v == null) return list;
            if (w.icbm != null) { list.Add(v.mainBody); return list; }
            foreach (var b in FlightGlobals.Bodies)
                if (b != null && b.Radius > 0 && b != Planetarium.fetch.Sun &&
                    (w.vehicle is IStrikeBodyFilter filter ? filter.CanTargetBody(b) : b == v.mainBody || b.referenceBody == v.mainBody)) list.Add(b);
            return list;
        }

        // ------------------------------------------------------------------------------------------------
        // Points of interest
        // ------------------------------------------------------------------------------------------------

        const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static object Read(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj as Type ?? obj.GetType();
            var owner = obj is Type ? null : obj;
            return t.GetField(name, Members)?.GetValue(owner) ?? t.GetProperty(name, Members)?.GetValue(owner, null);
        }

        void AddStockSites(CelestialBody b)
        {
            var setup = PSystemSetup.Instance;
            if (setup == null) return;
            if (Read(setup, "StockLaunchSites") is System.Collections.IEnumerable sites)
                foreach (var site in sites)
                {
                    var siteBody = Read(site, "Body");
                    if (!ReferenceEquals(siteBody, b) && Convert.ToString(siteBody) != b.bodyName) continue;
                    if (!(Read(site, "spawnPoints") is System.Collections.IEnumerable points)) continue;
                    foreach (var point in points)
                        AddPoi(Convert.ToString(Read(site, "launchSiteName")), b, Read(point, "latitude"), Read(point, "longitude"));
                }
            if (Read(setup, "SpaceCenterFacilities") is System.Collections.IEnumerable facilities)
                foreach (var site in facilities)
                {
                    var siteBody = Read(site, "hostBody") ?? Read(site, "Body") ?? Read(site, "body");
                    if (!ReferenceEquals(siteBody, b) && Convert.ToString(siteBody) != b.bodyName) continue;
                    if (!(Read(site, "spawnPoints") is System.Collections.IEnumerable points)) continue;
                    foreach (var point in points)
                        AddPoi(Convert.ToString(Read(site, "facilityDisplayName") ?? Read(site, "name")), b,
                            Read(point, "latitude"), Read(point, "longitude"));
                }
        }

        /// <summary>
        /// The launch sites KSCSwitcher offers on the home world (Real Solar System and Sol put the real spaceports
        /// there: Cape Canaveral, Baikonur, Kourou and the rest), read from its config.
        /// </summary>
        void AddKscSwitcherSites(CelestialBody b)
        {
            if (b != FlightGlobals.GetHomeBody() || GameDatabase.Instance == null) return;
            foreach (var root in GameDatabase.Instance.GetConfigNodes("KSCSWITCHER"))
            {
                var sites = root.GetNode("LaunchSites");
                if (sites == null) continue;
                foreach (var site in sites.GetNodes("Site"))
                {
                    var city = site.GetNode("PQSCity");
                    if (city == null || !city.HasValue("latitude") || !city.HasValue("longitude")) continue;
                    string name = site.GetValue("displayName") ?? site.GetValue("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.StartsWith("#")) name = KSP.Localization.Localizer.Format(name.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim());
                    if (double.TryParse(city.GetValue("latitude"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
                        && double.TryParse(city.GetValue("longitude"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon))
                        AddPoi("Launch site: " + name, b, lat, lon);
                }
            }
        }

        void AddPoi(string name, CelestialBody b, object latitude, object longitude)
        {
            if (string.IsNullOrEmpty(name) || latitude == null || longitude == null) return;
            double lat = Convert.ToDouble(latitude), lon = Convert.ToDouble(longitude);
            if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon)) return;
            if (pois.Exists(p => p.name == name && p.body == b)) return;
            pois.Add(new Poi { name = name, body = b, lat = lat, lon = lon });
        }

        void BuildPois(CelestialBody b)
        {
            poisFor = b;
            pois.Clear();
            if (b == null) return;
            AddStockSites(b);
            try { AddKscSwitcherSites(b); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Strike planner couldn't read KSCSwitcher's launch sites: " + e.Message); }
            if (string.Equals(b.bodyName, "Earth", StringComparison.OrdinalIgnoreCase))
                foreach (var c in WorldCities.All) AddPoi("City: " + c.name, b, c.lat, c.lon);
            try { AddKerbalKonstructs(b); }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Strike planner couldn't read Kerbal Konstructs: " + e.Message); }
            // vessels on the ground there (bases, other people's silos)
            foreach (var v in FlightGlobals.Vessels)
                if (v != null && v.mainBody == b && (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED || v.situation == Vessel.Situations.PRELAUNCH)
                    && v.vesselType != VesselType.Debris && v.vesselType != VesselType.Flag && v.vesselType != VesselType.SpaceObject && v != FlightGlobals.ActiveVessel)
                    pois.Add(new Poi { name = "Vessel: " + v.vesselName, body = b, lat = v.latitude, lon = v.longitude });
            pois.Sort((x, y) => string.Compare(x.name, y.name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Kerbal Konstructs' static groups (Kerbin Side's towns, cities and bases), by reflection.</summary>
        void AddKerbalKonstructs(CelestialBody b)
        {
            Type db = null;
            foreach (var a in AssemblyLoader.loadedAssemblies)
                if (a.name == "KerbalKonstructs") { db = a.assembly.GetType("KerbalKonstructs.Core.StaticDatabase"); break; }
            if (db == null) return;
            if (Read(db, "allGroupCenters") is System.Collections.IEnumerable groups)
                foreach (var g in groups)
                {
                    if (!ReferenceEquals(Read(g, "CelestialBody"), b) || Read(g, "isHidden") is bool hidden && hidden) continue;
                    AddPoi(Pretty(Convert.ToString(Read(g, "Group"))), b, Read(g, "RefLatitude"), Read(g, "RefLongitude"));
                }
            var manager = db.Assembly.GetType("KerbalKonstructs.Core.LaunchSiteManager");
            if (Read(manager, "allLaunchSites") is System.Collections.IEnumerable sites)
                foreach (var site in sites)
                    if (ReferenceEquals(Read(site, "body"), b))
                        AddPoi(Convert.ToString(Read(site, "LaunchSiteName")), b, Read(site, "refLat"), Read(site, "refLon"));
        }

        static string Pretty(string group)
        {
            string s = (group ?? "").Replace('_', ' ').Trim();
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : group;
        }

        // ------------------------------------------------------------------------------------------------
        // Picking on the map or the ground
        // ------------------------------------------------------------------------------------------------

        void Update()
        {
            if (!show) { picking = false; return; }
            if (Time.time > nextScan) { nextScan = Time.time + 1f; ScanWeapons(); }
            if (target.Valid && Time.time > nextAssess) { nextAssess = Time.time + 0.5f; Assess(); }
            if (!picking || !Input.GetMouseButtonDown(0)) return;
            Vector2 gm = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (rect.Contains(gm)) return;
            if (Pick(Input.mousePosition, out var b, out double lat, out double lon))
            {
                picking = false;
                SetTarget(b, lat, lon, "Picked point");
            }
        }

        static bool Pick(Vector3 mouse, out CelestialBody body, out double lat, out double lon)
        {
            body = null; lat = lon = 0;
            if (MapView.MapIsEnabled)
            {
                var cam = PlanetariumCamera.Camera;
                if (cam == null) return false;
                Ray ray = cam.ScreenPointToRay(mouse);
                double best = double.MaxValue;
                foreach (var b in FlightGlobals.Bodies)
                {
                    if (b == null || b.scaledBody == null) continue;
                    Vector3d c = b.scaledBody.transform.position;
                    double r = b.Radius * ScaledSpace.InverseScaleFactor;
                    Vector3d o = ray.origin, d = ray.direction;
                    Vector3d oc = o - c;
                    double bb = Vector3d.Dot(oc, d), cc = oc.sqrMagnitude - r * r, disc = bb * bb - cc;
                    if (disc < 0) continue;
                    double t = -bb - Math.Sqrt(disc);
                    if (t < 0 || t >= best) continue;
                    best = t;
                    Vector3d world = ScaledSpace.ScaledToLocalSpace(o + d * t);
                    body = b;
                    lat = b.GetLatitude(world);
                    lon = b.GetLongitude(world);
                }
                return body != null;
            }
            var fc = FlightCamera.fetch;
            if (fc == null || fc.mainCamera == null) return false;
            if (!Physics.Raycast(fc.mainCamera.ScreenPointToRay(mouse), out RaycastHit hit, 1e6f, 1 << 15)) return false;
            body = FlightGlobals.currentMainBody;
            lat = body.GetLatitude(hit.point);
            lon = body.GetLongitude(hit.point);
            return true;
        }

        void SetTarget(CelestialBody b, double lat, double lon, string name)
        {
            if (b == null || double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon)) return;
            lat = Math.Max(-90, Math.Min(90, lat));
            lon = ((lon % 360) + 540) % 360 - 180;
            body = b;
            reachable = false;
            target = new StrikeTarget { body = b, lat = lat, lon = lon, name = name, height = Height() };
            latText = lat.ToString("0.0000", CultureInfo.InvariantCulture);
            lonText = lon.ToString("0.0000", CultureInfo.InvariantCulture);
            confirmUntil = 0f;
            nextAssess = 0f;
        }

        double Height()
        {
            return double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && !double.IsNaN(h) && !double.IsInfinity(h) ? Math.Max(0, h) : 0;
        }

        // ------------------------------------------------------------------------------------------------
        // Can it get there?
        // ------------------------------------------------------------------------------------------------

        void Assess()
        {
            var w = Current;
            var v = FlightGlobals.ActiveVessel;
            reachable = false;
            if (w == null || v == null || !target.Valid) { assessment = ""; return; }
            target.height = Height();
            if (w.icbm != null)
            {
                if (target.body != v.mainBody) { assessment = "Out of reach: an ICBM only strikes the body it's on."; return; }
                double ground = NukeStrikeMath.CentralAngle(v.latitude, v.longitude, target.lat, target.lon) * target.body.Radius;
                double max = NukeStrike.MaxRange(w.icbm, target.body);
                double min = w.icbm.minStaticLaunchRange;
                string maxS = double.IsInfinity(max) ? "anywhere on " + target.body.bodyName : NukeStrikeMath.Distance(max);
                if (ground < min) assessment = $"Too close: {NukeStrikeMath.Distance(ground)} (minimum {NukeStrikeMath.Distance(min)}).";
                else if (ground > max) assessment = $"Out of range: {NukeStrikeMath.Distance(ground)} away, range {maxS}.";
                else
                {
                    reachable = true;
                    assessment = $"In range: {NukeStrikeMath.Distance(ground)} away (range {maxS}).";
                    if (!follow && ground > 20000) assessment += " Without the follow camera it will be lost when it leaves physics range.";
                }
                return;
            }
            reachable = w.vehicle.CanReach(target, out assessment, out double flightTime);
            if (reachable) assessment += " Flight time: " + NukeStrikeMath.Duration(flightTime) + ".";
        }

        void Launch()
        {
            var w = Current;
            Assess();
            if (w == null || !target.Valid || !reachable) return;
            target.height = Height();
            if (w.icbm != null) NukeStrike.LaunchBda(w.icbm, target, follow);
            else
            {
                var launcher = w.Part.vessel;
                var plan = new NukeStrike.Plan { target = target, launcher = launcher, weapon = w.Title,
                    follow = follow, launchUT = Planetarium.GetUniversalTime() };
                var launched = w.vehicle.Launch(target, out assessment);
                if (launched != null)
                {
                    NukeStrike.LastPlan = plan;
                    if (follow && NukeStrike.Instance != null)
                        NukeStrike.Instance.StartCoroutine(NukeStrike.Instance.FollowWhenClear(w.Part, launcher, plan));
                }
            }
            confirmUntil = 0f;
            nextScan = 0f;
        }

        // ------------------------------------------------------------------------------------------------
        // The window
        // ------------------------------------------------------------------------------------------------

        void OnGUI()
        {
            if (!show || uiHidden)
            {
                SetLock(false);
                return;
            }
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(windowId, rect, Draw, "BDNukes strike planner", GUILayout.Width(420f));
            SetLock(rect.Contains(Event.current.mousePosition));
            DrawMarker();
        }

        void DrawMarker()
        {
            if (!target.Valid || Event.current.type != EventType.Repaint) return;
            Vector3d world = target.World;
            Vector3 screen;
            if (MapView.MapIsEnabled)
            {
                var cam = PlanetariumCamera.Camera;
                if (cam == null) return;
                screen = cam.WorldToScreenPoint(ScaledSpace.LocalToScaledSpace(world));
            }
            else
            {
                var fc = FlightCamera.fetch;
                if (fc == null || fc.mainCamera == null) return;
                screen = fc.mainCamera.WorldToScreenPoint(world);
            }
            if (screen.z <= 0) return;
            var old = GUI.color;
            GUI.color = new Color(1f, 0.3f, 0.2f, 1f);
            GUI.Label(new Rect(screen.x - 9, Screen.height - screen.y - 12, 300, 24), "⊕ " + target.name);
            GUI.color = old;
        }

        void Draw(int id)
        {
            var v = FlightGlobals.ActiveVessel;
            var plan = NukeStrike.LastPlan;
            if (plan != null && plan.launcher != null && v != plan.launcher)
            {
                GUILayout.Label($"Last strike: {plan.weapon} at {plan.target.name}");
                if (GUILayout.Button("Return to " + plan.launcher.vesselName)) NukeStrike.SwitchTo(plan.launcher);
                GUILayout.Space(6);
            }

            if (weapons.Count == 0)
            {
                GUILayout.Label("No strike weapons on this vessel. Fit a GPS-guided ICBM (Minuteman III, R-36M2, Trident II, ...) or a BDNukes strike vehicle.");
                GUI.DragWindow();
                return;
            }
            var w = Current;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(28))) { weaponIndex = (weaponIndex + weapons.Count - 1) % weapons.Count; nextAssess = 0; }
            GUILayout.Label($"{weaponIndex + 1}/{weapons.Count}  {w.Title}");
            if (GUILayout.Button(">", GUILayout.Width(28))) { weaponIndex = (weaponIndex + 1) % weapons.Count; nextAssess = 0; }
            GUILayout.EndHorizontal();
            w = Current;
            if (w.vehicle != null) GUILayout.Label(w.vehicle.StrikeDescription);
            if (w.vehicle is IStrikeWarheadCarrier carrier && carrier.ReleasedWarheads > 0)
            {
                GUILayout.Label("Released warheads:");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < carrier.ReleasedWarheads; i++)
                    if (GUILayout.Button((i + 1).ToString()))
                    {
                        var rv = carrier.FollowWarhead(i);
                        if (rv != null) NukeStrike.SwitchTo(rv);
                    }
                GUILayout.EndHorizontal();
            }

            // body
            var bodies = Bodies(w);
            if (bodies.Count == 0)
            {
                GUILayout.Label(w.vehicle != null ? "No suitable target bodies." : "Nothing in reach.");
                GUI.DragWindow();
                return;
            }
            if (body == null || !bodies.Contains(body)) { body = bodies[0]; target = default; }
            if (bodies.Count > 1)
            {
                GUILayout.Label("Target body:");
                int bi = bodies.IndexOf(body);
                var names = bodies.ConvertAll(b => b.bodyName).ToArray();
                int nb = GUILayout.SelectionGrid(bi, names, Math.Min(4, names.Length));
                if (nb != bi) { body = bodies[nb]; target = default; }
            }
            if (poisFor != body) BuildPois(body);
            // targets
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(52));
            filter = GUILayout.TextField(filter);
            GUILayout.EndHorizontal();
            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(150));
            int shown = 0;
            foreach (var p in pois)
            {
                if (filter.Length > 0 && p.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Button(p.name)) SetTarget(p.body, p.lat, p.lon, p.name);
                shown++;
            }
            if (shown == 0) GUILayout.Label(pois.Count == 0 ? $"No known places on {body.bodyName}: pick one on the map." : "No matches.");
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(picking, picking ? "Click the map or ground..." : "Pick on map / ground", HighLogic.Skin.button) != picking) picking = !picking;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Lat", GUILayout.Width(26));
            latText = GUILayout.TextField(latText, GUILayout.Width(80));
            GUILayout.Label("Lon", GUILayout.Width(28));
            lonText = GUILayout.TextField(lonText, GUILayout.Width(80));
            if (GUILayout.Button("Set") && double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out double la)
                && double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
                SetTarget(body, Math.Max(-90, Math.Min(90, la)), lo, "Coordinates");
            GUILayout.EndHorizontal();

            if (w.vehicle != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Burst height (m, blank = auto)", GUILayout.Width(200));
                heightText = GUILayout.TextField(heightText);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            if (target.Valid)
            {
                GUILayout.Label($"Target: {target.name}, {NukeStrikeMath.LatLon(target.lat, target.lon)} on {target.body.bodyName}");
                GUILayout.Label(assessment);
            }
            else GUILayout.Label("Pick a target.");

            follow = GUILayout.Toggle(follow, "Follow the missile with the camera");

            GUI.enabled = target.Valid && reachable;
            bool armed = Time.time < confirmUntil;
            var old = GUI.color;
            GUI.color = armed ? new Color(1f, 0.35f, 0.3f) : old;
            if (GUILayout.Button(armed ? "CONFIRM LAUNCH" : "Launch", GUILayout.Height(30)))
            {
                if (armed) Launch();
                else confirmUntil = Time.time + 4f;
            }
            GUI.color = old;
            GUI.enabled = true;
            GUI.DragWindow();
        }
    }
}
