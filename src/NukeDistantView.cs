using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Bursts seen from far away. The cinematic (cloud, fireball, flash) only exists in the flight scene around the
    /// active vessel, so a burst on the Mun was invisible from Kerbin or from a satellite in orbit: nothing of it was
    /// drawn in scaled space, where KSP renders distant planets. This draws it there: a blinding flash, the fireball
    /// cooling from white through orange to a dull red glow, then the sunlit debris (or mushroom) cloud spreading and
    /// fading over minutes. Bursts out in space get the flash, the fireball and a fast, fading debris shell instead of
    /// a cloud. Each stage keeps a minimum size on screen, so a burst a whole moon away is a visible point of light.
    ///
    /// The bursts are NukeAlert's records, which are saved with the game: a quickload or another save shows exactly the
    /// bursts that happened there, and a salvo is one burst. A burst is left to the real cloud whenever this scene draws
    /// one for it within the flight cameras' reach; planets in front hide it (checked by hand, so the sprite can be
    /// pulled toward the camera and never cut in half by the planet it sits on), and a body's shadow darkens the cloud.
    /// Drawn with Graphics.DrawMesh on the scaled-space layer, to the scaled camera in flight and the planetarium camera
    /// in map view and the tracking station.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public class NukeDistantView : MonoBehaviour
    {
        const int ScaledLayer = 10;
        /// <summary>Closer than this (m) to the camera, a sprite would sit in the player's face: leave it.</summary>
        const double MinRange = 20000d;

        static Mesh quad;
        static Material glowMat, dustMat;
        MaterialPropertyBlock block;
        Camera cam;
        static readonly int TintID = Shader.PropertyToID("_TintColor");
        static readonly int ColorID = Shader.PropertyToID("_Color");
        static float warnAt;
        static readonly System.Collections.Generic.HashSet<NukeBurstRecord> seen = new System.Collections.Generic.HashSet<NukeBurstRecord>();

        void Start()
        {
            block = new MaterialPropertyBlock();
        }

        /// <summary>How long a burst stays visible from afar (s).</summary>
        static float Life(NukeBurstRecord r, bool nearGround)
        {
            float W = Mathf.Max(r.W, 0.01f);
            if (!nearGround && r.space) return Mathf.Max(GlowTime(r, nearGround), 60f * Mathf.Pow(W, 0.2f));
            return 300f * Mathf.Pow(W, 0.2f) * (r.space ? 0.5f : 1f);
        }

        static float FlashTime(float W) => 0.25f + 0.12f * Mathf.Pow(W, 0.3f);
        static float GlowTime(NukeBurstRecord r, bool nearGround) => 6f * Mathf.Pow(Mathf.Max(r.W, 0.01f), 0.3f) * (r.space ? 0.6f : 1f);

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
                // own copies, drawn after the planets
                glowMat = new Material(NukeAssets.Glow) { renderQueue = 3000 };
                dustMat = new Material(NukeAssets.Mist) { renderQueue = 2999 };
                // soft particles would fade against a depth the scaled camera may not have: keep them hard
                if (glowMat.HasProperty("_InvFade")) glowMat.SetFloat("_InvFade", 100f);
                if (dustMat.HasProperty("_InvFade")) dustMat.SetFloat("_InvFade", 100f);
            }
        }

        /// <summary>Furthest the flight scene's own cameras draw (m): beyond it the real cloud can't be seen.</summary>
        static double LocalReach()
        {
            double reach = 0d;
            var fc = FlightCamera.fetch;
            if (fc != null && fc.cameras != null)
                foreach (var c in fc.cameras) if (c != null) reach = Math.Max(reach, c.farClipPlane);
            return reach > 0d ? reach : 750000d;
        }

        /// <summary>Does the segment from a to b (world) pass through any body other than skip and skip2?</summary>
        static bool Blocked(Vector3d a, Vector3d b, CelestialBody skip, CelestialBody skip2, double margin)
        {
            Vector3d d = b - a;
            double len = d.magnitude;
            if (len <= 0d) return false;
            d /= len;
            foreach (var body in FlightGlobals.Bodies)
            {
                if (body == null || body == skip || body == skip2) continue;
                Vector3d oc = a - body.position;
                double t = -Vector3d.Dot(oc, d);
                if (t <= 0d || t >= len) { if (oc.sqrMagnitude > body.Radius * body.Radius) continue; }
                Vector3d closest = a + d * Math.Max(0d, Math.Min(len, t));
                if ((closest - body.position).sqrMagnitude < (body.Radius - margin) * (body.Radius - margin)) return true;
            }
            return false;
        }

        void LateUpdate()
        {
            var records = NukeAlert.Records;
            if (records.Count == 0 || Planetarium.fetch == null || FlightGlobals.Bodies == null) return;
            var s = NukeSettings.Current;
            if (!s.distantView) return;
            try
            {
                bool map = MapView.MapIsEnabled || HighLogic.LoadedScene == GameScenes.TRACKSTATION;
                cam = map ? PlanetariumCamera.Camera : (ScaledCamera.Instance != null ? ScaledCamera.Instance.cam : null);
                if (cam == null) return;
                double now = Planetarium.GetUniversalTime();
                Vector3 camScaled = cam.transform.position;
                Vector3d camWorld = ScaledSpace.ScaledToLocalSpace(camScaled);
                bool flight = HighLogic.LoadedScene == GameScenes.FLIGHT && !map;
                double reach = flight ? LocalReach() : 0d;
                float pxAngle = cam.fieldOfView * Mathf.Deg2Rad / Mathf.Max(Screen.height, 1);
                float scaledPerM = 1f / ScaledSpace.ScaleFactor;
                CelestialBody sun = Planetarium.fetch.Sun;
                double lightSpeed = Math.Max(1e5d, s.waveSpeed * 1000d);

                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    if (now - r.ut < 0d) continue;
                    bool nearGround = r.NearGround;
                    var body = FlightGlobals.GetBodyByName(r.body);
                    if (body == null) continue;

                    // bursts on the ground turn with their world; bursts in space stay where they went off
                    Vector3d world = nearGround || !r.space ? body.GetWorldSurfacePosition(r.lat, r.lon, r.alt) : r.WorldPos(body);
                    double dist = (world - camWorld).magnitude;
                    // the light travels with the radiation front: a burst a planet away is seen when its radiation
                    // gets here, not the instant it goes off
                    float t = (float)(now - r.ut - dist / lightSpeed);
                    if (t < 0f || t > Life(r, nearGround)) continue;
                    if (!map && dist < MinRange) continue;
                    // the real cloud is drawn for it here, and the camera is close enough to see it
                    if (flight && dist < reach && NukeFXManager.HasCloudNear(body, world, 50000d + 400d * t)) continue;

                    Vector3d up = (world - body.position).normalized;
                    // hidden behind a planet (its own included): tested from just above the burst point, or above the
                    // body's datum sphere for a burst in a crater or valley below it
                    double rad = Math.Max((world - body.position).magnitude, body.Radius) + Math.Max(50d, 0.0005d * body.Radius);
                    Vector3d probe = body.position + up * rad;
                    if (Blocked(camWorld, probe, null, null, 0d)) continue;

                    Vector3 sc = ScaledSpace.LocalToScaledSpace(world);
                    Vector3 toCam = camScaled - sc;
                    float distS = toCam.magnitude;
                    if (distS <= 0f) continue;
                    toCam /= distS;
                    float px = distS * pxAngle;   // one screen pixel at the burst, in scaled units

                    float W = Mathf.Max(r.W, 0.01f);
                    float R = 70f * Mathf.Pow(W, 0.4f) * (r.space ? 2.2f : 1f);   // fireball radius (m); bigger in vacuum
                    // however far away, a bigger burst is a bigger, brighter point of light (x1 at 1 kt, ~x3 at 1 Mt)
                    float pxK = Mathf.Clamp(Mathf.Pow(W, 0.16f), 0.7f, 4f);
                    if (seen.Add(r))
                        Debug.Log(string.Format("[BDNukes]: Distant view: {0:0} kt burst at {1} seen from {2:0} km, {3:0.0} s after it went off.",
                            r.W, r.body, dist / 1000d, now - r.ut)
                            + string.Format(" Camera '{0}' (map {1}, far {2:0}, layer 10 drawn {3}, depth {4}), {5:0} scaled units off, a pixel there {6:0.0}; glow {7} ({8}).",
                                cam.name, map, cam.farClipPlane, (cam.cullingMask & (1 << ScaledLayer)) != 0, cam.depth, distS, px,
                                glowMat != null ? glowMat.shader.name : "missing", glowMat != null && glowMat.shader.isSupported ? "ok" : "unsupported"));

                    // flash: a blinding point for the first moments
                    float tFlash = FlashTime(W);
                    if (t < tFlash * 4f)
                    {
                        float k = Mathf.Exp(-t / tFlash);
                        float size = Mathf.Max(R * 2f * scaledPerM, (8f + 22f * k) * pxK * px);
                        Draw(glowMat, sc, up, size, toCam, distS, new Color(1f, 0.97f, 0.9f, 0.9f * k));
                    }
                    // fireball: white-hot, cooling through orange to a dull red as it rises and fades
                    float tGlow = GlowTime(r, nearGround);
                    if (t < tGlow)
                    {
                        float u = t / tGlow;
                        float heat = 1f - u;
                        Color c = Color.Lerp(new Color(0.55f, 0.12f, 0.03f), new Color(1f, 0.6f, 0.25f), Mathf.SmoothStep(0f, 1f, heat));
                        c = Color.Lerp(c, new Color(1f, 0.95f, 0.85f), Mathf.Clamp01(1f - t / Mathf.Max(tFlash * 3f, 0.5f)));
                        float grow = Mathf.Lerp(1f, r.space ? 3f : 2f, Mathf.Sqrt(u));
                        float size = Mathf.Max(R * grow * scaledPerM, (3f + 4f * heat) * pxK * px);
                        c.a = 0.7f * heat * heat + 0.06f;
                        Vector3 rise = nearGround ? (Vector3)(up * (R * grow * scaledPerM)) : Vector3.zero;
                        Draw(glowMat, sc + rise, up, size, toCam, distS, c);
                    }

                    if (!nearGround && r.space)
                    {
                        // out in space: the debris shell races outward, glowing faintly as it thins
                        float tShell = Life(r, false);
                        float shellR = s.vacuumDebrisSpeed * t;
                        float fade = 1f - Mathf.SmoothStep(0f, 1f, t / tShell);
                        float a = 0.25f * fade * Mathf.Clamp01(t / 0.5f);
                        if (a > 0.002f)
                            Draw(glowMat, sc, up, Mathf.Max(shellR * scaledPerM, 5f * pxK * px), toCam, distS, new Color(0.75f, 0.8f, 1f, a));
                        // the hot debris at its heart stays a point of light, slowly reddening, well after the flash
                        float core = fade * Mathf.Clamp01(t / Mathf.Max(0.5f, tGlow * 0.5f));
                        if (core > 0.01f)
                            Draw(glowMat, sc, up, (2.5f + 1.5f * fade) * pxK * px, toCam, distS,
                                Color.Lerp(new Color(1f, 0.45f, 0.2f), new Color(1f, 0.85f, 0.7f), fade) * new Color(1f, 1f, 1f, 0.55f * core));
                        continue;
                    }

                    // the cloud: a mushroom where there is air, a spreading debris and dust plume on airless worlds,
                    // lit by the sun (dark on the night side and in another body's shadow), thinning out over minutes
                    float life = Life(r, nearGround);
                    if (t > 0.5f)
                    {
                        float u = t / life;
                        float capR = r.space ? 3000f * Mathf.Pow(W, 0.3f) * Mathf.Sqrt(Mathf.Clamp01(t / 40f))
                                             : 2500f * Mathf.Pow(W, 0.3f) * Mathf.Clamp01(t / (30f * Mathf.Pow(W, 0.1f)) + 0.2f);
                        float height = !nearGround ? 0.3f * capR
                                     : r.space ? capR * 0.6f : 6000f * Mathf.Pow(W, 0.26f) * Mathf.Clamp01(t / 90f + 0.1f);
                        Vector3d sunDir = (sun.position - world).normalized;
                        float lit = Mathf.Clamp01(0.15f + 1.2f * (float)Vector3d.Dot(up, sunDir));
                        // eclipse: another body between the cloud and the sun (not the sun itself, nor its own world)
                        if (lit > 0f && Blocked(world + up * height, sun.position, body, sun, 0d)) lit = 0f;
                        Color dust = r.space ? new Color(0.62f, 0.58f, 0.54f) : new Color(0.72f, 0.62f, 0.55f);
                        Color c = dust * (0.12f + 0.88f * lit);
                        float fadeIn = Mathf.Clamp01((t - 0.5f) / 4f);
                        c.a = 0.9f * fadeIn * (1f - Mathf.SmoothStep(0f, 1f, u));
                        float size = Mathf.Max(capR * scaledPerM, 4f * px);
                        Vector3 centre = sc + (Vector3)(up * Mathf.Max(height * scaledPerM, nearGround ? size * 0.6f : 0f));
                        Draw(dustMat, centre, up, size, toCam, distS, c);
                        // the fire's afterglow inside the cloud, just after the fireball fades
                        float ember = Mathf.Clamp01(1f - (t - 0.6f * tGlow) / (2f * tGlow));
                        if (ember > 0f && t > 0.6f * tGlow)
                            Draw(glowMat, centre, up, size * 0.7f, toCam, distS, new Color(0.9f, 0.35f, 0.08f, 0.24f * ember));
                    }
                }
            }
            catch (Exception e)
            {
                if (Time.unscaledTime > warnAt) { warnAt = Time.unscaledTime + 30f; Debug.LogWarning("[BDNukes]: Distant burst view error: " + e.Message); }
            }
        }

        /// <summary>
        /// A camera-facing sprite of radius size (scaled units). It is pulled toward the camera by its own size, so the
        /// planet it sits on can't cut it in half (whether a planet hides it is decided by hand before this). c is the
        /// intended colour and opacity: KSP's particle shaders double the tint, so it is halved here.
        /// </summary>
        void Draw(Material mat, Vector3 pos, Vector3d up, float size, Vector3 toCam, float distS, Color c)
        {
            if (cam == null) return;
            pos += toCam * Mathf.Min(size, 0.5f * distS);
            // a burst at Moho seen from Kerbin is millions of scaled units off, past the camera's far plane: bring
            // the sprite in along the same line and shrink it to match, so it covers the same patch of sky
            Vector3 fromCam = pos - cam.transform.position;
            float d = fromCam.magnitude, keep = 0.4f * cam.farClipPlane;
            if (d > keep && d > 0f)
            {
                float k = keep / d;
                pos = cam.transform.position + fromCam * k;
                size *= k;
            }
            Quaternion rot = Quaternion.LookRotation(pos - cam.transform.position, cam.transform.up);
            var m = Matrix4x4.TRS(pos, rot, Vector3.one * size);
            c *= 0.5f;
            block.Clear();
            block.SetColor(TintID, c);
            block.SetColor(ColorID, c);
            Graphics.DrawMesh(quad, m, mat, ScaledLayer, cam, 0, block, false, false);
        }
    }
}
