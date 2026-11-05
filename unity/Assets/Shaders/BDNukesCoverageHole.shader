// Copies an EVE cloud coverage map (equirectangular) with holes blown in it. Each blast pushes the cloud deck
// outward with the shock: the cloud that was inside the growing disc is not just erased but dragged out and
// squeezed into a dense rim that travels with the front, so the deck visibly shoves aside. Behind the front the
// disc is clear; as the hole heals the rim relaxes back and the cloud closes in.
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
            float4 _HoleRing[8];  // x rim compression, y ragged edge seed, z shock-front flash, w rim width (hole radii)
            float _HoleCount;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 src = i.uv;      // where the cloud now here came from
                float keep = 1.0;
                float dens = 1.0;
                float flash = 0.0;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    if (k >= (int)_HoleCount) break;
                    float4 h = _Holes[k];
                    float4 rg = _HoleRing[k];
                    float2 d = src - h.xy;
                    d.x = frac(d.x + 0.5) - 0.5;                    // longitude wraps
                    float lat = (h.y - 0.5) * 3.14159265;
                    float cx = 2.0 * max(cos(lat), 0.02);           // u spans 2 pi, v spans pi
                    float2 dm = float2(d.x * cx, d.y);              // in v units, locally isotropic
                    float len = length(dm);
                    float ang = atan2(dm.y, dm.x);
                    float ragged = 1.0 + 0.12 * sin(ang * 5.0 + rg.y) + 0.07 * sin(ang * 11.0 + 2.0 * rg.y)
                                 + 0.04 * sin(ang * 23.0 + 3.0 * rg.y);
                    float R = max(h.z * ragged, 1e-7);
                    float r = len / R;
                    float w = max(rg.w, 0.05);

                    // the disc behind the front is blown clear
                    keep *= 1.0 - h.w * (1.0 - smoothstep(0.82, 1.0, r));

                    // the rim: everything that was inside radius 1 + w is now squeezed into [1, 1 + w]
                    if (r < 1.0 + w && r >= 1.0)
                    {
                        float u = (r - 1.0) / w;                    // 0 at the hole's edge .. 1 at the rim's outer edge
                        // most of the swept cloud piles up right at the front, thinning outward
                        float rs = (1.0 + w) * u * u;
                        rs = lerp(r, rs, h.w);                       // healing lets the rim relax back
                        float2 dir = dm / max(len, 1e-9);
                        float2 nd = dir * (rs * R);
                        nd.x /= cx;
                        src = h.xy + nd;
                        // squeezed cloud is thicker: strongest at the front
                        dens *= 1.0 + rg.x * h.w * (1.0 - u) * (1.0 - u) * 2.0;
                    }
                    // while the shock is still racing through the deck, the air at its front condenses: a bright,
                    // dense ring flashing across the clouds
                    float fe = (r - 1.0) / 0.05;
                    flash += rg.z * exp(-fe * fe);
                }
                float4 c = tex2D(_MainTex, src);
                float4 orig = tex2D(_MainTex, i.uv);
                // the flash condenses even thin air: lift the coverage there, not just scale it
                return saturate(c * keep * dens + flash * (0.35 + orig));
            }
            ENDCG
        }
    }
    Fallback Off
}
