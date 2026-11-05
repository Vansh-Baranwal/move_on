using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RobotEngine.Views;

namespace RobotEngine.Sim
{
    /// <summary>
    /// Persistent visual sightings used as waypoints when a later command asks for
    /// an object the camera has already seen.
    /// </summary>
    internal static class VisualMemoryDatabase
    {
        private const int MaxSightings = 256;
        private static CacheDocument document;
        private static string loadedPath;

        [Serializable]
        private sealed class CacheDocument
        {
            public List<Sighting> sightings = new List<Sighting>();
        }

        [Serializable]
        private sealed class Sighting
        {
            public string colour;
            public string shape;
            public float x;
            public float z;
            public long lastSeenTicks;
            public int observations;
        }

        public static void Observe(List<Blob> blobs, AgentCamera camera)
        {
            if (blobs == null || camera == null)
            {
                return;
            }

            CacheDocument data = Load();
            bool changed = false;
            bool shouldPersist = false;
            for (int i = 0; i < blobs.Count; i++)
            {
                Blob blob = blobs[i];
                if (blob == null)
                {
                    continue;
                }

                Vector3 point;
                if (!camera.TryGetWorldPointAt(
                    blob.Cx / RobotVision.FrameWidth,
                    blob.Cy / RobotVision.FrameHeight,
                    out point))
                {
                    continue;
                }

                Sighting match = null;
                float bestDistance = 0.65f * 0.65f;
                for (int j = 0; j < data.sightings.Count; j++)
                {
                    Sighting candidate = data.sightings[j];
                    if (candidate.colour != blob.Colour || candidate.shape != blob.Shape)
                    {
                        continue;
                    }

                    float dx = candidate.x - point.x;
                    float dz = candidate.z - point.z;
                    float distance = dx * dx + dz * dz;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        match = candidate;
                    }
                }

                if (match == null)
                {
                    data.sightings.Add(new Sighting
                    {
                        colour = blob.Colour,
                        shape = blob.Shape,
                        x = point.x,
                        z = point.z,
                        lastSeenTicks = DateTime.UtcNow.Ticks,
                        observations = 1
                    });
                    shouldPersist = true;
                }
                else
                {
                    long now = DateTime.UtcNow.Ticks;
                    bool persistUpdate = now - match.lastSeenTicks >= TimeSpan.FromSeconds(5).Ticks;
                    int count = Mathf.Max(1, match.observations);
                    match.x = (match.x * count + point.x) / (count + 1);
                    match.z = (match.z * count + point.z) / (count + 1);
                    match.observations = count + 1;
                    if (persistUpdate)
                    {
                        match.lastSeenTicks = now;
                        shouldPersist = true;
                    }
                }

                changed = true;
            }

            if (data.sightings.Count > MaxSightings)
            {
                data.sightings.Sort((a, b) => a.lastSeenTicks.CompareTo(b.lastSeenTicks));
                data.sightings.RemoveRange(0, data.sightings.Count - MaxSightings);
                changed = true;
                shouldPersist = true;
            }

            if (changed && shouldPersist)
            {
                Save(data);
            }
        }

        public static bool TryFindNearest(Vector2 from, string colour, string shape, out Vector2 position)
        {
            position = Vector2.zero;
            CacheDocument data = Load();
            long freshnessLimit = DateTime.UtcNow.AddDays(-30).Ticks;
            Sighting best = null;
            float bestDistance = float.MaxValue;
            bool requireShapeMatch = !string.IsNullOrEmpty(shape);

            for (int pass = 0; pass < 2; pass++)
            {
                best = null;
                bestDistance = float.MaxValue;
                for (int i = 0; i < data.sightings.Count; i++)
                {
                    Sighting candidate = data.sightings[i];
                    if (candidate.lastSeenTicks < freshnessLimit || candidate.colour == "cyan")
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(colour) && candidate.colour != colour)
                    {
                        continue;
                    }

                    if (pass == 0 && requireShapeMatch && !ShapeMatches(shape, candidate.shape))
                    {
                        continue;
                    }

                    float dx = candidate.x - from.x;
                    float dz = candidate.z - from.y;
                    float distance = dx * dx + dz * dz;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }

                if (best != null || !requireShapeMatch)
                {
                    break;
                }
            }

            if (best == null)
            {
                return false;
            }

            position = new Vector2(best.x, best.z);
            return true;
        }

        public static void Clear()
        {
            document = new CacheDocument();
            loadedPath = GetPath();
            try
            {
                if (File.Exists(loadedPath))
                {
                    File.Delete(loadedPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Could not clear visual memory: " + ex.Message);
            }
        }

        private static bool ShapeMatches(string requested, string observed)
        {
            if (requested == "cone")
            {
                return observed == "tapered";
            }

            if (requested == "boxy")
            {
                return observed == "boxy";
            }

            if (requested == "round")
            {
                return observed == "round";
            }

            // The pixel classifier has no dedicated cylinder category.
            return requested != "cylinder" || observed != "tapered";
        }

        private static CacheDocument Load()
        {
            string path = GetPath();
            if (document != null && loadedPath == path)
            {
                return document;
            }

            loadedPath = path;
            document = new CacheDocument();
            try
            {
                if (File.Exists(path))
                {
                    CacheDocument loaded = JsonUtility.FromJson<CacheDocument>(File.ReadAllText(path));
                    if (loaded != null && loaded.sightings != null)
                    {
                        document = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Could not load visual memory; starting with an empty cache: " + ex.Message);
            }

            return document;
        }

        private static void Save(CacheDocument data)
        {
            string path = GetPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(data, true));
                document = data;
                loadedPath = path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Could not save visual memory: " + ex.Message);
            }
        }

        private static string GetPath()
        {
            return Path.Combine(Application.persistentDataPath, "move_on", "visual_memory.json");
        }
    }
}