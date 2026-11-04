using System;
using UnityEngine;

namespace RobotEngine.World
{
    public static class LayoutValidator
    {
        private const float WallClearance = 0.6f;
        private const float ObjectClearance = 0.8f;
        private const float DropZoneMargin = 0.35f;
        private const float RobotClearance = 0.35f;

        public readonly struct LayoutObject
        {
            public readonly Vector2 Position;
            public readonly float Height;
            public readonly string Shape;

            public LayoutObject(Vector2 position, float height, string shape)
            {
                Position = position;
                Height = height;
                Shape = shape;
            }
        }

        public static void Validate(LayoutObject[] objects, Vector2 robotStart, Vector2 dropZoneCenter,
            float dropZoneSize, float roomSize)
        {
            if (objects == null || objects.Length == 0)
            {
                throw new InvalidOperationException("World layout is empty.");
            }

            float halfRoom = roomSize / 2f;
            for (int i = 0; i < objects.Length; i++)
            {
                LayoutObject current = objects[i];
                float footprintRadius = FootprintRadius(current);
                if (Mathf.Min(halfRoom - Mathf.Abs(current.Position.x), halfRoom - Mathf.Abs(current.Position.y)) <
                    WallClearance + footprintRadius)
                {
                    throw new InvalidOperationException("World layout violates wall clearance.");
                }

                float dropDistance = DistanceToSquare(current.Position, dropZoneCenter, dropZoneSize / 2f);
                if (dropDistance < DropZoneMargin + footprintRadius)
                {
                    throw new InvalidOperationException("World layout violates drop-zone clearance.");
                }

                if (Vector2.Distance(current.Position, robotStart) < RobotClearance + footprintRadius)
                {
                    throw new InvalidOperationException("World layout violates robot-start clearance.");
                }

                for (int j = i + 1; j < objects.Length; j++)
                {
                    float minimumDistance = ObjectClearance + footprintRadius + FootprintRadius(objects[j]);
                    if (Vector2.Distance(current.Position, objects[j].Position) < minimumDistance)
                    {
                        throw new InvalidOperationException("World layout contains overlapping object clearance.");
                    }
                }
            }

            for (int i = 0; i < objects.Length; i++)
            {
                if (!HasApproachPoint(objects, i, robotStart, dropZoneCenter, dropZoneSize))
                {
                    throw new InvalidOperationException("World layout contains an unreachable object.");
                }
            }
        }

        private static float FootprintRadius(LayoutObject item)
        {
            return item.Shape == "sphere" || item.Shape == "cylinder" || item.Shape == "cone" ? 0.35f : 0.4f;
        }

        private static float DistanceToSquare(Vector2 point, Vector2 center, float halfSize)
        {
            Vector2 delta = new Vector2(Mathf.Abs(point.x - center.x), Mathf.Abs(point.y - center.y));
            return Mathf.Max(delta.x - halfSize, 0f) + Mathf.Max(delta.y - halfSize, 0f);
        }

        private static bool HasApproachPoint(LayoutObject[] objects, int targetIndex, Vector2 start,
            Vector2 dropZoneCenter, float dropZoneSize)
        {
            LayoutObject target = objects[targetIndex];
            Vector2 toTarget = (target.Position - start).normalized;
            Vector2 perpendicular = new Vector2(-toTarget.y, toTarget.x);
            Vector2[] approachPoints =
            {
                target.Position - toTarget * 0.65f,
                target.Position + toTarget * 0.65f,
                target.Position - perpendicular * 0.65f,
                target.Position + perpendicular * 0.65f
            };

            for (int i = 0; i < approachPoints.Length; i++)
            {
                if (DistanceToSquare(approachPoints[i], dropZoneCenter, dropZoneSize / 2f) < 0.35f)
                {
                    continue;
                }

                bool blocked = false;
                for (int j = 0; j < objects.Length; j++)
                {
                    if (j != targetIndex && DistanceToSegment(objects[j].Position, start, approachPoints[i]) <
                        FootprintRadius(objects[j]) + 0.25f)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (!blocked)
                {
                    return true;
                }
            }

            return false;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
        {
            Vector2 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < Mathf.Epsilon)
            {
                return Vector2.Distance(point, start);
            }

            float projection = Mathf.Clamp01(Vector2.Dot(point - start, segment) / lengthSquared);
            return Vector2.Distance(point, start + segment * projection);
        }
    }
}
