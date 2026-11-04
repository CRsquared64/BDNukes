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

            sampler3D _Noise;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4x4 _WorldToCloud;   // world -> detonation-local (rotation + translation)
            float4 _BoxMin, _BoxMax;   // marching volume, local

            // --- shapes ---
            float4 _CapCenter;   // xyz centre, w cap scale (m)
            float4 _CapShape;    // x ring radius, y tube radius, z dome radius (x cap scale), w vertical squash
            float4 _CapXZ;       // x horizontal spread, y overhang, z roughness 0 (smooth young ball) .. 1, w cap growth rate (1/s)
            float4 _StemBase;    // xyz ground centre, w radius
            float4 _StemTop;     // xyz top centre, w radius
            float4 _StemVis;     // x dust column top y, y trail bottom y, z density, w base flare
            float4 _SkirtCenter; // xyz centre on the ground, w radius
            float4 _Skirt;       // x height, y density, z inner thinning, w outflow speed (m/s)
            float4 _Surge;       // x ring radius, y height, z density, w ring width
            float4 _Wilson;      // x radius, y thickness, z density, w 1 = hemisphere above the ground
            float4 _Rings[3];    // per ring: x height (local y), y radius, z tube, w density
            float4 _RingOffset;  // xyz horizontal drift of the rings
            float4 _CollarPos[5];   // per collar: xyz centre on the stem axis, w density
            float4 _CollarShape[5]; // per collar: x outer radius, y thickness, z droop, w unused
            float4 _GroundY;     // x ground height (local y)
            float4 _Wind;        // xyz horizontal unit vector downwind, w shear: horizontal lean per metre of height
            float4 _WindFlow;    // x wind near the ground (m/s), y wind at the cap (m/s), z downwind stretch of the cap, w downwind stretch of the dust
            float4 _StemMid;     // xyz bend control point of the stem (quadratic Bezier between base and top)
            float4 _Age;         // x dissipation 0..1 (thinning, fraying), y unused

            // --- appearance ---
            float4 _Density;     // x cap density, y extinction per m at density 1, z erosion, w noise scale (1/m)
            float4 _Anim;        // x time (s), y roll rate (rad/s), z stem updraft (m/s), w flow period (s)
            float4 _Fire;        // x incandescence, y temperature 0..1, z flicker, w soot
            float4 _SunDir;      // xyz local unit vector to the sun, w daylight 0..1
            float4 _SunCol;      // rgb sunlight, a direct-sun visibility at the cap
            float4 _SunLow;      // x direct-sun visibility near the ground
            float4 _Ambient;     // rgb skylight
            float4 _GroundCol;   // rgb light bounced up from the ground
            float4 _CapAlbedo;   // rgb cap colour (young brownish -> mature)
            float4 _StemAlbedo;  // rgb stem colour
            float4 _DustAlbedo;  // rgb skirt and surge colour
            float4 _CondAlbedo;  // rgb condensation colour
            float4 _Glow;        // rgb fireball light (x strength), a falloff radius (m)
            float4 _HazeCol;     // rgb haze colour, a haze amount
            float4 _March;       // x steps, y shadow steps, z overall fade, w debug (1 = show noise)

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

            float2 BoxHit(float3 ro, float3 rd)
            {
                float3 inv = 1.0 / rd;
                float3 a = (_BoxMin.xyz - ro) * inv;
                float3 b = (_BoxMax.xyz - ro) * inv;
                float3 lo = min(a, b), hi = max(a, b);
                return float2(max(max(lo.x, lo.y), lo.z), min(min(hi.x, hi.y), hi.z));
            }

            float SMin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            float RoundCone(float3 p, float3 a, float3 b, float ra, float rb, out float u)
            {
                float3 ab = b - a;
                u = saturate(dot(p - a, ab) / max(dot(ab, ab), 1e-3));
                return length(p - (a + ab * u)) - lerp(ra, rb, u);
            }

            // The stem as a tube whose axis bends with the wind: a quadratic Bezier from the base through the bend
            // control point to the top, parametrised by height (the stem is never more than ~45 degrees over).
            float3 StemAxis(float u)
            {
                float a = 1.0 - u;
                return a * a * _StemBase.xyz + 2.0 * a * u * _StemMid.xyz + u * u * _StemTop.xyz;
            }

            float BentStem(float3 p, out float u, out float3 axis)
            {
                float h = max(_StemTop.y - _StemBase.y, 1.0);
                float uu = (p.y - _StemBase.y) / h;
                u = saturate(uu);
                axis = StemAxis(u);
                float r = lerp(_StemBase.w, _StemTop.w, u);
                float radial = length(p.xz - axis.xz) - r;
                // rounded ends below the base and above the top
                float over = uu < 0.0 ? -uu * h : (uu > 1.0 ? (uu - 1.0) * h : 0.0);
                return over > 0.0 ? length(float2(max(radial, 0.0), over)) : radial;
            }

            // downwind stretch: points on the downwind side are pulled back toward the centre before the shape is
            // evaluated, so the shape itself reaches further downwind
            float3 Stretch(float3 q, float k)
            {
                float along = dot(q.xz, _Wind.xz);
                float pulled = along > 0.0 ? along / (1.0 + k) : along / (1.0 + 0.25 * k);
                q.xz += _Wind.xz * (pulled - along);
                return q;
            }

            // stable per-pixel jitter (interleaved gradient noise): no frame-to-frame shimmer without TAA
            float IGN(float2 pix)
            {
                return frac(52.9829189 * frac(dot(pix, float2(0.06711056, 0.00583715))));
            }

            float HG(float c, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5) * 0.0795775;
            }

            float3 Blackbody(float x)
            {
                x = saturate(x);
                float3 c = lerp(float3(0.0, 0.0, 0.0), float3(0.5, 0.05, 0.01), smoothstep(0.0, 0.18, x));
                c = lerp(c, float3(1.0, 0.22, 0.03), smoothstep(0.12, 0.4, x));
                c = lerp(c, float3(1.0, 0.55, 0.14), smoothstep(0.35, 0.62, x));
                c = lerp(c, float3(1.0, 0.85, 0.55), smoothstep(0.58, 0.85, x));
                c = lerp(c, float3(1.0, 0.97, 0.92), smoothstep(0.82, 1.0, x));
                return c * (0.25 + 3.75 * x * x * x);
            }

            // ------------------------------------------------------------------------------------------------
            // flow-mapped noise
            // ------------------------------------------------------------------------------------------------

            // Two copies of the noise carried along vel, half a period apart, crossfaded so each is replaced
            // before it stretches; the blend is renormalised so the contrast doesn't pulse.
            float FlowNoise(float3 p, float3 vel, float freq, float period, float3 seed, int ch)
            {
                float ph = _Anim.x / period;
                float a = frac(ph), b = frac(ph + 0.5);
                float wa = 1.0 - abs(2.0 * a - 1.0), wb = 1.0 - wa;
                float3 pa = (p - vel * (a * period)) * freq + seed + float3(0.37, 0.71, 0.13) * floor(ph);
                float3 pb = (p - vel * (b * period)) * freq + seed + float3(0.59, 0.23, 0.87) * floor(ph + 0.5) + 0.5;
                float4 sa = tex3Dlod(_Noise, float4(pa, 0));
                float4 sb = tex3Dlod(_Noise, float4(pb, 0));
                float na = ch == 0 ? sa.r : sa.g, nb = ch == 0 ? sb.r : sb.g;
                float n = wa * na + wb * nb;
                return saturate(0.6 + (n - 0.6) / sqrt(wa * wa + wb * wb));
            }

            // cap flow: poloidal roll (up the middle, out over the top, down the outside, in underneath) plus
            // expansion, in cap-relative metres
            float3 CapFlow(float3 q, out float coreDist)
            {
                float cs = _CapCenter.w;
                float rxz = length(q.xz);
                float2 dir = rxz > 1e-3 ? q.xz / rxz : float2(1.0, 0.0);
                float ringR = _CapShape.x * cs * _CapXZ.x;
                float2 v = float2(rxz - ringR, q.y);           // offset from the tube core in its plane
                coreDist = length(v) / max(max(_CapShape.y, _CapShape.z) * cs, 1.0);
                float k = exp(-dot(v, v) / (1.5 * cs * cs));
                float om = _Anim.y * k;
                float3 rad = float3(dir.x, 0.0, dir.y);
                // relative to the cap the air above blows downwind and the air below lags: billows stream off the
                // downwind side
                float3 shear = float3(_Wind.x, 0.0, _Wind.z) * ((_WindFlow.y - _WindFlow.x) * 0.5 * q.y / max(cs, 1.0) + 0.15 * _WindFlow.y * smoothstep(0.0, cs, dot(q.xz, _Wind.xz)));
                return om * (rad * v.y - float3(0.0, 1.0, 0.0) * v.x) + q * _CapXZ.w + shear;
            }

            // ------------------------------------------------------------------------------------------------
            // the cloud
            // ------------------------------------------------------------------------------------------------

            struct Sample
            {
                float density;
                float3 albedo;
                float cap;       // fraction of the density that is cap (fire can only live there)
                float core;      // distance from the cap's hot core, 0..1+
                float fireN;     // convective flicker noise
                float hf;        // height up the cloud 0..1
            };

            float CapSDF(float3 p, float n, out float3 q)
            {
                float cs = _CapCenter.w;
                q = p - _CapCenter.xyz;
                // wind shear leans the upper cap downwind, and the cloud stretches out downwind as it ages
                float3 qs = q;
                qs.xz -= _Wind.xz * (_Wind.w * q.y);
                qs = Stretch(qs, _WindFlow.z);
                float3 c = qs / cs;
                c.xz /= max(_CapXZ.x, 0.2);
                float rxz0 = length(c.xz);
                c.y += _CapXZ.y * 0.35 * smoothstep(0.7, 1.25, rxz0) * (1.0 - smoothstep(-0.3, 0.25, c.y));
                c.y /= _CapShape.w;
                float torus = length(float2(length(c.xz) - _CapShape.x, c.y)) - _CapShape.y;
                float dome = length(c - float3(0.0, 0.18, 0.0)) - _CapShape.z;
                float rough = lerp(0.18, 1.0, _CapXZ.z);
                // a few big lopsided lobes, so no two caps (and no two sides of one) are the same shape
                float lobe = tex3Dlod(_Noise, float4(q * (_Density.w * 0.3) + float3(0.71, 0.29, 0.53), 0)).r;
                float lobes = (0.62 - lobe) * 0.8 * cs * rough;
                return SMin(torus, dome, 0.25) * cs + lobes + (0.5 - n) * (0.75 + 0.45 * _Age.x) * cs * rough;
            }

            float Cond(float3 p, float nWisp)
            {
                float d = 0.0;
                // Wilson cloud: a thin shell just behind the shock
                if (_Wilson.z > 0.001)
                {
                    float r = length(p);
                    float x = (r - _Wilson.x) / max(_Wilson.y, 1.0);
                    float shell = exp(-x * x * 2.0);
                    if (_Wilson.w > 0.5) shell *= smoothstep(_GroundY.x, _GroundY.x + _Wilson.y, p.y);
                    d = max(d, shell * _Wilson.z * smoothstep(0.3, 0.75, nWisp));
                }
                // condensation rings: thin horizontal tori around the column
                [unroll] for (int i = 0; i < 3; i++)
                {
                    float4 rg = _Rings[i];
                    if (rg.w > 0.001)
                    {
                        float3 q = p - _RingOffset.xyz - float3(0.0, rg.x, 0.0);
                        float tor = length(float2(length(q.xz) - rg.y, q.y * 1.3)) / max(rg.z, 1.0);
                        d = max(d, exp(-tor * tor * 1.5) * rg.w * smoothstep(0.25, 0.8, nWisp));
                    }
                }
                // stem collars: drooping bells of mist wrapped around the stem
                [unroll] for (int j = 0; j < 5; j++)
                {
                    float4 cp = _CollarPos[j];
                    if (cp.w > 0.001)
                    {
                        float4 sh = _CollarShape[j];
                        float3 q = p - cp.xyz;
                        float rr = length(q.xz);
                        float y = q.y + sh.z * rr;                   // bell: lower further out
                        float band = 1.0 - saturate(abs(y) / max(sh.y, 1.0));
                        float span = 1.0 - smoothstep(sh.x * 0.75, sh.x, rr);
                        d = max(d, band * band * span * cp.w * smoothstep(0.25, 0.8, nWisp));
                    }
                }
                return d;
            }

            Sample Cloud(float3 p, bool detail)
            {
                Sample s;
                s.density = 0.0; s.albedo = _CapAlbedo.rgb; s.cap = 0.0; s.core = 1.0; s.fireN = 0.5; s.hf = 0.0;
                float ns = _Density.w;
                float per = _Anim.w;

                float topY = _CapCenter.y + (_CapShape.y + 0.3) * _CapCenter.w * _CapShape.w;
                s.hf = saturate((p.y - _GroundY.x) / max(topY - _GroundY.x, 1.0));

                // ---- cap / fireball ----
                float3 q;
                float capBound = CapSDF(p, 1.0, q);
                float capDen = 0.0;
                if (capBound < 0.0)
                {
                    float core;
                    float3 vel = CapFlow(q, core);
                    float big = FlowNoise(q, vel, ns, per, float3(0.0, 0.0, 0.0), 0);
                    float mid = FlowNoise(q, vel, ns * 2.3, per * 0.7, float3(0.31, 0.17, 0.53), 0);
                    float n = smoothstep(0.42, 0.78, 0.65 * big + 0.35 * mid);
                    float sdf = CapSDF(p, n, q);
                    float soft = 0.12 * _CapCenter.w * (1.0 + 2.0 * _Age.x);
                    // the downwind end is older, sheared-off cloud: thinner and more ragged
                    float along = dot(q.xz, _Wind.xz) / (_CapCenter.w * (1.0 + _WindFlow.z));
                    float tail = lerp(1.0, 0.3, saturate(along * 0.8) * saturate(_WindFlow.z));
                    capDen = saturate(-sdf / soft + 0.1) * _Density.x * (1.0 - 0.75 * _Age.x) * tail;
                    if (detail && capDen > 0.0)
                    {
                        float fine = FlowNoise(q, vel, ns * 4.2, per * 0.5, float3(0.7, 0.2, 0.4), 1);
                        capDen = saturate(capDen - (1.0 - fine) * 0.1 * lerp(0.3, 1.0, _CapXZ.z));
                        // fast convective flicker for the fire, rising through the ball
                        s.fireN = FlowNoise(q, vel * 1.8 + float3(0.0, 0.25 * _CapCenter.w * _CapXZ.w + 20.0, 0.0), ns * 3.1, per * 0.35, float3(0.11, 0.83, 0.29), 0);
                    }
                    s.core = core;
                }

                // ---- stem: updraft with a slow twist, billows stretched along it ----
                float stemDen = 0.0;
                if (_StemVis.z > 0.001)
                {
                    float u;
                    float3 axis;
                    float d0 = BentStem(p, u, axis);
                    float flare = 1.0 + _StemVis.w * pow(1.0 - u, 6.0);
                    float localR = lerp(_StemBase.w, _StemTop.w, u) * flare;
                    if (d0 - (flare - 1.0) * _StemBase.w - 0.45 * localR < 0.0)
                    {
                        float3 r = p - axis;
                        float tw = u * 2.5 + _Anim.x * 0.06;
                        float st = sin(tw), ct = cos(tw);
                        float3 sq = float3(ct * r.x - st * r.z, (p.y - _GroundY.x) * 0.35, st * r.x + ct * r.z);
                        float3 sv = float3(0.0, 0.35 * _Anim.z, 0.0) + float3(_Wind.x, 0.0, _Wind.z) * (0.3 * lerp(_WindFlow.x, _WindFlow.y, u));
                        float n = FlowNoise(sq, sv, ns * 1.4, per, float3(0.43, 0.61, 0.07), 0);
                        n = smoothstep(0.38, 0.8, n);
                        float sdf = d0 - (flare - 1.0) * _StemBase.w + (0.5 - n) * 0.9 * localR;
                        float soft = 0.3 * localR;
                        float mask = max(smoothstep(_StemVis.x, _StemVis.x - soft * 2.0, p.y),
                                         smoothstep(_StemVis.y, _StemVis.y + soft * 2.0, p.y));
                        stemDen = saturate(-sdf / max(soft, 1.0)) * _StemVis.z * mask * (1.0 - 0.85 * _Age.x);
                    }
                }

                // ---- dust skirt and base surge: rolling outward along the ground ----
                float dustDen = 0.0;
                float3 sp = Stretch(p - _SkirtCenter.xyz, _WindFlow.w);
                float rxz = length(sp.xz);
                float2 rdir = rxz > 1e-3 ? sp.xz / rxz : float2(1.0, 0.0);
                if (_Skirt.y > 0.001 && rxz < _SkirtCenter.w * 1.5 && sp.y < _Skirt.x * 2.0)
                {
                    float3 vel = float3(rdir.x, 0.25, rdir.y) * _Skirt.w + float3(_Wind.x, 0.0, _Wind.z) * (0.6 * _WindFlow.x);
                    float n = smoothstep(0.35, 0.8, FlowNoise(sp, vel, ns * 1.3, per, float3(0.2, 0.9, 0.5), 0));
                    float rr = rxz / max(_SkirtCenter.w, 1.0);
                    // a dome-topped mound: tallest around the stem's foot, sloping out to the rim
                    float top = _Skirt.x * lerp(1.0, 0.35, rr * rr);
                    float sd = max(rr - 1.0, (sp.y - top) / max(_Skirt.x, 1.0)) + (0.5 - n) * 0.9;
                    dustDen = saturate(-sd / 0.18) * _Skirt.y;
                }
                if (_Surge.z > 0.001)
                {
                    float3 gp = Stretch(p - float3(_SkirtCenter.x, _GroundY.x, _SkirtCenter.z), _WindFlow.w * 0.5);
                    float gr = length(gp.xz);
                    float2 gdir = gr > 1e-3 ? gp.xz / gr : float2(1.0, 0.0);
                    float3 vel = float3(gdir.x, 0.05, gdir.y) * _Skirt.w * 2.0;
                    float n = smoothstep(0.35, 0.8, FlowNoise(gp, vel, ns * 2.0, per * 0.8, float3(0.8, 0.4, 0.1), 0));
                    float tor = length(float2((gr - _Surge.x) / max(_Surge.w, 1.0), gp.y / max(_Surge.y, 1.0))) - 1.0 + (0.5 - n) * 1.2;
                    dustDen = max(dustDen, saturate(-tor / 0.2) * _Surge.z);
                }

                // ---- condensation (wispy, white) ----
                float condDen = 0.0;
                if (_Wilson.z + _Rings[0].w + _Rings[1].w + _Rings[2].w + _CollarPos[0].w + _CollarPos[1].w + _CollarPos[2].w + _CollarPos[3].w + _CollarPos[4].w > 0.001)
                {
                    float wisp = FlowNoise(p, float3(0.0, 2.0, 0.0), ns * 3.5, per, float3(0.5, 0.5, 0.5), 1);
                    condDen = Cond(p, wisp);
                }

                float total = capDen + stemDen + dustDen + condDen;
                s.density = max(max(capDen, stemDen), max(dustDen, condDen));
                if (total > 1e-4)
                {
                    float inv = 1.0 / total;
                    s.albedo = (capDen * _CapAlbedo.rgb + stemDen * _StemAlbedo.rgb + dustDen * _DustAlbedo.rgb + condDen * _CondAlbedo.rgb) * inv;
                    s.cap = capDen * inv;
                }
                return s;
            }

            // cheap occluder for the shadow and sky-occlusion marches: shapes with mid-value noise
            float CloudCheap(float3 p)
            {
                float3 q;
                float cap = saturate(-CapSDF(p, 0.55, q) / (0.25 * _CapCenter.w) + 0.15) * _Density.x;
                float stem = 0.0;
                if (_StemVis.z > 0.001)
                {
                    float u;
                    float3 axis;
                    float d0 = BentStem(p, u, axis);
                    float flare = 1.0 + _StemVis.w * pow(1.0 - u, 6.0);
                    float localR = lerp(_StemBase.w, _StemTop.w, u) * flare;
                    stem = saturate(-(d0 - (flare - 1.0) * _StemBase.w) / max(0.3 * localR, 1.0)) * _StemVis.z;
                }
                return max(cap, stem);
            }

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
                    if (t >= t1 || T < 0.008) break;
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
                            od += (j < 3 ? Cloud(spos, false).density : CloudCheap(spos)) * sl;
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
                        float occ = CloudCheap(p + float3(0.0, 0.35 * cs, 0.0)) + CloudCheap(p + float3(0.0, 0.8 * cs, 0.0));
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
                        t += dt * 1.6; // skip empty space faster
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
