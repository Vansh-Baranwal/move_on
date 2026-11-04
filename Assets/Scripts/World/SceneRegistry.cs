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

        public static bool IsInsideDropZone(Vector3 position)
        {
            return Mathf.Abs(position.x - dropZoneCenter.x) <= dropZoneHalfSize &&
                Mathf.Abs(position.z - dropZoneCenter.z) <= dropZoneHalfSize;
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
