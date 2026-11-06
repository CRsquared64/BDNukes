// BDNukes procedural rocket exhaust plume, drawn additively on a unit cone mesh built by ModuleNukePlume.
//
// The mesh is generic: vertices sit on a unit circle (xz) at axial fraction y = 0 (nozzle) .. 1 (tail). The vertex
// shader shapes it into the plume, in metres, from _Length, _Radius, _Throttle and _Vacuum, so the same mesh
// serves a tight sea-level jet and a wide vacuum bloom. The object's local +Y is the exhaust direction.
//
// Look:
//  - a white-yellow hot core right at the nozzle, fading into the plume colour downstream;
//  - Mach (shock) diamonds: a train of bright knots along the axis at sea level, fading with distance and with
//    altitude (_Diamonds, set from ambient pressure, times 1 - _Vacuum);
//  - in vacuum the plume flares wide and goes faint and even;
//  - turbulence: 3D value-noise fBm in metres scrolling downstream, plus a fast global flicker;
//  - a surface shell can't hold a volume, so brightness follows |N.V| (the chord through a round jet is longest
//    at its middle): soft fresnel edges instead of a hard cone silhouette. The impact parameter from |N.V| also
//    places the diamonds on the axis;
//  - soft depth fade against geometry when the camera renders a depth texture (_SoftFade > 0, set per camera
//    from C#; with no depth texture the fade is skipped).
Shader "BDNukes/Plume"
{
    Properties
    {
        _Color ("Plume colour", Color) = (1, 0.6, 0.3, 1)
        _CoreColor ("Core colour", Color) = (1, 0.95, 0.8, 1)
        _Throttle ("Throttle", Range(0, 1)) = 1
        _Vacuum ("Vacuum expansion", Range(0, 1)) = 0
        _Diamonds ("Shock diamonds", Range(0, 1)) = 1
        _Length ("Length (m)", Float) = 8
        _Radius ("Nozzle radius (m)", Float) = 0.4
        _Intensity ("Intensity", Float) = 1
        _Seed ("Noise seed", Float) = 0
        _SoftFade ("Depth fade enabled", Float) = 0
        _InvFade ("Depth fade sharpness (1/m)", Float) = 1.5
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4 _Color;
            float4 _CoreColor;
            float _Throttle;
            float _Vacuum;
            float _Diamonds;
            float _Length;
            float _Radius;
            float _Intensity;
            float _Seed;
            float _SoftFade;
            float _InvFade;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 localPos : TEXCOORD2;   // metres, plume frame (y downstream)
                float4 axial : TEXCOORD3;      // x axial fraction 0..1, y metres from nozzle, z plume length, w local radius
                float4 screenPos : TEXCOORD4;
            };

            // plume length in metres: shorter at low throttle, longer when the jet is under-expanded in vacuum
            float PlumeLength()
            {
                return max(_Length, 0.01) * lerp(0.4, 1.0, saturate(_Throttle)) * (1.0 + 0.8 * _Vacuum);
            }

            v2f vert(appdata v)
            {
                v2f o;
                float s = saturate(v.vertex.y);
                float len = PlumeLength();
                float R = max(_Radius, 0.001);

                // radius profile: sea level is a near-straight jet (half-angle ~5 deg, slight swell with throttle),
                // vacuum flares out fast right behind the nozzle (half-angle ~35 deg with a sqrt-like bloom)
                float tanHalf = lerp(0.07 + 0.03 * _Throttle, 0.7, _Vacuum);
                float p = lerp(1.0, 0.55, _Vacuum);
                float sc = max(s, 0.02);
                float rad = R + len * tanHalf * pow(sc, p);
                // slope dr/dy for the cone normal
                float slope = tanHalf * p * pow(sc, p - 1.0);

                float2 ring = v.vertex.xz;
                float3 lp = float3(ring.x * rad, s * len, ring.y * rad);
                float3 ln = normalize(float3(ring.x, -slope, ring.y));

                o.localPos = lp;
                o.worldPos = mul(unity_ObjectToWorld, float4(lp, 1.0)).xyz;
                o.worldNormal = UnityObjectToWorldNormal(ln);
                o.pos = UnityWorldToClipPos(o.worldPos);
                o.axial = float4(s, s * len, len, rad);
                o.screenPos = ComputeScreenPos(o.pos);
                // eye depth of the displaced vertex (COMPUTE_EYEDEPTH would use the undisplaced unit mesh)
                o.screenPos.z = -UnityWorldToViewPos(o.worldPos).z;
                return o;
            }

            // ---- procedural noise (sin-free hash, stable at the metre-scale coordinates used here) ----
            float Hash3(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                float3 u = f * f * (3.0 - 2.0 * f);
                float n000 = Hash3(i);
                float n100 = Hash3(i + float3(1, 0, 0));
                float n010 = Hash3(i + float3(0, 1, 0));
                float n110 = Hash3(i + float3(1, 1, 0));
                float n001 = Hash3(i + float3(0, 0, 1));
                float n101 = Hash3(i + float3(1, 0, 1));
                float n011 = Hash3(i + float3(0, 1, 1));
                float n111 = Hash3(i + float3(1, 1, 1));
                float a = lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y);
                float b = lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y);
                return lerp(a, b, u.z);
            }

            float Fbm3(float3 p)
            {
                float sum = 0.0;
                float amp = 0.5;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    sum += amp * ValueNoise3(p);
                    p = p * 2.03 + float3(17.1, 5.3, 11.7);
                    amp *= 0.5;
                }
                return sum / 0.875;
            }

            float4 frag(v2f i) : SV_Target
            {
                float s = i.axial.x;
                float d = i.axial.y;
                float len = i.axial.z;
                float R = max(_Radius, 0.001);
                float thr = saturate(_Throttle);
                float vac = saturate(_Vacuum);

                float3 N = normalize(i.worldNormal);
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float ndv = abs(dot(N, V));
                // impact parameter of the view ray through the round jet, as a fraction of its radius
                float b = sqrt(saturate(1.0 - ndv * ndv));

                // fresnel edge softening: the chord through the jet thins to nothing at its silhouette
                float thick = pow(ndv, lerp(1.6, 0.9, vac));

                // axial envelope: soft start at the nozzle lip, fading out towards the tail
                float env = smoothstep(0.0, 0.025, s) * pow(saturate(1.0 - s), lerp(1.6, 2.4, vac));

                // turbulence scrolling downstream; eddies grow with distance from the nozzle
                float scroll = _Time.y * (6.0 + 22.0 * thr);
                float freq = 1.0 / (R * lerp(1.5, 4.0, vac) + 0.15 * d);
                float3 np = float3(i.localPos.x, i.localPos.y - scroll, i.localPos.z) * freq;
                float n = Fbm3(np + _Seed * 13.7);
                float turb = lerp(1.0, 0.35 + 1.3 * n, saturate(s * 1.6 + 0.15));
                // fast global flicker of combustion
                float flick = 0.9 + 0.1 * ValueNoise3(float3(_Time.y * 23.0, _Seed * 7.1, 0.5))
                                  + 0.05 * sin(_Time.y * 61.0 + _Seed * 3.0);

                // hot core: white-yellow, hugging the nozzle and the axis
                float core = exp(-s * lerp(9.0, 16.0, vac)) * pow(ndv, 2.5);

                // Mach diamonds: knots on the axis, spaced ~1.1 nozzle diameters (a little longer at full thrust)
                float cellLen = R * lerp(1.7, 2.4, thr);
                float phase = d / cellLen - 0.35;
                float cell = floor(phase);
                float a = abs(frac(phase) - 0.5) * 2.0;           // 0 at the knot, 1 between knots
                float kx = a * 2.6 + b * 3.2;                     // linear sum in the exponent -> diamond contours
                float knot = exp(-kx * kx);
                float machAmp = saturate(_Diamonds) * (1.0 - vac) * (1.0 - vac) * step(0.0, phase)
                              * exp(-max(cell, 0.0) * 0.32) * smoothstep(0.05, 0.35, thr)
                              * (0.85 + 0.3 * n);

                // colour: core colour near the nozzle into the plume colour, reddening a touch towards the tail
                float3 plumeCol = lerp(_CoreColor.rgb, _Color.rgb, saturate(s * 3.0));
                plumeCol = lerp(plumeCol, _Color.rgb * float3(1.0, 0.75, 0.6), saturate(s * 1.5 - 0.5));

                float3 col = plumeCol * env * thick * turb * lerp(0.75, 0.28, vac);
                col += _CoreColor.rgb * core * 2.2;
                col += lerp(_CoreColor.rgb, float3(1, 1, 1), 0.4) * knot * machAmp * 1.4;

                // brightness with throttle; both faces of the shell are drawn, hence the 0.5
                col *= 0.5 * lerp(0.3, 1.0, thr) * step(0.001, thr) * flick * _Intensity;

                // soft intersection with geometry, only where the camera has a depth texture
                if (_SoftFade > 0.5)
                {
                    float rawZ = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos));
                    float sceneZ = LinearEyeDepth(rawZ);
                    float fade = saturate(_InvFade * (sceneZ - i.screenPos.z));
                    col *= fade;
                }

                return float4(max(col, 0.0), 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
