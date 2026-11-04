// BDNukes volumetric mushroom cloud.
//
// Drawn on a box around the cloud. Each pixel raymarches a density field: a rolling torus and dome for the cap,
// a bent round cone for the stem and a flattened ring of dust for the skirt, each a soft signed-distance shape
// eroded by tiling 3D billow noise. Every sample is lit by the sun through a short shadow march (Beer-Lambert,
// with a two-lobe phase function and the "powder" darkening of cloud edges), sky ambient from above and the
// fireball's own glow. The parameters come from NukeCinematic each frame, in the detonation's local frame
// (y up, origin at the burst point), so floating-origin shifts don't matter.
//
// KSP renders the flight scene with more than one camera (near and far). The march is clipped to the current
// camera's near/far range and to the scene depth, so each camera draws its own slice and terrain hides what's
// behind it.
Shader "BDNukes/VolumetricCloud"
{
    Properties
    {
        _Noise ("Billow noise (3D)", 3D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            sampler3D _Noise;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4x4 _WorldToCloud;   // world -> detonation-local (rotation + translation only)
            float4x4 _CloudToWorld;
            float4 _BoxMin, _BoxMax;   // marching volume, local

            float4 _CapCenter;   // xyz centre, w cap scale (m)
            float4 _CapShape;    // x torus ring radius, y tube radius, z dome radius (all x cap scale), w vertical squash
            float4 _CapXZ;       // x horizontal spread of the cap (pancaking), y overhang, z unused, w unused
            float4 _StemBase;    // xyz ground centre, w radius
            float4 _StemTop;     // xyz top centre, w radius
            float4 _StemVis;     // x dust column top y, y trail bottom y, z density, w flare
            float4 _SkirtCenter; // xyz centre on the ground, w radius
            float4 _Skirt;       // x height, y density, z inner hole fraction, w unused
            float4 _Density;     // x cap density, y extinction per m at density 1, z erosion, w noise scale (1/m)
            float4 _Anim;        // x boil time, y cap roll angle (rad, poloidal circulation), z unused, w unused
            float4 _Fire;        // x incandescence (0 = cold smoke), y temperature 0..1 (white-hot .. dull red), z hot-core radius (x cap scale), w unused
            float4 _SunDir;      // xyz local unit vector to the sun, w daylight 0..1
            float4 _SunCol;      // rgb sunlight, a direct-sun visibility at the cap
            float4 _SunLow;      // x direct-sun visibility near the ground
            float4 _Ambient;     // rgb skylight
            float4 _Albedo;      // rgb lit dust colour
            float4 _AlbedoDark;  // rgb shadowed/underside colour
            float4 _Glow;        // rgb fireball glow (pre-multiplied by strength), a glow falloff radius (m)
            float4 _GlowCenter;  // xyz fireball centre, w unused
            float4 _HazeCol;     // rgb haze colour, a haze amount
            float4 _March;       // x steps, y shadow steps, z overall fade, w debug (1 = show billow noise)

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityWorldToClipPos(o.worldPos);
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float2 BoxHit(float3 ro, float3 rd)
            {
                float3 inv = 1.0 / rd;
                float3 a = (_BoxMin.xyz - ro) * inv;
                float3 b = (_BoxMax.xyz - ro) * inv;
                float3 lo = min(a, b), hi = max(a, b);
                return float2(max(max(lo.x, lo.y), lo.z), min(min(hi.x, hi.y), hi.z));
            }

            float SMin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            // signed distance to a round cone from a (radius ra) to b (radius rb)
            float RoundCone(float3 p, float3 a, float3 b, float ra, float rb, out float u)
            {
                float3 ab = b - a;
                u = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-3));
                float3 c = a + ab * u;
                return length(p - c) - lerp(ra, rb, u);
            }

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }

            // Where a point of the cap was before the poloidal roll: the smoke circulates up through the middle of the
            // ring and down its outside, so the noise is sampled at the point rotated back around the tube's core.
            // That's what makes the cap visibly churn instead of sliding a texture over it.
            float3 RollBack(float3 p)
            {
                float cs = _CapCenter.w;
                float3 q = p - _CapCenter.xyz;
                float rxz = length(q.xz);
                float2 dir = rxz > 1e-3 ? q.xz / rxz : float2(1.0, 0.0);
                float ringR = _CapShape.x * cs;
                float2 v = float2(rxz - ringR, q.y);                 // offset from the tube's core, in its plane
                float k = exp(-dot(v, v) / (1.2 * cs * cs));         // full roll near the core, fading out
                float a = -_Anim.y * k;
                float sa = sin(a), ca = cos(a);
                v = float2(ca * v.x - sa * v.y, sa * v.x + ca * v.y);
                float r2 = ringR + v.x;
                return _CapCenter.xyz + float3(dir.x * r2, v.y, dir.y * r2);
            }

            // billow noise: R = large billows, G = fine erosion detail
            float Billows(float3 p, float detail)
            {
                float ns = _Density.w;
                float t = _Anim.x;
                float3 pr = RollBack(p);
                float big = tex3Dlod(_Noise, float4(pr * ns + float3(0.0, -0.015 * t, 0.0), 0)).r;
                float mid = tex3Dlod(_Noise, float4(pr * ns * 2.3 + float3(0.31, 0.17 - 0.03 * t, 0.53), 0)).r;
                float fine = tex3Dlod(_Noise, float4(pr * ns * 6.1 + float3(0.011 * t, -0.06 * t, 0.0), 0)).g;
                float n = 0.65 * big + 0.35 * mid;
                n = smoothstep(0.42, 0.78, n);                        // stretch the contrast: deep gaps between lumps
                return saturate(n - detail * (1.0 - fine) * 0.15);
            }

            // incandescent colour by temperature: white-hot, yellow, orange, cherry, dull red, black
            float3 Blackbody(float x)
            {
                x = saturate(x);
                float3 c = lerp(float3(0.0, 0.0, 0.0), float3(0.55, 0.06, 0.01), smoothstep(0.0, 0.2, x));
                c = lerp(c, float3(1.0, 0.25, 0.04), smoothstep(0.15, 0.45, x));
                c = lerp(c, float3(1.0, 0.62, 0.2), smoothstep(0.4, 0.7, x));
                c = lerp(c, float3(1.0, 0.92, 0.75), smoothstep(0.65, 1.0, x));
                return c * (0.3 + 2.7 * x * x);
            }

            // shape density 0..1 and how far up the cloud this point is (0 ground .. 1 cap top)
            // n: billow noise 0..1 pushing the surface out (lumps) or in (gaps between them); 1 gives the most
            // inflated shape, used as a cheap bound before any noise is fetched
            float Shape(float3 p, float n, out float heightFrac, out float capness)
            {
                float cs = _CapCenter.w;
                // cap: torus (rolling ring) blended into a dome on top
                float3 q = (p - _CapCenter.xyz) / cs;
                q.xz /= max(_CapXZ.x, 0.2);
                // the outer underside curls down past the rim (overhanging cauliflower)
                float rxz0 = length(q.xz);
                q.y += _CapXZ.y * 0.35 * smoothstep(0.7, 1.25, rxz0) * (1.0 - smoothstep(-0.3, 0.25, q.y));
                q.y /= _CapShape.w;
                float2 tq = float2(length(q.xz) - _CapShape.x, q.y);
                float torus = length(tq) - _CapShape.y;
                float dome = length(q - float3(0.0, 0.18, 0.0)) - _CapShape.z;
                float capD = SMin(torus, dome, 0.25) * cs + (0.5 - n) * 0.6 * cs;
                float capDen = saturate(-capD / (0.18 * cs) + 0.15) * _Density.x;

                // stem: bent round cone from the ground to under the cap, flared at the base
                float u;
                float3 mid = lerp(_StemBase.xyz, _StemTop.xyz, 0.5) + float3(0.0, 0.0, 0.0);
                float stemD = RoundCone(p, _StemBase.xyz, _StemTop.xyz, _StemBase.w, _StemTop.w, u);
                float flare = 1.0 + _StemVis.w * pow(1.0 - u, 6.0);
                stemD -= (flare - 1.0) * _StemBase.w;
                stemD += (0.5 - n) * 0.9 * lerp(_StemBase.w, _StemTop.w, u) * flare;
                float stemSoft = 0.3 * lerp(_StemBase.w, _StemTop.w, u) * flare;
                float stemMask = max(smoothstep(_StemVis.x, _StemVis.x - stemSoft * 2.0, p.y),
                                     smoothstep(_StemVis.y, _StemVis.y + stemSoft * 2.0, p.y));
                float stemDen = saturate(-stemD / max(stemSoft, 1.0)) * _StemVis.z * stemMask;

                // skirt: a flattened ring of dust on the ground, thinning toward the middle
                float3 s = p - _SkirtCenter.xyz;
                float rr = length(s.xz) / max(_SkirtCenter.w, 1.0);
                float hh = s.y / max(_Skirt.x, 1.0);
                float skirtD = length(float2(rr * 1.0, hh)) - 1.0 + (0.5 - n) * 0.6;
                float ring = smoothstep(_Skirt.z * 0.5, _Skirt.z, rr);
                float skirtDen = saturate(-skirtD / 0.3) * _Skirt.y * lerp(0.35, 1.0, ring);

                float topY = _CapCenter.y + _CapShape.y * cs * _CapShape.w;
                heightFrac = saturate((p.y - _SkirtCenter.y) / max(topY - _SkirtCenter.y, 1.0));
                capness = capDen / max(capDen + stemDen + skirtDen, 1e-4);
                return max(capDen, max(stemDen, skirtDen));
            }

            float Density(float3 p, float detail, out float heightFrac, out float capness, out float n)
            {
                n = 0.0;
                float hf0, c0;
                if (Shape(p, 1.0, hf0, c0) <= 0.0) { heightFrac = hf0; capness = c0; return 0.0; }
                // billows push the surface into cauliflower lumps, then a little erosion frays their edges
                n = Billows(p, detail);
                float base = Shape(p, n, heightFrac, capness);
                if (base <= 0.0) return 0.0;
                float er = _Density.z;
                return saturate((base - er * (1.0 - n)) / max(1.0 - er, 0.05));
            }

            float DensityCheap(float3 p)
            {
                float h, c, n;
                return Density(p, 0.0, h, c, n);
            }

            float HG(float c, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5) * 0.0795775; // 1/(4 pi)
            }

            fixed4 frag(v2f i, float face : VFACE) : SV_Target
            {
                float3 camW = _WorldSpaceCameraPos;
                float3 rdW = normalize(i.worldPos - camW);
                float3 ro = mul(_WorldToCloud, float4(camW, 1.0)).xyz;
                float3 rd = normalize(mul((float3x3)_WorldToCloud, rdW));

                float2 hit = BoxHit(ro, rd);
                if (hit.y <= max(hit.x, 0.0)) discard;

                // this camera's slice of depth, measured along the ray
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float cosA = max(dot(rdW, camFwd), 1e-3);
                float nearT = _ProjectionParams.y / cosA;
                float farT = _ProjectionParams.z / cosA;

                // draw from the front face if it's in front of the near plane, otherwise from the back face
                bool front = face > 0;
                if (front != (hit.x >= nearT)) discard;

                float2 uv = i.screenPos.xy / i.screenPos.w;
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float sceneT = sceneEye / cosA;

                float t0 = max(hit.x, nearT);
                float t1 = min(min(hit.y, farT), sceneT);
                if (t1 <= t0) discard;

                int steps = (int)_March.x;
                float len = t1 - t0;
                float dt = len / steps;
                float jitter = Hash(uv * _ScreenParams.xy + frac(_Time.y * 7.13) * 91.7);
                float t = t0 + dt * jitter;

                float sigmaK = _Density.y;
                float3 sun = normalize(_SunDir.xyz);
                float cosSun = dot(rd, sun);
                float phase = lerp(HG(cosSun, 0.55), HG(cosSun, -0.25), 0.3) * 12.566; // ~1 on average
                float shadowLen = 0.5 * _CapCenter.w;
                int sSteps = (int)_March.y;

                float T = 1.0;
                float3 col = 0.0;
                [loop]
                for (int k = 0; k < 128; k++)
                {
                    if (k >= steps || T < 0.01) break;
                    float3 p = ro + rd * t;
                    float hf, capness, nz;
                    float d = Density(p, 1.0, hf, capness, nz);
                    if (_March.w > 0.5 && d > 0.002) return fixed4(nz, nz, nz, 1.0);
                    if (d > 0.002)
                    {
                        // light reaching this sample from the sun, through the cloud
                        float od = 0.0;
                        float sl = shadowLen / sSteps;
                        float3 sp = p;
                        [loop]
                        for (int j = 0; j < 8; j++)
                        {
                            if (j >= sSteps) break;
                            sp += sun * sl;
                            od += DensityCheap(sp) * sl;
                            sl *= 1.6;
                        }
                        float sunT = exp(-sigmaK * od);
                        float powder = 1.0 - exp(-2.0 * d * sigmaK * shadowLen * 0.25);
                        float vis = lerp(_SunLow.x, _SunCol.a, hf);
                        float3 direct = _SunCol.rgb * (_SunDir.w * vis * sunT * phase * lerp(0.6, 1.0, powder));

                        // skylight: brighter on top, dimmer deep inside and underneath
                        float3 amb = _Ambient.rgb * lerp(0.45, 1.0, saturate(0.5 + 0.5 * sunT)) * lerp(0.7, 1.05, hf);
                        // deep in shadow the blue sky is mostly blocked: what's left is grey light bounced off the
                        // ground and the rest of the cloud
                        float ambGrey = dot(amb, float3(0.333, 0.333, 0.334));
                        amb = lerp(float3(ambGrey * 1.02, ambGrey, ambGrey * 0.95), amb, saturate(sunT * 1.5 + 0.2));

                        float3 alb = lerp(_AlbedoDark.rgb, _Albedo.rgb, saturate(0.35 + 0.65 * hf + 0.3 * sunT));
                        float3 lit = alb * (direct + amb);

                        // the fireball's glow on its own smoke: strongest inside the cap
                        float fd = length(p - _GlowCenter.xyz);
                        lit += _Glow.rgb * exp(-fd / max(_Glow.a, 1.0)) * lerp(0.4, 1.0, capness);

                        // incandescence: the fireball itself, hottest in its core and along the turbulent filaments
                        // the roll drags through it, cooling into dark smoke at the edges
                        if (_Fire.x > 0.001)
                        {
                            float core = saturate(1.0 - fd / max(_Fire.z * _CapCenter.w, 1.0));
                            float heat = _Fire.y * (0.35 + 0.65 * core) * (0.55 + 0.6 * nz) * capness;
                            lit = lerp(lit, lit * 0.25, saturate(_Fire.x * 0.8)) + Blackbody(heat) * _Fire.x * (0.4 + 0.6 * core);
                        }

                        float sigma = d * sigmaK;
                        float stepT = exp(-sigma * dt);
                        col += T * (1.0 - stepT) * lit;
                        T *= stepT;
                    }
                    t += dt;
                }

                float a = (1.0 - T) * _March.z;
                col *= _March.z;
                // aerial perspective over the whole column of cloud
                col = lerp(col, _HazeCol.rgb * a, _HazeCol.a);
                return fixed4(col, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
