using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Lightning in megaton clouds. Big shots (Ivy Mike, Castle Bravo) showed bright discharges in and under the
    /// cloud during the first minute: the rising column carries charge separated by the intense ionisation and
    /// the violent updraft, a bit like a thunderstorm. Here a few jagged bolts flicker across the cap's
    /// underside, from the cap down into the stem and along the stem, each with a quick bluish flash that lights
    /// the cloud around it. It's kept occasional: a strike every few seconds, more for bigger yields and humid air.
    /// </summary>
    internal partial class NukeCinematic
    {
        const int BoltPool = 3;
        const int BoltPoints = 33;  // 2^5 + 1 points per bolt (midpoint displacement)

        bool lightningFx;
        float lightningRate;        // strikes per second at the start of the window
        float lightningStart, lightningEnd;
        float nextStrike;
        Layer boltGlow;
        LineRenderer[] bolts;
        float[] boltStart, boltLen, boltGain;
        Vector3[] boltMid;
        int[] boltStrokes;
        Light boltLight;
        readonly Vector3[] boltPts = new Vector3[BoltPoints];
        System.Random boltRng;

        void SetupLightning()
        {
            float hum = prof.humidity;
            // humid air lowers the threshold a little and makes strikes more frequent
            float minYield = s.lightningMinYield * Mathf.Lerp(1f, 0.7f, hum);
            lightningFx = airFx && s.lightning && W >= minYield && airW > 0.5f;
            if (!lightningFx) return;
            lightningRate = 0.22f * Mathf.Pow(W / 1000f, 0.3f) * (0.5f + hum);
            lightningStart = Mathf.Max(2f, 1.5f * tauG);
            lightningEnd = lightningStart + 60f * Mathf.Sqrt(yieldTimeScale);
        }

        void BuildLightning(int seed)
        {
            if (!lightningFx) return;
            boltRng = new System.Random(seed + 61);
            nextStrike = lightningStart + Rand() * 3f;
            bolts = new LineRenderer[BoltPool];
            boltStart = new float[BoltPool];
            boltLen = new float[BoltPool];
            boltGain = new float[BoltPool];
            boltMid = new Vector3[BoltPool];
            boltStrokes = new int[BoltPool];
            for (int k = 0; k < BoltPool; k++)
            {
                var go = new GameObject("LightningBolt" + k);
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = NukeAssets.Bolt;
                lr.useWorldSpace = false;
                lr.positionCount = BoltPoints;
                lr.textureMode = LineTextureMode.Stretch;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 0;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.7f, 0.7f), new Keyframe(1f, 0.25f));
                lr.enabled = false;
                bolts[k] = lr;
                boltStart[k] = -1000f;
            }
            var lightGo = new GameObject("LightningLight");
            lightGo.transform.SetParent(transform, false);
            boltLight = lightGo.AddComponent<Light>();
            boltLight.type = LightType.Point;
            boltLight.color = new Color(0.78f, 0.82f, 1f);
            boltLight.intensity = 0f;
            boltLight.shadows = LightShadows.None;
            boltLight.enabled = false;
        }

        float Rand() => (float)boltRng.NextDouble();

        /// <summary>Stem axis point at height fraction u, in the detonation's local frame.</summary>
        Vector3 StemPoint(float u) => new Vector3(0f, groundY + u * StemTop, 0f) + Lean(u);

        /// <summary>A point on the cap's underside: ring radius fraction f of the torus, at angle a.</summary>
        Vector3 CapUnderside(float f, float a)
        {
            float ringR = 0.78f * capScale * capXZS * f;
            float y = -capScale * capYS * (0.15f + 0.2f * Rand());
            return capPosL + new Vector3(Mathf.Cos(a) * ringR, y, Mathf.Sin(a) * ringR);
        }

        void StartStrike()
        {
            int k = 0;
            for (int i = 1; i < BoltPool; i++) if (boltStart[i] < boltStart[k]) k = i;
            if (t - boltStart[k] < boltLen[k]) return; // all busy

            Vector3 a, b;
            float pick = Rand();
            float ang = Rand() * 2f * Mathf.PI;
            if (pick < 0.5f)
            {
                // across the underside of the cap
                a = CapUnderside(0.5f + 0.5f * Rand(), ang);
                b = CapUnderside(0.4f + 0.6f * Rand(), ang + Mathf.PI * (0.35f + 0.6f * Rand()));
            }
            else if (pick < 0.85f)
            {
                // from the cap down into the stem
                a = CapUnderside(0.2f + 0.4f * Rand(), ang);
                b = StemPoint(0.35f + 0.4f * Rand());
            }
            else
            {
                // along the stem
                float u0 = 0.25f + 0.3f * Rand();
                a = StemPoint(u0 + 0.35f);
                b = StemPoint(u0);
            }
            Jagged(a, b);
            var lr = bolts[k];
            lr.SetPositions(boltPts);
            boltMid[k] = boltPts[BoltPoints / 2];
            boltStart[k] = t;
            boltStrokes[k] = 1 + (int)(Rand() * 3f);
            boltLen[k] = 0.12f + 0.11f * boltStrokes[k];
            boltGain[k] = 0.7f + 0.6f * Rand();
            lr.enabled = true;
        }

        /// <summary>Midpoint displacement from a to b: each level kinks every segment sideways by a shrinking amount.</summary>
        void Jagged(Vector3 a, Vector3 b)
        {
            boltPts[0] = a;
            boltPts[BoltPoints - 1] = b;
            float len = (b - a).magnitude;
            Vector3 dir = (b - a) / Mathf.Max(1f, len);
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            Vector3 side2 = Vector3.Cross(dir, side);
            float amp = len * 0.16f;
            for (int step = BoltPoints - 1; step > 1; step /= 2)
            {
                for (int i = 0; i + step < BoltPoints; i += step)
                {
                    int m = i + step / 2;
                    Vector3 mid = (boltPts[i] + boltPts[i + step]) * 0.5f;
                    mid += side * ((Rand() - 0.5f) * 2f * amp) + side2 * ((Rand() - 0.5f) * 2f * amp);
                    boltPts[m] = mid;
                }
                amp *= 0.52f;
            }
        }

        void UpdateLightning()
        {
            // strikes thin out as the cloud stabilises
            float active = 1f - SS(lightningStart, lightningEnd, t);
            if (t > lightningStart && t < lightningEnd && t >= nextStrike)
            {
                StartStrike();
                float rate = Mathf.Max(0.02f, lightningRate * (0.25f + 0.75f * active));
                nextStrike = t + -Mathf.Log(Mathf.Max(1e-3f, Rand())) / rate + 0.3f;
            }

            int n = boltGlow.Begin();
            float best = 0f;
            Vector3 bestPos = capPosL;
            float width = Mathf.Max(capScale * 0.004f, camDist * 0.0012f, 2f);
            for (int k = 0; k < BoltPool; k++)
            {
                float age = t - boltStart[k];
                float b = 0f;
                if (age >= 0f && age < boltLen[k])
                {
                    // a few return strokes, each a sharp spike with a fast decay
                    for (int j = 0; j < boltStrokes[k]; j++)
                    {
                        float tj = age - j * 0.09f;
                        if (tj >= 0f) b = Mathf.Max(b, Mathf.Exp(-tj / 0.035f));
                    }
                    b *= boltGain[k] * FadeOut;
                }
                var lr = bolts[k];
                if (b < 0.01f)
                {
                    if (lr.enabled) lr.enabled = false;
                    if (k < n) boltGlow.Set(k, capPosL, 0f, new Color(0, 0, 0, 0), 0f);
                    continue;
                }
                if (!lr.enabled) lr.enabled = true;
                lr.widthMultiplier = width * (0.6f + 0.4f * b);
                Color c = new Color(0.9f, 0.93f, 1f) * Mathf.Clamp01(b * 1.4f);
                c.a = 1f;
                lr.startColor = c;
                lr.endColor = c * 0.8f;
                // the lit patch of cloud around the channel
                if (k < n) boltGlow.Set(k, boltMid[k], capScale * 0.9f, new Color(0.62f, 0.68f, 1f, 0.3f * b), 0f);
                if (b > best) { best = b; bestPos = boltMid[k]; }
            }
            boltGlow.End(n);

            bool lit = best > 0.01f;
            if (boltLight.enabled != lit) boltLight.enabled = lit;
            if (lit)
            {
                boltLight.transform.localPosition = bestPos;
                boltLight.intensity = 6f * best;
                boltLight.range = Mathf.Clamp(capScale * 4f, 2000f, 120000f);
            }
        }
    }
}
