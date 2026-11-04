// Composites the volumetric cloud, raymarched at reduced resolution, back over the full-resolution scene with a
// depth-aware (joint bilateral) upsample: each of the four nearest low-res samples is weighted by how close the
// scene depth it was marched against is to this pixel's own depth, so terrain and craft in front of the cloud
// keep sharp edges instead of a blurred halo.
Shader "BDNukes/VolumeComposite"
{
    Properties
    {
        _MainTex ("Low-res cloud (premultiplied)", 2D) = "black" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;   // low-res: x = 1/width, y = 1/height, z = width, w = height
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float Eye(float2 uv) { return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv)); }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 size = _MainTex_TexelSize.zw;
                float2 p = i.uv * size - 0.5;
                float2 base = floor(p);
                float2 f = p - base;
                float d0 = Eye(i.uv);
                float4 sum = 0.0;
                float wsum = 0.0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 o = float2(k & 1, k >> 1);
                    float2 uv = (base + o + 0.5) / size;
                    float wb = (o.x > 0.5 ? f.x : 1.0 - f.x) * (o.y > 0.5 ? f.y : 1.0 - f.y);
                    float dz = abs(Eye(uv) - d0) / max(d0, 1.0);
                    float w = wb / (1e-3 + dz * 40.0) + 1e-5;
                    sum += tex2D(_MainTex, uv) * w;
                    wsum += w;
                }
                return sum / wsum;
            }
            ENDCG
        }
    }
    Fallback Off
}
