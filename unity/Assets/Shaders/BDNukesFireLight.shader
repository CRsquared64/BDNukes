// The fireball lighting the world: a full-screen additive pass, drawn by the flight cameras' command buffer
// (NukeVolumeCompositor.Build) after the scene, that rebuilds each pixel's world position from the camera depth and
// adds the fireballs' blackbody light to it. Inverse-square falloff with a soft core about the fireball's size,
// windowed to zero at the light's range; the surface normal comes from the depth itself, so ground and craft facing
// the fireball catch it and the far sides of hills don't. Sky pixels (depth at the far plane) are left alone.
// Uniforms (NukeFireLighting.Apply in unity/Assets/Shared/NukeVolumeShader.cs):
//   _FireLightPos[4]  xyz world position, w soft core radius (m)
//   _FireLightCol[4]  rgb blackbody colour * strength, w range (m)
//   _FireLightParams  x light count, y most added light per pixel, z wrap
Shader "BDNukes/FireLight"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"

            #define MAX_FIRE_LIGHTS 4

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _FireLightPos[MAX_FIRE_LIGHTS];
            float4 _FireLightCol[MAX_FIRE_LIGHTS];
            float4 _FireLightParams;

            struct appdata
            {
                float4 vertex : POSITION;   // the quad's corners are already in clip space (-1..1)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;      // screen uv for the depth texture
                float3 ray : TEXCOORD1;     // camera space (+z forward), at unit eye depth
            };

            v2f vert(appdata v)
            {
                v2f o;
                float2 ndc = v.vertex.xy;   // y up: the top of the image
                // match ordinary geometry: when Unity flips the projection (rendering into a texture on Direct3D) clip
                // space y is flipped too, so this uv addresses the depth texture just as ComputeScreenPos would
                o.pos = float4(ndc.x, ndc.y * _ProjectionParams.x, 0.5, 1.0);
                o.uv = ndc * 0.5 + 0.5;
                // the view ray through this point of the screen, from the camera's own (unflipped) projection
                float4x4 p = unity_CameraProjection;
                o.ray = float3((ndc.x + p._m02) / p._m00, (ndc.y + p._m12) / p._m11, 1.0);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                if (Linear01Depth(raw) > 0.9999) return 0;           // sky: nothing there to light
                float eye = LinearEyeDepth(raw);
                float3 wpos = mul(unity_CameraToWorld, float4(i.ray * eye, 1.0)).xyz;

                // surface normal from the reconstructed positions, turned to face the camera
                float3 toCam = _WorldSpaceCameraPos - wpos;
                float3 n = cross(ddy(wpos), ddx(wpos));
                float nn = dot(n, n);
                n = nn > 1e-12 ? n * rsqrt(nn) : normalize(toCam);
                if (dot(n, toCam) < 0.0) n = -n;

                float wrap = _FireLightParams.z;
                int count = (int)_FireLightParams.x;
                float3 sum = 0.0;
                [unroll] for (int k = 0; k < MAX_FIRE_LIGHTS; k++)
                {
                    if (k < count)
                    {
                        float3 l = _FireLightPos[k].xyz - wpos;
                        float d2 = max(dot(l, l), 1e-4);
                        float core2 = _FireLightPos[k].w * _FireLightPos[k].w;
                        float falloff = core2 / (d2 + core2);           // inverse square, soft inside the core
                        float r = sqrt(d2) / _FireLightCol[k].w;
                        float r2 = r * r;
                        float window = saturate(1.0 - r2 * r2);
                        window *= window;                               // smooth to zero at the range
                        float ndl = saturate((dot(n, l * rsqrt(d2)) + wrap) / (1.0 + wrap));
                        // inside the fireball's own core everything is lit, whichever way it faces
                        ndl = lerp(ndl, 1.0, saturate(core2 / d2 - 1.0));
                        sum += _FireLightCol[k].rgb * (falloff * window * ndl);
                    }
                }
                // cap the brightest channel (keeping the hue) so a close fireball doesn't wash everything to white
                float m = max(sum.r, max(sum.g, sum.b));
                float cap = _FireLightParams.y;
                if (m > cap) sum *= cap / m;
                return float4(sum, 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
