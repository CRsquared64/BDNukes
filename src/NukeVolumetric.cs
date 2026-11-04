using System.IO;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// The raymarched nuclear cloud: fireball, cap, stem, dust skirt, base surge, Wilson cloud, condensation rings
    /// and stem collars, drawn on a box around the cloud with the BDNukes/VolumetricCloud shader from
    /// GameData/BDNukes/Shaders. The uniforms are set by NukeVolumeShader (unity/Assets/Shared), the same code the
    /// Unity test renders use. Without the bundle (or on a platform it wasn't built for) Available is false and
    /// the sprite cloud is used instead.
    /// </summary>
    internal class NukeVolumetric
    {
        static bool loadTried;
        static Shader shader, holeShader;
        static Texture3D noise;
        static Mesh cube;
        /// <summary>The baked ground-burst fireball (null without it in the bundle).</summary>
        public static NukeSimData SimGround;

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
                foreach (var sh in bundle.LoadAllAssets<Shader>())
                {
                    if (sh.name == "BDNukes/VolumetricCloud") shader = sh;
                    else if (sh.name == "BDNukes/CoverageHole") holeShader = sh;
                }
                var simFrames = new System.Collections.Generic.Dictionary<string, Texture>();
                foreach (var tx in bundle.LoadAllAssets<Texture3D>())
                {
                    if (tx.name == "BDNukesBillows") noise = tx;
                    else simFrames[tx.name] = tx;
                }
                foreach (var ta in bundle.LoadAllAssets<TextAsset>())
                {
                    if (!ta.name.EndsWith("_meta")) continue;
                    var meta = JsonUtility.FromJson<NukeSimMeta>(ta.text);
                    if (meta == null || meta.times == null) continue;
                    var d = new NukeSimData { meta = meta, frames = new Texture[meta.times.Length] };
                    for (int i = 0; i < d.frames.Length; i++)
                        simFrames.TryGetValue(meta.variant + "_" + i.ToString("00"), out d.frames[i]);
                    if (d.Valid && meta.variant == "ground") SimGround = d;
                }
                if (noise == null) foreach (var tx in bundle.LoadAllAssets<Texture3D>()) { noise = tx; break; }
                Debug.Log("[BDNukes]: Fireball simulation " + (SimGround != null ? "loaded (" + SimGround.frames.Length + " frames)." : "not in the bundle."));
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

        /// <summary>The blit material that cuts blast holes into EVE's cloud coverage maps (null without the bundle).</summary>
        public static Material MakeHoleMaterial()
        {
            if (!loadTried) Load();
            return holeShader != null && holeShader.isSupported ? new Material(holeShader) : null;
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

        public void Update(ref NukeVolumeState v, int steps)
        {
            bool on = NukeVolumeShader.Visible(ref v);
            if (rend.enabled != on) rend.enabled = on;
            if (!on) return;
            NukeVolumeShader.Bounds(ref v, out var min, out var max);
            box.transform.localPosition = 0.5f * (min + max);
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = max - min;
            NukeVolumeShader.Apply(mat, ref v, owner.worldToLocalMatrix, min, max, steps);
        }
    }
}
