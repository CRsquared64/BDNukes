using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BDArmory.Control;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Effects ranges for a yield, NUKEMAP style. Blast ranges are the ground ranges for a burst at the optimum
    /// height for that overpressure, which scale with the cube root of the yield (Glasstone &amp; Dolan, "The Effects
    /// of Nuclear Weapons", 1977, ch. III, fig. 3.73 and the cube-root scaling law, 3.60-3.64). The 1 kt constants are
    /// fitted to the airburst table that the Wikipedia article "Effects of nuclear explosions" draws from Glasstone
    /// (1 kt / 20 kt / 1 Mt / 20 Mt: 20 psi 0.2 / 0.6 / 2.4 / 6.4 km, 5 psi 0.6 / 1.7 / 6.2 / 17 km, 1 psi 1.7 / 4.7 /
    /// 17 / 47 km, third-degree burns 0.6 / 2.5 / 12 / 38 km), which are also what NUKEMAP's defaults give:
    ///   20 psi (heavy damage, concrete buildings destroyed)  ~ 0.23 km * W^(1/3)
    ///    5 psi (most buildings collapse)                     ~ 0.62 km * W^(1/3)
    ///    1 psi (windows break, light injuries)               ~ 1.72 km * W^(1/3)
    ///   third-degree burns (thermal, clear air)              ~ 0.60 km * W^0.42 (thermal falls off slower than cube root)
    ///   fireball                                             ~ 70 m * W^0.4 (Glasstone 2.12; as NukeCinematic draws it)
    ///
    /// Scaling for Kerbin: BDArmory's own nuclear damage is not scaled down for Kerbin's size (it works on
    /// BDModuleNuke.thermalRadius in real metres), so the overpressure rings are left unscaled too. Only the
    /// fireball follows sizeScale, so it matches the fireball you see. The thermal ring is drawn at the weapon's
    /// BDArmory thermalRadius (the range it actually does damage); the textbook burn range is shown alongside.
    /// </summary>
    internal struct NukeEffectRanges
    {
        public float W, fireball, psi20, psi5, psi1, burns3, thermalBD;

        public static NukeEffectRanges For(float yieldKt, float thermalRadius)
        {
            float W = Mathf.Max(yieldKt, 0.001f);
            float cube = Mathf.Pow(W, 1f / 3f);
            return new NukeEffectRanges
            {
                W = W,
                fireball = 70f * Mathf.Pow(W, 0.4f) * NukeSettings.Current.sizeScale,
                psi20 = 230f * cube,
                psi5 = 620f * cube,
                psi1 = 1720f * cube,
                burns3 = 600f * Mathf.Pow(W, 0.42f),
                thermalBD = thermalRadius,
            };
        }

        /// <summary>The thermal ring's radius: BDArmory's thermalRadius when the part has one, else the burn range.</summary>
        public float Thermal => thermalBD > 0f ? thermalBD : burns3;

        public static string Km(float m) => m >= 10000f ? (m / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + " km"
            : (m / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " km";

        public static PartModule FindNuke(Part p)
        {
            if (p == null || p.Modules == null) return null;
            for (int i = 0; i < p.Modules.Count; i++)
                if (p.Modules[i] != null && p.Modules[i].moduleName == "BDModuleNuke") return p.Modules[i];
            return null;
        }

        /// <summary>Reads yield and thermalRadius off a BDModuleNuke (an internal BDArmory class: by KSPField name).</summary>
        public static bool Read(PartModule nuke, out float yieldKt, out float thermalRadius)
        {
            yieldKt = thermalRadius = 0f;
            if (nuke == null) return false;
            try
            {
                yieldKt = Convert.ToSingle(nuke.Fields.GetValue("yield"), CultureInfo.InvariantCulture);
                thermalRadius = Convert.ToSingle(nuke.Fields.GetValue("thermalRadius"), CultureInfo.InvariantCulture);
                return yieldKt > 0f;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Part menu and editor tooltip readout of a warhead's effects ranges, added by Patches/NukeRangeInfo.cfg to
    /// every part with a BDModuleNuke. Follows the yield when a ModuleNukeYieldSelector changes it.
    /// </summary>
    public class ModuleNukeRangeInfo : PartModule
    {
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Fireball radius")] public string fireballText = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "20 psi (heavy damage)")] public string psi20Text = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "5 psi (most buildings)")] public string psi5Text = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "1 psi (windows)")] public string psi1Text = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Thermal (BDArmory)")] public string thermalText = "";
        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "3rd-degree burns")] public string burnsText = "";

        PartModule nuke;
        float lastYield = -1f, lastThermal = -1f, nextCheck;

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Refresh();
        }

        public void Update()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            Refresh();
        }

        void Refresh()
        {
            if (nuke == null) nuke = NukeEffectRanges.FindNuke(part);
            bool ok = NukeEffectRanges.Read(nuke, out float y, out float th);
            foreach (var f in Fields) if (f != null) f.guiActive = f.guiActiveEditor = ok;
            if (!ok || (y == lastYield && th == lastThermal)) return;
            lastYield = y; lastThermal = th;
            var r = NukeEffectRanges.For(y, th);
            fireballText = NukeEffectRanges.Km(r.fireball);
            psi20Text = NukeEffectRanges.Km(r.psi20);
            psi5Text = NukeEffectRanges.Km(r.psi5);
            psi1Text = NukeEffectRanges.Km(r.psi1);
            thermalText = th > 0f ? NukeEffectRanges.Km(th) : "none";
            burnsText = NukeEffectRanges.Km(r.burns3);
        }

        public override string GetInfo()
        {
            try
            {
                if (!NukeEffectRanges.Read(NukeEffectRanges.FindNuke(part), out float y, out float th)) return "";
                var r = NukeEffectRanges.For(y, th);
                var sb = new StringBuilder();
                sb.AppendLine("Effects ranges at " + ModuleNukeYieldSelector.FormatYield(y) + " (optimum burst height):");
                sb.AppendLine("Fireball: " + NukeEffectRanges.Km(r.fireball));
                sb.AppendLine("20 psi, heavy damage: " + NukeEffectRanges.Km(r.psi20));
                sb.AppendLine("5 psi, most buildings: " + NukeEffectRanges.Km(r.psi5));
                sb.AppendLine("1 psi, windows: " + NukeEffectRanges.Km(r.psi1));
                if (th > 0f) sb.AppendLine("Thermal (BDArmory damage): " + NukeEffectRanges.Km(th));
                sb.AppendLine("3rd-degree burns: " + NukeEffectRanges.Km(r.burns3));
                return sb.ToString();
            }
            catch { return ""; }
        }

        public override string GetModuleDisplayName() => "Nuclear effects ranges";
    }

    /// <summary>
    /// In-flight effects-range overlay (Alt+F8, or the settings window): terrain-hugging rings around where the
    /// active vessel's nuke would go off, for the fireball, 20 / 5 / 1 psi and the thermal radius, each labelled,
    /// with the yield. The weapon is the weapon manager's selected weapon if it is a nuke, else the first
    /// BDModuleNuke on the vessel. The aim point is BDArmory's bomb impact prediction (bomb aimer) for bombs,
    /// the missile's target position once it has one, and otherwise the ground under the vessel.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeRangeOverlay : MonoBehaviour
    {
        public static bool Shown;

        struct Ring
        {
            public string name;
            public Color color;
            public float radius;
            public LineRenderer line;
            public double[] lat, lon, alt;
            public Vector2 label;
            public bool labelVisible;
        }

        static readonly string[] Names = { "Fireball", "20 psi", "5 psi", "1 psi", "Thermal" };
        static readonly Color[] Colors =
        {
            new Color(1f, 0.15f, 0.1f), new Color(1f, 0.55f, 0.1f), new Color(1f, 0.92f, 0.2f),
            new Color(1f, 1f, 1f), new Color(0.72f, 0.35f, 1f),
        };

        static FieldInfo showBombAimerField;

        Ring[] rings;
        Material mat;
        GameObject root;
        NukeEffectRanges ranges;
        bool haveWeapon, visible;
        string weaponName = "", aimSource = "";
        CelestialBody ringBody;
        double cLat, cLon;
        float nextFind, nextResample;
        Part nukePart;
        PartModule nuke;
        Vessel nukeVessel;
        MissileFire mf;
        GUIStyle labelStyle, boxStyle;
        bool uiHidden;

        void Awake()
        {
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
        }

        void OnDestroy()
        {
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            if (root != null) Destroy(root);
            if (mat != null) Destroy(mat);
        }

        void OnHideUI() { uiHidden = true; }
        void OnShowUI() { uiHidden = false; }

        public static void Toggle()
        {
            Shown = !Shown;
            ScreenMessages.PostScreenMessage("BDNukes effects-range overlay: " + (Shown ? "on" : "off"), 2f, ScreenMessageStyle.UPPER_CENTER);
        }

        void Build()
        {
            if (rings != null) return;
            var sh = Shader.Find("KSP/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            mat = new Material(sh);
            root = new GameObject("BDNukes_RangeOverlay");
            int n = NukeSettings.Current.rangeOverlayPoints;
            rings = new Ring[Names.Length];
            for (int i = 0; i < rings.Length; i++)
            {
                var go = new GameObject("Ring_" + Names[i]);
                go.transform.SetParent(root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = mat;
                lr.useWorldSpace = true;
                lr.loop = true;
                lr.positionCount = n;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.startWidth = lr.endWidth = 1f;
                var c = Colors[i];
                c.a = 0.9f;
                lr.startColor = lr.endColor = c;
                lr.enabled = false;
                rings[i] = new Ring
                {
                    name = Names[i], color = Colors[i], line = lr,
                    lat = new double[n], lon = new double[n], alt = new double[n],
                };
            }
        }

        void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            if (rings != null) for (int i = 0; i < rings.Length; i++) rings[i].line.enabled = on;
        }

        void Update()
        {
            if (NukeSettings.AltKeyDown(NukeSettings.Current.rangeOverlayKey)) Toggle();
        }

        void LateUpdate()
        {
            if (!Shown || MapView.MapIsEnabled || !FlightGlobals.ready || FlightGlobals.ActiveVessel == null || FlightCamera.fetch == null)
            {
                SetVisible(false);
                return;
            }
            try { UpdateOverlay(); }
            catch (Exception e)
            {
                Debug.LogWarning("[BDNukes]: Range overlay error, turning it off: " + e.Message);
                Shown = false;
                SetVisible(false);
            }
        }

        void UpdateOverlay()
        {
            var v = FlightGlobals.ActiveVessel;
            // no weapon aboard: look again once a second, not every frame
            if (Time.unscaledTime >= nextFind || v != nukeVessel || (nukePart != null && nukePart.vessel != v))
            {
                nextFind = Time.unscaledTime + 1f;
                FindWeapon(v);
            }
            haveWeapon = NukeEffectRanges.Read(nuke, out float y, out float th);
            if (!haveWeapon)
            {
                SetVisible(false);
                return;
            }
            var r = NukeEffectRanges.For(y, th);
            Build();
            var body = v.mainBody;
            Vector3 aim = AimPoint(v, out aimSource);
            double lat = body.GetLatitude(aim), lon = body.GetLongitude(aim);

            // resample the terrain when the aim point or the yield moves (a few times a second at most)
            float smallest = Mathf.Max(20f, Mathf.Max(0.01f * r.fireball, 0.003f * r.thermalBD)); // a moving aircraft mustn't resample big rings every 0.2 s
            bool moved = body != ringBody || r.W != ranges.W || r.thermalBD != ranges.thermalBD || r.fireball != ranges.fireball
                || SurfaceDistance(body, lat, lon, cLat, cLon) > smallest;
            if (moved && Time.unscaledTime >= nextResample)
            {
                nextResample = Time.unscaledTime + 0.2f;
                ringBody = body; cLat = lat; cLon = lon; ranges = r;
                Resample(body);
            }
            if (ringBody != body) { SetVisible(false); return; }
            SetVisible(true);

            Camera cam = FlightCamera.fetch.mainCamera;
            Vector3 camPos = cam != null ? cam.transform.position : v.transform.position;
            for (int i = 0; i < rings.Length; i++)
            {
                var ring = rings[i];
                int n = ring.lat.Length;
                float bestX = float.NegativeInfinity;
                ring.labelVisible = false;
                float dist = 0f;
                for (int k = 0; k < n; k++)
                {
                    Vector3 p = body.GetWorldSurfacePosition(ring.lat[k], ring.lon[k], ring.alt[k]);
                    ring.line.SetPosition(k, p);
                    if (k == 0) dist = (p - camPos).magnitude;
                    if (cam != null)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(p);
                        if (sp.z > 0f && sp.x > bestX && sp.x < Screen.width - 60f && sp.y > 0f && sp.y < Screen.height)
                        {
                            bestX = sp.x;
                            ring.label = new Vector2(sp.x, Screen.height - sp.y);
                            ring.labelVisible = true;
                        }
                    }
                }
                // a constant on-screen width: thicker the further the ring is from the camera
                Vector3 centre = body.GetWorldSurfacePosition(cLat, cLon, ring.alt[0]);
                float centreDist = (centre - camPos).magnitude;
                ring.line.widthMultiplier = Mathf.Max(0.5f, 0.0025f * Mathf.Min(dist, centreDist + ring.radius));
                rings[i] = ring;
            }
        }

        static double SurfaceDistance(CelestialBody b, double lat1, double lon1, double lat2, double lon2)
        {
            double d2r = Math.PI / 180d;
            double p1 = lat1 * d2r, p2 = lat2 * d2r, dl = (lon2 - lon1) * d2r;
            double c = Math.Sin(p1) * Math.Sin(p2) + Math.Cos(p1) * Math.Cos(p2) * Math.Cos(dl);
            return b.Radius * Math.Acos(Math.Max(-1d, Math.Min(1d, c)));
        }

        /// <summary>Great-circle rings around the centre, each point on the terrain (or the sea surface).</summary>
        void Resample(CelestialBody body)
        {
            float[] radii = { ranges.fireball, ranges.psi20, ranges.psi5, ranges.psi1, ranges.Thermal };
            double d2r = Math.PI / 180d;
            double phi = cLat * d2r, lam = cLon * d2r;
            for (int i = 0; i < rings.Length; i++)
            {
                var ring = rings[i];
                ring.radius = radii[i];
                int n = ring.lat.Length;
                double delta = radii[i] / body.Radius;
                // lift the line off the ground a little, more for bigger rings (coarser terrain between samples)
                double lift = Math.Max(3d, radii[i] * 0.004d);
                for (int k = 0; k < n; k++)
                {
                    double th = 2d * Math.PI * k / n;
                    double lat2 = Math.Asin(Math.Sin(phi) * Math.Cos(delta) + Math.Cos(phi) * Math.Sin(delta) * Math.Cos(th));
                    double lon2 = lam + Math.Atan2(Math.Sin(th) * Math.Sin(delta) * Math.Cos(phi), Math.Cos(delta) - Math.Sin(phi) * Math.Sin(lat2));
                    ring.lat[k] = lat2 / d2r;
                    ring.lon[k] = lon2 / d2r;
                    double g = body.pqsController != null ? body.TerrainAltitude(ring.lat[k], ring.lon[k]) : 0d;
                    if (body.ocean && g < 0d) g = 0d;
                    ring.alt[k] = g + lift;
                }
                rings[i] = ring;
            }
        }

        void FindWeapon(Vessel v)
        {
            nukeVessel = v;
            nukePart = null;
            nuke = null;
            weaponName = "";
            mf = null;
            for (int i = 0; i < v.parts.Count && mf == null; i++)
                mf = v.parts[i].FindModuleImplementing<MissileFire>();
            if (mf != null)
            {
                try
                {
                    var w = mf.selectedWeapon;
                    var wp = w?.GetPart();
                    var pm = NukeEffectRanges.FindNuke(wp);
                    if (pm != null)
                    {
                        nukePart = wp; nuke = pm;
                        weaponName = w.GetShortName();
                    }
                }
                catch { }
            }
            if (nuke == null)
            {
                for (int i = 0; i < v.parts.Count && nuke == null; i++)
                {
                    var pm = NukeEffectRanges.FindNuke(v.parts[i]);
                    if (pm != null) { nuke = pm; nukePart = v.parts[i]; }
                }
                if (nukePart != null) weaponName = nukePart.partInfo != null ? nukePart.partInfo.title : nukePart.name;
            }
        }

        Vector3 AimPoint(Vessel v, out string source)
        {
            if (mf != null && nukePart != null)
            {
                try
                {
                    var w = mf.selectedWeapon;
                    if (w != null && w.GetPart() == nukePart)
                    {
                        string cls = w.GetWeaponClass().ToString();
                        if (cls == "Bomb")
                        {
                            if (showBombAimerField == null)
                                showBombAimerField = typeof(MissileFire).GetField("showBombAimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            bool shown = showBombAimerField == null || (bool)showBombAimerField.GetValue(mf);
                            if (shown && mf.bombAimerPosition != Vector3.zero)
                            {
                                source = "bomb impact prediction";
                                return mf.bombAimerPosition;
                            }
                        }
                        var m = mf.CurrentMissile;
                        if (m != null && m.part == nukePart && m.TargetAcquired && m.TargetPosition != Vector3.zero)
                        {
                            source = "missile target";
                            return m.TargetPosition;
                        }
                    }
                }
                catch { }
            }
            source = "ground below the vessel";
            return v.CoM;
        }

        void OnGUI()
        {
            if (!Shown || uiHidden || !visible || !haveWeapon || rings == null || MapView.MapIsEnabled) return;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, wordWrap = false };
                boxStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.UpperLeft, wordWrap = false, richText = true };
            }
            for (int i = 0; i < rings.Length; i++)
            {
                if (!rings[i].labelVisible) continue;
                string text = rings[i].name + " " + NukeEffectRanges.Km(rings[i].radius);
                var pos = rings[i].label;
                var rect = new Rect(pos.x + 6f, pos.y - 9f, 160f, 20f);
                // a dark drop shadow keeps the label readable over snow and cloud
                labelStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, labelStyle);
                labelStyle.normal.textColor = rings[i].color;
                GUI.Label(rect, text, labelStyle);
            }
            var r = ranges;
            var sb = new StringBuilder();
            sb.Append("<b>").Append(weaponName).Append("</b>  ").Append(ModuleNukeYieldSelector.FormatYield(r.W)).Append('\n');
            sb.Append("Aim: ").Append(aimSource).Append('\n');
            sb.Append("<color=#ff3020>Fireball</color> ").Append(NukeEffectRanges.Km(r.fireball)).Append('\n');
            sb.Append("<color=#ff8c1a>20 psi heavy damage</color> ").Append(NukeEffectRanges.Km(r.psi20)).Append('\n');
            sb.Append("<color=#ffeb33>5 psi most buildings</color> ").Append(NukeEffectRanges.Km(r.psi5)).Append('\n');
            sb.Append("<color=#ffffff>1 psi windows</color> ").Append(NukeEffectRanges.Km(r.psi1)).Append('\n');
            sb.Append("<color=#b859ff>Thermal ").Append(r.thermalBD > 0f ? "(BDArmory)" : "(burns)").Append("</color> ").Append(NukeEffectRanges.Km(r.Thermal));
            if (r.thermalBD > 0f) sb.Append("\n3rd-degree burns ").Append(NukeEffectRanges.Km(r.burns3));
            string info = sb.ToString();
            var size = boxStyle.CalcSize(new GUIContent(info));
            GUI.Box(new Rect(Screen.width - size.x - 24f, 110f, size.x + 12f, size.y + 6f), info, boxStyle);
        }
    }
}
