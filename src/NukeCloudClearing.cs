using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Blasts holes in EVE's volumetric cloud decks. Each EVE raymarched layer reads its clouds from a coverage map
    /// (an equirectangular texture, the material's "CloudCoverage"); while a hole is active the layer gets a copy of
    /// that map with the hole cut in by BDNukes/CoverageHole. The hole grows with the shock, piles the cloud it
    /// pushed out into a ring at its edge, drifts with the clouds (it's in the map's own UVs) and heals over minutes.
    /// When every hole on a layer has healed, EVE's own texture goes back. All through reflection, so without EVE,
    /// or if its internals change, nothing happens.
    /// </summary>
    internal static class NukeCloudClearing
    {
        const int MaxHoles = 8;
        const string CoverageProp = "CloudCoverage";

        class Hole
        {
            public Vector2 uv;
            public float radiusUV;     // final radius, in v units
            public double start; public float grow, hold, heal;
            public float seed;
        }

        class Layer
        {
            public object volume;
            public Material mat;
            public Texture original;
            public RenderTexture rt;
            public readonly List<Hole> holes = new List<Hole>();
        }

        static bool initDone, available;
        static Type cloudsObjectType;
        static PropertyInfo layerProp;
        static FieldInfo rotField, minAltField, maxAltField, bodyField, coverageField, matField;
        static MethodInfo uvMethod;
        static readonly Dictionary<object, Layer> layers = new Dictionary<object, Layer>();
        static readonly Vector4[] holeArr = new Vector4[MaxHoles];
        static readonly Vector4[] ringArr = new Vector4[MaxHoles];
        static Material holeMat;
        static double nextRedraw;

        static void Init()
        {
            initDone = true;
            try
            {
                Assembly atm = null;
                foreach (var la in AssemblyLoader.loadedAssemblies) if (la.name == "Atmosphere") atm = la.assembly;
                if (atm == null) return;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                cloudsObjectType = atm.GetType("Atmosphere.CloudsObject");
                var volType = atm.GetType("Atmosphere.CloudsRaymarchedVolume");
                if (cloudsObjectType == null || volType == null) return;
                layerProp = cloudsObjectType.GetProperty("LayerRaymarchedVolume", all);
                rotField = volType.GetField("cloudRotationMatrix", all);
                minAltField = volType.GetField("cloudMinAltitude", all);
                maxAltField = volType.GetField("cloudMaxAltitude", all);
                bodyField = volType.GetField("parentCelestialBody", all);
                coverageField = volType.GetField("coverageMap", all);
                matField = volType.GetField("raymarchedCloudMaterial", all);
                Type wrapper = coverageField != null ? coverageField.FieldType : null;
                // a private static helper on EVE's TextureWrapper
                uvMethod = wrapper != null ? wrapper.GetMethod("GetEquirectangularUV", all | BindingFlags.Static, null, new[] { typeof(Vector3) }, null) : null;
                available = layerProp != null && rotField != null && minAltField != null && maxAltField != null && bodyField != null
                            && coverageField != null && matField != null && uvMethod != null;
                if (!available)
                    Debug.LogWarning("[BDNukes]: EVE found, but its cloud coverage can't be reached; blasts won't clear clouds. Missing:"
                        + (layerProp == null ? " LayerRaymarchedVolume" : "") + (rotField == null ? " cloudRotationMatrix" : "")
                        + (minAltField == null || maxAltField == null ? " cloudMin/MaxAltitude" : "") + (bodyField == null ? " parentCelestialBody" : "")
                        + (coverageField == null ? " coverageMap" : "") + (matField == null ? " raymarchedCloudMaterial" : "")
                        + (uvMethod == null ? " GetEquirectangularUV" : ""));
                else Debug.Log("[BDNukes]: EVE cloud coverage hooked; blasts will clear clouds.");
            }
            catch (Exception e)
            {
                available = false;
                Debug.LogWarning("[BDNukes]: Couldn't hook EVE cloud coverage: " + e.Message);
            }
        }

        /// <summary>
        /// A detonation at worldPos (burst altitude ASL) clears cloud out to clearRadius metres from the burst,
        /// the clearing front racing out at shockSpeed.
        /// </summary>
        public static void Blast(CelestialBody body, Vector3 worldPos, double burstAlt, float clearRadius, float shockSpeed, float healTime)
        {
            if (!NukeSettings.Current.cloudClearing || body == null || clearRadius <= 0f) return;
            if (!initDone) Init();
            if (!available) return;
            holeMat = holeMat ?? NukeVolumetric.MakeHoleMaterial();
            if (holeMat == null) return;
            try
            {
                Vector3 up = (worldPos - (Vector3)body.position).normalized;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(cloudsObjectType))
                {
                    if (o == null) continue;
                    object vol = layerProp.GetValue(o, null);
                    if (vol == null || !ReferenceEquals(bodyField.GetValue(vol), body)) continue;
                    float lo = (float)minAltField.GetValue(vol), hi = (float)maxAltField.GetValue(vol);
                    // the clearing sphere where it meets the deck (its nearest part to the burst)
                    float dz = Mathf.Max(0f, Mathf.Min(Mathf.Abs(lo - (float)burstAlt), Mathf.Abs(hi - (float)burstAlt)));
                    if (burstAlt >= lo && burstAlt <= hi) dz = 0f;
                    if (dz >= clearRadius) continue;
                    float r = Mathf.Sqrt(clearRadius * clearRadius - dz * dz);
                    double deckAlt = Math.Max(lo, Math.Min(hi, burstAlt));
                    Vector3 deckPoint = (Vector3)body.position + up * (float)(body.Radius + deckAlt);
                    var rot = (Matrix4x4)rotField.GetValue(vol);
                    Vector3 dir = rot.MultiplyPoint(deckPoint).normalized;
                    var uv = (Vector2)uvMethod.Invoke(uvMethod.IsStatic ? null : coverageField.GetValue(vol), new object[] { dir });

                    if (!layers.TryGetValue(vol, out var layer))
                    {
                        var mat = matField.GetValue(vol) as Material;
                        if (mat == null || !mat.HasProperty(CoverageProp)) continue;
                        var tex = mat.GetTexture(CoverageProp);
                        // EVE can use a cubemap coverage; the hole pass only knows 2D lat/long maps
                        if (tex == null || tex.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) continue;
                        layer = new Layer { volume = vol, mat = mat, original = tex };
                        // twice EVE's resolution (up to 8k): the hole and its ring are drawn far finer than one coverage
                        // texel (~2 km on Kerbin), so their edges don't show as blocks in the clouds
                        int maxTex = Mathf.Min(8192, SystemInfo.maxTextureSize);
                        int scale = Mathf.Clamp(maxTex / Mathf.Max(tex.width, tex.height, 1), 1, 2);
                        layer.rt = new RenderTexture(tex.width * scale, tex.height * scale, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                        {
                            name = "BDNukesCloudCoverage", useMipMap = false, filterMode = FilterMode.Bilinear,
                            wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp,
                        };
                        layer.rt.Create();
                        layers[vol] = layer;
                    }
                    if (layer.holes.Count >= MaxHoles) layer.holes.RemoveAt(0);
                    layer.holes.Add(new Hole
                    {
                        uv = uv,
                        radiusUV = r / (Mathf.PI * (float)(body.Radius + deckAlt)),
                        start = NukeFXManager.FxTime,
                        grow = Mathf.Max(0.5f, Mathf.Sqrt(r * r + dz * dz) / Mathf.Max(50f, shockSpeed)),
                        hold = 0.4f * healTime,
                        heal = healTime,
                        seed = UnityEngine.Random.Range(0f, 6.28f),
                    });
                    Debug.Log(string.Format("[BDNukes]: Blast clears a {0:F0} m hole in the EVE cloud deck at {1:F0}-{2:F0} m.", r, lo, hi));
                }
            }
            catch (Exception e)
            {
                available = false;
                Debug.LogWarning("[BDNukes]: Clearing EVE clouds failed, turning it off: " + (e.InnerException ?? e).Message);
                RestoreAll();
            }
        }

        /// <summary>Per frame (NukeFXManager.LateUpdate): redraws the holed coverage maps and keeps them bound.</summary>
        public static void Update()
        {
            if (layers.Count == 0) return;
            double now = NukeFXManager.FxTime;
            // keep our maps bound every frame, but only redraw them a few times a second (a full-planet blit each)
            if (now < nextRedraw)
            {
                foreach (var kv in layers)
                    if (kv.Value.mat != null && kv.Value.mat.GetTexture(CoverageProp) != kv.Value.rt) kv.Value.mat.SetTexture(CoverageProp, kv.Value.rt);
                return;
            }
            nextRedraw = now + 0.2f;
            List<object> done = null;
            foreach (var kv in layers)
            {
                var layer = kv.Value;
                layer.holes.RemoveAll(h => now - h.start > h.hold + h.heal);
                if (layer.holes.Count == 0 || layer.mat == null)
                {
                    (done = done ?? new List<object>()).Add(kv.Key);
                    continue;
                }
                int n = 0;
                foreach (var h in layer.holes)
                {
                    float age = (float)(now - h.start);
                    float k = Mathf.Clamp01(age / h.grow);
                    float grow = 1f - (1f - k) * (1f - k);   // fast at first, easing as the shock weakens
                    float clear = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((age - h.hold) / h.heal));
                    holeArr[n] = new Vector4(h.uv.x, h.uv.y, Mathf.Max(1e-6f, h.radiusUV * grow), clear);
                    // z: the condensation flash at the shock front, strong while the front is still crossing the deck
                    ringArr[n] = new Vector4(0.45f * (1f - 0.6f * k) + 0.15f, h.seed, 1.2f * (1f - k) * Mathf.Clamp01(age / 0.3f), 0f);
                    n++;
                }
                for (int i = n; i < MaxHoles; i++) { holeArr[i] = Vector4.zero; ringArr[i] = Vector4.zero; }
                holeMat.SetVectorArray("_Holes", holeArr);
                holeMat.SetVectorArray("_HoleRing", ringArr);
                holeMat.SetFloat("_HoleCount", n);
                Graphics.Blit(layer.original, layer.rt, holeMat);
                if (layer.mat.GetTexture(CoverageProp) != layer.rt) layer.mat.SetTexture(CoverageProp, layer.rt);
            }
            if (done != null) foreach (var key in done) Restore(key);
        }

        static void Restore(object key)
        {
            if (!layers.TryGetValue(key, out var layer)) return;
            if (layer.mat != null && layer.original != null) layer.mat.SetTexture(CoverageProp, layer.original);
            if (layer.rt != null) { layer.rt.Release(); UnityEngine.Object.Destroy(layer.rt); }
            layers.Remove(key);
        }

        /// <summary>Puts every layer's own coverage map back (scene change, errors).</summary>
        public static void RestoreAll()
        {
            foreach (var key in new List<object>(layers.Keys)) Restore(key);
        }
    }
}
