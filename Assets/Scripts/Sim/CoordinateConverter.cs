using UnityEngine;

namespace RobotEngine.Sim
{
    public static class CoordinateConverter
    {
        public static Vector3 ApiToUnityPosition(float x, float y, float height = 0f)
        {
            return new Vector3(x, height, y);
        }

        public static Vector2 UnityToApiPosition(Vector3 position)
        {
            return new Vector2(position.x, position.z);
        }

        public static float ApiHeadingToUnityYaw(float heading)
        {
            // Unity forward is (sin yaw, cos yaw), so yaw = heading + 90 maps (cos h, -sin h).
            return heading + 90f;
        }

        public static float UnityYawToApiHeading(float yaw)
        {
            return NormalizeHeading(yaw - 90f);
        }

        public static float NormalizeHeading(float heading)
        {
            return Mathf.Repeat(heading, 360f);
        }
    }
}
