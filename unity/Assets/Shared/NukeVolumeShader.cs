// Shared by the BDNukes plugin and the Unity test harness (unity/Assets/Editor/BDNukesBuild.cs), so the test
// renders use exactly the uniforms KSP sends. UnityEngine only: no KSP types in here.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BDNukes
{
    /// <summary>One frame of the volumetric cloud, in the detonation's local frame (y up, burst point at the origin).</summary>
    public struct NukeVolumeState
    {
        public float groundY;

        // cap / fireball
        public Vector3 capCenter;
        public float capScale, ringR, tube, dome, squash, xzSpread, overhang;
        public float roughness;     // 0 smooth young fireball .. 1 fully turbulent cap
        public float growthRate;    // d(ln capScale)/dt, carries billows outward as the cap expands
        public float capDensity;
        public float extinction;    // 1/m at density 1: fixed per detonation (~2.5 / fireball radius)

        // stem
        public Vector3 stemBase, stemTop;
        public Vector3 stemMid;     // bend control point (quadratic Bezier between base and top)
        public float stemBaseR, stemTopR, stemDustTop, stemTrailBottom, stemDensity, stemFlare, updraft;

        // wind
        public Vector3 windDir;     // horizontal unit vector downwind
        public float windLow, windHigh;   // m/s near the ground and at the cap
        public float shear;         // horizontal lean of the cap per metre of height
        public float capStretch, dustStretch;   // downwind elongation (0 = round)
        public float dissipation;   // 0 fresh .. 1 frayed and thinning

        // baked fluid simulation of the fireball
        public Texture simA, simB;
        public float simBlend, simMix, simFire, simDensity;
        public Vector3 simOrigin, simSize;

        // the planet, which hides the cloud from orbit (scaled-space views leave no depth buffer)
        public Vector3 planetCenter; public float planetRadius;

        // skirt and base surge
        public Vector3 skirtCenter;
        public float skirtR, skirtH, skirtDensity, skirtThin, outflow;
        public float surgeR, surgeH, surgeWidth, surgeDensity;

        // condensation
        public float wilsonR, wilsonThick, wilsonDensity; public bool wilsonHemisphere;
        public float pileusOffset, pileusThick, pileusDensity;   // ice-cap veil over the rising cap
        public float velumRadius, velumThick, velumDensity, velumHeight;   // the vast condensation veil (humid air)
        public float precursorR, precursorH, precursorDensity, precursorWidth;   // thermal precursor dust ring
        public float machR, machH, machDensity, machWidth;   // Mach stem dust ring (air bursts), centred under the burst
        public float groundFire, groundFireR, groundFireH, groundFireTemp;      // fire burning on the ground
        public Vector3 plumeLow, plumeMid, plumeTop;   // fallout plume (low downwind end, bend, top under the cap)
        public float plumeLowR, plumeTopR, plumeDensity;
        public Vector4 ring0, ring1, ring2;   // y, radius, tube, density
        public Vector3 ringOffset;
        public Vector4 collar0, collar1, collar2, collar3, collar4;          // xyz centre, w density
        public Vector4 collarS0, collarS1, collarS2, collarS3, collarS4;     // radius, thickness, droop

        // motion
        public float time, rollRate, flowPeriod, noiseScale;

        // fire
        public float fire, fireTemp, flicker, soot;
        public float ember, emberTemp, emberWidth;   // the burning ring inside the cap's vortex
        public Color glow; public float glowRadius;

        // light and air
        public Vector3 sunDir; public float daylight;
        public Color sunCol; public float sunVisCap, sunVisLow;
        public Color ambient, groundCol;
        public Color capAlbedo, stemAlbedo, dustAlbedo, condAlbedo;
        public Color hazeCol; public float haze;
    }

    public static class NukeVolumeShader
    {
        static readonly Vector4[] rings = new Vector4[3];
        static readonly Vector4[] collarPos = new Vector4[5];
        static readonly Vector4[] collarShape = new Vector4[5];

        /// <summary>Box (local) that holds everything with density in this state.</summary>
        public static void Bounds(ref NukeVolumeState v, out Vector3 min, out Vector3 max)
        {
            float cs = Mathf.Max(1f, v.capScale);
            float capHalfW = (v.ringR + v.tube + 0.45f) * v.xzSpread * cs * 1.15f;
            float capHalfH = (Mathf.Max(v.tube, v.dome) * v.squash + 0.6f) * cs;
            min = v.capCenter - new Vector3(capHalfW, capHalfH, capHalfW);
            max = v.capCenter + new Vector3(capHalfW, capHalfH + 0.2f * cs, capHalfW);
            if (v.stemDensity > 0.001f)
            {
                float r = Mathf.Max(v.stemBaseR * (1f + v.stemFlare), v.stemTopR) * 1.5f;
                min = Vector3.Min(min, Vector3.Min(v.stemBase, v.stemTop) - new Vector3(r, 0f, r));
                max = Vector3.Max(max, Vector3.Max(v.stemBase, v.stemTop) + new Vector3(r, 0f, r));
            }
            if (v.skirtDensity > 0.001f || v.surgeDensity > 0.001f)
            {
                float r = Mathf.Max(v.skirtR * 1.4f, v.surgeR + v.surgeWidth * 1.5f);
                float h = Mathf.Max(v.skirtH * 1.7f, v.surgeH * 1.5f);
                min = Vector3.Min(min, v.skirtCenter - new Vector3(r, 0f, r));
                max = Vector3.Max(max, v.skirtCenter + new Vector3(r, h, r));
            }
            if (v.wilsonDensity > 0.001f)
            {
                float r = v.wilsonR + v.wilsonThick;
                min = Vector3.Min(min, new Vector3(-r, v.wilsonHemisphere ? v.groundY : -r, -r));
                max = Vector3.Max(max, new Vector3(r, r, r));
            }
            RingBounds(v.ring0, v.ringOffset, ref min, ref max);
            RingBounds(v.ring1, v.ringOffset, ref min, ref max);
            RingBounds(v.ring2, v.ringOffset, ref min, ref max);
            // condensation collars round the stem (centre xyz, radius, thickness, droop)
            CollarBounds(v.collar0, v.collarS0, ref min, ref max);
            CollarBounds(v.collar1, v.collarS1, ref min, ref max);
            CollarBounds(v.collar2, v.collarS2, ref min, ref max);
            CollarBounds(v.collar3, v.collarS3, ref min, ref max);
            CollarBounds(v.collar4, v.collarS4, ref min, ref max);
            if (v.precursorDensity > 0.001f)
            {
                float r = v.precursorR + v.precursorWidth;
                min = Vector3.Min(min, v.skirtCenter + new Vector3(-r, 0f, -r));
                max = Vector3.Max(max, v.skirtCenter + new Vector3(r, v.precursorH * 2f, r));
            }
            if (v.machDensity > 0.001f)
            {
                float r = v.machR + 0.5f * v.machWidth;
                min = Vector3.Min(min, new Vector3(-r, v.groundY, -r));
                max = Vector3.Max(max, new Vector3(r, v.groundY + v.machH * 2f, r));
            }
            if (v.groundFire > 0.001f)
            {
                float r = v.groundFireR * 1.3f;
                min = Vector3.Min(min, v.skirtCenter + new Vector3(-r, 0f, -r));
                max = Vector3.Max(max, v.skirtCenter + new Vector3(r, v.groundFireH * 1.6f, r));
            }
            if (v.plumeDensity > 0.001f)
            {
                float r = Mathf.Max(v.plumeLowR, v.plumeTopR) * 1.3f;
                Vector3 e = new Vector3(r, r * 0.2f, r);
                min = Vector3.Min(min, Vector3.Min(v.plumeLow, Vector3.Min(v.plumeMid, v.plumeTop)) - e);
                max = Vector3.Max(max, Vector3.Max(v.plumeLow, Vector3.Max(v.plumeMid, v.plumeTop)) + e);
            }
            if (v.velumDensity > 0.001f)
            {
                Vector3 vc = v.capCenter + new Vector3(0f, v.velumHeight, 0f);
                Vector3 ve = new Vector3(v.velumRadius * 1.2f, v.velumThick * 2.5f, v.velumRadius * 1.2f);
                min = Vector3.Min(min, vc - ve - new Vector3(0f, 0.3f * v.velumRadius, 0f));   // the drooping rim
                max = Vector3.Max(max, vc + ve);
            }
            if (v.simMix > 0.001f && v.simA != null)
            {
                min = Vector3.Min(min, v.simOrigin);
                max = Vector3.Max(max, v.simOrigin + v.simSize);
            }
            // the downwind stretch reaches beyond the round shapes
            Vector3 down = new Vector3(v.windDir.x, 0f, v.windDir.z);
            float capReach = capHalfW * (v.capStretch + v.shear * 1.5f);
            Vector3 capFar = v.capCenter + down * capReach;
            min = Vector3.Min(min, capFar - new Vector3(capHalfW, capHalfH, capHalfW) * 0.5f);
            max = Vector3.Max(max, capFar + new Vector3(capHalfW, capHalfH, capHalfW) * 0.5f);
            if (v.skirtDensity > 0.001f || v.surgeDensity > 0.001f)
            {
                Vector3 dustFar = v.skirtCenter + down * (Mathf.Max(v.skirtR, v.surgeR) * v.dustStretch);
                min = Vector3.Min(min, dustFar - new Vector3(v.skirtR, 0f, v.skirtR));
                max = Vector3.Max(max, dustFar + new Vector3(v.skirtR, v.skirtH * 1.7f, v.skirtR));
            }
            if (v.stemDensity > 0.001f)
            {
                float r = Mathf.Max(v.stemBaseR * (1f + v.stemFlare), v.stemTopR) * 1.5f;
                min = Vector3.Min(min, v.stemMid - new Vector3(r, 0f, r));
                max = Vector3.Max(max, v.stemMid + new Vector3(r, 0f, r));
            }
            min.y = Mathf.Min(min.y, v.groundY - 30f);
        }

        static void RingBounds(Vector4 rg, Vector3 offset, ref Vector3 min, ref Vector3 max)
        {
            if (rg.w <= 0.001f) return;
            float r = rg.y + rg.z * 1.5f;
            min = Vector3.Min(min, offset + new Vector3(-r, rg.x - rg.z * 2f, -r));
            max = Vector3.Max(max, offset + new Vector3(r, rg.x + rg.z * 2f, r));
        }

        static void CollarBounds(Vector4 c, Vector4 shape, ref Vector3 min, ref Vector3 max)
        {
            if (c.w <= 0.001f) return;
            float r = shape.x + shape.y * 1.5f;
            float h = shape.y * 2f;
            Vector3 ctr = new Vector3(c.x, c.y, c.z);
            min = Vector3.Min(min, ctr - new Vector3(r, h + Mathf.Abs(shape.z), r));
            max = Vector3.Max(max, ctr + new Vector3(r, h, r));
        }

        /// <summary>
        /// Box (local) the cloud's shadow can fall in: the cloud's own box plus its footprint on the ground along
        /// the sun (capped for a low sun, when the shadow would stretch out to the horizon).
        /// </summary>
        public static void ShadowBounds(ref NukeVolumeState v, Vector3 min, Vector3 max, out Vector3 smin, out Vector3 smax)
        {
            Vector3 sun = v.sunDir.sqrMagnitude > 1e-6f ? v.sunDir.normalized : Vector3.up;
            float h = Mathf.Max(1f, max.y - v.groundY);
            float reach = Mathf.Min(h / Mathf.Max(0.12f, sun.y), 6f * h);
            Vector3 shift = new Vector3(-sun.x, 0f, -sun.z) / Mathf.Max(1e-3f, new Vector2(sun.x, sun.z).magnitude + 1e-3f) * reach * new Vector2(sun.x, sun.z).magnitude / Mathf.Max(1e-3f, Mathf.Sqrt(1f - Mathf.Min(0.9999f, sun.y * sun.y)));
            smin = Vector3.Min(min, min + shift);
            smax = Vector3.Max(max, max + shift);
            smin.y = v.groundY - 200f;
            smax.y = max.y;
        }

        public static void Apply(Material mat, ref NukeVolumeState v, Matrix4x4 worldToCloud, Vector3 min, Vector3 max, int steps, bool debugNoise = false)
        {
            float cs = Mathf.Max(1f, v.capScale);
            mat.SetMatrix("_WorldToCloud", worldToCloud);
            mat.SetVector("_BoxMin", min);
            mat.SetVector("_BoxMax", max);
            mat.SetVector("_CapCenter", new Vector4(v.capCenter.x, v.capCenter.y, v.capCenter.z, cs));
            mat.SetVector("_CapShape", new Vector4(v.ringR, v.tube, v.dome, v.squash));
            mat.SetVector("_CapXZ", new Vector4(v.xzSpread, v.overhang, v.roughness, v.growthRate));
            mat.SetVector("_StemBase", new Vector4(v.stemBase.x, v.stemBase.y, v.stemBase.z, v.stemBaseR));
            mat.SetVector("_StemTop", new Vector4(v.stemTop.x, v.stemTop.y, v.stemTop.z, v.stemTopR));
            mat.SetVector("_StemVis", new Vector4(v.stemDustTop, v.stemTrailBottom, v.stemDensity, v.stemFlare));
            mat.SetVector("_SkirtCenter", new Vector4(v.skirtCenter.x, v.skirtCenter.y, v.skirtCenter.z, v.skirtR));
            mat.SetVector("_Skirt", new Vector4(v.skirtH, v.skirtDensity, v.skirtThin, v.outflow));
            mat.SetVector("_Surge", new Vector4(v.surgeR, v.surgeH, v.surgeDensity, v.surgeWidth));
            mat.SetVector("_Wilson", new Vector4(v.wilsonR, v.wilsonThick, v.wilsonDensity, v.wilsonHemisphere ? 1f : 0f));
            rings[0] = v.ring0; rings[1] = v.ring1; rings[2] = v.ring2;
            mat.SetVectorArray("_Rings", rings);
            mat.SetVector("_RingOffset", v.ringOffset);
            collarPos[0] = v.collar0; collarPos[1] = v.collar1; collarPos[2] = v.collar2; collarPos[3] = v.collar3; collarPos[4] = v.collar4;
            collarShape[0] = v.collarS0; collarShape[1] = v.collarS1; collarShape[2] = v.collarS2; collarShape[3] = v.collarS3; collarShape[4] = v.collarS4;
            mat.SetVectorArray("_CollarPos", collarPos);
            mat.SetVectorArray("_CollarShape", collarShape);
            mat.SetVector("_GroundY", new Vector4(v.groundY, 0f, 0f, 0f));
            Vector3 wd = new Vector3(v.windDir.x, 0f, v.windDir.z);
            wd = wd.sqrMagnitude > 1e-6f ? wd.normalized : Vector3.right;
            mat.SetVector("_Wind", new Vector4(wd.x, 0f, wd.z, v.shear));
            mat.SetVector("_WindFlow", new Vector4(v.windLow, v.windHigh, v.capStretch, v.dustStretch));
            mat.SetVector("_StemMid", v.stemMid);
            mat.SetVector("_Age", new Vector4(v.dissipation, 0f, 0f, 0f));
            mat.SetVector("_Ember", new Vector4(v.ember, v.emberTemp, v.emberWidth, 0f));
            mat.SetVector("_Planet", new Vector4(v.planetCenter.x, v.planetCenter.y, v.planetCenter.z, v.planetRadius));
            bool sim = v.simMix > 0.001f && v.simA != null && v.simB != null;
            if (sim) { mat.SetTexture("_SimA", v.simA); mat.SetTexture("_SimB", v.simB); }
            mat.SetVector("_Sim", new Vector4(v.simBlend, sim ? v.simMix : 0f, v.simFire, v.simDensity));
            mat.SetVector("_SimOrigin", v.simOrigin);
            mat.SetVector("_SimSize", v.simSize);
            mat.SetVector("_Pileus", new Vector4(v.pileusOffset, v.pileusThick, v.pileusDensity, 0f));
            mat.SetVector("_Velum", new Vector4(v.velumRadius, v.velumThick, v.velumDensity, v.velumHeight));
            mat.SetVector("_GroundFire", new Vector4(v.groundFire, v.groundFireR, v.groundFireH, v.groundFireTemp));
            mat.SetVector("_Precursor", new Vector4(v.precursorR, v.precursorH, v.precursorDensity, v.precursorWidth));
            mat.SetVector("_MachStem", new Vector4(v.machR, v.machH, v.machDensity, v.machWidth));
            mat.SetVector("_PlumeA", new Vector4(v.plumeLow.x, v.plumeLow.y, v.plumeLow.z, v.plumeLowR));
            mat.SetVector("_PlumeM", new Vector4(v.plumeMid.x, v.plumeMid.y, v.plumeMid.z, v.plumeDensity));
            mat.SetVector("_PlumeB", new Vector4(v.plumeTop.x, v.plumeTop.y, v.plumeTop.z, v.plumeTopR));
            mat.SetVector("_Density", new Vector4(v.capDensity, v.extinction, 0.3f, v.noiseScale));
            mat.SetVector("_Anim", new Vector4(v.time, v.rollRate, v.updraft, Mathf.Max(0.5f, v.flowPeriod)));
            mat.SetVector("_Fire", new Vector4(v.fire, v.fireTemp, v.flicker, v.soot));
            mat.SetVector("_Glow", new Vector4(v.glow.r, v.glow.g, v.glow.b, v.glowRadius));
            mat.SetVector("_SunDir", new Vector4(v.sunDir.x, v.sunDir.y, v.sunDir.z, v.daylight));
            mat.SetVector("_SunCol", new Vector4(v.sunCol.r, v.sunCol.g, v.sunCol.b, v.sunVisCap));
            mat.SetVector("_SunLow", new Vector4(v.sunVisLow, 0f, 0f, 0f));
            mat.SetVector("_Ambient", v.ambient);
            mat.SetVector("_GroundCol", v.groundCol);
            mat.SetVector("_CapAlbedo", v.capAlbedo);
            mat.SetVector("_StemAlbedo", v.stemAlbedo);
            mat.SetVector("_DustAlbedo", v.dustAlbedo);
            mat.SetVector("_CondAlbedo", v.condAlbedo);
            mat.SetVector("_HazeCol", new Vector4(v.hazeCol.r, v.hazeCol.g, v.hazeCol.b, v.haze));
            mat.SetVector("_March", new Vector4(steps, 4f, 1f, debugNoise ? 1f : 0f));
        }

        /// <summary>Anything left to draw in this state.</summary>
        public static bool Visible(ref NukeVolumeState v) =>
            v.simMix > 0.003f || v.plumeDensity > 0.003f || v.precursorDensity > 0.003f || v.machDensity > 0.003f || v.groundFire > 0.003f || v.capDensity > 0.003f || v.stemDensity > 0.003f || v.skirtDensity > 0.003f || v.surgeDensity > 0.003f ||
            v.wilsonDensity > 0.003f || v.pileusDensity > 0.003f || v.velumDensity > 0.003f || v.ring0.w + v.ring1.w + v.ring2.w > 0.003f ||
            v.collar0.w + v.collar1.w + v.collar2.w + v.collar3.w + v.collar4.w > 0.003f;
    }

    /// <summary>
    /// A standalone timeline of a surface burst, used by the test renders (KSP drives the state from
    /// NukeCinematic instead). Mirrors the cinematic's main curves: fireball growth and cooling, the rise,
    /// the ball-to-torus morph, the stem, skirt, surge, Wilson cloud and condensation rings.
    /// </summary>
    public static class NukeVolumeTestTimeline
    {
        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        public static NukeVolumeState At(float W, float t, float sunVis, float haze, float bulb = 0f, float humidity = 0.7f)
        {
            float R = 70f * Mathf.Pow(W, 0.4f);
            float tauG = 2.5f + 2.2f * Mathf.Pow(W, 0.3f);
            float riseTime = 90f;
            float tauRise = riseTime / 2.3f;
            float t0 = 0.4f * tauG;
            float H = 5800f * Mathf.Pow(W, 0.26f) * 0.6f;
            float riseDelta = Mathf.Max(2f * R, H * 0.85f);
            float capRadius = Mathf.Max(1.6f * R, 0.28f * H);
            float rise = 1f - Mathf.Exp(-Mathf.Max(0f, t - t0) / tauRise);
            float morph = SS(0.15f * tauG, 2f * tauG + 4f, t);
            float grow = 1f - Mathf.Exp(-t / 0.15f);
            float x = t / tauG;
            float c = 420f;
            var v = new NukeVolumeState();
            v.groundY = 0f;
            v.time = t;
            float capScale = R * (1f + (capRadius / R - 1f) * rise) * Mathf.Lerp(Mathf.Max(0.05f, grow), 1f, morph);
            // wind: 8 m/s near the ground, 16 m/s aloft, blowing along +x; the cap picks it up over its rise
            float windLow = 8f, windHigh = 16f;
            float lag = 0.45f * riseTime;
            float tl = t - lag * (1f - Mathf.Exp(-t / lag));
            Vector3 drift = new Vector3(windHigh * tl, 0f, 0f);
            v.windDir = Vector3.right;
            v.windLow = windLow; v.windHigh = windHigh;
            float capH = R * 0.9f + riseDelta * rise;
            v.shear = Mathf.Clamp((windHigh - windLow) * t / Mathf.Max(capH, 1f) * 0.12f, 0f, 0.5f);
            v.capStretch = Mathf.Clamp((windHigh - windLow * 0.5f) * Mathf.Max(0f, t - 0.3f * riseTime) * 0.35f / Mathf.Max(capRadius, 1f), 0f, 0.8f);
            v.dustStretch = Mathf.Clamp(windLow * t * 0.5f / Mathf.Max(R * 3f, 1f), 0f, 2f);
            v.dissipation = SS(2f * riseTime, 5f * riseTime, t);
            v.capCenter = new Vector3(0f, R * 0.9f + riseDelta * rise, 0f) + drift;
            v.capScale = capScale;
            float column = NukeVolumeShapes.Column(W, 0f);
            bulb = Mathf.Max(bulb, column);
            NukeVolumeShapes.Cap(morph, 0f, 1f, bulb, out v.ringR, out v.tube, out v.dome, out v.squash);
            v.squash *= Mathf.Lerp(1f, 1.35f, column * morph);
            v.xzSpread = Mathf.Lerp(1f, 0.65f, column);
            v.overhang = 0.45f * morph;
            v.roughness = SS(0.05f * tauG, 0.6f * tauG, t);
            v.growthRate = Mathf.Clamp((capRadius / R - 1f) * Mathf.Exp(-Mathf.Max(0f, t - t0) / tauRise) / tauRise / Mathf.Max(1f, capScale / R), 0f, 0.5f) + (t < 1f ? 1f : 0f);
            v.capDensity = 1f;
            v.extinction = 2f / R;
            v.rollRate = 0.35f * Mathf.Exp(-t / tauRise) + 0.03f;
            v.flowPeriod = 6f;
            v.noiseScale = Mathf.Lerp(1f, 1.8f, SS(2.5f, 4.5f, Mathf.Log10(Mathf.Max(W, 1f)))) / (0.5f * Mathf.Max(capRadius, 3f * R));
            // optically thick: each billow goes opaque within a fraction of its own size, so the light stops at the
            // first lumps instead of showing several layers of them blurred together (megaton caps were translucent mush)
            v.extinction = Mathf.Max(2f / R, 20f * v.noiseScale);

            float start = 0.25f * tauG;
            if (t > start)
            {
                float yTop = Mathf.Max(R, v.capCenter.y - 0.35f * capScale);
                float trailLen = yTop * (0.25f + 0.45f * SS(start, start + 10f, t));
                float dustFront = yTop * Mathf.Lerp(0.3f, 1.05f, SS(start, start + 0.08f * riseTime + 4f, t));
                v.stemBase = new Vector3(windLow * t * 0.3f, 0f, 0f);
                v.stemTop = new Vector3(0f, yTop, 0f) + drift;
                // the low stem stands nearly upright in the weak low wind and bends over higher up
                v.stemMid = new Vector3(Mathf.Lerp(v.stemBase.x, v.stemTop.x, 0.1f), 0.5f * yTop, 0f);
                float stemR = 0.17f * capScale * Mathf.Lerp(1f, 0.72f, bulb) * Mathf.Lerp(1f, 3.4f, column);
                v.stemBaseR = stemR * Mathf.Lerp(1.1f, 0.8f, column);
                v.stemTopR = stemR;
                v.stemFlare = Mathf.Lerp(1.2f, 0.6f, column);
                v.stemDustTop = dustFront;
                v.stemTrailBottom = yTop - trailLen;
                v.stemDensity = 0.9f * SS(start, start + 2f, t);
                v.updraft = 60f * Mathf.Exp(-t / riseTime) + 10f;
            }
            float tt = t;
            v.skirtCenter = new Vector3(windLow * t * 0.3f, 0f, 0f);
            v.skirtR = R * (0.8f + 1.0f * (1f - Mathf.Exp(-tt / 8f)));
            v.skirtH = R * (0.6f + 1.0f * (1f - Mathf.Exp(-tt / 8f)));
            {
                float growK = 1f - Mathf.Exp(-tt / 10f);
                v.skirtR = Mathf.Lerp(v.skirtR, (0.4f + 1.2f * growK) * capRadius, column);
                v.skirtH = Mathf.Lerp(v.skirtH, (0.08f + 0.1f * growK) * capRadius, column);
            }
            v.skirtDensity = Mathf.Lerp(0.75f, 0.16f, column) * SS(0.2f, 1.2f, t);
            v.skirtThin = 0.35f;
            v.outflow = 30f * Mathf.Exp(-t / 20f) + 3f;
            float rs = R + c * t;
            float surgeMax = R * 6f;
            v.surgeR = Mathf.Min(rs, surgeMax);
            v.surgeH = 0.5f * R * (1f + 0.04f * t);
            v.surgeWidth = 0.8f * R + 0.04f * v.surgeR;
            float surgeEnd = surgeMax / c + 4f;
            v.surgeDensity = 0.14f * SS(0.3f, 1f, t) * (1f - SS(surgeEnd, surgeEnd + 25f, t));

            float tW = Mathf.Min(2.5f * R / c, 4f);
            v.wilsonDensity = (1f - column) * 0.3f * SS(0.03f, 0.2f * tW, t) * (1f - SS(0.5f * tW, 1.3f * tW, t));
            v.wilsonR = Mathf.Min(R * 1.2f + c * t * 0.9f, 3.6f * R);
            v.wilsonThick = 0.3f * R;
            v.wilsonHemisphere = true;

            Vector4 Ring(int k)
            {
                float y0 = R * (1.25f + 0.75f * k);
                float tk = y0 / c * 0.6f + 0.2f + 0.15f * k;
                float hold = 3f + 0.5f * tauG;
                float vis = SS(tk, tk + 0.6f, t) * (1f - SS(tk + hold, tk + hold + 4f + 0.5f * tauG, t));
                float rk = R * (1.5f + 0.32f * k) * (1f + 0.015f * t) * (1f + 0.4f * rise);
                return new Vector4(y0 + 0.4f * riseDelta * rise, rk, 0.3f * R, 0.3f * vis);
            }
            v.ring0 = Ring(0); v.ring1 = Ring(1); v.ring2 = Ring(2);
            v.ring0.w *= 1f - column; v.ring1.w *= 1f - column; v.ring2.w *= 1f - column;
            NukeVolumeGround.GroundFire(R, t, tauG, 1f, out v.groundFire, out v.groundFireR, out v.groundFireH, out v.groundFireTemp);
            NukeVolumeGround.Precursor(R, t, Mathf.Min(rs, surgeMax * 1.5f), 1f, out v.precursorR, out v.precursorH, out v.precursorDensity, out v.precursorWidth);
            NukeVolumeGround.Plume(t, riseTime, 0.8f * NukeVolumeShapes.PlumeYield(W), v.capCenter, capScale, 0f, v.windDir, windLow,
                out v.plumeLow, out v.plumeMid, out v.plumeTop, out v.plumeLowR, out v.plumeTopR, out v.plumeDensity);
            v.pileusOffset = 0.12f * capScale;
            v.pileusThick = 0.08f * capScale;
            NukeVolumeShapes.Velum(W, t, tauG, riseTime, humidity, 0f, capScale, v.ringR + v.tube, out v.velumRadius, out v.velumThick, out v.velumDensity, out v.velumHeight);
            v.velumDensity *= 1f - column;
            v.pileusDensity = (1f - column) * 0.9f * SS(0.4f * tauG, 1.2f * tauG, t) * (1f - SS(0.25f * riseTime, 0.55f * riseTime, t));
            v.ringOffset = drift * 0.8f;

            float heatK = (t < 0.5f ? 1.3f : 1f) * Mathf.Exp(-Mathf.Max(0f, x - 0.35f) / 0.85f);
            v.fire = 4f * heatK;
            v.fireTemp = Mathf.Clamp01(1.05f - 0.75f * x);
            v.flicker = Mathf.Lerp(0.25f, 1f, v.roughness);
            v.soot = Mathf.Lerp(1f, 0.25f, column) * 0.8f * SS(0.3f * tauG, 1.2f * tauG, t) * (1f - SS(3f * tauG, 6f * tauG, t));
            float glow = Mathf.Exp(-t / tauG);
            v.ember = 3.2f * Mathf.Exp(-t / (3f * tauG + 15f)) * SS(0.2f * tauG, 1.0f * tauG, t);
            v.emberTemp = 0.64f;
            v.emberWidth = 0.6f;
            v.glow = new Color(1f, 0.52f, 0.16f) * (2.5f * glow + 0.9f * v.ember);
            v.glowRadius = 0.9f * capScale;

            v.sunDir = new Vector3(-0.5f, 0.6f, 0.45f).normalized;
            v.daylight = 1f;
            v.sunCol = new Color(1.0f, 0.97f, 0.92f);
            v.sunVisCap = sunVis; v.sunVisLow = sunVis;
            v.ambient = Color.Lerp(new Color(0.46f, 0.55f, 0.7f), new Color(0.55f, 0.57f, 0.6f), 1f - sunVis);
            v.groundCol = new Color(0.16f, 0.15f, 0.11f) * Mathf.Lerp(0.4f, 1f, sunVis);
            float young = 1f - SS(0.5f * riseTime, 2f * riseTime, t);
            v.capAlbedo = Color.Lerp(new Color(0.5f, 0.47f, 0.44f), new Color(0.42f, 0.31f, 0.24f), young);
            v.capAlbedo = Color.Lerp(v.capAlbedo, new Color(0.80f, 0.70f, 0.56f), 0.75f * column);
            v.stemAlbedo = new Color(0.46f, 0.4f, 0.34f);
            v.dustAlbedo = new Color(0.62f, 0.6f, 0.56f);
            v.condAlbedo = new Color(0.95f, 0.96f, 0.98f);
            v.hazeCol = Color.Lerp(new Color(0.62f, 0.7f, 0.8f), new Color(0.56f, 0.58f, 0.61f), 1f - sunVis);
            v.haze = haze;
            return v;
        }
    }

    /// <summary>
    /// Reduced-resolution rendering: the volumes are raymarched into a low-res buffer (camera size / divisor) and
    /// composited over the scene by BDNukes/VolumeComposite with a depth-aware upsample. The buffer runs on each
    /// camera at AfterForwardAlpha; the low-res target is sized to whichever camera executes it.
    /// </summary>
    public static class NukeVolumeCompositor
    {
        static readonly List<Material> occluderMatScratch = new List<Material>();

        public struct Item
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
            public Matrix4x4 shadowMatrix;   // the shadow box, drawn at full resolution before the cloud
            public Material shadowMaterial;  // null: no shadow
        }

        /// <summary>A refracting shock shell: drawn over a copy of what the camera has drawn so far.</summary>
        public struct Shock
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public Material material;
        }

        // ---- fireball lighting ----
        /// <summary>A fireball lighting the scene around it (world space, the frame's floating origin).</summary>
        public struct FireLight
        {
            public Vector3 position;
            public float core;     // m: soft core radius of the inverse-square falloff (about the fireball's radius)
            public float range;    // m: the light is windowed smoothly to zero here
            public Color color;    // blackbody colour times strength (the strength at the core)
        }
        // ---- end fireball lighting ----

        static readonly int LowRes = Shader.PropertyToID("_BDNukesVolumeLowRes");
        static readonly int PartDepth = Shader.PropertyToID("_BDNukesPartDepth");
        static readonly int PartDepthOn = Shader.PropertyToID("_BDNukesPartDepthOn");
        static readonly int Grab = Shader.PropertyToID("_BDNukesShockGrab");
        static readonly List<Shock> noShocks = new List<Shock>();

        /// <summary>
        /// Everything BDNukes draws volumetrically goes through this buffer, run only by the flight cameras, so other
        /// mods' cameras (depth and shadow-map passes, reflection probes) never draw it. Order: ground shadows at full
        /// resolution, then the cloud (directly, or at 1/divisor resolution and composited), then the shock shells
        /// over a copy of the result.
        /// </summary>
        public static void Build(CommandBuffer cb, List<Item> items, Material composite, int divisor, List<Shock> shocks = null,
            List<FireLight> fireLights = null, Material fireLightMat = null, List<Renderer> occluders = null, Material occluderMat = null)
        {
            cb.Clear();
            shocks = shocks ?? noShocks;
            // ---- fireball lighting ----
            bool lit = fireLights != null && fireLights.Count > 0 && fireLightMat != null;
            // ---- end fireball lighting ----
            if (items.Count == 0 && shocks.Count == 0 && !lit) return;
            int d = composite != null ? Mathf.Clamp(divisor, 1, 4) : 1;
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            foreach (var it in items)
                if (it.mesh != null && it.shadowMaterial != null) cb.DrawMesh(it.mesh, it.shadowMatrix, it.shadowMaterial, 0, 0);
            // ---- fireball lighting ----
            // after the cloud shadows (the fireball lights ground the cloud shades from the sun) and before the cloud,
            // which isn't in the depth buffer and lights itself
            if (lit)
            {
                NukeFireLighting.Apply(fireLightMat, fireLights);
                cb.DrawMesh(NukeFireLighting.ScreenQuad(), Matrix4x4.identity, fireLightMat, 0, 0);
            }
            // ---- end fireball lighting ----
            // the vessels' own depth: the cloud's march stops at it too, whatever shader the parts use
            bool parts = items.Count > 0 && occluders != null && occluders.Count > 0 && occluderMat != null;
            cb.SetGlobalFloat(PartDepthOn, parts ? 1f : 0f);
            if (parts)
            {
                cb.GetTemporaryRT(PartDepth, -1, -1, 24, FilterMode.Point, RenderTextureFormat.RFloat);
                cb.SetRenderTarget(PartDepth);
                cb.ClearRenderTarget(true, true, new Color(1e9f, 0f, 0f, 0f));
                foreach (var r in occluders)
                {
                    if (r == null) continue;
                    r.GetSharedMaterials(occluderMatScratch);   // no per-renderer array allocation
                    int subs = occluderMatScratch.Count;
                    for (int i = 0; i < Mathf.Max(1, subs); i++) cb.DrawRenderer(r, occluderMat, i, 0);
                }
                cb.SetGlobalTexture(PartDepth, PartDepth);
                cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            }
            if (items.Count > 0)
            {
                if (d <= 1)
                {
                    foreach (var it in items)
                        if (it.mesh != null && it.material != null) cb.DrawMesh(it.mesh, it.matrix, it.material, 0, 0);
                }
                else
                {
                    cb.GetTemporaryRT(LowRes, -d, -d, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                    cb.SetRenderTarget(LowRes);
                    cb.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));
                    foreach (var it in items)
                        if (it.mesh != null && it.material != null) cb.DrawMesh(it.mesh, it.matrix, it.material, 0, 0);
                    cb.Blit(LowRes, BuiltinRenderTextureType.CameraTarget, composite);
                    cb.ReleaseTemporaryRT(LowRes);
                }
            }
            if (parts) cb.ReleaseTemporaryRT(PartDepth);
            if (shocks.Count > 0)
            {
                cb.GetTemporaryRT(Grab, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.Default);
                cb.Blit(BuiltinRenderTextureType.CameraTarget, Grab);
                cb.SetGlobalTexture(Grab, Grab);
                cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                foreach (var sh in shocks)
                    if (sh.mesh != null && sh.material != null) cb.DrawMesh(sh.mesh, sh.matrix, sh.material, 0, 0);
                cb.ReleaseTemporaryRT(Grab);
            }
        }
    }

    // ---- fireball lighting ----
    /// <summary>
    /// The fireball lighting the world: its blackbody colour, and the uniforms of BDNukes/FireLight, a full-screen
    /// additive pass that rebuilds each pixel's world position from the camera depth and adds the fireballs' light.
    /// </summary>
    public static class NukeFireLighting
    {
        public const int MaxLights = 4;
        /// <summary>Most light the pass adds to one pixel (brightest channel), so a close fireball doesn't wash out to white.</summary>
        public const float PixelCap = 1.6f;
        /// <summary>Wrap lighting: surfaces turned somewhat away from the fireball still catch a little (light scattered by the air).</summary>
        public const float Wrap = 0.35f;

        static readonly Vector4[] pos = new Vector4[MaxLights];
        static readonly Vector4[] col = new Vector4[MaxLights];
        static readonly int PosId = Shader.PropertyToID("_FireLightPos");
        static readonly int ColId = Shader.PropertyToID("_FireLightCol");
        static readonly int ParamsId = Shader.PropertyToID("_FireLightParams");
        static Mesh quad;

        /// <summary>
        /// Colour of a black body at this temperature (K), brightest channel 1. A fit to the Planckian locus in sRGB
        /// (Tanner Helland's), good from ~1000 K to 40000 K: dull red at 1500 K, orange at 2500 K, near white at 6500 K.
        /// </summary>
        public static Color Blackbody(float kelvin)
        {
            float t = Mathf.Clamp(kelvin, 1000f, 40000f) / 100f;
            float r, g, b;
            if (t <= 66f)
            {
                r = 1f;
                g = Mathf.Clamp01((99.4708025861f * Mathf.Log(t) - 161.1195681661f) / 255f);
                b = t <= 19f ? 0f : Mathf.Clamp01((138.5177312231f * Mathf.Log(t - 10f) - 305.0447927307f) / 255f);
            }
            else
            {
                r = Mathf.Clamp01(329.698727446f * Mathf.Pow(t - 60f, -0.1332047592f) / 255f);
                g = Mathf.Clamp01(288.1221695283f * Mathf.Pow(t - 60f, -0.0755148492f) / 255f);
                b = 1f;
            }
            return new Color(r, g, b);
        }

        /// <summary>Sets the pass's uniforms: up to MaxLights fireballs, the strongest first if there are more.</summary>
        public static void Apply(Material mat, List<NukeVolumeCompositor.FireLight> lights)
        {
            if (lights.Count > MaxLights) lights.Sort((a, b) => Strength(b).CompareTo(Strength(a)));
            int n = Mathf.Min(lights.Count, MaxLights);
            for (int i = 0; i < MaxLights; i++)
            {
                if (i < n)
                {
                    var l = lights[i];
                    float core = Mathf.Max(1f, l.core);
                    pos[i] = new Vector4(l.position.x, l.position.y, l.position.z, core);
                    col[i] = new Vector4(l.color.r, l.color.g, l.color.b, Mathf.Max(1.5f * core, l.range));
                }
                else
                {
                    pos[i] = new Vector4(0f, 0f, 0f, 1f);
                    col[i] = new Vector4(0f, 0f, 0f, 1f);
                }
            }
            mat.SetVectorArray(PosId, pos);
            mat.SetVectorArray(ColId, col);
            mat.SetVector(ParamsId, new Vector4(n, PixelCap, Wrap, 0f));
        }

        static float Strength(NukeVolumeCompositor.FireLight l) => Mathf.Max(l.color.r, Mathf.Max(l.color.g, l.color.b)) * l.range;

        /// <summary>A quad covering clip space (-1..1): the shader puts it on screen itself and ignores the matrices.</summary>
        public static Mesh ScreenQuad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "BDNukesFireLightQuad" };
            quad.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
            return quad;
        }
    }
    // ---- end fireball lighting ----

    /// <summary>Mushroom shapes and the condensation veil, shared by the plugin and the test timeline.</summary>
    public static class NukeVolumeShapes
    {
        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        /// <summary>
        /// Cap shape at ball-to-torus morph m. bulb 0 = the wide rolling torus, 1 = a tight, tall, bulbous head (a
        /// smaller ring, a fatter tube and dome, taller). pancake/aspect: the profile's tropopause spreading and
        /// cap aspect (1 = none).
        /// </summary>
        /// <summary>
        /// 0 .. 1: how much the cloud is a rising column of billows rather than a mushroom. Sub-kiloton bursts (the
        /// Davy Crockett) never punch a wide cap: the fireball's heat lifts a lumpy, cauliflower column nearly as wide
        /// as its head. W in kt; lowYield is the profile's own small-yield factor.
        /// </summary>
        public static float Column(float W, float lowYield)
        {
            float lg = Mathf.Log10(Mathf.Max(W, 1e-4f));
            float k = Mathf.Clamp01((0.7f - lg) / 2f);
            return Mathf.Max(lowYield, k * k * (3f - 2f * k));
        }

        /// <summary>0 .. 1: a fallout plume visible below the cap only from tens of kilotons up.</summary>
        public static float PlumeYield(float W)
        {
            float k = Mathf.Clamp01((Mathf.Log10(Mathf.Max(W, 1e-4f)) - 1.5f) / 1f);
            return k * k * (3f - 2f * k);
        }

        public static void Cap(float m, float pancake, float aspect, float bulb, out float ringR, out float tube, out float dome, out float squash)
        {
            ringR = Mathf.Lerp(0.05f, Mathf.Lerp(0.62f, 0.34f, bulb) * Mathf.Lerp(1f, 0.62f, pancake), m);
            tube = Mathf.Lerp(0.3f, Mathf.Lerp(0.48f, 0.56f, bulb) * (1f + 0.45f * pancake), m);
            dome = Mathf.Lerp(0.85f, Mathf.Lerp(0.4f, 0.66f, bulb), m);
            float sq = Mathf.Clamp(0.78f * aspect * Mathf.Lerp(1f, 1.4f, bulb), 0.62f, Mathf.Lerp(0.9f, 1.15f, bulb));
            squash = Mathf.Lerp(1f, sq, m);
        }

        /// <summary>
        /// The velum: a vast lens of condensation over the rising fireball where the air is humid (more of it under
        /// an overcast), several cap widths across for big yields, forming as the fireball rises and evaporating.
        /// </summary>
        public static void Velum(float W, float t, float tauG, float riseTime, float humidity, float overcast,
            float capScale, float capWidth, out float radius, out float thick, out float density, out float height)
        {
            float humid = SS(0.2f, 0.65f, humidity) * (0.7f + 0.6f * overcast);
            float big = SS(1f, 3f, Mathf.Log10(Mathf.Max(W, 0.01f)));     // 10 kt .. 1 Mt
            float life = SS(0.3f * tauG, 1.0f * tauG, t) * (1f - SS(0.45f * riseTime, 1.0f * riseTime, t));
            radius = Mathf.Min(capScale * capWidth * Mathf.Lerp(1.8f, 2.6f, big) * (0.75f + 0.25f * SS(0.3f * tauG, 3f * tauG, t)), 14000f);
            thick = Mathf.Min(0.2f * capScale, 0.06f * radius);
            height = (0.75f + 0.2f * (1f - life)) * capScale;
            density = 0.12f * humid * life * Mathf.Lerp(0.5f, 1f, big);   // translucent: a veil, not a plate
        }
    }

    /// <summary>The thermal precursor and the fallout plume, shared by the plugin and the test timeline.</summary>
    public static class NukeVolumeGround
    {
        static float SS(float a, float b, float x)
        {
            float k = Mathf.Clamp01((x - a) / (b - a));
            return k * k * (3f - 2f * k);
        }

        /// <summary>
        /// Thermal precursor: for a low burst over land, a hot dusty layer boils off the ground under the flash and runs
        /// out ahead of the shock for the first seconds. shockGround = the shock's radius along the ground now.
        /// </summary>
        public static void Precursor(float R, float t, float shockGround, float groundFactor, out float radius, out float height, out float density, out float width)
        {
            radius = shockGround * 1.04f;
            height = 0.12f * R;
            width = 0.35f * R + 0.05f * shockGround;
            density = 0.35f * groundFactor * SS(0.1f, 0.4f, t) * (1f - SS(0.4f * R / 100f + 1.5f, 0.4f * R / 100f + 4f, t));
        }

        /// <summary>
        /// Mach stem: in an air burst the shock reflects off the ground, and beyond about one burst height the
        /// reflected shock catches the incident one and merges into a near-vertical Mach stem that runs out along the
        /// surface. It raises a thin, sharp-edged ring of dust ahead of the later base surge. hb = burst height (m),
        /// shockR = the shock's radius from the burst now. Fades over ~15 s.
        /// </summary>
        public static void MachStem(float R, float hb, float shockR, float c, out float radius, out float height, out float density, out float width)
        {
            radius = Mathf.Min(Mathf.Sqrt(Mathf.Max(0f, shockR * shockR - hb * hb)), 60f * R);
            // grows a little taller as the stem builds up beyond the regular-reflection zone
            height = R * (0.15f + 0.15f * SS(0.5f * hb, 3f * hb + R, radius));
            width = 0.3f * R + 0.03f * radius;
            density = 0f;
            if (shockR <= hb) return;
            float age = (shockR - hb) / Mathf.Max(c, 1f);   // s since the shock first touched the ground
            // only a burst high enough off the ground (0.5 R) to reflect the shock, and low enough (~8 R) for it to stay strong
            float band = SS(0.4f * R, 0.7f * R, hb) * (1f - SS(6f * R, 9f * R, hb));
            // weak while the reflection is still regular, the stem proper from about one burst height out
            float stem = Mathf.Lerp(0.45f, 1f, SS(0.4f * hb, 1.2f * hb, radius));
            density = 0.4f * band * stem * SS(0f, 0.25f, age) * (1f - SS(5f, 16f, age));
        }

        /// <summary>
        /// Ground fire: for a burst on or near land the ground under the fireball burns, brightest while the fireball
        /// sits on it, dying down over tens of seconds. groundFactor 0 (high or over water) .. 1 (contact burst).
        /// </summary>
        public static void GroundFire(float R, float t, float tauG, float groundFactor, out float brightness, out float radius, out float height, out float temp)
        {
            float life = 3f * tauG + 8f;
            float k = SS(0.15f * tauG, 0.6f * tauG, t) * (1f - SS(0.4f * life, life, t));
            brightness = 4f * groundFactor * k;
            radius = R * (1.1f + 0.5f * SS(0f, life, t));
            height = R * Mathf.Lerp(0.55f, 0.3f, SS(0f, life, t));
            temp = Mathf.Lerp(0.85f, 0.4f, SS(0.3f * tauG, life, t));
        }

        /// <summary>
        /// Fallout plume: dust falling out of the cap's underside and drifting downwind, from the cap down to the
        /// ground well downwind. dirt 0 (clean airburst) .. 1 (dirty surface burst).
        /// </summary>
        public static void Plume(float t, float riseTime, float dirt, Vector3 capCenter, float capScale, float groundY,
            Vector3 windDir, float windLow, out Vector3 low, out Vector3 mid, out Vector3 top, out float lowR, out float topR, out float density)
        {
            var down = new Vector3(windDir.x, 0f, windDir.z).normalized;
            float age = Mathf.Max(0f, t - 0.4f * riseTime);
            float reach = Mathf.Min(windLow * age * 1.4f + 0.5f * capScale, 12f * capScale);
            top = capCenter + new Vector3(0f, -0.35f * capScale, 0f) + down * (0.25f * capScale);
            low = new Vector3(capCenter.x, groundY, capCenter.z) + down * reach;
            mid = Vector3.Lerp(low, top, 0.5f) + down * (0.15f * reach);
            topR = 0.55f * capScale;
            lowR = 0.8f * capScale + 0.1f * reach;
            density = 0.2f * dirt * SS(0.5f * riseTime, 1.2f * riseTime, t);
        }
    }
}
