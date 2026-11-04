// Writes the eye depth of the loaded vessels' parts into BDNukes' own depth texture, drawn by the flight cameras'
// command buffer just before the volumetric cloud. Some part shaders (mod PBR shaders, transparent parts) never reach
// Unity's camera depth texture, so the cloud's raymarch would run straight through them and draw over the ship; the
// march also stops at this depth.
Shader "BDNukes/PartDepth"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float eye : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.eye = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.eye, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
