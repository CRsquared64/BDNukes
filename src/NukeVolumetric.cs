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
        static Shader shader, holeShader, compositeShader, shadowShader, shockShader, waterShader;
        // ---- fireball lighting ----
        static Shader fireLightShader;
        // ---- end fireball lighting ----
        static Texture3D noise;
        static Mesh cube;
        /// <summary>The baked ground-burst fireball (null without it in the bundle).</summary>
        public static NukeSimData SimGround;
        /// <summary>The baked free-air fireball (null without it).</summary>
        public static NukeSimData SimAir;
        /// <summary>The baked whole surface-burst mushroom (null without it).</summary>
        public static NukeSimData SimMushroom;

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
                    else if (sh.name == "BDNukes/WaterWave") waterShader = sh;
                    // ---- fireball lighting ----
                    else if (sh.name == "BDNukes/FireLight") fireLightShader = sh;
                    else if (sh.name == "BDNukes/PartDepth") partDepthShader = sh;
                    // ---- end fireball lighting ----
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
                    {
                        // Unity names each saved frame after its file (f00, f01...), the same in every variant, so
                        // load it by its asset path; the name lookup is for bundles built with unique names
                        string fname = "f" + i.ToString("00");
                        d.frames[i] = bundle.LoadAsset<Texture3D>("assets/sim/" + meta.variant + "/" + fname + ".asset");
                        if (d.frames[i] == null) simFrames.TryGetValue(meta.variant + "_" + i.ToString("00"), out d.frames[i]);
                    }
                    if (!d.Valid)
                        Debug.LogWarning("[BDNukes]: Fireball simulation '" + meta.variant + "' is incomplete in the shader bundle ("
                            + System.Array.FindAll(d.frames, f => f != null).Length + " of " + d.frames.Length + " frames).");
                    if (d.Valid && meta.variant == "ground") SimGround = d;
                    if (d.Valid && meta.variant == "air") SimAir = d;
                    if (d.Valid && meta.variant == "mushroom") SimMushroom = d;
                }
                if (noise == null) foreach (var tx in bundle.LoadAllAssets<Texture3D>()) { noise = tx; break; }
                Debug.Log("[BDNukes]: Fireball simulations: ground " + (SimGround != null ? SimGround.frames.Length + " frames" : "none")
                    + ", air " + (SimAir != null ? SimAir.frames.Length + " frames" : "none")
                    + ", mushroom " + (SimMushroom != null ? SimMushroom.frames.Length + " frames" : "none") + ".");
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
        static readonly System.Collections.Generic.List<Camera> pausedCams = new System.Collections.Generic.List<Camera>();
        static bool camHooks;

        /// <summary>
        /// Other mods sometimes render a flight camera into a depth or shadow-map texture (Unity logs "Depth|ShadowMap
        /// RenderTexture requested without a depth buffer"). The buffer's frame copy (for the shock shells) and its
        /// draws can't run on a target with no colour: Unity then logs "Invalid surface for grabbing pixels" every frame,
        /// which preceded a crash. So the buffer is taken off a camera for any render into such a target, and put back
        /// after it.
        /// </summary>
        static void OnAnyPreCull(Camera c)
        {
            if (c == null || lowResBuffer == null || !lowResCams.Contains(c)) return;
            var t = c.targetTexture;
            if (t == null) return;
            var f = t.format;
            if (f == RenderTextureFormat.Depth || f == RenderTextureFormat.Shadowmap)
            {
                c.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
                pausedCams.Add(c);
            }
        }

        static void OnAnyPostRender(Camera c)
        {
            if (c == null || !pausedCams.Remove(c)) return;
            if (lowResBuffer != null && lowResCams.Contains(c)) c.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
        }
        static readonly System.Collections.Generic.List<NukeVolumeCompositor.Shock> shockItems = new System.Collections.Generic.List<NukeVolumeCompositor.Shock>();

        /// <summary>Queues a refracting shock shell for this frame (drawn by the flight cameras only).</summary>
        public static void AddShock(Matrix4x4 matrix, Material material)
        {
            if (material != null) shockItems.Add(new NukeVolumeCompositor.Shock { mesh = Sphere(), matrix = matrix, material = material });
        }
        static UnityEngine.Rendering.CommandBuffer lowResBuffer;
        static Material compositeMat;

        // ---- fireball lighting ----
        static readonly System.Collections.Generic.List<NukeVolumeCompositor.FireLight> fireLights = new System.Collections.Generic.List<NukeVolumeCompositor.FireLight>();
        static Material fireLightMat;

        /// <summary>The full-screen fireball light pass can run (the bundle has a supported BDNukes/FireLight).</summary>
        public static bool FireLightAvailable
        {
            get
            {
                if (!loadTried) Load();
                return fireLightShader != null && fireLightShader.isSupported;
            }
        }

        /// <summary>Queues a fireball lighting the scene this frame (world space; drawn by the flight cameras only).</summary>
        public static void AddFireLight(Vector3 worldPos, float core, float range, Color colorTimesStrength)
        {
            if (!FireLightAvailable) return;
            if (colorTimesStrength.maxColorComponent < 1e-3f || range <= 0f) return;
            fireLights.Add(new NukeVolumeCompositor.FireLight { position = worldPos, core = core, range = range, color = colorTimesStrength });
        }
        // ---- end fireball lighting ----

        static int Divisor => compositeShader != null && compositeShader.isSupported ? Mathf.Clamp(NukeSettings.Current.volumetricResolution + NukeFXManager.ExtraDivisor, 1, 3) : 1;

        /// <summary>Volumetric clouds queued to draw this frame (read by the adaptive quality before the compositor runs).</summary>
        public static int QueuedClouds => lowResItems.Count;

        /// <summary>Per frame, after every detonation has updated (NukeFXManager.LateUpdate): rebuilds the low-res pass.</summary>
        public static void UpdateCompositor()
        {
            if (lowResBuffer == null && lowResItems.Count == 0 && shockItems.Count == 0 && fireLights.Count == 0) return;
            if (lowResBuffer == null) lowResBuffer = new UnityEngine.Rendering.CommandBuffer { name = "BDNukes volumetric cloud" };
            if (compositeMat == null && compositeShader != null) compositeMat = new Material(compositeShader);
            // ---- fireball lighting ----
            if (fireLights.Count > 0)
            {
                if (fireLightMat == null && FireLightAvailable) fireLightMat = new Material(fireLightShader);
                EnableDepth();   // the pass rebuilds world positions from the depth, even with the sprite cloud
            }
            // ---- end fireball lighting ----
            if (!camHooks)
            {
                camHooks = true;
                Camera.onPreCull += OnAnyPreCull;
                Camera.onPostRender += OnAnyPostRender;
            }
            var cams = FlightCamera.fetch != null ? FlightCamera.fetch.cameras : null;
            lowResCams.RemoveAll(c => c == null);
            if (cams != null)
                foreach (var c in cams)
                    if (c != null && !lowResCams.Contains(c))
                    {
                        c.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
                        lowResCams.Add(c);
                    }
            if (lowResItems.Count > 0) GatherOccluders();
            else occluders.Clear();
            NukeVolumeCompositor.Build(lowResBuffer, lowResItems, compositeMat, Divisor, shockItems, fireLights, fireLightMat, occluders, partDepthMat);
            lowResItems.Clear();
            shockItems.Clear();
            fireLights.Clear();   // fireball lighting
        }

        static Shader partDepthShader;
        static Material partDepthMat;
        static readonly System.Collections.Generic.List<Renderer> occluders = new System.Collections.Generic.List<Renderer>();

        /// <summary>The loaded vessels' visible part renderers, drawn into BDNukes' own depth so the cloud stops at them.</summary>
        static void GatherOccluders()
        {
            occluders.Clear();
            if (partDepthShader == null || !partDepthShader.isSupported) return;
            if (partDepthMat == null) partDepthMat = new Material(partDepthShader);
            var vessels = FlightGlobals.VesselsLoaded;
            if (vessels == null) return;
            foreach (var v in vessels)
            {
                if (v == null || v.parts == null) continue;
                foreach (var p in v.parts)
                {
                    if (p == null) continue;
                    var rs = p.FindModelRenderersCached();
                    if (rs == null) continue;
                    for (int i = 0; i < rs.Count; i++)
                    {
                        var r = rs[i];
                        if (r != null && r.enabled && r.isVisible && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer))
                            occluders.Add(r);
                    }
                }
            }
        }

        /// <summary>Takes the low-res pass off the cameras (scene teardown).</summary>
        public static void ReleaseCompositor()
        {
            foreach (var c in lowResCams) if (c != null && lowResBuffer != null) c.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, lowResBuffer);
            lowResCams.Clear();
            pausedCams.Clear();
            lowResItems.Clear();
            shockItems.Clear();
            fireLights.Clear();   // fireball lighting
            if (lowResBuffer != null) { lowResBuffer.Release(); lowResBuffer = null; }
        }

        /// <summary>The refracting shock shell material (null without the bundle).</summary>
        public static Material MakeShockMaterial()
        {
            if (!loadTried) Load();
            return shockShader != null && shockShader.isSupported ? new Material(shockShader) : null;
        }

        /// <summary>The raised-sea material for nuclear water waves (null without the bundle).</summary>
        public static Material MakeWaterWaveMaterial()
        {
            if (!loadTried) Load();
            return waterShader != null && waterShader.isSupported ? new Material(waterShader) : null;
        }

        static Mesh sphere;
        public static Mesh Sphere()
        {
            if (sphere != null) return sphere;
            // a fine UV sphere (radius 0.5, like Unity's primitive): the shock front spans the whole view up close,
            // and the primitive's 20 segments show as facets along its silhouette
            const int seg = 128, rings = 64;
            var verts = new Vector3[(seg + 1) * (rings + 1)];
            var norms = new Vector3[verts.Length];
            for (int r = 0; r <= rings; r++)
            {
                float th = Mathf.PI * r / rings;
                for (int g = 0; g <= seg; g++)
                {
                    float ph = 2f * Mathf.PI * g / seg;
                    var n = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    verts[r * (seg + 1) + g] = 0.5f * n;
                    norms[r * (seg + 1) + g] = n;
                }
            }
            var tris = new int[seg * rings * 6];
            int k = 0;
            for (int r = 0; r < rings; r++)
                for (int g = 0; g < seg; g++)
                {
                    int a = r * (seg + 1) + g, b = a + seg + 1;
                    tris[k++] = a; tris[k++] = a + 1; tris[k++] = b;
                    tris[k++] = b; tris[k++] = a + 1; tris[k++] = b + 1;
                }
            sphere = new Mesh { name = "BDNukesSphere", vertices = verts, normals = norms, triangles = tris };
            sphere.RecalculateBounds();
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


        public NukeVolumetric(Transform detonation)
        {
            owner = detonation;
            box = new GameObject("VolumetricCloud");
            box.transform.SetParent(detonation, false);
            // a transform only: the cloud is drawn by the flight cameras' command buffer, never as a scene renderer
            mat = new Material(shader);
            mat.SetTexture("_Noise", noise);
            if (shadowShader != null && shadowShader.isSupported)
            {
                shadowBox = new GameObject("VolumetricCloudShadow");
                shadowBox.transform.SetParent(detonation, false);
                shadowMat = new Material(shadowShader);
                shadowMat.SetTexture("_Noise", noise);
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
            bool shadow = on && shadowMat != null && NukeSettings.Current.cloudShadows && v.daylight > 0.02f;
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
            lowResItems.Add(new NukeVolumeCompositor.Item
                {
                    mesh = Cube(), matrix = box.transform.localToWorldMatrix, material = mat,
                    shadowMatrix = shadow ? shadowBox.transform.localToWorldMatrix : Matrix4x4.identity,
                    shadowMaterial = shadow ? shadowMat : null,
                });
        }
    }
}
