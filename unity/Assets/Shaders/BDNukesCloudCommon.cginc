// Shared by BDNukesVolumetricCloud.shader and BDNukesCloudShadow.shader: the uniforms and the cloud's density
// field (shapes, flow-mapped noise, the baked fireball). Included after UnityCG.cginc.
#ifndef BDNUKES_CLOUD_COMMON
#define BDNUKES_CLOUD_COMMON


sampler3D _Noise;
sampler3D _SimA, _SimB;   // baked fluid simulation: R smoke (sqrt), G flame, B temperature
float4 _Sim;              // x blend A->B, y mix (1 = the sim is the fireball), z fire strength, w density scale
float4 _SimOrigin;        // xyz local min corner of the sim box
float4 _SimSize;          // xyz local size of the sim box
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
float4 _Ember;       // x glow of the burning ring inside the cap's vortex, y its temperature, z core width (x tube)
float4 _Planet;      // xyz planet centre (local), w radius below which nothing is drawn (the planet hides it)
float4 _Pileus;      // x height above the cap surface (m), y thickness (m), z density, w unused

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
    float skip;      // conservative distance to the nearest possible cloud (0 inside or unknown)
    float capY;      // height relative to the cap centre, in cap scales (negative = underside)
    float simHeat;   // baked fireball heat at this point (0 outside the sim)
};

// lobeMode: 0 = sample the lobe noise, 1 = most inflated (a cheap bound), 2 = average (cheap occluder)
float CapSDF(float3 p, float n, float lobeMode, out float3 q)
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
    c.y += _CapXZ.y * 0.45 * smoothstep(0.65, 1.2, rxz0) * (1.0 - smoothstep(-0.4, 0.3, c.y));
    c.y /= _CapShape.w;
    float torus = length(float2(length(c.xz) - _CapShape.x, c.y)) - _CapShape.y;
    float dome = length(c - float3(0.0, 0.18, 0.0)) - _CapShape.z;
    float rough = lerp(0.18, 1.0, _CapXZ.z);
    // a few big lopsided lobes, so no two caps (and no two sides of one) are the same shape
    float lobe = lobeMode < 0.5 ? tex3Dlod(_Noise, float4(q * (_Density.w * 0.3) + float3(0.71, 0.29, 0.53), 0)).r
                                : (lobeMode < 1.5 ? 0.9 : 0.62);
    float lobes = (0.62 - lobe) * 0.8 * cs * rough;
    return SMin(torus, dome, 0.25) * cs + lobes + (0.5 - n) * (0.62 + 0.45 * _Age.x) * cs * rough;
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
    s.skip = 0.0; s.capY = 0.0; s.simHeat = 0.0;
    float ns = _Density.w;
    float per = _Anim.w;

    float topY = _CapCenter.y + (_CapShape.y + 0.3) * _CapCenter.w * _CapShape.w;
    s.hf = saturate((p.y - _GroundY.x) / max(topY - _GroundY.x, 1.0));

    // ---- cap / fireball ----
    float3 q;
    float capBound = CapSDF(p, 1.0, 1.0, q);
    float capDen = 0.0;
    // the cap's distance field is stretched by its squash and spread: scale it down to stay safe
    float skip = capBound * 0.6 * min(min(_CapShape.w, _CapXZ.x), 1.0);
    if (capBound < 0.0)
    {
        float core;
        float3 vel = CapFlow(q, core);
        // domain warp: a slowly evolving large-scale field bends the billow lookups, so lumps curl,
        // fold and roll into each other like a fluid instead of sliding as fixed blobs
        float cs0 = _CapCenter.w;
        float3 wq = q * (ns * 0.45) + float3(0.13, 0.57, 0.31) + _Anim.x * float3(0.006, 0.011, 0.004);
        float2 wa = tex3Dlod(_Noise, float4(wq, 0)).rg;
        float wb = tex3Dlod(_Noise, float4(wq.zxy + float3(0.43, 0.21, 0.77), 0)).r;
        float3 warp = (float3(wa.x, wb, wa.y) - float3(0.68, 0.68, 0.42)) * (0.3 * cs0 * lerp(0.3, 1.0, _CapXZ.z));
        float3 qw = q + warp;
        float big = FlowNoise(qw, vel, ns, per, float3(0.0, 0.0, 0.0), 0);
        // the mid billows ride the flow a little faster than the big ones: they boil against each other
        float mid = FlowNoise(qw, vel * 1.35, ns * 2.3, per * 0.7, float3(0.31, 0.17, 0.53), 0);
        float n = smoothstep(0.38, 0.82, 0.9 * big + 0.1 * mid);
        float sdf = CapSDF(p, n, 0.0, q);
        float soft = 0.22 * _CapCenter.w * (1.0 + 2.0 * _Age.x);
        // the downwind end is older, sheared-off cloud: thinner and more ragged
        float along = dot(q.xz, _Wind.xz) / (_CapCenter.w * (1.0 + _WindFlow.z));
        float tail = lerp(1.0, 0.3, saturate(along * 0.8) * saturate(_WindFlow.z));
        capDen = saturate(-sdf / soft + 0.1) * _Density.x * (1.0 - 0.75 * _Age.x) * tail;
        if (detail && capDen > 0.0)
        {
            
            // (no fine-grain erosion: the cap stays smooth, rounded billows)
            // fast convective flicker for the fire, rising through the ball
            if (_Fire.x + _Ember.x > 0.001 || _March.w > 0.5) s.fireN = FlowNoise(q, vel * 1.8 + float3(0.0, 0.25 * _CapCenter.w * _CapXZ.w + 20.0, 0.0), ns * 3.1, per * 0.35, float3(0.11, 0.83, 0.29), 0);
        }
        s.core = core;
        s.capY = q.y / max(_CapCenter.w, 1.0);
        // the burning ring is hot, thin gas rather than smoke: its glow shines out through the cap
        if (_Ember.x > 0.001)
        {
            float cw = max(_Ember.z, 0.05);
            float ring = exp(-(core * core) / (cw * cw));
            capDen *= lerp(1.0, 0.3, ring * saturate(_Ember.x));
        }
    }

    // ---- baked fluid simulation: the fireball itself while it burns and rolls up ----
    if (_Sim.y > 0.001)
    {
        float3 su = (p - _SimOrigin.xyz) / max(_SimSize.xyz, 1.0);
        float3 bd = max(max(-su, su - 1.0), 0.0) * _SimSize.xyz;
        skip = min(skip, length(bd) * 0.9);
        float simDen = 0.0;
        if (all(su > 0.0) && all(su < 1.0))
        {
            float4 sv = lerp(tex3Dlod(_SimA, float4(su, 0)), tex3Dlod(_SimB, float4(su, 0)), _Sim.x);
            simDen = sv.r * sv.r;
            if (detail && simDen > 0.003)
            {
                // sub-voxel detail riding the rising gas, at the sim's own scale: cauliflower folds a few voxels
                // across and finer flame wisps, so the soft voxels read as sharp, folded fire
                float sf = 1.0 / max(_SimSize.y, 1.0);
                float3 sp = p - _SimOrigin.xyz;
                float fold = FlowNoise(sp, float3(0.0, 0.05 * _SimSize.y, 0.0), sf * 9.0, per * 0.4, float3(0.27, 0.61, 0.19), 0);
                float wisp = FlowNoise(sp, float3(0.0, 0.09 * _SimSize.y, 0.0), sf * 26.0, per * 0.3, float3(0.71, 0.13, 0.47), 1);
                simDen = saturate(simDen * 1.7 - (1.0 - smoothstep(0.35, 0.8, fold)) * 0.32 - (1.0 - wisp) * 0.1 - 0.01);
            }
            simDen *= _Sim.w;
            // heat from the smooth temperature field, the flame field only adding to it: the flame's thin outer
            // tongues would otherwise glow as hard as the core and read as spikes
            s.simHeat = max(0.6 * sv.g, 0.85 * sv.b) * _Sim.y * saturate(simDen * 4.0);
        }
        capDen = lerp(capDen, simDen, _Sim.y);
        if (simDen > 0.0) s.core = lerp(s.core, 1.0 - sqrt(saturate(s.simHeat)), _Sim.y);
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
        float stemOut = d0 - (flare - 1.0) * _StemBase.w - 0.45 * localR;
        skip = min(skip, stemOut * 0.6);
        if (stemOut < 0.0)
        {
            float3 r = p - axis;
            float tw = u * 2.5 + _Anim.x * 0.06;
            float st = sin(tw), ct = cos(tw);
            float3 sq = float3(ct * r.x - st * r.z, (p.y - _GroundY.x) * 0.35, st * r.x + ct * r.z);
            float3 sv = float3(0.0, 0.35 * _Anim.z, 0.0) + float3(_Wind.x, 0.0, _Wind.z) * (0.3 * lerp(_WindFlow.x, _WindFlow.y, u));
            float n = FlowNoise(sq, sv, ns * 1.4, per, float3(0.43, 0.61, 0.07), 0);
            n = smoothstep(0.38, 0.8, n);
            float sdf = d0 - (flare - 1.0) * _StemBase.w + (0.5 - n) * 1.35 * localR;
            float soft = 0.3 * localR;
            float mask = max(smoothstep(_StemVis.x, _StemVis.x - soft * 2.0, p.y),
                             smoothstep(_StemVis.y, _StemVis.y + soft * 2.0, p.y));
            stemDen = saturate(-sdf / max(soft, 1.0)) * _StemVis.z * mask * (1.0 - 0.85 * _Age.x);
        }
    }

    // ---- dust skirt and base surge: rolling outward along the ground ----
    float dustDen = 0.0;
    if (_Skirt.y + _Surge.z > 0.001)
    {
        // a box around the skirt and surge (stretch only pulls inward, so this stays conservative)
        float ext = max(_SkirtCenter.w * 1.5, _Surge.x + _Surge.w * 1.6) * (1.0 + _WindFlow.w);
        float3 bd = abs(p - _SkirtCenter.xyz - float3(0.0, _Skirt.x, 0.0)) - float3(ext, max(_Skirt.x * 2.0, _Surge.y * 2.0), ext);
        skip = min(skip, length(max(bd, 0.0)));
    }
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
        float sd = max(rr - 1.0, (sp.y - top) / max(_Skirt.x, 1.0)) + (0.5 - n) * 1.25;
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
    if (_Wilson.z + _Pileus.z + _Rings[0].w + _Rings[1].w + _Rings[2].w + _CollarPos[0].w + _CollarPos[1].w + _CollarPos[2].w + _CollarPos[3].w + _CollarPos[4].w > 0.001)
    {
        skip = 0.0; // thin condensation shells: no skipping while they're about
        float wisp = FlowNoise(p, float3(0.0, 2.0, 0.0), ns * 3.5, per, float3(0.5, 0.5, 0.5), 1);
        condDen = Cond(p, wisp);
        // pileus: a smooth veil of condensation draped over the top of the rising cap, where it
        // lifts moist air through its condensation level
        if (_Pileus.z * (1.0 - _Sim.y) > 0.001)
        {
            float3 pq;
            float sd = CapSDF(p, 0.55, 2.0, pq);
            float x = (sd - _Pileus.x) / max(_Pileus.y, 1.0);
            float top = smoothstep(-0.1, 0.35, pq.y / max(_CapCenter.w, 1.0));
            condDen = max(condDen, exp(-x * x * 2.0) * top * _Pileus.z * (1.0 - _Sim.y) * smoothstep(0.2, 0.7, wisp));
        }
    }

    s.skip = max(skip, 0.0);
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
    float cap = saturate(-CapSDF(p, 0.55, 2.0, q) / (0.25 * _CapCenter.w) + 0.15) * _Density.x;
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
    if (_Sim.y > 0.001)
    {
        float3 su = (p - _SimOrigin.xyz) / max(_SimSize.xyz, 1.0);
        if (all(su > 0.0) && all(su < 1.0))
        {
            float r = tex3Dlod(_SimA, float4(su, 0)).r;
            cap = lerp(cap, r * r * _Sim.w, _Sim.y);
        }
        else cap *= 1.0 - _Sim.y;
    }
    return max(cap, stem);
}

// ------------------------------------------------------------------------------------------------

#endif
