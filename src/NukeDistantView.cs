using System;
using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Bursts seen from far away. The cinematic (cloud, fireball, flash) only exists in the flight scene around the
    /// active vessel, so a burst on the Mun was invisible from Kerbin or from a satellite in orbit: nothing of it was
    /// drawn in scaled space, where KSP renders distant planets. This draws it there: a blinding flash, the fireball
    /// cooling from white through orange to a dull red glow, then the sunlit debris (or mushroom) cloud spreading and
    /// fading over minutes. Planets in front hide it as they hide anything in scaled space. Each stage keeps a minimum
    /// size on screen, so a burst a whole moon away is a visible point of light rather than a sub-pixel speck.
    ///
    /// Drawn with Graphics.DrawMesh on the scaled-space layer (camera null), so the flight view's scaled camera and the
    /// map view both show it. Bursts are kept in a static list (by UT), so they survive vessel switches and scene changes.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class NukeDistantView : MonoBehaviour
    {
        class Burst
        {
            public CelestialBody body;
            public double lat, lon, alt, ut;
            public float W;
            public bool airless;
        }

        static readonly List<Burst> bursts = new List<Burst>();
        const int MaxBursts = 24;
        const int ScaledLayer = 10;

        static Mesh quad;
        static Material glowMat, dustMat;
        MaterialPropertyBlock block;
        static readonly int TintID = Shader.PropertyToID("_TintColor");
        static readonly int ColorID = Shader.PropertyToID("_Color");

        public static void OnDetonation(CelestialBody body, Vector3 pos, float W, float delay)
        {
            try
            {
                if (body == null || W <= 0f || Planetarium.fetch == null) return;
                bursts.Add(new Burst
                {
                    body = body,
                    lat = body.GetLatitude(pos), lon = body.GetLongitude(pos), alt = body.GetAltitude(pos),
                    ut = Planetarium.GetUniversalTime() + Mathf.Max(0f, delay),
                    W = W,
                    airless = !body.atmosphere || body.GetAltitude(pos) >= body.atmosphereDepth,
                });
                while (bursts.Count > MaxBursts) bursts.RemoveAt(0);
            }
            catch (Exception e) { Debug.LogWarning("[BDNukes]: Distant view of a burst failed: " + e.Message); }
        }

        static float Life(Burst b) => 300f * Mathf.Pow(Mathf.Max(b.W, 0.01f), 0.2f) * (b.airless ? 0.5f : 1f);

        void Start()
        {
            block = new MaterialPropertyBlock();
        }

        static void EnsureAssets()
        {
            if (quad == null)
            {
                quad = new Mesh { name = "BDNukesDistantQuad" };
                quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
                quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
                quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
                quad.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            }
            if (glowMat == null || dustMat == null)
            {
                NukeAssets.Init();
                NukeAssets.RebuildTextures();
                if (NukeAssets.Glow == null || NukeAssets.Mist == null) return;
                // own copies: scaled space draws in its own queue, after the planets so depth hides what is behind them
                glowMat = new Material(NukeAssets.Glow) { renderQueue = 3000 };
                dustMat = new Material(NukeAssets.Mist) { renderQueue = 2999 };
                // soft particles would fade against a depth the scaled camera may not have: keep them hard
                foreach (var m in new[] { glowMat, dustMat }) if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 100f);
            }
        }

        void LateUpdate()
        {
            if (bursts.Count == 0 || Planetarium.fetch == null) return;
            if (!NukeSettings.Current.distantView) return;
            try
            {
                double now = Planetarium.GetUniversalTime();
                bursts.RemoveAll(b => b.body == null || now - b.ut > Life(b));
                if (bursts.Count == 0) return;
                EnsureAssets();
                if (glowMat == null || dustMat == null) return;

                bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
                Camera cam = map ? (PlanetariumCamera.Camera) : (ScaledCamera.Instance != null ? ScaledCamera.Instance.cam : null);
                if (cam == null) return;
                Vector3 camScaled = cam.transform.position;
                float pxAngle = cam.fieldOfView * Mathf.Deg2Rad / Mathf.Max(Screen.height, 1);
                CelestialBody sun = Planetarium.fetch.Sun;

                foreach (var b in bursts)
                {
                    float t = (float)(now - b.ut);
                    if (t < 0f) continue;
                    // the flight scene keeps the real cinematic for every burst on the body you're at (live, or restored
                    // after a scene change): only bursts on other worlds need this stand-in there
                    if (!map && b.body == FlightGlobals.currentMainBody) continue;
                    Vector3d world = b.body.GetWorldSurfacePosition(b.lat, b.lon, b.alt);
                    Vector3d up = (world - b.body.position).normalized;
                    Vector3 sc = ScaledSpace.LocalToScaledSpace(world);
                    float dist = (sc - camScaled).magnitude;
                    if (dist <= 0f) continue;
                    float px = dist * pxAngle;   // one screen pixel at the burst, in scaled units

                    float W = Mathf.Max(b.W, 0.01f);
                    float R = 70f * Mathf.Pow(W, 0.4f) * (b.airless ? 2.2f : 1f);   // fireball radius (m); bigger in vacuum
                    float scaledPerM = 1f / ScaledSpace.ScaleFactor;

                    // flash: a blinding point for the first moments
                    float tFlash = 0.25f + 0.12f * Mathf.Pow(W, 0.3f);
                    if (t < tFlash * 4f)
                    {
                        float k = Mathf.Exp(-t / tFlash);
                        float size = Mathf.Max(R * 2f * scaledPerM, (8f + 22f * k) * px);
                        Draw(glowMat, sc + (Vector3)(up * (R * scaledPerM)), up, size, new Color(1f, 0.97f, 0.9f, 0.45f * k));
                    }
                    // fireball: white-hot, cooling through orange to a dull red as it rises and fades
                    float tGlow = 6f * Mathf.Pow(W, 0.3f) * (b.airless ? 0.6f : 1f);
                    if (t < tGlow)
                    {
                        float u = t / tGlow;
                        float heat = 1f - u;
                        Color c = Color.Lerp(new Color(0.55f, 0.12f, 0.03f), new Color(1f, 0.6f, 0.25f), Mathf.SmoothStep(0f, 1f, heat));
                        c = Color.Lerp(c, new Color(1f, 0.95f, 0.85f), Mathf.Clamp01(1f - t / Mathf.Max(tFlash * 3f, 0.5f)));
                        float grow = Mathf.Lerp(1f, b.airless ? 3f : 2f, Mathf.Sqrt(u));
                        float size = Mathf.Max(R * grow * scaledPerM, (3f + 4f * heat) * px);
                        c.a = 0.35f * heat * heat + 0.03f;
                        // rising with the fireball, not floating a whole glow-width above it
                        Draw(glowMat, sc + (Vector3)(up * (R * grow * scaledPerM)), up, size, c);
                    }
                    // the cloud: a mushroom where there is air, a spreading debris and dust plume on airless worlds,
                    // lit by the sun (dark on the night side), thinning out over minutes
                    float life = Life(b);
                    if (t > 0.5f && t < life)
                    {
                        float u = t / life;
                        float capR = b.airless ? 3000f * Mathf.Pow(W, 0.3f) * Mathf.Sqrt(Mathf.Clamp01(t / 40f))
                                               : 2500f * Mathf.Pow(W, 0.3f) * Mathf.Clamp01(t / (30f * Mathf.Pow(W, 0.1f)) + 0.2f);
                        float height = b.airless ? capR * 0.6f : 6000f * Mathf.Pow(W, 0.26f) * Mathf.Clamp01(t / 90f + 0.1f);
                        Vector3d sunDir = (sun.position - world).normalized;
                        float lit = Mathf.Clamp01(0.15f + 1.2f * (float)Vector3d.Dot(up, sunDir));
                        Color dust = b.airless ? new Color(0.62f, 0.58f, 0.54f) : new Color(0.72f, 0.62f, 0.55f);
                        Color c = dust * (0.12f + 0.88f * lit);
                        float fadeIn = Mathf.Clamp01((t - 0.5f) / 4f);
                        c.a = 0.55f * fadeIn * (1f - Mathf.SmoothStep(0f, 1f, u));
                        float size = Mathf.Max(capR * scaledPerM, 4f * px);
                        Vector3 centre = sc + (Vector3)(up * (Mathf.Max(height * scaledPerM, size * 0.6f)));
                        Draw(dustMat, centre, up, size, c);
                        // the fire's afterglow inside the cloud, just after the fireball fades
                        float ember = Mathf.Clamp01(1f - (t - 0.6f * tGlow) / (2f * tGlow));
                        if (ember > 0f && t > 0.6f * tGlow)
                            Draw(glowMat, centre, up, size * 0.7f, new Color(0.9f, 0.35f, 0.08f, 0.12f * ember));
                    }
                }
            }
            catch (Exception e)
            {
                if (Time.unscaledTime > warnAt) { warnAt = Time.unscaledTime + 30f; Debug.LogWarning("[BDNukes]: Distant burst view error: " + e.Message); }
            }
        }

        static float warnAt;

        void Draw(Material mat, Vector3 pos, Vector3d up, float size, Color c)
        {
            Camera cam = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION ? PlanetariumCamera.Camera
                : ScaledCamera.Instance != null ? ScaledCamera.Instance.cam : null;
            if (cam == null) return;
            // a billboard facing the camera
            Quaternion rot = Quaternion.LookRotation(pos - cam.transform.position, cam.transform.up);
            var m = Matrix4x4.TRS(pos, rot, Vector3.one * size);
            block.Clear();
            block.SetColor(TintID, c);
            block.SetColor(ColorID, c);
            Graphics.DrawMesh(quad, m, mat, ScaledLayer, cam, 0, block, false, false);
        }

        void OnDestroy() { }
    }
}
