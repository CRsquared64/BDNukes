using System.Collections.Generic;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Retarding parachute for heavy free-fall nuclear bombs. The real Tsar Bomba used a ~1600 m^2 chute
    /// so the Tu-95V could get ~45 km away before the 188 s fall to its 4 km burst altitude ended.
    ///
    /// Drag is applied as F = 1/2 rho v^2 Cd A along -v (surface frame) once the bomb is released.
    /// The canopy and rigging are generated procedurally and trail behind the bomb along its velocity.
    /// </summary>
    public class ModuleNukeParachute : PartModule
    {
        [KSPField] public float canopyArea = 1600f;      // m^2
        [KSPField] public float dragCoefficient = 0.8f;
        [KSPField] public float deployDelay = 2f;        // s after release
        [KSPField] public float inflationTime = 3f;      // s from pack-out to full inflation
        [KSPField] public float riserLength = 45f;       // m from tail to canopy skirt
        [KSPField] public float minDensity = 0.005f;     // won't deploy in near-vacuum
        [KSPField] public string attachPoint = "0, 0, -4"; // part-local point the risers meet (tail)

        [KSPField(isPersistant = true, guiActive = true, guiName = "Retard Chute")]
        [UI_Toggle(disabledText = "Off", enabledText = "Auto", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.All)]
        public bool autoDeploy = true;

        [KSPField(isPersistant = true)] public bool deployed;
        [KSPField(isPersistant = true)] public bool cut;

        Vessel startVessel;
        float releaseTime = -1f;
        float deployTime;
        Vector3 attachLocal;
        GameObject canopy;
        LineRenderer[] risers;
        Vector3[] rimLocal;

        [KSPEvent(guiActive = true, guiName = "Deploy Chute")]
        public void DeployEvent() { Deploy(); }

        [KSPEvent(guiActive = true, guiName = "Cut Chute")]
        public void CutEvent() { Cut(); }

        [KSPAction("Deploy Chute")]
        public void DeployAction(KSPActionParam p) { Deploy(); }

        [KSPAction("Cut Chute")]
        public void CutAction(KSPActionParam p) { Cut(); }

        public override void OnStart(StartState state)
        {
            attachLocal = ParseVector(attachPoint);
            if (!HighLogic.LoadedSceneIsFlight) return;
            startVessel = vessel;
            if (deployed && !cut) { deployTime = Time.time - inflationTime; ShowCanopy(true); }
        }

        static Vector3 ParseVector(string s)
        {
            var p = s.Split(',');
            if (p.Length != 3) return Vector3.zero;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Float;
            float.TryParse(p[0].Trim(), ns, ci, out float x); float.TryParse(p[1].Trim(), ns, ci, out float y); float.TryParse(p[2].Trim(), ns, ci, out float z);
            return new Vector3(x, y, z);
        }

        void Deploy()
        {
            if (deployed || cut || !HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || vessel.atmDensity < minDensity) return;
            deployed = true;
            deployTime = Time.time;
            ShowCanopy(true);
        }

        void Cut()
        {
            if (!deployed || cut) return;
            cut = true;
            ShowCanopy(false);
        }

        void OnDestroy()
        {
            if (canopy != null) Destroy(canopy);
        }

        public void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || vessel == null || vessel.packed) return;

            if (releaseTime < 0f && startVessel != null && vessel != startVessel) releaseTime = Time.time;
            if (autoDeploy && !deployed && !cut && releaseTime >= 0f && Time.time - releaseTime > deployDelay) Deploy();
            if (!deployed || cut) return;

            Vector3 v = vessel.srf_velocity;
            float speed = v.magnitude;
            if (speed < 0.5f) return;
            float inflate = Inflation();
            // kN: KSP rigidbody masses are in tonnes
            float drag = 0.5f * (float)vessel.atmDensity * speed * speed * dragCoefficient * canopyArea * inflate / 1000f;
            part.AddForceAtPosition(-v / speed * drag, part.transform.TransformPoint(attachLocal));
        }

        float Inflation()
        {
            float k = Mathf.Clamp01((Time.time - deployTime) / Mathf.Max(0.1f, inflationTime));
            return k * k * (3f - 2f * k);
        }

        void LateUpdate()
        {
            if (canopy == null || !canopy.activeSelf || vessel == null) return;
            Vector3 v = vessel.srf_velocity;
            Vector3 back = v.sqrMagnitude > 1f ? -v.normalized : (Vector3)vessel.upAxis;

            Vector3 tail = part.transform.TransformPoint(attachLocal);
            float inflate = Inflation();
            float breathe = 1f + 0.03f * Mathf.Sin(Time.time * 2.3f);
            float lateral = Mathf.Lerp(0.08f, 1f, inflate) * breathe;
            float length = riserLength * Mathf.Lerp(0.6f, 1f, inflate);

            canopy.transform.position = tail + back * length;
            canopy.transform.rotation = Quaternion.FromToRotation(Vector3.up, back);
            canopy.transform.localScale = new Vector3(lateral, Mathf.Lerp(1.4f, 1f, inflate), lateral);

            for (int i = 0; i < risers.Length; i++)
            {
                risers[i].SetPosition(0, tail);
                risers[i].SetPosition(1, canopy.transform.TransformPoint(rimLocal[i]));
            }
        }

        void ShowCanopy(bool show)
        {
            if (show && canopy == null) BuildCanopy();
            if (canopy != null) canopy.SetActive(show);
        }

        // ----------------------------------------------------------------------------------------
        // Canopy: shallow dome with alternating orange/white gores and a vent at the apex,
        // double-sided so it reads from inside too. Unparented (world space), positioned every frame.
        // ----------------------------------------------------------------------------------------

        void BuildCanopy()
        {
            float r = Mathf.Sqrt(canopyArea / Mathf.PI);
            const int gores = 28, rings = 10;
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();
            for (int j = 0; j <= rings; j++)
            {
                float a = Mathf.Lerp(0.08f, 1f, j / (float)rings) * Mathf.PI * 0.5f; // 0.08 leaves an apex vent
                float ringR = Mathf.Sin(a) * r;
                float y = Mathf.Cos(a) * r * 0.6f;
                for (int g = 0; g <= gores; g++)
                {
                    float th = g / (float)gores * 2f * Mathf.PI;
                    // gores bulge slightly between seams
                    float bulge = 1f + 0.035f * Mathf.Abs(Mathf.Sin(th * gores * 0.5f));
                    verts.Add(new Vector3(Mathf.Cos(th) * ringR * bulge, y, Mathf.Sin(th) * ringR * bulge));
                    bool orange = g % 2 == 0;
                    cols.Add(orange ? new Color(0.95f, 0.45f, 0.12f) : new Color(0.93f, 0.92f, 0.88f));
                }
            }
            int stride = gores + 1;
            for (int j = 0; j < rings; j++)
                for (int g = 0; g < gores; g++)
                {
                    int a = j * stride + g, b = a + 1, c = a + stride, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                    // back faces
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
            var mesh = new Mesh { name = "NukeChuteCanopy" };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            canopy = new GameObject("BDNukes_Canopy");
            canopy.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = canopy.AddComponent<MeshRenderer>();
            mr.sharedMaterial = VertexColorMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // risers: one per two gores, from the skirt to the tail
            int n = gores / 2;
            risers = new LineRenderer[n];
            rimLocal = new Vector3[n];
            var lineMat = VertexColorMaterial();
            for (int i = 0; i < n; i++)
            {
                rimLocal[i] = verts[rings * stride + i * 2];
                var go = new GameObject("Riser" + i);
                go.transform.SetParent(canopy.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = lr.endWidth = 0.12f;
                lr.sharedMaterial = lineMat;
                lr.startColor = lr.endColor = new Color(0.85f, 0.83f, 0.78f);
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                risers[i] = lr;
            }
            canopy.SetActive(false);
        }

        static Material vertexColorMat;

        /// <summary>A lit-looking opaque material that uses mesh vertex colours.</summary>
        static Material VertexColorMaterial()
        {
            if (vertexColorMat != null) return vertexColorMat;
            Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("KSP/Unlit") ?? Shader.Find("KSP/Diffuse");
            vertexColorMat = new Material(sh) { color = new Color(0.9f, 0.9f, 0.9f, 1f) };
            return vertexColorMat;
        }
    }
}
