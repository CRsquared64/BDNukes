using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Dial-a-yield for variable-yield warheads (B61, W80...). The yield is chosen in the editor, or in flight
    /// while the weapon is still attached to its carrier. It writes yield, thermalRadius and fluence into the
    /// part's BDModuleNuke (an internal BDArmory class, reached by moduleName and its KSPFields).
    /// thermalRadius = thermalRadiusAtMax * (yield / maxYield)^(1/3); fluence = yield.
    /// </summary>
    public class ModuleNukeYieldSelector : PartModule
    {
        /// <summary>Selectable yields in kilotons, comma separated.</summary>
        [KSPField] public string yields = "1";

        [KSPField] public int defaultIndex = -1;

        /// <summary>thermalRadius at the largest yield (m).</summary>
        [KSPField] public float thermalRadiusAtMax = 2400f;

        /// <summary>Lock the yield once the weapon leaves its carrier. False for hand-placed charges (the SADM).</summary>
        [KSPField] public bool lockAfterRelease = true;

        /// <summary>Selected index into yields; -1 means "use defaultIndex".</summary>
        [KSPField(isPersistant = true)] public int yieldIndex = -1;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Yield")]
        [UI_ChooseOption(scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public string selectedYield = "";

        [KSPField(guiActive = false, guiActiveEditor = false, guiName = "Yield")]
        public string yieldReadout = "";

        float[] values = new float[0];
        string[] labels = new string[0];
        PartModule nuke;
        Vessel startVessel;
        MissileBase missile;
        bool locked;
        int appliedIndex = -2;

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            ParseYields();
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            ParseYields();
            if (values.Length == 0)
            {
                // a chooser with no options throws when the part menu opens
                Fields["selectedYield"].guiActive = Fields["selectedYield"].guiActiveEditor = false;
                Debug.LogWarning("[BDNukes]: " + part.partInfo?.title + " has ModuleNukeYieldSelector with no valid yields ('" + yields + "').");
                return;
            }
            if (yieldIndex < 0 || yieldIndex >= values.Length) yieldIndex = DefaultIndex();
            selectedYield = labels[yieldIndex];

            foreach (var ctrl in new[] { Fields["selectedYield"].uiControlEditor, Fields["selectedYield"].uiControlFlight })
            {
                if (ctrl is UI_ChooseOption choose)
                {
                    choose.options = labels;
                    choose.display = labels;
                    choose.onFieldChanged = OnYieldChanged;
                    choose.onSymmetryFieldChanged = OnYieldChanged;
                }
            }

            if (HighLogic.LoadedSceneIsFlight)
            {
                startVessel = vessel;
                missile = part.FindModuleImplementing<MissileBase>();
            }
            Apply();
        }

        void ParseYields()
        {
            var list = new List<float>();
            foreach (var s in (yields ?? "").Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && y > 0f) list.Add(y);
            values = list.ToArray();
            labels = new string[values.Length];
            for (int i = 0; i < values.Length; i++) labels[i] = FormatYield(values[i]);
        }

        int DefaultIndex() => defaultIndex >= 0 && defaultIndex < values.Length ? defaultIndex : values.Length - 1;

        public static string FormatYield(float kt)
        {
            if (kt >= 1000f) return (kt / 1000f).ToString("0.##", CultureInfo.InvariantCulture) + " Mt";
            return kt.ToString("0.###", CultureInfo.InvariantCulture) + " kt";
        }

        void OnYieldChanged(BaseField field, object oldValue)
        {
            SyncFromLabel();
        }

        void SyncFromLabel()
        {
            if (locked) return;
            int i = Array.IndexOf(labels, selectedYield);
            if (i >= 0) yieldIndex = i;
            Apply();
        }

        void Apply()
        {
            if (values.Length == 0) return;
            if (yieldIndex < 0 || yieldIndex >= values.Length) yieldIndex = DefaultIndex();
            selectedYield = labels[yieldIndex];
            yieldReadout = labels[yieldIndex];
            if (appliedIndex == yieldIndex && nuke != null) return;

            if (nuke == null)
            {
                foreach (PartModule pm in part.Modules)
                    if (pm != null && pm.moduleName == "BDModuleNuke") { nuke = pm; break; }
            }
            if (nuke == null)
            {
                Debug.LogWarning("[BDNukes]: " + part.partInfo?.title + " has ModuleNukeYieldSelector but no BDModuleNuke.");
                return;
            }

            float y = values[yieldIndex];
            float yMax = 0f;
            foreach (float v in values) yMax = Mathf.Max(yMax, v);
            float thermal = thermalRadiusAtMax * Mathf.Pow(y / yMax, 1f / 3f);
            SetField(nuke, "yield", y);
            SetField(nuke, "thermalRadius", thermal);
            SetField(nuke, "fluence", y);
            appliedIndex = yieldIndex;
        }

        static void SetField(PartModule pm, string name, float value)
        {
            if (!pm.Fields.SetValue(name, value))
                Debug.LogWarning("[BDNukes]: Could not set " + pm.moduleName + "." + name);
        }

        public void Update()
        {
            if (values.Length == 0) return;
            if (HighLogic.LoadedSceneIsFlight && lockAfterRelease && !locked && vessel != null)
            {
                bool launched = (startVessel != null && vessel != startVessel) || (missile != null && missile.HasFired);
                if (launched)
                {
                    locked = true;
                    Fields["selectedYield"].guiActive = false;
                    Fields["yieldReadout"].guiActive = true;
                }
            }
            // Catches symmetry counterparts and any path that changed the label without a callback.
            if (!locked && (selectedYield != labels[Mathf.Clamp(yieldIndex, 0, labels.Length - 1)] || appliedIndex != yieldIndex))
                SyncFromLabel();
        }

        public override string GetInfo()
        {
            ParseYields();
            if (values.Length == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("Selectable yield");
            sb.AppendLine(string.Join(" / ", labels));
            sb.AppendLine("Selected: " + labels[DefaultIndex()] + " (change in the editor or before launch)");
            return sb.ToString();
        }
    }
}
