using System;
using BurstPQS;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

[assembly: KSPAssembly("BDNukes.BurstPQS", 1, 0)]
[assembly: KSPAssemblyDependency("BDNukes", 1, 3)]
[assembly: KSPAssemblyDependency("BurstPQS", 1, 0)]

namespace BDNukes.BurstPQSCompat
{
    /// <summary>
    /// BurstPQS fast path for BDNukes' crater terrain mod. Without it BurstPQS can't batch a body that has craters and
    /// falls back to building its whole surface the old way, which mods written only for the batch path (Mirage's
    /// terrain heights among them) don't survive: the ground comes out in spikes and pits. Same shape as
    /// NukeCrater.Offset and PQSMod_BDNukesCrater.SiteFade, in a job, added only for quads a crater reaches.
    /// </summary>
    [BurstCompile]
    [BatchPQSMod(typeof(PQSMod_BDNukesCrater))]
    internal class BatchPQSMod_BDNukesCrater : BatchPQSMod<PQSMod_BDNukesCrater>
    {
        const int CraterStride = 16;   // dir xyz, east xyz, north xyz, cosInfluence, radius, depth, rim, ph1, ph2, ph3
        const int SiteStride = 5;      // dir xyz, cos inner, cos outer

        [BurstCompile]
        struct HeightJob : IBatchPQSHeightJob, IDisposable
        {
            [ReadOnly] public NativeArray<double> craters;
            [ReadOnly] public NativeArray<double> sites;
            public int craterCount, siteCount;
            public double radius;
            public double reach;

            public void BuildHeights(in BuildHeightsData data)
            {
                var dirs = data.directionFromCenter;
                var heights = data.vertHeight;
                for (int i = 0; i < data.VertexCount; i++)
                {
                    Vector3d d = dirs[i];
                    double3 v = new double3(d.x, d.y, d.z);
                    double h = 0d;
                    for (int c = 0; c < craterCount; c++) h += Offset(v, c * CraterStride);
                    if (h == 0d) continue;
                    heights[i] += h * SiteFade(v);
                }
            }

            double Offset(double3 v, int o)
            {
                double3 dir = new double3(craters[o], craters[o + 1], craters[o + 2]);
                double cosInf = craters[o + 9];
                double c = math.dot(v, dir);
                if (c <= cosInf) return 0d;
                double3 east = new double3(craters[o + 3], craters[o + 4], craters[o + 5]);
                double3 north = new double3(craters[o + 6], craters[o + 7], craters[o + 8]);
                double crR = craters[o + 10], depth = craters[o + 11], rim = craters[o + 12];
                double ang = math.acos(math.min(1d, c));
                double r = ang * radius;
                double az = math.atan2(math.dot(v, east), math.dot(v, north));
                double ragged = 1d + 0.05 * math.sin(5d * az + craters[o + 13]) + 0.035 * math.sin(11d * az + craters[o + 14])
                                + 0.02 * math.sin(23d * az + craters[o + 15]);
                double x = r / (crR * ragged);
                if (x <= 1d) return -depth + (depth + rim) * x * x;
                double rch = reach / ragged;
                if (x >= rch) return 0d;
                double k = (rch - x) / (rch - 1d);
                return rim * k * k * k;
            }

            double SiteFade(double3 v)
            {
                double f = 1d;
                for (int s = 0; s < siteCount; s++)
                {
                    int o = s * SiteStride;
                    double c = math.dot(v, new double3(sites[o], sites[o + 1], sites[o + 2]));
                    double cosOut = sites[o + 4], cosIn = sites[o + 3];
                    if (c <= cosOut) continue;
                    if (c >= cosIn) return 0d;
                    double k = (c - cosOut) / (cosIn - cosOut);
                    f *= 1d - k * k * (3d - 2d * k);
                }
                return f;
            }

            public void Dispose()
            {
                if (craters.IsCreated) craters.Dispose();
                if (sites.IsCreated) sites.Dispose();
            }
        }

        public BatchPQSMod_BDNukesCrater(PQSMod_BDNukesCrater mod) : base(mod) { }

        public override void OnQuadPreBuild(PQ quad, BatchPQSJobSet jobSet)
        {
            base.OnQuadPreBuild(quad, jobSet);
            var mod = Mod;
            if (mod == null || mod.craters == null || mod.craters.Count == 0 || mod.sphere == null) return;

            // only the craters whose influence reaches this quad
            Vector3d qd = quad.positionPlanet.normalized;
            double half = Math.PI / 4d / (1 << Mathf.Clamp(quad.subdivision, 0, 30)) * 1.6;
            int n = 0;
            foreach (var c in mod.craters)
                if (Reaches(c, qd, half)) n++;
            if (n == 0) return;

            var cr = new NativeArray<double>(n * CraterStride, Allocator.Persistent);
            int k = 0;
            foreach (var c in mod.craters)
            {
                if (!Reaches(c, qd, half)) continue;
                int o = k++ * CraterStride;
                cr[o] = c.dir.x; cr[o + 1] = c.dir.y; cr[o + 2] = c.dir.z;
                cr[o + 3] = c.east.x; cr[o + 4] = c.east.y; cr[o + 5] = c.east.z;
                cr[o + 6] = c.north.x; cr[o + 7] = c.north.y; cr[o + 8] = c.north.z;
                cr[o + 9] = c.cosInfluence; cr[o + 10] = c.radius; cr[o + 11] = c.depth; cr[o + 12] = c.rim;
                cr[o + 13] = c.ph1; cr[o + 14] = c.ph2; cr[o + 15] = c.ph3;
            }
            int sn = mod.siteDirs.Count;
            var st = new NativeArray<double>(Math.Max(1, sn * SiteStride), Allocator.Persistent);
            for (int s = 0; s < sn; s++)
            {
                int o = s * SiteStride;
                st[o] = mod.siteDirs[s].x; st[o + 1] = mod.siteDirs[s].y; st[o + 2] = mod.siteDirs[s].z;
                st[o + 3] = mod.siteCosIn[s]; st[o + 4] = mod.siteCosOut[s];
            }
            var job = new HeightJob
            {
                craters = cr, sites = st, craterCount = n, siteCount = sn,
                radius = mod.sphere.radius, reach = NukeCrater.EjectaReach,
            };
            jobSet.Add(job);
        }

        static bool Reaches(NukeCrater c, Vector3d qd, double half)
        {
            double a = Math.Acos(Math.Max(-1d, Math.Min(1d, Vector3d.Dot(qd, c.dir))));
            return a <= half + c.influenceAngle;
        }
    }
}
