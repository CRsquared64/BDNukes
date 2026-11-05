using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Early-time effects of an air or tower burst:
    ///  - rope tricks: in the rapatronic photos of tower shots, bright spikes run out of the fireball's underside
    ///    along the tower's guy wires, the wires vaporised by the flash ahead of the fireball's own surface;
    ///  - the Mach stem's dust ring (sprite fallback; the volumetric ring lives in the shader's _MachStem);
    ///  - the cloud-deck flash: an EVE deck above or below the burst lit up by the fireball, white with the flash,
    ///    then orange as it cools, fading with distance.
    /// All of it on the FX clock (t), so photo mode slows and freezes it with the rest.
    /// </summary>
    internal partial class NukeCinematic
    {
        // ---- rope tricks ----
        const float RopeEnd = 0.6f;     // s: real ones last ~0.3 ms to tens of ms; stretched so they're seen
        bool ropeFx;
        LineRenderer[] ropes;
        Vector3[] ropeDir;
        float[] ropeLen, ropeGain;

        // ---- Mach stem ----
        bool machFx;
        Layer machRing;

        // ---- cloud-deck flash ----
        bool deckFlashFx, deckSampled;
        float deckFlashEnd;
        readonly List<NukeWeather.Deck> flashDecks = new List<NukeWeather.Deck>();
        GameObject[] deckDiscs;
        Material[] deckMats;

        /// <summary>Decides which early effects this burst gets (after the regime and groundFx are known).</summary>
        void SetupEarlyFx()
        {
            ropeFx = airFx && s.ropeTricks && !restored && !overOcean && body.atmosphere && hb < 2f * R && W < 100f;
            machFx = airFx && s.machStemDust && body.atmosphere && hb > 0.4f * R && hb < 9f * R;
            deckFlashFx = airFx && s.cloudDeckFlash && !restored && body.atmosphere;
            deckFlashEnd = 0.8f * tauGlow + 3f;
        }

        void BuildEarlyFx(int seed)
        {
            var rng = new System.Random(seed + 71);
            if (ropeFx) BuildRopes(rng);
            if (deckFlashFx) BuildDeckDiscs();
        }

        void UpdateEarlyFx()
        {
            if (ropes != null) UpdateRopes();
            if (machRing != null && volume == null) UpdateMachRing();
            if (deckDiscs != null) UpdateDeckFlash();
        }

        Mesh deckMesh;

        void DestroyEarlyFx()
        {
            if (deckMats != null) foreach (var m in deckMats) if (m != null) Destroy(m);
            if (deckMesh != null) Destroy(deckMesh);
        }

        // ----------------------------------------------------------------------------------------
        // Rope tricks
        // ----------------------------------------------------------------------------------------

        void BuildRopes(System.Random rng)
        {
            int n = 6 + rng.Next(7);    // 6..12 guy wires
            ropes = new LineRenderer[n];
            ropeDir = new Vector3[n];
            ropeLen = new float[n];
            ropeGain = new float[n];
            float a0 = (float)rng.NextDouble() * 2f * Mathf.PI;
            for (int k = 0; k < n; k++)
            {
                // guy wires fan out evenly round the tower, sloping 15-50 degrees down to their anchors
                float az = a0 + (k + 0.35f * ((float)rng.NextDouble() - 0.5f)) / n * 2f * Mathf.PI;
                float dip = Mathf.Lerp(15f, 50f, (float)rng.NextDouble()) * Mathf.Deg2Rad;
                ropeDir[k] = new Vector3(Mathf.Cos(az) * Mathf.Cos(dip), -Mathf.Sin(dip), Mathf.Sin(az) * Mathf.Cos(dip));
                ropeLen[k] = Mathf.Lerp(0.15f, 0.4f, (float)rng.NextDouble());
                ropeGain[k] = Mathf.Lerp(0.6f, 1.2f, (float)rng.NextDouble());

                var go = new GameObject("RopeTrick" + k);
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = NukeAssets.Bolt;
                lr.useWorldSpace = false;
                lr.positionCount = 2;
                lr.textureMode = LineTextureMode.Stretch;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 0;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                // a thin spike: widest where it leaves the fireball, tapering to a point
                lr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.6f, 0.55f), new Keyframe(1f, 0.08f));
                lr.enabled = false;
                ropes[k] = lr;
            }
        }

        void UpdateRopes()
        {
            float env = SS(0f, 0.03f, t) * (1f - SS(0.2f, RopeEnd, t)) * airW;
            if (env < 0.005f)
            {
                if (t > RopeEnd)
                {
                    foreach (var lr in ropes) if (lr != null) Destroy(lr.gameObject);
                    ropes = null;
                }
                else foreach (var lr in ropes) lr.enabled = false;
                return;
            }
            // the young fireball's radius (the volume's dome is 0.85 x its cap scale)
            float rf = 0.85f * R * Mathf.Max(0.05f, 1f - Mathf.Exp(-t / 0.15f));
            float width = Mathf.Max(0.012f * R, camDist * 0.0008f, 0.5f);
            Vector3 c0 = capPosL;
            for (int k = 0; k < ropes.Length; k++)
            {
                var lr = ropes[k];
                Vector3 d = ropeDir[k];
                // the spikes run ahead of the fireball's surface and shorten as it catches up; the part below the
                // ground is hidden by the terrain, so each wire dies as the fireball reaches its anchor
                float len = R * ropeLen[k] * (1f - 0.5f * SS(0.1f, RopeEnd, t));
                lr.SetPosition(0, c0 + d * (0.8f * rf));
                lr.SetPosition(1, c0 + d * (rf + len));
                float b = Mathf.Clamp01(env * ropeGain[k] * (1f - 0.6f * haze));
                lr.widthMultiplier = width * (0.7f + 0.3f * b);
                Color col = new Color(1f, 0.93f, 0.8f) * b;
                col.a = 1f;
                lr.startColor = col;
                lr.endColor = col * 0.7f;
                if (!lr.enabled) lr.enabled = true;
            }
        }

        // ----------------------------------------------------------------------------------------
        // Mach stem dust ring (sprite fallback)
        // ----------------------------------------------------------------------------------------

        void UpdateMachRing()
        {
            int n = machRing.Begin();
            NukeVolumeGround.MachStem(R, hb, ShockRadius(t), c, out float rad, out float h, out float den, out float w);
            float vis = Mathf.Clamp01(den / 0.4f) * FadeOut;
            float glow = Mathf.Pow(Glow(t), 0.6f);
            // enough puffs to close the ring: they widen as it spreads
            float spacing = 2f * Mathf.PI * Mathf.Max(rad, 1f) / Mathf.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                float r0 = machRing.r0[i], r1 = machRing.r1[i], r2 = machRing.r2[i], r3 = machRing.r3[i];
                float theta = (i + r1) / n * 2f * Mathf.PI;
                float rr = rad - r0 * r0 * 0.8f * w;    // packed at the front, thinning out behind it
                float a = vis > 0.003f && rr > 0.2f * R ? 0.5f * vis * (1f - 0.6f * r0) : 0f;
                float y = h * (0.25f + 0.35f * r2) * (1f - 0.4f * r0);
                Vector3 p = new Vector3(Mathf.Cos(theta) * rr, y, Mathf.Sin(theta) * rr);
                Vector3 nL = new Vector3(Mathf.Cos(theta) * 0.6f, 0.8f, Mathf.Sin(theta) * 0.6f).normalized;
                Vector3 fireL = (capPosL - (p + ground.localPosition)).normalized;
                Color col = Shade(surgeCol, nL, glow * 0.6f, fireL);
                float size = Mathf.Max(h * (1.4f + 0.6f * r3), 1.5f * spacing);
                machRing.Set(i, p, size, WithAlpha(col, a), Billow(r3));
            }
            machRing.End(n);
        }

        // ----------------------------------------------------------------------------------------
        // Cloud-deck flash
        // ----------------------------------------------------------------------------------------

        void BuildDeckDiscs()
        {
            // a flat, two-sided unit quad in the local horizontal plane, white vertex colours for the particle shader
            var mesh = deckMesh = new Mesh { name = "BDNukesDeckFlash" };
            mesh.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f) };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, 0.01f, 1f));
            deckDiscs = new GameObject[2];
            deckMats = new Material[2];
            for (int k = 0; k < 2; k++)
            {
                var go = new GameObject(k == 0 ? "DeckFlashAbove" : "DeckFlashBelow");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                deckMats[k] = new Material(NukeAssets.Glow) { name = "BDNukesDeckFlash" };
                mr.sharedMaterial = deckMats[k];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.SetActive(false);
                deckDiscs[k] = go;
            }
        }

        void UpdateDeckFlash()
        {
            if (t > deckFlashEnd)
            {
                if (deckMesh != null) { Destroy(deckMesh); deckMesh = null; }
                foreach (var go in deckDiscs) if (go != null) Destroy(go);
                deckDiscs = null;
                return;
            }
            if (!deckSampled)
            {
                // the decks don't change in the few seconds this lasts: one look is enough
                deckSampled = true;
                NukeWeather.Sample(body, lat, lon, flashDecks);
            }
            // the deck nearest above the burst is lit on its underside, the one nearest below on its top
            int above = -1, below = -1;
            float burstASL = (float)alt;
            for (int i = 0; i < flashDecks.Count; i++)
            {
                var d = flashDecks[i];
                if (d.coverage < 0.05f) continue;
                if (d.baseAlt > burstASL) { if (above < 0) above = i; }
                else if (d.topAlt < burstASL) below = i;
            }
            float flash = Mathf.Clamp01(FlashIntensity(t));
            float inc = Mathf.Pow(Glow(t), 0.8f) * FadeOut;
            Color fireCol = Color.Lerp(NukeFireLighting.Blackbody(FireballKelvin), new Color(0.96f, 0.97f, 1f), flash);
            float window = 1f - SS(0.3f * tauGlow + 1f, deckFlashEnd, t);
            float camASL = FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null
                ? (float)body.GetAltitude(FlightCamera.fetch.mainCamera.transform.position) : burstASL;
            float bright = (1.5f * flash + 1.2f * inc) * window * Mathf.Lerp(1f, 0.55f, daylight) * (1f - 0.75f * haze) * airW;
            SetDeckDisc(0, above, true, burstASL, camASL, bright, fireCol);
            SetDeckDisc(1, below, false, burstASL, camASL, bright, fireCol);
        }

        void SetDeckDisc(int k, int deck, bool aboveBurst, float burstASL, float camASL, float bright, Color fireCol)
        {
            var go = deckDiscs[k];
            if (deck < 0 || bright < 0.005f)
            {
                if (go.activeSelf) go.SetActive(false);
                return;
            }
            var d = flashDecks[deck];
            float surf = aboveBurst ? d.baseAlt : d.topAlt;
            float dz = Mathf.Max(1f, Mathf.Abs(surf - burstASL));
            // the lit patch: brightest straight over (or under) the fireball, falling off with distance (inverse
            // square, about 6 fireball radii to half)
            float atten = 1f / (1f + Mathf.Pow(dz / (6f * Mathf.Max(R, 0.5f * capScale)), 2f));
            float b = bright * atten * d.coverage;
            // seen from the far side of the deck it glows through the cloud, dimmer
            bool farSide = aboveBurst ? camASL > surf : camASL < surf;
            if (farSide) b *= 0.4f;
            if (b < 0.005f)
            {
                if (go.activeSelf) go.SetActive(false);
                return;
            }
            // the patch's half-brightness radius ~0.77 dz (cosine-weighted inverse square); the soft texture is at half
            // brightness ~0.4 of the way out
            float half = Mathf.Clamp(1.9f * dz + 1.5f * capScale, 4f * R, 50000f);
            go.transform.localPosition = new Vector3(0f, surf - burstASL, 0f) + new Vector3(capPosL.x, 0f, capPosL.z);
            go.transform.localScale = new Vector3(2f * half, 1f, 2f * half);
            var mat = deckMats[k];
            // the particle shaders read colour * _TintColor * 2
            mat.mainTexture = NukeAssets.Mist.mainTexture;
            Color tint = fireCol * (0.5f * Mathf.Min(b, 2f));
            tint.a = 0.5f;
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", tint);
            else mat.color = tint;
            if (!go.activeSelf) go.SetActive(true);
        }
    }
}
