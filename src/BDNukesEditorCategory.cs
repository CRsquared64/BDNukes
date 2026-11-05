using System;
using KSP.UI.Screens;
using RUI.Icons.Selectable;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Adds a "BDNukes" filter page to the VAB/SPH part list. It gathers every nuclear part:
    /// BDNukes' own, plus anything else carrying a BDArmory BDModuleNuke (e.g. the AIR-2 Genie,
    /// AIM-26 Falcon and BDArmory Extended's Small Nuclear Warhead). Parts are sorted into subcategories
    /// by what they are.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class BDNukesEditorCategory : MonoBehaviour
    {
        static readonly Color FilterColor = new Color(0.95f, 0.78f, 0.12f);
        Icon icon;

        void Awake()
        {
            DontDestroyOnLoad(this);
            GameEvents.onGUIEditorToolbarReady.Add(OnEditorToolbarReady);
        }

        void OnDestroy()
        {
            GameEvents.onGUIEditorToolbarReady.Remove(OnEditorToolbarReady);
        }

        void OnEditorToolbarReady()
        {
            try
            {
                if (icon == null) icon = new Icon("BDNukes_Trefoil", MakeTrefoil(false), MakeTrefoil(true), true);
                var filter = PartCategorizer.AddCustomFilter("BDNukes", "BDNukes", icon, FilterColor);
                AddSub(filter, "All Nuclear Weapons", IsNuke);
                AddSub(filter, "Bombs", ap => IsNuke(ap) && Classify(ap) == Kind.Bomb);
                AddSub(filter, "Missiles", ap => IsNuke(ap) && Classify(ap) == Kind.Missile);
                AddSub(filter, "ICBMs & Reentry Vehicles", ap => IsNuke(ap) && Classify(ap) == Kind.Ballistic);
                AddSub(filter, "Nuclear Artillery", ap => IsNuke(ap) && Classify(ap) == Kind.Artillery);
                AddSub(filter, "Warheads", ap => IsNuke(ap) && Classify(ap) == Kind.Warhead);
            }
            catch (Exception e)
            {
                Debug.LogError("[BDNukes]: Failed to add editor category: " + e);
            }
        }

        void AddSub(PartCategorizer.Category filter, string name, Func<AvailablePart, bool> include)
        {
            PartCategorizer.AddCustomSubcategoryFilter(filter, name, name, icon, include);
        }

        // ----------------------------------------------------------------------------------------

        enum Kind { Bomb, Missile, Ballistic, Artillery, Warhead }

        static bool IsNuke(AvailablePart ap)
        {
            if (ap == null || ap.partConfig == null) return false;
            // the Orion pulse engine is propulsion, not a weapon: it lives in Engines
            if (FindModule(ap, "ModuleOrionPulseEngine") != null) return false;
            if (ap.name.StartsWith("bdnukes", StringComparison.OrdinalIgnoreCase)) return true;
            return FindModule(ap, "BDModuleNuke") != null;
        }

        static Kind Classify(AvailablePart ap)
        {
            if (FindModule(ap, "ModuleWeapon") != null) return Kind.Artillery;
            if (FindModule(ap, "ModuleReentryVehicle") != null || FindModule(ap, "ModuleMIRVBus") != null) return Kind.Ballistic;

            var launcher = FindModule(ap, "MissileLauncher");
            if (launcher != null)
            {
                string homing = (launcher.GetValue("homingType") ?? "").ToLowerInvariant();
                string type = (launcher.GetValue("missileType") ?? "").ToLowerInvariant();
                if (homing == "agmballistic" && type != "bomb" && ap.partConfig.GetValue("bdacategory") != "Bombs") return Kind.Ballistic;
                if (type == "bomb" || ap.partConfig.GetValue("bdacategory") == "Bombs") return Kind.Bomb;
                return Kind.Missile;
            }

            // free-fall bombs released by a decoupler (e.g. the Tsar Bomba)
            if (ap.partConfig.GetValue("bdacategory") == "Bombs") return Kind.Bomb;
            return Kind.Warhead;
        }

        static ConfigNode FindModule(AvailablePart ap, string moduleName)
        {
            foreach (var m in ap.partConfig.GetNodes("MODULE"))
                if (m.GetValue("name") == moduleName) return m;
            return null;
        }

        // ----------------------------------------------------------------------------------------
        // Radiation trefoil icon, drawn procedurally (white on transparent; KSP tints it).
        // ----------------------------------------------------------------------------------------

        static Texture2D MakeTrefoil(bool selected)
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            float brightness = selected ? 1f : 0.85f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f, v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float ang = Mathf.Atan2(v, u) * Mathf.Rad2Deg;
                    // three 60-degree blades centred at 90, 210 and 330 degrees
                    float rel = Mathf.Repeat(ang - 90f + 30f, 120f);
                    bool blade = r > 0.3f && r < 0.92f && rel < 60f;
                    bool hub = r < 0.2f;
                    float edge = Mathf.Clamp01((0.95f - r) * 20f);
                    float a = (blade || hub) ? edge : 0f;
                    px[y * size + x] = new Color(brightness, brightness, brightness, a);
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
