// Copies an EVE cloud coverage map (equirectangular) with holes blown in it: each blast clears a disc of cloud
// that grows with the shock, pushes the cloud it removed into a denser ring at its edge, and heals over time.
// The holes are in the coverage map's own UVs, so they drift with EVE's cloud motion.
Shader "BDNukes/CoverageHole"
{
    Properties
    {
        _MainTex ("Coverage", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Holes[8];     // xy centre (uv), z radius (in v units: metres / (pi * deck radius)), w clearing 0..1
            float4 _HoleRing[8];  // x push (ring of piled-up cloud at the edge), y ragged edge seed
            float _HoleCount;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float keep = 1.0;
                float push = 0.0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    if (k >= (int)_HoleCount) break;
                    float4 h = _Holes[k];
                    float2 d = i.uv - h.xy;
                    d.x = frac(d.x + 0.5) - 0.5;                    // longitude wraps
                    float lat = (h.y - 0.5) * 3.14159265;
                    d.x *= 2.0 * cos(lat);                          // u spans 2 pi, v spans pi
                    float ang = atan2(d.y, d.x);
                    float ragged = 1.0 + 0.12 * sin(ang * 5.0 + _HoleRing[k].y) + 0.07 * sin(ang * 11.0 + 2.0 * _HoleRing[k].y);
                    float r = length(d) / max(h.z * ragged, 1e-7);
                    keep *= 1.0 - h.w * (1.0 - smoothstep(0.55, 1.0, r));
                    float e = (r - 1.08) / 0.16;
                    push += _HoleRing[k].x * h.w * exp(-e * e);
                }
                return saturate(c * keep + c * push);
            }
            ENDCG
        }
    }
    Fallback Off
}
