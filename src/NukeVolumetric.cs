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
        static Shader shader, holeShader, compositeShader, shadowShader, shockShader;
        static Texture3D noise;
        static Mesh cube;
        /// <summary>The baked ground-burst fireball (null without it in the bundle).</summary>
        public static NukeSimData SimGround;
        /// <summary>The baked free-air fireball (null without it).</summary>
        public static NukeSimData SimAir;

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
                    else if (sh.name == "BDNukes/VolumeComposite") compositeShader = sh;
                    else if (sh.name == "BDNukes/CloudShadow") shadowShader = sh;
                    else if (sh.name == "BDNukes/ShockDistortion") shockShader = sh;
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
                    if (d.Valid && meta.variant == "air") SimAir = d;
                }
                if (noise == null) foreach (var tx in bundle.LoadAllAssets<Texture3D>()) { noise = tx; break; }
                Debug.Log("[BDNukes]: Fireball simulations: ground " + (SimGround != null ? SimGround.frames.Length + " frames" : "none")
                    + ", air " + (SimAir != null ? SimAir.frames.Length + " frames" : "none") + ".");
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

        // ---- reduced-resolution rendering: one command buffer, run by each flight camera ----
        static readonly System.Collections.Generic.List<NukeVolumeCompositor.Item> lowResItems = new System.Collections.Generic.List<NukeVolumeCompositor.Item>();
        static readonly System.Collections.Generic.List<Camera> lowResCams = new System.Collections.Generic.List<Camera>();
        static UnityEngine.Rendering.CommandBuffer lowResBuffer;
        static Material compositeMat;

        static int Divisor => compositeShader != null && compositeShader.isSupported ? Mathf.Clamp(NukeSettings.Current.volumetricResolution, 1, 3) : 1;

        /// <summary>Per frame, after every detonation has updated (NukeFXManager.LateUpdate): rebuilds the low-res pass.</summary>
        public static void UpdateCompositor()
        {
            if (lowResBuffer == null && lowResItems.Count == 0) return;
            if (lowResBuffer == null) lowResBuffer = new UnityEngine.Rendering.CommandBuffer { name = "BDNukes volumetric cloud" };
            if (compositeMat == null && compositeShader != null) compositeMat = new Material(compositeShader);
            var cams = FlightCamera.fetch != null ? FlightCamera.fetch.cameras : null;
            lowResCams.RemoveAll(c => c == null);
            if (cams != null)
                foreach (var c in cams)
                    if (c != null && !lowResCams.Contains(c))
                    {
                        c.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
                        lowResCams.Add(c);
                    }
            NukeVolumeCompositor.Build(lowResBuffer, lowResItems, compositeMat, Divisor);
            lowResItems.Clear();
        }

        /// <summary>Takes the low-res pass off the cameras (scene teardown).</summary>
        public static void ReleaseCompositor()
        {
            foreach (var c in lowResCams) if (c != null && lowResBuffer != null) c.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
            lowResCams.Clear();
            lowResItems.Clear();
            if (lowResBuffer != null) { lowResBuffer.Release(); lowResBuffer = null; }
        }

        /// <summary>The refracting shock shell material (null without the bundle).</summary>
        public static Material MakeShockMaterial()
        {
            if (!loadTried) Load();
            return shockShader != null && shockShader.isSupported ? new Material(shockShader) : null;
        }

        static Mesh sphere;
        public static Mesh Sphere()
        {
            if (sphere != null) return sphere;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return sphere;
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
        readonly GameObject box, shadowBox;
        readonly Material mat, shadowMat;
        readonly MeshRenderer rend, shadowRend;

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
            if (shadowShader != null && shadowShader.isSupported)
            {
                shadowBox = new GameObject("VolumetricCloudShadow");
                shadowBox.transform.SetParent(detonation, false);
                shadowBox.AddComponent<MeshFilter>().sharedMesh = Cube();
                shadowRend = shadowBox.AddComponent<MeshRenderer>();
                shadowMat = new Material(shadowShader);
                shadowMat.SetTexture("_Noise", noise);
                shadowRend.sharedMaterial = shadowMat;
                shadowRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                shadowRend.receiveShadows = false;
                shadowRend.enabled = false;
            }
            EnableDepth();
        }

        public void Destroy()
        {
            if (mat != null) Object.Destroy(mat);
            if (box != null) Object.Destroy(box);
            if (shadowMat != null) Object.Destroy(shadowMat);
            if (shadowBox != null) Object.Destroy(shadowBox);
        }

        public void Update(ref NukeVolumeState v, int steps)
        {
            bool on = NukeVolumeShader.Visible(ref v);
            bool lowRes = Divisor > 1;
            bool direct = on && !lowRes;
            bool shadow = on && shadowMat != null && NukeSettings.Current.cloudShadows && v.daylight > 0.02f;
            if (rend.enabled != direct) rend.enabled = direct;
            if (shadowRend != null && shadowRend.enabled != (shadow && !lowRes)) shadowRend.enabled = shadow && !lowRes;
            if (!on) return;
            NukeVolumeShader.Bounds(ref v, out var min, out var max);
            box.transform.localPosition = 0.5f * (min + max);
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = max - min;
            NukeVolumeShader.Apply(mat, ref v, owner.worldToLocalMatrix, min, max, steps);
            if (shadow)
            {
                NukeVolumeShader.ShadowBounds(ref v, min, max, out var smin, out var smax);
                shadowBox.transform.localPosition = 0.5f * (smin + smax);
                shadowBox.transform.localRotation = Quaternion.identity;
                shadowBox.transform.localScale = smax - smin;
                NukeVolumeShader.Apply(shadowMat, ref v, owner.worldToLocalMatrix, min, max, steps);
                shadowMat.SetVector("_ShadowMin", smin);
                shadowMat.SetVector("_ShadowMax", smax);
                shadowMat.SetVector("_ShadowParams", new Vector4(NukeSettings.Current.cloudShadowStrength, 6f * Mathf.Max(1f, max.y - v.groundY), 0f, 0f));
            }
            if (lowRes)
                lowResItems.Add(new NukeVolumeCompositor.Item
                {
                    mesh = Cube(), matrix = box.transform.localToWorldMatrix, material = mat,
                    shadowMatrix = shadow ? shadowBox.transform.localToWorldMatrix : Matrix4x4.identity,
                    shadowMaterial = shadow ? shadowMat : null,
                });
        }
    }
}
