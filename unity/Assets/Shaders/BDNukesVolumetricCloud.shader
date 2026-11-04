// BDNukes volumetric nuclear cloud: fireball, cap, stem, dust skirt, base surge, Wilson cloud, condensation
// rings and stem collars, all in one raymarched volume.
//
// The cloud is drawn on a box around it. Each pixel marches a density field built from soft signed-distance
// shapes (a torus-and-dome cap that starts as a ball, a bent round cone for the stem, a ring of dust on the
// ground, thin shells and rings of condensation), displaced and eroded by tiling 3D billow noise.
//
// Motion: the noise is sampled in metres at a fixed physical scale (so billows don't "zoom" as the cap grows)
// and moved by a flow field, the cap's poloidal roll and expansion, the stem's updraft and twist, the skirt's
// outflow, using two phase-offset copies that crossfade (a flow map), so it churns forever without smearing.
//
// Fire: a temperature field hottest along the cap's ring core, flickering with faster convective noise, shown as
// blackbody emission, with soot darkening the outer shell while it burns.
//
// Light: sun through a shadow march with a multiple-scattering approximation (several octaves of falling
// extinction and phase anisotropy), sky light occluded by the cloud above, warm ground bounce from below, the
// fireball's own light, the haze and the EVE overcast. A hue-preserving shoulder keeps the white-hot core from
// clipping to flat white.
//
// KSP renders with more than one camera (near and far): the march is clipped to the current camera's range and to
// the scene depth, so the cameras composite it and terrain hides what's behind it.
Shader "BDNukes/VolumetricCloud"
{
    Properties
    {
        _Noise ("Billow noise (3D)", 3D) = "white" {}
        _SimA ("Fireball sim frame A (3D)", 3D) = "black" {}
        _SimB ("Fireball sim frame B (3D)", 3D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"
            #include "BDNukesCloudCommon.cginc"

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

            // ------------------------------------------------------------------------------------------------
            // helpers
            // ------------------------------------------------------------------------------------------------

            fixed4 frag(v2f i, float face : VFACE) : SV_Target
            {
                float3 camW = _WorldSpaceCameraPos;
                float3 rdW = normalize(i.worldPos - camW);
                float3 ro = mul(_WorldToCloud, float4(camW, 1.0)).xyz;
                float3 rd = normalize(mul((float3x3)_WorldToCloud, rdW));

                float2 hit = BoxHit(ro, rd);
                if (hit.y <= max(hit.x, 0.0)) discard;

                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float cosA = max(dot(rdW, camFwd), 1e-3);
                float nearT = _ProjectionParams.y / cosA;
                float farT = _ProjectionParams.z / cosA;
                bool front = face > 0;
                if (front != (hit.x >= nearT)) discard;

                float2 uv = i.screenPos.xy / i.screenPos.w;
                float sceneT = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv)) / cosA;
                float t0 = max(hit.x, nearT);
                float t1 = min(min(hit.y, farT), sceneT);
                // from orbit the planet is drawn by the scaled-space camera, which leaves no depth here: stop the
                // ray where it meets the planet itself, so clouds behind the horizon or limb stay hidden
                if (_Planet.w > 0.0)
                {
                    float3 oc = ro - _Planet.xyz;
                    float b = dot(oc, rd);
                    float c = dot(oc, oc) - _Planet.w * _Planet.w;
                    float disc = b * b - c;
                    if (disc > 0.0)
                    {
                        float tp = -b - sqrt(disc);
                        if (tp > 0.0) t1 = min(t1, tp);
                    }
                }
                if (t1 <= t0) discard;

                int steps = (int)_March.x;
                float dt = (t1 - t0) / steps;
                float jitter = IGN(uv * _ScreenParams.xy);
                float t = t0 + dt * jitter;

                float sigmaK = _Density.y;
                float3 sun = normalize(_SunDir.xyz);
                float cosSun = dot(rd, sun);
                float cs = _CapCenter.w;
                float shadowLen = 0.45 * cs;
                int sSteps = (int)_March.y;

                float T = 1.0;
                float3 col = 0.0;
                [loop]
                for (int k = 0; k < 160; k++)
                {
                    if (t >= t1 || T < 0.015) break;
                    float3 p = ro + rd * t;
                    Sample sm = Cloud(p, true);
                    float d = sm.density;
                    if (_March.w > 0.5 && d > 0.002) return fixed4(sm.fireN, sm.fireN, sm.fireN, 1.0);
                    if (d > 0.002)
                    {
                        // sunlight through the cloud
                        float od = 0.0;
                        float sl = shadowLen / sSteps;
                        float3 spos = p + sun * sl * jitter;
                        [loop]
                        for (int j = 0; j < 8; j++)
                        {
                            if (j >= sSteps) break;
                            spos += sun * sl;
                            // near the sample the billows themselves cast the shadow (cauliflower relief),
                            // further out the smooth shape is enough
                            od += (j < 1 ? Cloud(spos, false).density : CloudCheap(spos)) * sl;
                            sl *= 1.55;
                        }
                        // multiple scattering: octaves of weaker extinction, softer phase, less energy
                        float3 ms = 0.0;
                        float ea = 1.0, eb = 1.0, ec = 1.0;
                        [unroll] for (int o = 0; o < 3; o++)
                        {
                            float ph = lerp(HG(cosSun, 0.6 * ec), HG(cosSun, -0.25 * ec), 0.3) * 12.566;
                            ms += eb * exp(-sigmaK * od * ea) * ph;
                            ea *= 0.45; eb *= 0.42; ec *= 0.5;
                        }
                        float powder = 1.0 - exp(-2.0 * d * sigmaK * shadowLen * 0.25);
                        float vis = lerp(_SunLow.x, _SunCol.a, sm.hf);
                        float3 direct = _SunCol.rgb * (_SunDir.w * vis) * ms * lerp(0.55, 1.0, powder);

                        // sky from above, occluded by the cloud over this point; ground bounce from below
                        float occ = 2.0 * CloudCheap(p + float3(0.0, 0.5 * cs, 0.0));
                        float skyVis = exp(-sigmaK * occ * 0.3 * cs);
                        float3 amb = _Ambient.rgb * lerp(0.55, 1.15, skyVis) + _GroundCol.rgb * (1.0 - sm.hf) * 0.6;

                        // soot on the burning shell (where the flicker noise is low), fresh debris colour elsewhere
                        float fireMask = smoothstep(0.3, 0.8, sm.fireN);
                        float3 alb = sm.albedo;
                        alb = lerp(alb, float3(0.07, 0.06, 0.055), _Fire.w * sm.cap * saturate(0.4 + sm.core) * (1.0 - 0.7 * fireMask));
                        float3 lit = alb * (direct + amb);

                        // the fireball lights its own smoke, the stem and the skirt
                        float3 fv = p - _CapCenter.xyz;
                        float fd2 = dot(fv, fv) / max(_Glow.a * _Glow.a, 1.0);
                        lit += alb * _Glow.rgb / (1.0 + 3.0 * fd2);
                        // the vapour above catches the fire's light from far further: the glowing underside of the veil
                        lit += alb * _Glow.rgb * sm.cond * 0.7 / (1.0 + 0.35 * fd2);

                        // the burning ring: a band of fire inside the cap's vortex core that outlives the fireball,
                        // glimpsed through gaps in the smoke and lighting the cap from inside
                        if (_Ember.x > 0.001 && sm.cap > 0.01)
                        {
                            // hottest along the vortex core, a dull red-orange glow reaching out to the surface,
                            // strongest on the underside and the hanging lobes, broken up by the smoke
                            float ring = exp(-(sm.core * sm.core) / 1.1);
                            float under = lerp(0.35, 1.0, smoothstep(0.35, -0.35, sm.capY));
                            float flick = smoothstep(0.25, 0.85, sm.fireN);
                            float eh = _Ember.y * ring * lerp(0.4, 1.2, flick);
                            lit += Blackbody(eh) * (_Ember.x * sm.cap * ring * under * lerp(0.3, 1.0, flick));
                        }

                        // the simulated fireball's own flame: white-hot where it burns hardest, through yellow and
                        // orange to dull red, with soot darkening the cooled smoke
                        if (sm.simHeat > 0.002)
                        {
                            // saturated fire: the mid temperatures (orange, yellow) carry most of the surface, white only
                            // in the hottest cores; the cooler smoke between the flames goes dark with soot
                            float sh = saturate(pow(saturate(sm.simHeat * 1.15), 0.8));
                            lit = lerp(lit, lit * 0.25, saturate(sh * 2.5));
                            float3 fc = Blackbody(sh);
                            float lum = dot(fc, float3(0.3, 0.59, 0.11));
                            fc = lerp(float3(lum, lum, lum), fc, 1.25);       // a touch more saturation
                            lit += max(fc, 0.0) * _Sim.z;
                        }

                        // the ground fire: flame tongues over the scorched ground, dark smoke between them
                        if (sm.groundHeat > 0.002)
                        {
                            float gh = sm.groundHeat;
                            lit = lerp(lit, lit * 0.3, saturate(gh * 2.0));
                            float3 fc = Blackbody(gh);
                            float lum = dot(fc, float3(0.3, 0.59, 0.11));
                            lit += max(lerp(float3(lum, lum, lum), fc, 1.3), 0.0) * _GroundFire.x;
                        }

                        // incandescence
                        if (_Fire.x > 0.001 && sm.cap > 0.01)
                        {
                            float heat = _Fire.y * saturate(1.0 - 0.5 * sm.core * sm.core);
                            // tongues of flame through the soot: hot where the convective noise is high
                            heat *= lerp(1.0, 0.15 + 1.05 * fireMask, _Fire.z);
                            float3 em = Blackbody(heat) * _Fire.x * sm.cap;
                            lit = lit * (1.0 - saturate(_Fire.x * 0.6) * sm.cap) + em;
                        }

                        float sigma = d * sigmaK;
                        float stepT = exp(-sigma * dt);
                        col += T * (1.0 - stepT) * lit;
                        T *= stepT;
                        t += dt;
                    }
                    else
                    {
                        t += max(dt * 1.6, sm.skip); // jump across empty space
                    }
                }

                float a = (1.0 - T) * _March.z;
                col *= _March.z;
                col = lerp(col, _HazeCol.rgb * a, _HazeCol.a);
                // hue-preserving shoulder: the white-hot core keeps its gradient instead of clipping
                float m = max(max(col.r, col.g), col.b);
                if (m > 0.85) col *= (0.85 + (1.0 - exp(-(m - 0.85) * 1.6)) * 0.6) / m;
                return fixed4(col, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
