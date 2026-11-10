using System;
using System.Collections.Generic;
using BDArmory.Weapons.Missiles;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A MIRV bus releasing its warheads in flight. On the way down, as the bus nears the top of the air (or at the
    /// top of a short arc that never gets that high), it pushes its re-entry vehicles off one by one; each drifts
    /// clear and then steers onto its own aim point in the footprint, so the salvo fans out on its way in: faint
    /// trails in vacuum, glowing re-entry streaks as the air thickens. They fly with the bus (positions kept as
    /// offsets from it, so floating origin and frame changes never matter), and when the bus bursts each warhead
    /// bursts where it actually is: the same height above its own ground, a moment apart as they arrive.
    /// </summary>
    internal class NukeMirvSalvo : MonoBehaviour
    {
        class RV
        {
            public Vector3 offset;        // m from the bus's CoM (world axes)
            public Vector3 vel;           // m/s relative to the bus
            public Vector3 aimVel;        // the sideways speed that lands it on its aim point
            public float releaseAt;       // s after the salvo starts
            public bool out_;             // pushed off the bus
            public GameObject go;
            public LineRenderer trail;    // streak back along the flight path
            public Material trailMat;
        }

        static readonly Dictionary<Part, NukeMirvSalvo> salvos = new Dictionary<Part, NukeMirvSalvo>();
        static Material bodyMat;
        static Mesh coneMesh;

        Part bus;
        Vessel vessel;
        readonly List<RV> rvs = new List<RV>();
        float t;
        bool detonated;

        /// <summary>The salvo a bus has released, or null.</summary>
        public static NukeMirvSalvo For(Part p) => p != null && salvos.TryGetValue(p, out var s) && s != null ? s : null;

        /// <summary>Starts the release from a bus: one RV per extra warhead, each with its point in the footprint.</summary>
        public static NukeMirvSalvo Release(ModuleMIRVBus mod)
        {
            var part = mod.part;
            Vessel v = part.vessel;
            CelestialBody body = v.mainBody;
            var go = new GameObject("BDNukes_MIRVSalvo");
            var s = go.AddComponent<NukeMirvSalvo>();
            s.bus = part;
            s.vessel = v;
            salvos[part] = s;

            Vector3 up = (Vector3)(v.CoMD - body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(v.latitude + 0.01, v.longitude, v.altitude) - v.CoMD), up).normalized;
            Vector3 east = Vector3.Cross(up, north);
            if (Vector3.Dot(east, (Vector3)(body.GetWorldSurfacePosition(v.latitude, v.longitude + 0.01, v.altitude) - v.CoMD)) < 0f) east = -east;

            // time left to the ground, roughly: the drift has that long to open the footprint out
            double agl = Math.Max(1000d, v.altitude - Math.Max(0d, v.terrainAltitude));
            double vz = Math.Min(-50d, v.verticalSpeed);
            double g = body.gravParameter / ((body.Radius + v.altitude) * (body.Radius + v.altitude));
            double tLeft = (vz + Math.Sqrt(vz * vz + 2d * g * agl)) / g;   // falling from agl with -vz down and g
            tLeft = Math.Max(10d, tLeft) * 1.15d;                          // the air slows the last part

            var offsets = mod.FootprintOffsets(mod.warheads - 1);
            Vector3 fwd = Vector3.ProjectOnPlane((Vector3)v.obt_velocity, up).normalized;
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 aim = east * offsets[i].x + north * offsets[i].y;
                // pushed off sideways and a little back, as a bus backs away from each RV it lets go
                Vector3 push = (aim.sqrMagnitude > 1f ? aim.normalized : Quaternion.AngleAxis(i * 137.5f, up) * east) * 1.5f - fwd * 0.6f;
                var rv = new RV
                {
                    offset = push * 0.8f,
                    vel = push,
                    aimVel = aim / (float)tLeft,
                    releaseAt = 0.2f + i * Mathf.Max(0.35f, mod.interval * 0.8f),
                };
                s.rvs.Add(rv);
            }
            Debug.Log($"[BDNukes]: MIRV bus {part.partInfo?.title}: releasing {offsets.Length} RVs at {v.altitude / 1000d:0.0} km, about {tLeft:0} s from the ground, over a {mod.spread:0} m footprint.");
            return s;
        }

        void MakeVisual(RV rv)
        {
            rv.go = new GameObject("BDNukes_RV");
            rv.go.layer = 0;
            var mf = rv.go.AddComponent<MeshFilter>();
            mf.sharedMesh = Cone();
            var mr = rv.go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = BodyMaterial();
            // a streak drawn back along the path, not a TrailRenderer: above ~750 m/s KSP holds the craft nearly still in
            // world space and moves the world instead (Krakensbane), so a recorded trail would only show the RV's slow
            // drift away from the bus, never its plunge
            rv.trail = rv.go.AddComponent<LineRenderer>();
            var sh = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
            rv.trailMat = new Material(sh);
            rv.trail.sharedMaterial = rv.trailMat;
            rv.trail.positionCount = 2;
            rv.trail.useWorldSpace = true;
            rv.trail.numCapVertices = 2;
            rv.trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rv.trail.receiveShadows = false;
        }

        static Material BodyMaterial()
        {
            if (bodyMat != null) return bodyMat;
            bodyMat = new Material(Shader.Find("KSP/Diffuse") ?? Shader.Find("Standard")) { color = new Color(0.22f, 0.2f, 0.19f) };
            return bodyMat;
        }

        /// <summary>A re-entry vehicle: a blunt cone ~2.5 m long, nose along +z.</summary>
        static Mesh Cone()
        {
            if (coneMesh != null) return coneMesh;
            const int N = 16;
            const float L = 2.5f, R = 0.3f, nose = 0.06f;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            verts.Add(new Vector3(0f, 0f, 0f));        // base centre
            for (int i = 0; i < N; i++)
            {
                float a = i * 2f * Mathf.PI / N;
                verts.Add(new Vector3(Mathf.Cos(a) * R, Mathf.Sin(a) * R, 0f));
                verts.Add(new Vector3(Mathf.Cos(a) * nose, Mathf.Sin(a) * nose, L));
            }
            verts.Add(new Vector3(0f, 0f, L));          // nose tip
            int tip = verts.Count - 1;
            for (int i = 0; i < N; i++)
            {
                int b0 = 1 + 2 * i, n0 = b0 + 1, b1 = 1 + 2 * ((i + 1) % N), n1 = b1 + 1;
                tris.AddRange(new[] { 0, b1, b0 });          // base
                tris.AddRange(new[] { b0, b1, n1, b0, n1, n0 }); // side
                tris.AddRange(new[] { n0, n1, tip });        // blunt nose
            }
            coneMesh = new Mesh { name = "BDNukesRV" };
            coneMesh.SetVertices(verts);
            coneMesh.SetTriangles(tris, 0);
            coneMesh.RecalculateNormals();
            coneMesh.RecalculateBounds();
            UnityEngine.Object.DontDestroyOnLoad(coneMesh);
            return coneMesh;
        }

        void FixedUpdate()
        {
            if (detonated) return;
            if (bus == null || vessel == null || vessel.state == Vessel.State.DEAD) { Finish(); return; }
            float dt = TimeWarp.fixedDeltaTime;
            t += dt;
            for (int i = 0; i < rvs.Count; i++)
            {
                var rv = rvs[i];
                if (!rv.out_)
                {
                    if (t < rv.releaseAt) continue;
                    rv.out_ = true;
                    MakeVisual(rv);
                }
                // after a few seconds clear of the bus it steers onto its aim point
                float since = t - rv.releaseAt;
                if (since > 3f) rv.vel = Vector3.MoveTowards(rv.vel, rv.aimVel, Mathf.Max(2f, rv.aimVel.magnitude * 0.25f) * dt);
                rv.offset += rv.vel * dt;
            }
        }

        void LateUpdate()
        {
            if (detonated || bus == null || vessel == null) return;
            Vector3 com = (Vector3)vessel.CoMD;
            Vector3 dir = (Vector3)vessel.srf_velocity;
            Quaternion rot = dir.sqrMagnitude > 1f ? Quaternion.LookRotation(dir.normalized) : bus.transform.rotation;
            // plasma: from ~1.5 km/s in air that's thick enough to matter
            float speed = (float)vessel.srfSpeed;
            float air = Mathf.Clamp01((float)vessel.atmDensity / 0.004f);
            float plasma = Mathf.Clamp01((speed - 1500f) / 3000f) * Mathf.Sqrt(air);
            Color head = Color.Lerp(new Color(0.75f, 0.8f, 0.9f, 0.12f), new Color(1f, 0.62f, 0.3f, 0.9f), plasma);
            // a faint short streak in vacuum, a long glowing one once it's burning in
            float len = Mathf.Lerp(40f, Mathf.Min(3000f, speed * 0.6f), plasma);
            Vector3 back = rot * Vector3.back;
            for (int i = 0; i < rvs.Count; i++)
            {
                var rv = rvs[i];
                if (!rv.out_ || rv.go == null) continue;
                Vector3 p = com + rv.offset;
                rv.go.transform.SetPositionAndRotation(p, rot);
                rv.trail.SetPosition(0, p);
                rv.trail.SetPosition(1, p + back * len);
                rv.trail.startWidth = Mathf.Lerp(0.5f, 9f, plasma);
                rv.trail.endWidth = Mathf.Lerp(0.1f, 30f, plasma);
                rv.trail.startColor = head;
                rv.trail.endColor = new Color(head.r, head.g * 0.8f, head.b * 0.8f, 0f);
            }
        }

        /// <summary>
        /// The bus has burst at `position`, `hAbove` m over its ground: each RV that's out bursts where it is, the
        /// same height over its own ground, a moment apart; the ones still aboard go off round the burst point as
        /// before. Returns the RVs' points and delays.
        /// </summary>
        public List<KeyValuePair<Vector3, float>> Detonate(CelestialBody body, Vector3 position, double hAbove, float interval)
        {
            var list = new List<KeyValuePair<Vector3, float>>();
            detonated = true;
            float vz = vessel != null ? Mathf.Max(100f, -(float)vessel.verticalSpeed) : 1000f;
            Vector3 up = ((Vector3)((Vector3d)position - body.position)).normalized;
            for (int i = 0; i < rvs.Count; i++)
            {
                var rv = rvs[i];
                if (!rv.out_) continue;
                Vector3 p = position + Vector3.ProjectOnPlane(rv.offset, up);
                double lat = body.GetLatitude(p), lon = body.GetLongitude(p);
                double ground = body.pqsController != null ? body.TerrainAltitude(lat, lon, true) : 0d;
                if (body.ocean && ground < 0d) ground = 0d;
                Vector3 burst = body.GetWorldSurfacePosition(lat, lon, ground + hAbove);
                // an RV still above the bus arrives a little later
                float delay = Mathf.Clamp(Vector3.Dot(rv.offset, up) / vz, 0f, 3f) + UnityEngine.Random.Range(0.05f, 0.5f) * Mathf.Max(0.2f, interval);
                list.Add(new KeyValuePair<Vector3, float>(burst, delay));
                if (rv.go != null) Destroy(rv.go, delay);
            }
            Destroy(gameObject, 4f);
            return list;
        }

        /// <summary>RVs not yet pushed off the bus when it went.</summary>
        public int StillAboard
        {
            get { int n = 0; foreach (var rv in rvs) if (!rv.out_) n++; return n; }
        }

        void Finish()
        {
            detonated = true;
            foreach (var rv in rvs) if (rv.go != null) Destroy(rv.go, 2f);
            Destroy(gameObject, 2.5f);
        }

        void OnDestroy()
        {
            foreach (var rv in rvs)
            {
                if (rv.go != null) Destroy(rv.go);
                if (rv.trailMat != null) Destroy(rv.trailMat);
            }
            if (bus != null && salvos.TryGetValue(bus, out var s) && s == this) salvos.Remove(bus);
        }
    }
}
