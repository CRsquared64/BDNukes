// The shock front as a refracting shell: a sphere at the shock radius that bends the scene behind it. The
// density jump across the front is what makes it visible in film of real tests, as a shimmering ring that is
// strongest where the line of sight grazes the shell (its silhouette). For a ground burst the shell is cut at the
// ground, a dome.
Shader "BDNukes/ShockDistortion"
{
    Properties
    {
        _Strength ("Strength", Float) = 0.02
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        GrabPass { "_BDNukesShockGrab" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _BDNukesShockGrab;
            float _Strength;       // screen-space offset at the silhouette
            float4 _ShockParams;   // x ground height (object-local y, in sphere units), y rim brightening, z thickness

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 grab : TEXCOORD0;
                float3 nV : TEXCOORD1;      // view-space normal
                float3 vV : TEXCOORD2;      // view-space direction to the camera
                float localY : TEXCOORD3;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                o.nV = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal));
                o.vV = -normalize(UnityObjectToViewPos(v.vertex));
                o.localY = v.vertex.y;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (i.localY < _ShockParams.x) discard;           // below the ground: a dome
                float3 n = normalize(i.nV);
                float graze = 1.0 - abs(dot(n, normalize(i.vV)));
                // a thin shell: strong right at the silhouette, faint across the face
                float shell = pow(saturate(graze), max(2.0, 8.0 * _ShockParams.z)) + 0.08 * graze;
                float2 offset = n.xy * (_Strength * shell);
                float2 uv = i.grab.xy / i.grab.w + offset;
                fixed4 c = tex2D(_BDNukesShockGrab, uv);
                c.rgb += _ShockParams.y * shell * 0.25;           // the compressed air at the front catches the light
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
