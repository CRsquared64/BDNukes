using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BDNukes
{
    /// <summary>
    /// Reads the cloud decks above a point from EVE's raymarched volumetric clouds (EVE-Redux / Stock Volumetric
    /// Clouds), through reflection so BDNukes has no hard dependency: each deck's base and top altitude and its
    /// coverage straight above. Without EVE, or if its internals change, there are simply no decks.
    /// </summary>
    internal static class NukeWeather
    {
        public struct Deck
        {
            public float baseAlt, topAlt;  // m ASL
            public float coverage;         // 0..1 straight above the sample point
        }

        static bool initDone, available;
        static Type cloudsObjectType;
        static PropertyInfo layerProp;
        static MethodInfo sampleMethod;
        static FieldInfo minAltField, maxAltField, bodyField;
        static UnityEngine.Object[] objects;
        static float objectsTime = -100f;

        /// <summary>Sunlight lost under a fully covered volumetric deck.</summary>
        public const float DeckOpacity = 0.85f;

        static void Init()
        {
            initDone = true;
            try
            {
                foreach (var la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.name != "Atmosphere") continue;
                    cloudsObjectType = la.assembly.GetType("Atmosphere.CloudsObject");
                    var volType = la.assembly.GetType("Atmosphere.CloudsRaymarchedVolume");
                    if (cloudsObjectType == null || volType == null) return;
                    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    layerProp = cloudsObjectType.GetProperty("LayerRaymarchedVolume", all);
                    sampleMethod = volType.GetMethod("SampleCoverage", all, null,
                        new[] { typeof(Vector3), typeof(float).MakeByRefType(), typeof(bool) }, null);
                    minAltField = volType.GetField("cloudMinAltitude", all);
                    maxAltField = volType.GetField("cloudMaxAltitude", all);
                    bodyField = volType.GetField("parentCelestialBody", all);
                    available = layerProp != null && sampleMethod != null && minAltField != null && maxAltField != null && bodyField != null;
                    if (available) Debug.Log("[BDNukes]: EVE volumetric clouds found; nukes are lit for the overcast.");
                    return;
                }
            }
            catch (Exception e)
            {
                available = false;
                Debug.LogWarning("[BDNukes]: Couldn't hook EVE clouds: " + e.Message);
            }
        }

        /// <summary>The volumetric cloud decks over a lat/lon, lowest first. Returns false if there's no EVE to ask.</summary>
        static readonly float[] DeckHeights = { 0.15f, 0.3f, 0.5f, 0.7f };

        public static bool Sample(CelestialBody body, double lat, double lon, List<Deck> into)
        {
            into.Clear();
            if (!initDone) Init();
            if (!available || body == null) return false;
            try
            {
                if (objects == null || Time.time - objectsTime > 10f)
                {
                    objects = UnityEngine.Object.FindObjectsOfType(cloudsObjectType);
                    objectsTime = Time.time;
                }
                var args = new object[3];
                foreach (var o in objects)
                {
                    if (o == null) continue;
                    object vol = layerProp.GetValue(o, null);
                    if (vol == null || !ReferenceEquals(bodyField.GetValue(vol), body)) continue;
                    float lo = (float)minAltField.GetValue(vol), hi = (float)maxAltField.GetValue(vol);
                    if (hi <= lo) continue;
                    // EVE scales the map's cover by the cloud type's height profile: at one height (the middle) a
                    // cumulus deck can read almost clear while it looks solid. The most cover over the depth is used.
                    float cov = 0f;
                    for (int h = 0; h < DeckHeights.Length; h++)
                    {
                        args[0] = (Vector3)body.GetWorldSurfacePosition(lat, lon, lo + DeckHeights[h] * (hi - lo));
                        args[1] = 0f;
                        args[2] = false;
                        cov = Mathf.Max(cov, Mathf.Clamp01((float)sampleMethod.Invoke(vol, args)));
                    }
                    into.Add(new Deck { baseAlt = lo, topAlt = hi, coverage = cov });
                }
                into.Sort((a, b) => a.baseAlt.CompareTo(b.baseAlt));
                return true;
            }
            catch (Exception e)
            {
                available = false;
                Debug.LogWarning("[BDNukes]: EVE cloud sampling failed, overcast lighting is off: " + (e.InnerException ?? e).Message);
                return false;
            }
        }

        /// <summary>Fraction of direct sunlight reaching an altitude through the decks above it (straight up).</summary>
        public static float SunVisibility(List<Deck> decks, float altASL)
        {
            float vis = 1f;
            foreach (var d in decks)
            {
                if (altASL >= d.topAlt) continue;
                float inside = altASL <= d.baseAlt ? 1f : (d.topAlt - altASL) / (d.topAlt - d.baseAlt);
                vis *= 1f - DeckOpacity * d.coverage * inside;
            }
            return vis;
        }
    }
}
