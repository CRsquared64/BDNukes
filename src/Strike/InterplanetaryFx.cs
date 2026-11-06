using System;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// A spent Starlance stage falling away: the stage's slice of the procedural model (its "detach" transform,
    /// with its nozzles and their plumes, which ModuleNukePlume hides once they leave the part) is unparented and
    /// flown here as a simple ballistic body for a while, then removed. Not a vessel: no collisions, no persistence.
    ///
    /// The position is kept relative to the body's centre in double precision and integrated in the world frame
    /// (KSP's world moves under the active vessel: FloatingOrigin shifts the body with everything else, and
    /// Krakensbane's frame velocity is added in at the start), with gravity, a little drag in air and a slow tumble.
    /// </summary>
    internal class InterplanetaryStageDebris : MonoBehaviour
    {
        CelestialBody body;
        Vector3d rel, vel;
        Vector3 spin;
        float until;
        Vessel near;

        public static void Drop(Transform stage, Part from, Vector3 push, float life)
        {
            if (stage == null || from == null || from.vessel == null) return;
            var d = stage.gameObject.AddComponent<InterplanetaryStageDebris>();
            d.body = from.vessel.mainBody;
            d.near = from.vessel;
            stage.SetParent(null, true);
            d.rel = (Vector3d)stage.position - d.body.position;
            Vector3d v = from.rb != null ? (Vector3d)from.rb.velocity : from.vessel.srf_velocity;
            d.vel = v + Krakensbane.GetFrameVelocity() + (Vector3d)push;
            d.spin = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(4f, 12f);
            d.until = Time.time + life;
        }

        void LateUpdate()
        {
            if (body == null || Time.time > until || !HighLogic.LoadedSceneIsFlight)
            {
                Destroy(gameObject);
                return;
            }
            float dt = Time.deltaTime;
            if (dt > 0f)
            {
                Vector3d world = body.position + rel;
                Vector3d g = FlightGlobals.getGeeForceAtPosition(world, body);
                Vector3d a = g;
                if (body.atmosphere)
                {
                    double alt = rel.magnitude - body.Radius;
                    if (alt < body.atmosphereDepth)
                    {
                        double p = FlightGlobals.getStaticPressure(alt, body);             // kPa
                        double rho = p * 1000d / (287d * 240d);
                        Vector3d vs = vel - body.getRFrmVel(world);
                        a -= vs * (vs.magnitude * rho * 0.0015d);                       // ~0.003 m^2/kg ballistic coefficient
                    }
                }
                vel += a * dt;
                rel += vel * dt;
                double ground = body.pqsController != null
                    ? Math.Max(0d, body.TerrainAltitude(body.GetLatitude(body.position + rel), body.GetLongitude(body.position + rel), true))
                    : 0d;
                if (rel.magnitude < body.Radius + ground)
                {
                    Destroy(gameObject);
                    return;
                }
            }
            transform.position = body.position + rel;
            transform.Rotate(spin * dt, Space.World);
            // far from everything that's watching: gone
            Vessel av = FlightGlobals.ActiveVessel;
            if (av != null && ((Vector3d)transform.position - av.CoMD).sqrMagnitude > 4e10) Destroy(gameObject);
        }
    }

    /// <summary>
    /// The light drive's look: a growing blue-white glow while it charges, a blinding flash and a streak along the
    /// path at the jump, the same flash on arrival. Owned by ModuleNukeInterplanetary; a light, a LineRenderer and
    /// fades, nothing more.
    /// </summary>
    internal class InterplanetaryDriveFx
    {
        readonly Part part;
        GameObject go;
        Light light;
        LineRenderer streak;
        float flashStart = -100f, flashLen = 2.2f;
        float streakStart = -100f;
        public float charge;   // 0..1

        static readonly Color DriveColor = new Color(0.55f, 0.8f, 1f);

        public InterplanetaryDriveFx(Part p) { part = p; }

        void Ensure()
        {
            if (go != null) return;
            NukeAssets.Init();
            go = new GameObject("BDNukes_StarlanceDrive");
            light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = DriveColor;
            light.shadows = LightShadows.None;
            light.intensity = 0f;
            light.range = 200f;

            var sg = new GameObject("Streak");
            sg.transform.SetParent(go.transform, false);
            streak = sg.AddComponent<LineRenderer>();
            streak.useWorldSpace = true;
            streak.positionCount = 2;
            streak.sharedMaterial = NukeAssets.Bolt != null ? NukeAssets.Bolt : NukeAssets.Glow;
            streak.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            streak.receiveShadows = false;
            streak.enabled = false;
        }

        public void Flash(bool withStreak)
        {
            Ensure();
            flashStart = Time.time;
            if (withStreak) streakStart = Time.time;
        }

        public void Update(Vector3 pos, Vector3 velDir)
        {
            bool flashing = Time.time - flashStart < flashLen;
            bool streaking = Time.time - streakStart < 3f;
            if (charge <= 0.001f && !flashing && !streaking)
            {
                if (go != null && go.activeSelf) go.SetActive(false);
                return;
            }
            Ensure();
            if (!go.activeSelf) go.SetActive(true);
            go.transform.position = pos;

            float k = flashing ? 1f - (Time.time - flashStart) / flashLen : 0f;
            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.time * (6f + 30f * charge));
            light.intensity = Mathf.Max(charge * charge * 4f * pulse, k * k * 8f);
            light.range = Mathf.Max(40f + 260f * charge, k * 6000f);

            if (streaking && velDir.sqrMagnitude > 0.5f)
            {
                float t = (Time.time - streakStart) / 3f;
                float len = Mathf.Lerp(500f, 60000f, Mathf.Sqrt(t));
                float a = 1f - t;
                streak.enabled = true;
                streak.SetPosition(0, pos);
                streak.SetPosition(1, pos - velDir.normalized * len);
                streak.startWidth = 14f * (0.3f + a);
                streak.endWidth = 1f;
                var c0 = new Color(0.85f, 0.95f, 1f, a);
                var c1 = new Color(DriveColor.r, DriveColor.g, DriveColor.b, 0f);
                streak.startColor = c0;
                streak.endColor = c1;
            }
            else if (streak.enabled) streak.enabled = false;
        }

        public void Destroy()
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            go = null;
        }
    }
}
