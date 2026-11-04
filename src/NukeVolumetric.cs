using System.IO;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Inputs for one frame of the volumetric cloud, in the detonation's local frame (y up, burst point at the origin).
    /// NukeCinematic fills it from the same state that drives the sprite cloud, so both follow the same timeline.
    /// </summary>
    internal struct NukeVolumeState
    {
        public Vector3 capCenter;
        public float capScale, ringR, tube, dome, squash, xzSpread, overhang;
        public Vector3 stemBase, stemTop;
        public float stemBaseR, stemTopR, stemDustTop, stemTrailBottom, stemDensity, stemFlare;
        public Vector3 skirtCenter;
        public float skirtR, skirtH, skirtDensity;
        public float capDensity, time, roll;
        public float fire, fireTemp;
        public Color glow; public float glowRadius;
        public Vector3 sunDir; public float daylight;
        public Color sunCol; public float sunVisCap, sunVisLow;
        public Color ambient, albedo, albedoDark;
        public Color hazeCol; public float haze;
        public float groundY;
    }

    /// <summary>
    /// The raymarched mushroom cloud (and its fluid, incandescent fireball): a box around the cloud drawn with
    /// the BDNukes/VolumetricCloud shader from GameData/BDNukes/Shaders. Without the bundle (or on a platform it
    /// wasn't built for) Available is false and the sprite cloud is used instead.
    /// </summary>
    internal class NukeVolumetric
    {
        static bool loadTried;
        static Shader shader;
        static Texture3D noise;
        static Mesh cube;

        public static bool Available
        {
            get
            {
                if (!loadTried) Load();
                return shader != null && noise != null;
            }
        }

        static void Load()
        {
            loadTried = true;
            if (Application.platform != RuntimePlatform.WindowsPlayer) return; // the bundle is built for Windows / DirectX 11
            try
            {
                string plugins = Path.GetDirectoryName(typeof(NukeVolumetric).Assembly.Location) ?? ".";
                string path = Path.Combine(Path.Combine(Path.GetDirectoryName(plugins) ?? plugins, "Shaders"), "bdnukes-windows.shaders");
                if (!File.Exists(path)) return;
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) return;
                foreach (var sh in bundle.LoadAllAssets<Shader>()) if (sh.name == "BDNukes/VolumetricCloud") shader = sh;
                foreach (var tx in bundle.LoadAllAssets<Texture3D>()) noise = tx;
                if (shader != null && !shader.isSupported)
                {
                    Debug.LogWarning("[BDNukes]: Volumetric cloud shader isn't supported on this GPU; using the sprite cloud.");
                    shader = null;
                }
                Debug.Log("[BDNukes]: Volumetric clouds " + (shader != null && noise != null ? "loaded." : "not available (bundle incomplete)."));
            }
            catch (System.Exception e)
            {
                shader = null;
                Debug.LogWarning("[BDNukes]: Couldn't load the volumetric cloud shader: " + e.Message);
            }
        }

        static Mesh Cube()
        {
            if (cube != null) return cube;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return cube;
        }

        /// <summary>Scene depth lets terrain and craft in front of the cloud hide it.</summary>
        static void EnableDepth()
        {
            if (FlightCamera.fetch == null || FlightCamera.fetch.cameras == null) return;
            foreach (var cam in FlightCamera.fetch.cameras)
                if (cam != null && (cam.depthTextureMode & DepthTextureMode.Depth) == 0)
                    cam.depthTextureMode |= DepthTextureMode.Depth;
        }

        readonly Transform owner;
        readonly GameObject box;
        readonly Material mat;
        readonly MeshRenderer rend;

        public NukeVolumetric(Transform detonation)
        {
            owner = detonation;
            box = new GameObject("VolumetricCloud");
            box.transform.SetParent(detonation, false);
            box.AddComponent<MeshFilter>().sharedMesh = Cube();
            rend = box.AddComponent<MeshRenderer>();
            mat = new Material(shader);
            mat.SetTexture("_Noise", noise);
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;
            rend.enabled = false;
            EnableDepth();
        }

        public void Destroy()
        {
            if (mat != null) Object.Destroy(mat);
            if (box != null) Object.Destroy(box);
        }

        public void Hide() { if (rend.enabled) rend.enabled = false; }

        public void Update(ref NukeVolumeState v, int steps)
        {
            float cs = Mathf.Max(1f, v.capScale);
            float capHalfW = (v.ringR + v.tube + 0.35f) * v.xzSpread * cs * 1.15f;
            float capHalfH = (Mathf.Max(v.tube, v.dome) * v.squash + 0.45f) * cs;
            Vector3 min = Vector3.Min(v.capCenter - new Vector3(capHalfW, capHalfH, capHalfW), v.capCenter);
            Vector3 max = Vector3.Max(v.capCenter + new Vector3(capHalfW, capHalfH + 0.2f * cs, capHalfW), v.capCenter);
            if (v.stemDensity > 0.001f)
            {
                float r = Mathf.Max(v.stemBaseR * (1f + v.stemFlare), v.stemTopR) * 1.4f;
                min = Vector3.Min(min, Vector3.Min(v.stemBase, v.stemTop) - new Vector3(r, 0f, r));
                max = Vector3.Max(max, Vector3.Max(v.stemBase, v.stemTop) + new Vector3(r, 0f, r));
            }
            if (v.skirtDensity > 0.001f)
            {
                float r = v.skirtR * 1.35f;
                min = Vector3.Min(min, v.skirtCenter - new Vector3(r, 0f, r));
                max = Vector3.Max(max, v.skirtCenter + new Vector3(r, v.skirtH * 1.6f, r));
            }
            min.y = Mathf.Min(min.y, v.groundY - 30f);
            box.transform.localPosition = 0.5f * (min + max);
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = max - min;

            mat.SetMatrix("_WorldToCloud", owner.worldToLocalMatrix);
            mat.SetMatrix("_CloudToWorld", owner.localToWorldMatrix);
            mat.SetVector("_BoxMin", min);
            mat.SetVector("_BoxMax", max);
            mat.SetVector("_CapCenter", new Vector4(v.capCenter.x, v.capCenter.y, v.capCenter.z, cs));
            mat.SetVector("_CapShape", new Vector4(v.ringR, v.tube, v.dome, v.squash));
            mat.SetVector("_CapXZ", new Vector4(v.xzSpread, v.overhang, 0f, 0f));
            mat.SetVector("_StemBase", new Vector4(v.stemBase.x, v.stemBase.y, v.stemBase.z, v.stemBaseR));
            mat.SetVector("_StemTop", new Vector4(v.stemTop.x, v.stemTop.y, v.stemTop.z, v.stemTopR));
            mat.SetVector("_StemVis", new Vector4(v.stemDustTop, v.stemTrailBottom, v.stemDensity, v.stemFlare));
            mat.SetVector("_SkirtCenter", new Vector4(v.skirtCenter.x, v.skirtCenter.y, v.skirtCenter.z, v.skirtR));
            mat.SetVector("_Skirt", new Vector4(v.skirtH, v.skirtDensity, 0.35f, 0f));
            mat.SetVector("_Density", new Vector4(v.capDensity, 7f / cs, 0.3f, 1f / (1.3f * cs)));
            mat.SetVector("_Anim", new Vector4(v.time, v.roll, 0f, 0f));
            mat.SetVector("_Fire", new Vector4(v.fire, v.fireTemp, 0.7f, 0f));
            mat.SetVector("_Glow", new Vector4(v.glow.r, v.glow.g, v.glow.b, v.glowRadius));
            mat.SetVector("_GlowCenter", v.capCenter);
            mat.SetVector("_SunDir", new Vector4(v.sunDir.x, v.sunDir.y, v.sunDir.z, v.daylight));
            mat.SetVector("_SunCol", new Vector4(v.sunCol.r, v.sunCol.g, v.sunCol.b, v.sunVisCap));
            mat.SetVector("_SunLow", new Vector4(v.sunVisLow, 0f, 0f, 0f));
            mat.SetVector("_Ambient", v.ambient);
            mat.SetVector("_Albedo", v.albedo);
            mat.SetVector("_AlbedoDark", v.albedoDark);
            mat.SetVector("_HazeCol", new Vector4(v.hazeCol.r, v.hazeCol.g, v.hazeCol.b, v.haze));
            mat.SetVector("_March", new Vector4(steps, 5f, 1f, 0f));
            bool on = v.capDensity > 0.003f || v.stemDensity > 0.003f || v.skirtDensity > 0.003f;
            if (rend.enabled != on) rend.enabled = on;
        }
    }
}
