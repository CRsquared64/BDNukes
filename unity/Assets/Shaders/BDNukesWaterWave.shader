// The sea surface raised by a nuclear burst: a band of mesh laid over the ocean along the wave train (and a flat band
// for the shock ring an air burst sweeps across the water).
//
// The band doesn't paint water of its own over the game's ocean (whose colour depends on Scatterer, the time of day
// and the view, and which a flat colour never matched: the waves showed as a dark disc). Instead:
//   pass 1 shades the ocean already drawn: it works out how much light the wave's tilted, choppy surface sends to the
//          camera (sky by Fresnel, the sun's glint) against what the flat sea there sends, and multiplies the ocean by
//          that ratio, so crests facing the sky or the sun brighten and the backs of the waves darken, whatever the
//          ocean looked like;
//   pass 2 paints only what is white: foam on steep and breaking crests, broken into streaks, and the pale roughened
//          water of the shock ring.
// Vertex colour: r foam, g band visibility (edges, land), b shock roughening, a shading strength.
// uv2 (TEXCOORD1): the flat sea's normal at the vertex (the planet's curve).
Shader "BDNukes/WaterWave"
{
    Properties
    {
    }
    CGINCLUDE
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
        float4 flat : TEXCOORD1;     // xyz the flat sea's normal, w the height above the sea (m)
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float3 wpos : TEXCOORD0;
        float3 wn : TEXCOORD1;
        float3 wflat : TEXCOORD2;
        float2 opos : TEXCOORD3;     // position across the sea (m), for the chop and foam patterns
        float lift : TEXCOORD4;      // height above the flat sea (m)
        float4 col : COLOR;
    };

    v2f vert(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
        o.wn = UnityObjectToWorldNormal(v.normal);
        float3 f = dot(v.flat.xyz, v.flat.xyz) > 0.5 ? v.flat.xyz : float3(0, 1, 0);
        o.lift = v.flat.w;
        o.wflat = UnityObjectToWorldNormal(f);
        o.opos = v.vertex.xz;
        o.col = v.color;
        return o;
    }

    float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
    float Noise(float2 p)
    {
        float2 i = floor(p), f = frac(p);
        float2 u = f * f * (3.0 - 2.0 * f);
        return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), u.x), u.y);
    }
    // the small waves riding on the big ones, as a slope (two octaves), fading out with distance where they'd only alias
    float2 Chop(float2 p, float dist)
    {
        float e = 0.35;
        float2 g = 0;
        float s = 1.0 / 4.0, amp = 1.0;
        for (int k = 0; k < 2; k++)
        {
            float2 q = p * s;
            float n0 = Noise(q), nx = Noise(q + float2(e, 0)), nz = Noise(q + float2(0, e));
            g += amp * float2(nx - n0, nz - n0) / e;
            s *= 2.7; amp *= 0.5;
        }
        return g * saturate(1.0 - dist / 1500.0);
    }

    // light the surface with normal n sends toward the camera, as a brightness
    float Light(float3 n, float3 V, float3 L, float slick)
    {
        if (dot(n, V) < 0.0) n = -n;
        float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(n, V)), 5.0);
        float3 R = reflect(-V, n);
        float sky = Luminance(_WSkyColor.rgb) * lerp(1.15, 0.7, saturate(dot(R, _WUp)));
        float sharp = lerp(500.0, 25.0, slick);
        float glint = pow(saturate(dot(R, L)), sharp) * lerp(3.0, 0.4, slick) * Luminance(_WSunColor.rgb) * saturate(dot(_WUp, L) * 6.0);
        float body = Luminance(_WDeepColor.rgb) * (Luminance(_WAmbient.rgb) + saturate(dot(n, L)) * Luminance(_WSunColor.rgb) * 0.35);
        return body + sky * fres * (1.0 - 0.45 * slick) + glint;
    }

    float Fade(float dist) { return saturate(1.0 - dist / max(_WFade.y, 1.0)); }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Transparent-20" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        // pass 1: shade the ocean underneath (result = ocean * 2 * output; 0.5 leaves it as it was)
        Pass
        {
            Blend DstColor SrcColor
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f i) : SV_Target
            {
                float3 toCam = _WorldSpaceCameraPos - i.wpos;
                float dist = length(toCam);
                float3 V = toCam / max(dist, 1e-3);
                float3 L = normalize(_WSunDir);
                float3 flat = normalize(i.wflat);
                float slick = saturate(i.col.b);
                // tangent frame of the flat sea, to tilt the normals by the chop
                float3 tx = normalize(cross(flat, abs(flat.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 tz = cross(tx, flat);
                // the same small chop on both, so only the big wave (and the shock's extra roughening) shows; the
                // wave's own chop is stronger where its face is steep
                float2 c0 = Chop(i.opos, dist);
                float2 ch = c0 * (0.03 + 0.4 * slick + 0.05 * saturate(i.col.a));
                float3 n = normalize(normalize(i.wn) - tx * ch.x - tz * ch.y);
                float3 nFlat = normalize(flat - (tx * c0.x + tz * c0.y) * 0.03);
                float ratio = (Light(n, V, L, slick) + 0.04) / (Light(nFlat, V, L, 0.0) + 0.04);
                // a little stronger than the light alone: KSP's sea has its own texture and haze to show through
                ratio = clamp(pow(ratio, 1.4), 0.5, 1.9);
                float k = saturate(i.col.a) * saturate(i.col.g) * Fade(dist);
                return fixed4((0.5 * lerp(1.0, ratio, k)).xxx, 1.0);
            }
            ENDCG
        }
        // pass 2: foam and the shock ring's pale water, painted on top
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

            fixed4 frag(v2f i) : SV_Target
            {
                float3 toCam = _WorldSpaceCameraPos - i.wpos;
                float dist = length(toCam);
                float3 L = normalize(_WSunDir);
                float3 n = normalize(i.wn);
                float diffuse = saturate(dot(n, L));
                float foam = saturate(i.col.r);
                float slick = saturate(i.col.b);
                // foam breaks into patches and streaks, not a flat white band (fine detail fades with distance)
                float detail = saturate(1.0 - dist / 6000.0);
                float pat = Noise(i.opos / 14.0) * 0.6 + Noise(i.opos / 4.5) * 0.4;
                float cover = lerp(foam, smoothstep(1.0 - foam, 1.0 - foam + 0.25, pat), detail);
                float3 foamCol = _WAmbient.rgb * 2.2 + _WSunColor.rgb * (0.45 + 0.4 * diffuse);
                float3 rough = _WSkyColor.rgb + _WSunColor.rgb * 0.2 * diffuse;
                // a crest standing above the sea, seen from low down, hides the water behind it: draw its face as water
                float3 V = normalize(toCam);
                float3 flat = normalize(i.wflat);
                float graze = saturate(1.0 - 5.0 * abs(dot(flat, V)));
                float aBody = saturate(i.lift / 1.5) * graze * 0.9;
                float3 nb = dot(n, V) < 0.0 ? -n : n;
                float fres = 0.02 + 0.98 * pow(1.0 - saturate(dot(nb, V)), 5.0);
                float3 Rb = reflect(-V, nb);
                float3 skyR = _WSkyColor.rgb * lerp(1.15, 0.7, saturate(dot(Rb, _WUp)));
                float3 bodyCol = lerp(_WDeepColor.rgb * (_WAmbient.rgb + diffuse * _WSunColor.rgb * 0.35) * 2.0, skyR, fres * 0.85);
                float aFoam = cover * 0.95;
                float aSlick = slick * 0.3;
                float a = 1.0 - (1.0 - aFoam) * (1.0 - aSlick) * (1.0 - aBody);
                float3 col = (foamCol * aFoam + (rough * aSlick + bodyCol * aBody * (1.0 - aSlick)) * (1.0 - aFoam)) / max(a, 1e-3);
                // aerial perspective: far foam melts into the haze
                col = lerp(col, _WSkyColor.rgb, saturate(dist / max(_WFade.x, 1.0)) * 0.7);
                return fixed4(col, a * saturate(i.col.g) * Fade(dist));
            }
            ENDCG
        }
    }
    Fallback Off
}
