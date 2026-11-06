using System.Collections.Generic;
using System.Reflection;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Procedural rocket exhaust plume drawn with the BDNukes/Plume shader on a cone mesh generated in code, one per
    /// transform named <see cref="transformName"/> (local +Y = exhaust direction, as ModuleProceduralModel orients
    /// its exhaust transforms). The plume shape is done in the vertex shader, so the mesh is a unit cone.
    ///
    /// Throttle comes from, in order: SetPlumeThrottle (part.SendMessage from other modules, honoured for 0.5 s
    /// after each call), the part's BDArmory MissileLauncher (boost / cruise thrust), or a stock engine. Ambient
    /// pressure sets the vacuum expansion and fades the shock diamonds. Without the shader bundle it does nothing.
    /// </summary>
    public class ModuleNukePlume : PartModule
    {
        [KSPField] public string transformName = "exhaustTransform";
        [KSPField] public float length = 8f;           // m at full throttle, sea level
        [KSPField] public float radius = 0.4f;         // m at the nozzle
        [KSPField] public string color = "1,0.6,0.3";
        [KSPField] public string coreColor = "1,0.95,0.8";
        [KSPField] public bool vacuum = true;          // plume expands with altitude
        [KSPField] public float intensity = 1f;

        const int Segments = 24, Rings = 28;
        static bool loggedMissing;
        static FieldInfo currentThrustField;
        static bool thrustFieldTried;

        readonly List<Plume> plumes = new List<Plume>();
        Material mat;
        Mesh mesh;
        MissileLauncher missile;
        ModuleEngines engine;
        float externalThrottle;
        float externalTime = -10f;
        float throttle;

        class Plume
        {
            public Transform source;
            public GameObject go;
            public MeshRenderer mr;
        }

        /// <summary>Sets the plume throttle 0..1 (for part.SendMessage). Overrides the automatic source for 0.5 s.</summary>
        public void SetPlumeThrottle(float t)
        {
            externalThrottle = Mathf.Clamp01(t);
            externalTime = Time.time;
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight) return;
            var shader = NukeAssets.PlumeShader;
            if (shader == null)
            {
                if (!loggedMissing) Debug.Log("[BDNukes]: Plume shader not available; ModuleNukePlume plumes are disabled.");
                loggedMissing = true;
                return;
            }

            mat = new Material(shader) { renderQueue = 3005 };
            mat.SetColor("_Color", ParseColor(color, new Color(1f, 0.6f, 0.3f)));
            mat.SetColor("_CoreColor", ParseColor(coreColor, new Color(1f, 0.95f, 0.8f)));
            mat.SetFloat("_Length", length);
            mat.SetFloat("_Radius", radius);
            mat.SetFloat("_Intensity", intensity);
            mat.SetFloat("_Seed", (part.flightID % 997) * 0.173f);
            mat.SetFloat("_Throttle", 0f);
            mat.SetFloat("_Vacuum", 0f);
            mat.SetFloat("_Diamonds", 1f);
            mat.SetFloat("_SoftFade", 0f);

            mesh = BuildMesh(length, radius);
            missile = part.FindModuleImplementing<MissileLauncher>();
            if (missile == null) engine = part.FindModuleImplementing<ModuleEngines>();

            foreach (var t in part.FindModelTransforms(transformName))
            {
                if (t == null) continue;
                var go = new GameObject("BDNukesPlume");
                go.layer = t.gameObject.layer;
                go.transform.SetParent(t, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                // length and radius are in world metres, whatever the transform's scale
                var ls = t.lossyScale;
                go.transform.localScale = new Vector3(Inv(ls.x), Inv(ls.y), Inv(ls.z));
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                go.AddComponent<PlumeDepthHook>().mat = mat;
                mr.enabled = false;
                plumes.Add(new Plume { source = t, go = go, mr = mr });
            }
            if (plumes.Count == 0) Debug.Log("[BDNukes]: ModuleNukePlume on " + part.partInfo?.name + " found no '" + transformName + "' transforms.");
        }

        static float Inv(float v) => Mathf.Abs(v) > 1e-4f ? 1f / v : 1f;

        void Update()
        {
            if (mat == null || plumes.Count == 0) return;

            float target = TargetThrottle();
            // fast light-up, slightly slower die-down
            float rate = target > throttle ? 14f : 8f;
            throttle = Mathf.MoveTowards(throttle, target, rate * Time.deltaTime);
            if (target <= 0f && throttle < 0.02f) throttle = 0f;

            bool visible = throttle > 0.001f;
            if (visible)
            {
                double p = part.staticPressureAtm;
                if (double.IsNaN(p) || p < 0) p = 0;
                float atm = Mathf.Clamp01((float)p);
                float vac = 1f - atm;
                mat.SetFloat("_Throttle", throttle);
                mat.SetFloat("_Vacuum", vacuum ? vac * vac * vac : 0f);
                mat.SetFloat("_Diamonds", Mathf.Clamp01(atm * 1.6f));
            }

            for (int i = 0; i < plumes.Count; i++)
            {
                var pl = plumes[i];
                if (pl.go == null) continue;
                // hidden when its transform is gone or no longer part of this part (a decoupled booster)
                bool attached = pl.source != null && pl.source.IsChildOf(part.transform);
                bool on = visible && attached;
                if (pl.mr.enabled != on) pl.mr.enabled = on;
            }
        }

        float TargetThrottle()
        {
            if (Time.time - externalTime < 0.5f) return externalThrottle;
            if (missile != null) return MissileThrottle();
            if (engine != null)
            {
                if (!engine.EngineIgnited || engine.flameout || engine.maxThrust <= 0f) return 0f;
                return Mathf.Clamp01(engine.finalThrust / engine.maxThrust);
            }
            return 0f;
        }

        float MissileThrottle()
        {
            if (!missile.HasFired) return 0f;
            var st = missile.MissileState;
            if (st != MissileBase.MissileStates.Boost && st != MissileBase.MissileStates.Cruise) return 0f;
            float max = Mathf.Max(missile.thrust, missile.cruiseThrust, 1e-3f);
            float cur = st == MissileBase.MissileStates.Boost ? missile.thrust : missile.cruiseThrust;
            if (!thrustFieldTried)
            {
                thrustFieldTried = true;
                currentThrustField = typeof(MissileLauncher).GetField("currentThrust", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            if (currentThrustField != null)
            {
                try { cur = (float)currentThrustField.GetValue(missile); }
                catch { currentThrustField = null; }
            }
            if (cur <= 0f) return 0f;
            // keep a short cruise sustainer plume readable
            return Mathf.Clamp(cur / max, 0.3f, 1f);
        }

        void OnDestroy()
        {
            foreach (var pl in plumes)
                if (pl.go != null) Destroy(pl.go);
            plumes.Clear();
            if (mat != null) Destroy(mat);
            if (mesh != null) Destroy(mesh);
        }

        /// <summary>
        /// Unit cone: rings at axial fraction y = 0..1 (denser near the nozzle, where the core and diamonds are),
        /// each a unit circle in xz. The vertex shader scales it into metres, so the bounds cover the widest shape
        /// (full throttle, full vacuum: length x1.8, half-angle ~35 degrees).
        /// </summary>
        static Mesh BuildMesh(float length, float radius)
        {
            int cols = Segments + 1;
            var verts = new Vector3[cols * (Rings + 1)];
            var uvs = new Vector2[verts.Length];
            var norms = new Vector3[verts.Length];
            for (int r = 0; r <= Rings; r++)
            {
                float s = Mathf.Pow(r / (float)Rings, 1.4f);
                for (int c = 0; c < cols; c++)
                {
                    float a = c / (float)Segments * 2f * Mathf.PI;
                    float x = Mathf.Cos(a), z = Mathf.Sin(a);
                    int idx = r * cols + c;
                    verts[idx] = new Vector3(x, s, z);
                    norms[idx] = new Vector3(x, 0f, z);
                    uvs[idx] = new Vector2(c / (float)Segments, s);
                }
            }
            var tris = new int[Segments * Rings * 6];
            int k = 0;
            for (int r = 0; r < Rings; r++)
                for (int c = 0; c < Segments; c++)
                {
                    int i0 = r * cols + c, i1 = i0 + 1, i2 = i0 + cols, i3 = i2 + 1;
                    tris[k++] = i0; tris[k++] = i2; tris[k++] = i1;
                    tris[k++] = i1; tris[k++] = i2; tris[k++] = i3;
                }
            var m = new Mesh { name = "BDNukesPlumeCone" };
            m.vertices = verts;
            m.normals = norms;
            m.uv = uvs;
            m.triangles = tris;
            float maxLen = Mathf.Max(length, 0.01f) * 1.8f;
            float maxRad = Mathf.Max(radius, 0.001f) + maxLen * 0.75f;
            m.bounds = new Bounds(new Vector3(0f, maxLen * 0.5f, 0f), new Vector3(2f * maxRad, maxLen, 2f * maxRad));
            return m;
        }

        static Color ParseColor(string s, Color def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            var parts = s.Split(',');
            if (parts.Length < 3) return def;
            var c = def;
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return def;
                c[i] = v;
            }
            c.a = 1f;
            return c;
        }

        /// <summary>
        /// Turns the depth fade on only for the cameras that render a depth texture: KSP's cameras differ, and
        /// sampling _CameraDepthTexture on one that doesn't would fade the plume against stale depth.
        /// </summary>
        class PlumeDepthHook : MonoBehaviour
        {
            public Material mat;

            void OnWillRenderObject()
            {
                if (mat == null) return;
                var cam = Camera.current;
                bool depth = cam != null && (cam.depthTextureMode & DepthTextureMode.Depth) != 0;
                mat.SetFloat("_SoftFade", depth ? 1f : 0f);
            }
        }
    }
}
