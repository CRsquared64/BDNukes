// Composites the volumetric cloud, raymarched at reduced resolution, back over the full-resolution scene.
// A 3x3 joint-bilateral filter: the nine nearest low-res samples are weighted by a smooth tent (which also
// averages away the per-pixel raymarch jitter, so the cloud is smooth rather than speckled) and by how close the
// scene depth they were marched against is to this pixel's own depth, so terrain and craft in front of the cloud
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
                float2 p = i.uv * size - 0.5;          // in low-res texel units, texel centres at integers
                float2 c = floor(p + 0.5);
                float d0 = Eye(i.uv);
                float4 sum = 0.0;
                float wsum = 0.0;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 t = c + float2(x, y);
                    float2 uv = (t + 0.5) / size;
                    float2 dd = (t - p) / 1.25;
                    float wt = exp(-dot(dd, dd));                                  // smooth tent
                    float dz = abs(Eye(uv) - d0) / max(d0, 1.0);
                    float w = wt / (1e-3 + dz * 40.0) + 1e-6;                      // ...that respects depth edges
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
