using System.Collections.Generic;
using UnityEngine;

namespace RobotEngine.World
{
    /// <summary>
    /// Engine-private registry of fetchable objects and the drop zone. Everything here stays
    /// inside the engine boundary: none of it is exposed through the API.
    /// </summary>
    public static class SceneRegistry
    {
        private struct FetchableSnapshot
        {
            public GameObject Object;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static readonly List<FetchableSnapshot> fetchables = new List<FetchableSnapshot>();
        private static Vector3 dropZoneCenter;
        private static float dropZoneHalfSize;

        public static void RegisterFetchable(GameObject fetchable)
        {
            for (int i = 0; i < fetchables.Count; i++)
            {
                if (fetchables[i].Object == fetchable)
                {
                    return;
                }
            }

            FetchableSnapshot snapshot = new FetchableSnapshot
            {
                Object = fetchable,
                Position = fetchable.transform.position,
                Rotation = fetchable.transform.rotation
            };
            fetchables.Add(snapshot);
        }

        public static IEnumerable<GameObject> GetFetchables()
        {
            for (int i = 0; i < fetchables.Count; i++)
            {
                if (fetchables[i].Object != null)
                {
                    yield return fetchables[i].Object;
                }
            }
        }

        public static void SetDropZone(Vector3 center, float size)
        {
            dropZoneCenter = center;
            dropZoneHalfSize = size * 0.5f;
        }

        /// <summary>Engine-UI only: minimap draws the zone marker from this.</summary>
        public static Vector3 DropZoneCenter
        {
            get { return dropZoneCenter; }
        }

        public static float DropZoneHalfSize
        {
            get { return dropZoneHalfSize; }
        }

        public static bool IsInsideDropZone(Vector3 position)
        {
            return Mathf.Abs(position.x - dropZoneCenter.x) <= dropZoneHalfSize &&
                Mathf.Abs(position.z - dropZoneCenter.z) <= dropZoneHalfSize;
        }

        /// <summary>Finds an unoccupied, fully-inside landing point for a released object.</summary>
        public static bool TryFindFreeDropPosition(GameObject incoming, Vector3 preferred, out Vector3 position)
        {
            position = preferred;
            if (incoming == null)
            {
                return false;
            }

            Collider incomingCollider = incoming.GetComponent<Collider>();
            if (incomingCollider == null)
            {
                return false;
            }

            Vector3 extents = incomingCollider.bounds.extents;
            const float edgePadding = 0.05f;
            const float itemPadding = 0.04f;
            float minX = dropZoneCenter.x - dropZoneHalfSize + extents.x + edgePadding;
            float maxX = dropZoneCenter.x + dropZoneHalfSize - extents.x - edgePadding;
            float minZ = dropZoneCenter.z - dropZoneHalfSize + extents.z + edgePadding;
            float maxZ = dropZoneCenter.z + dropZoneHalfSize - extents.z - edgePadding;
            if (minX > maxX || minZ > maxZ)
            {
                return false;
            }

            float preferredX = Mathf.Clamp(preferred.x, minX, maxX);
            float preferredZ = Mathf.Clamp(preferred.z, minZ, maxZ);
            if (IsDropSpotFree(incoming, extents, preferredX, preferredZ, itemPadding))
            {
                position = new Vector3(preferredX, preferred.y, preferredZ);
                return true;
            }

            const float gridStep = 0.1f;
            float bestDistance = float.PositiveInfinity;
            float bestX = 0f;
            float bestZ = 0f;
            for (float x = minX; x <= maxX + 0.001f; x += gridStep)
            {
                for (float z = minZ; z <= maxZ + 0.001f; z += gridStep)
                {
                    if (!IsDropSpotFree(incoming, extents, x, z, itemPadding))
                    {
                        continue;
                    }

                    float dx = x - preferredX;
                    float dz = z - preferredZ;
                    float distance = dx * dx + dz * dz;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestX = x;
                        bestZ = z;
                    }
                }
            }

            if (float.IsPositiveInfinity(bestDistance))
            {
                return false;
            }

            position = new Vector3(bestX, preferred.y, bestZ);
            return true;
        }

        private static bool IsDropSpotFree(GameObject incoming, Vector3 incomingExtents,
            float x, float z, float padding)
        {
            for (int i = 0; i < fetchables.Count; i++)
            {
                GameObject other = fetchables[i].Object;
                if (other == null || other == incoming || !IsInsideDropZone(other.transform.position))
                {
                    continue;
                }

                Collider otherCollider = other.GetComponent<Collider>();
                if (otherCollider == null)
                {
                    continue;
                }

                Vector3 otherExtents = otherCollider.bounds.extents;
                Vector3 otherPosition = other.transform.position;
                if (Mathf.Abs(x - otherPosition.x) < incomingExtents.x + otherExtents.x + padding &&
                    Mathf.Abs(z - otherPosition.z) < incomingExtents.z + otherExtents.z + padding)
                {
                    return false;
                }
            }

            return true;
        }
        public static void Clear()
        {
            fetchables.Clear();
        }

        public static void Reset()
        {
            for (int i = 0; i < fetchables.Count; i++)
            {
                GameObject fetchable = fetchables[i].Object;
                if (fetchable == null)
                {
                    continue;
                }

                fetchable.transform.SetParent(null, true);
                Rigidbody body = fetchable.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.isKinematic = true;
                }

                foreach (Collider collider in fetchable.GetComponentsInChildren<Collider>(true))
                {
                    collider.enabled = true;
                }

                fetchable.transform.SetPositionAndRotation(fetchables[i].Position, fetchables[i].Rotation);
            }
        }
    }
}
