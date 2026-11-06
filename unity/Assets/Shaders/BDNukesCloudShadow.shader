// The volumetric cloud's shadow on the ground (and on craft). Drawn on a box around the region the shadow can
// fall in: for each pixel, the scene point behind it is rebuilt from the depth buffer and a short march toward
// the sun through the cloud's (cheap) density gives how much sunlight still reaches it; the scene is darkened by
// that (multiplicative blend). Sky pixels and points outside the box are left alone.
Shader "BDNukes/CloudShadow"
{
    Properties
    {
        _Noise ("Billow noise (3D)", 3D) = "white" {}
        _SimA ("Fireball sim frame A (3D)", 3D) = "black" {}
        _SimB ("Fireball sim frame B (3D)", 3D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Front
            ZWrite Off
            ZTest Always
            Blend DstColor Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "BDNukesCloudCommon.cginc"

            float4 _ShadowMin, _ShadowMax;   // local box the shadow can fall in
            float4 _ShadowParams;            // x strength 0..1, y max march length (m)

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

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float eye = LinearEyeDepth(raw);
                // nothing drawn here in this camera (sky, or the other camera's range)
                if (eye >= _ProjectionParams.z * 0.999 || eye <= _ProjectionParams.y) discard;

                float3 camW = _WorldSpaceCameraPos;
                float3 rdW = normalize(i.worldPos - camW);
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float3 pw = camW + rdW * (eye / max(dot(rdW, camFwd), 1e-3));
                float3 p = mul(_WorldToCloud, float4(pw, 1.0)).xyz;
                if (any(p < _ShadowMin.xyz) || any(p > _ShadowMax.xyz)) discard;

                float3 sun = normalize(_SunDir.xyz);
                if (sun.y < 0.02 || _SunDir.w < 0.01) discard;
                // march toward the sun until above the cloud
                float top = _ShadowMax.y;
                float len = min(_ShadowParams.y, max(0.0, (top - p.y) / sun.y));
                if (len <= 1.0) discard;
                const int N = 12;
                float dl = len / N;
                float jitter = frac(52.9829189 * frac(dot(uv * _ScreenParams.xy, float2(0.06711056, 0.00583715))));
                float od = 0.0;
                [unroll] for (int k = 0; k < N; k++)
                    od += CloudCheap(p + sun * ((k + jitter) * dl)) * dl;
                float T = exp(-_Density.y * od * 0.7);
                // no direct sun under an overcast deck, no shadow either
                float shade = lerp(1.0, T, _ShadowParams.x * saturate(_SunDir.w * 2.0) * _SunLow.x);
                // a shadow on the ground under the overcast isn't seen from above it (EVE's clouds leave no depth, so
                // this pixel's depth is the ground even where the deck covers it)
                shade = lerp(1.0, shade, DeckT(mul(_WorldToCloud, float4(camW, 1.0)).xyz, p));
                return fixed4(shade, shade, shade, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
