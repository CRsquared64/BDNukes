using System.Globalization;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// "Replay last detonation" (settings window button, Alt+F10): plays the most recent detonation's cinematic
    /// again from t = 0 at the same place, with its sound, flash and camera shake, but nothing else. It goes
    /// through the visuals-only path a restored cloud uses (no blast, crater, scorch, craft scorch, cloud
    /// clearing or EVE holes), the fallout plume is drawn without any dose, and the replay isn't recorded, so
    /// it never comes back after a scene change. A salvo or MIRV footprint (detonations up to SalvoWindow s
    /// before the last one) is replayed with its original spacing.
    /// </summary>
    internal partial class NukeCinematic
    {
        const float SalvoWindow = 20f;

        /// <summary>Spawns a visual-only copy of a recorded detonation, starting `delay` s from now.</summary>
        static void SpawnReplay(CelestialBody body, NukeRecord r, float startDelay)
        {
            NukeAssets.Init();
            var go = new GameObject("BDNukes_Replay");
            try
            {
                var cin = go.AddComponent<NukeCinematic>();
                cin.record = r;          // read for the place, wind and shape; never written back
                cin.restored = true;     // skips every side effect
                cin.replay = true;       // ...but starts at t = 0, with sound
                Vector3 pos = body.GetWorldSurfacePosition(r.lat, r.lon, r.alt);
                cin.Setup(body, pos, Mathf.Max(r.W, 0.001f), Mathf.Max(0f, startDelay), r.profile, NukeFXManager.LODScale());
            }
            catch
            {
                Destroy(go);
                throw;
            }
        }

        /// <summary>Replays the last detonation (or salvo). Returns a status line for the screen.</summary>
        public static string ReplayLast()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return "Replay works in flight only";
            var salvo = NukePersistence.LastSalvo(SalvoWindow);
            if (salvo.Count == 0) return "No detonation to replay yet";
            var body = FlightGlobals.currentMainBody;
            string where = salvo[salvo.Count - 1].body;
            if (body == null || body.bodyName != where) return "The last detonation was on " + where;
            double first = salvo[0].ut;
            int n = 0;
            foreach (var r in salvo)
            {
                try
                {
                    SpawnReplay(body, r, 0.5f + (float)(r.ut - first));
                    n++;
                }
                catch (System.Exception e) { Debug.LogWarning("[BDNukes]: Couldn't replay a detonation: " + e.Message); }
            }
            var last = salvo[salvo.Count - 1];
            Debug.Log(string.Format(CultureInfo.InvariantCulture, "[BDNukes]: Replaying {0} detonation(s), last {1} kt on {2}.", n, last.W, last.body));
            return n == 0 ? "Replay failed (see the log)"
                : "Replaying " + ModuleNukeYieldSelector.FormatYield(last.W) + (n > 1 ? " (" + n + " detonations)" : "") + ", visuals only";
        }
    }

    /// <summary>The replay hotkey (replayKey with Alt, Alt+F10 by default).</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal class NukeReplayHotkey : MonoBehaviour
    {
        void Update()
        {
            if (!NukeSettings.AltKeyDown(NukeSettings.Current.replayKey)) return;
            string msg;
            try { msg = NukeCinematic.ReplayLast(); }
            catch (System.Exception e)
            {
                Debug.LogError("[BDNukes]: Replay failed: " + e);
                msg = "Replay failed (see the log)";
            }
            ScreenMessages.PostScreenMessage("BDNukes: " + msg, 3f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}
