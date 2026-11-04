using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>One detonation, kept for as long as its cloud lasts so it can be rebuilt after a scene change.</summary>
    internal class NukeRecord
    {
        public string body;
        public double lat, lon, alt;
        public double ut;              // universal time of the detonation itself (after any fuze delay)
        public double endUT;           // when the cloud has gone
        public float W, bulb, windHeading;
        public int windSeed;
        public NukeCloudProfile profile;
        public NukeCinematic live;     // the running cinematic in this scene (Unity-null once it is destroyed)

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        public ConfigNode Save()
        {
            var n = new ConfigNode("DETONATION");
            n.AddValue("body", body);
            n.AddValue("lat", F(lat)); n.AddValue("lon", F(lon)); n.AddValue("alt", F(alt));
            n.AddValue("ut", F(ut)); n.AddValue("endUT", F(endUT));
            n.AddValue("W", F(W)); n.AddValue("bulb", F(bulb)); n.AddValue("windHeading", F(windHeading));
            n.AddValue("windSeed", windSeed);
            if (profile != null)
            {
                var p = n.AddNode("PROFILE");
                p.AddValue("style", profile.style);
                p.AddValue("dustiness", F(profile.dustiness)); p.AddValue("capAspect", F(profile.capAspect));
                p.AddValue("capSpread", F(profile.capSpread)); p.AddValue("stemWidth", F(profile.stemWidth));
                p.AddValue("stemCollars", profile.stemCollars);
                p.AddValue("skirtScale", F(profile.skirtScale)); p.AddValue("billowScale", F(profile.billowScale));
                p.AddValue("glowScale", F(profile.glowScale));
                p.AddValue("hasTint", profile.hasTint);
                p.AddValue("tint", ConfigNode.WriteColor(profile.tint));
            }
            return n;
        }

        public static NukeRecord Load(ConfigNode n)
        {
            var r = new NukeRecord();
            r.body = n.GetValue("body");
            if (string.IsNullOrEmpty(r.body)) return null;
            n.TryGetValue("lat", ref r.lat); n.TryGetValue("lon", ref r.lon); n.TryGetValue("alt", ref r.alt);
            n.TryGetValue("ut", ref r.ut); n.TryGetValue("endUT", ref r.endUT);
            n.TryGetValue("W", ref r.W); n.TryGetValue("bulb", ref r.bulb); n.TryGetValue("windHeading", ref r.windHeading);
            n.TryGetValue("windSeed", ref r.windSeed);
            var p = n.GetNode("PROFILE");
            if (p != null)
            {
                var pr = new NukeCloudProfile();
                string style = p.GetValue("style");
                if (!string.IsNullOrEmpty(style)) pr.style = style;
                p.TryGetValue("dustiness", ref pr.dustiness); p.TryGetValue("capAspect", ref pr.capAspect);
                p.TryGetValue("capSpread", ref pr.capSpread); p.TryGetValue("stemWidth", ref pr.stemWidth);
                p.TryGetValue("stemCollars", ref pr.stemCollars);
                p.TryGetValue("skirtScale", ref pr.skirtScale); p.TryGetValue("billowScale", ref pr.billowScale);
                p.TryGetValue("glowScale", ref pr.glowScale);
                p.TryGetValue("hasTint", ref pr.hasTint);
                p.TryGetValue("tint", ref pr.tint);
                r.profile = pr;
            }
            return r;
        }
    }

    /// <summary>
    /// Keeps detonations alive across scene changes: every cloud is recorded in the save (with its universal time),
    /// and whenever the flight scene is on that body again and the cloud hasn't run its course, it is rebuilt at the
    /// age it has reached. So you can switch to another vessel, go to the space centre and back, or reload, and the
    /// cloud is still there, further along. Only the visuals come back: the blast, the crater and the sound are not
    /// replayed.
    /// </summary>
    internal static class NukePersistence
    {
        static readonly List<NukeRecord> records = new List<NukeRecord>();
        static float nextTick;

        public static double Now => Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0d;

        public static NukeRecord Add(NukeRecord r)
        {
            records.Add(r);
            while (records.Count > 32) records.RemoveAt(0);
            return r;
        }

        public static void Load(ConfigNode node)
        {
            records.Clear();
            double now = Now;
            foreach (var cn in node.GetNodes("DETONATION"))
            {
                var r = NukeRecord.Load(cn);
                if (r != null && r.endUT > now) records.Add(r);
            }
        }

        public static void Save(ConfigNode node)
        {
            double now = Now;
            records.RemoveAll(r => r.endUT <= now);
            foreach (var r in records) node.AddNode(r.Save());
        }

        /// <summary>Called a few times a second in flight: rebuilds recorded clouds missing from this scene.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 0.5f;
            if (!NukeSettings.Current.enabled || !NukeSettings.Current.persistentClouds) return;
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready || NukeFXManager.Instance == null) return;
            var body = FlightGlobals.currentMainBody;
            if (body == null) return;
            double now = Now;
            records.RemoveAll(r => r.endUT <= now);
            foreach (var r in records)
            {
                if (r.live != null || r.body != body.bodyName) continue;
                try
                {
                    NukeCinematic.Restore(body, r);
                    Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Restored a {0} kt cloud on {1}, {2:F0} s after detonation.", r.W, r.body, now - r.ut));
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[BDNukes]: Couldn't restore a detonation: " + e.Message);
                    r.endUT = 0d;   // don't retry every tick
                }
            }
        }
    }
}
