// The sea surface raised by a nuclear burst: a band of mesh laid over the ocean along the wave train (and a flat band
// for the shock ring an air burst sweeps across the water). The mesh carries the true wave's normals, so crests and
// troughs light as water does: sky reflected by Fresnel, a sun glint, foam where the crests are steep or break on
// the shore. Vertex colour: r foam, b shock roughening (a "cat's paw" that dulls the glint and turns the water a matte, paler grey),
// a opacity (set on the CPU: faded at the band's edges, over land and where the wave is low).
Shader "BDNukes/WaterWave"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float3 _WSunDir;      // toward the sun
            float3 _WUp;          // local up at the burst
            float4 _WSunColor;
            float4 _WSkyColor;
            float4 _WDeepColor;
            float4 _WAmbient;
            float4 _WFade;        // x haze distance, y fade-out distance

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 wpos : TEXCOORD0;
                float3 wn : TEXCOORD1;
                float4 col : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.col = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.wn);
                float3 toCam = _WorldSpaceCameraPos - i.wpos;
                float dist = length(toCam);
                float3 V = toCam / max(dist, 1e-3);
                if (dot(n, V) < 0.0) n = -n;
                float foam = saturate(i.col.r);
                float slick = saturate(i.col.b);
                float3 L = normalize(_WSunDir);

                float ndv = saturate(dot(n, V));
                float fres = 0.02 + 0.98 * pow(1.0 - ndv, 5.0);
                float3 R = reflect(-V, n);
                float rUp = saturate(dot(R, _WUp));
                float3 sky = _WSkyColor.rgb * lerp(1.15, 0.7, rUp);   // brighter toward the horizon
                float diffuse = saturate(dot(n, L));
                float3 body = _WDeepColor.rgb * (_WAmbient.rgb + diffuse * _WSunColor.rgb * 0.35);
                float3 col = lerp(body, sky, fres * (1.0 - 0.45 * slick));
                // the glint: tight on calm water, smeared and dimmed where the shock has roughened the surface
                float sharp = lerp(600.0, 25.0, slick);
                float glint = pow(saturate(dot(R, L)), sharp) * lerp(4.0, 0.5, slick);
                col += _WSunColor.rgb * glint * saturate(dot(_WUp, L) * 6.0);
                // the roughened water scatters the whole sky's light back: a matte, paler ring
                float3 rough = sky * 1.0 + _WSunColor.rgb * 0.2 * diffuse;
                col = lerp(col, rough, slick * 0.65);
                // foam is lit like a cloud: mostly from the sky, brighter in the sun
                float3 foamCol = _WAmbient.rgb * 2.2 + _WSunColor.rgb * (0.45 + 0.4 * diffuse);
                col = lerp(col, foamCol, foam);
                // aerial perspective: far bands melt into the sky's haze, then fade away
                col = lerp(col, _WSkyColor.rgb, saturate(dist / max(_WFade.x, 1.0)) * 0.7);
                float a = saturate(i.col.a) * saturate(1.0 - dist / max(_WFade.y, 1.0));
                return fixed4(col, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
