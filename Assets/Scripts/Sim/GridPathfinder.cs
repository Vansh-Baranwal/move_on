using System.Collections.Generic;
using UnityEngine;
using RobotEngine.Robot;

namespace RobotEngine.Sim
{
    /// <summary>Grid A* using the robot capsule as the obstacle footprint.</summary>
    internal static class GridPathfinder
    {
        private const float GridMin = -4.5f;
        private const float GridStep = 0.25f;
        private const float PathSafetyMargin = 0.07f;
        private const int GridSize = 37;
        private const int CellCount = GridSize * GridSize;
        private static readonly int[] NeighborX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] NeighborZ = { -1, -1, -1, 0, 0, 1, 1, 1 };

        private struct QueueEntry
        {
            public int Index;
            public float Priority;

            public QueueEntry(int index, float priority)
            {
                Index = index;
                Priority = priority;
            }
        }

        private sealed class MinHeap
        {
            private readonly List<QueueEntry> entries = new List<QueueEntry>();

            public int Count
            {
                get { return entries.Count; }
            }

            public void Push(int index, float priority)
            {
                QueueEntry entry = new QueueEntry(index, priority);
                entries.Add(entry);
                int child = entries.Count - 1;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (entries[parent].Priority <= entry.Priority)
                    {
                        break;
                    }

                    entries[child] = entries[parent];
                    child = parent;
                }

                entries[child] = entry;
            }

            public QueueEntry Pop()
            {
                QueueEntry first = entries[0];
                QueueEntry last = entries[entries.Count - 1];
                entries.RemoveAt(entries.Count - 1);
                if (entries.Count == 0)
                {
                    return first;
                }

                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    if (left >= entries.Count)
                    {
                        break;
                    }

                    int right = left + 1;
                    int child = right < entries.Count &&
                        entries[right].Priority < entries[left].Priority ? right : left;
                    if (entries[child].Priority >= last.Priority)
                    {
                        break;
                    }

                    entries[parent] = entries[child];
                    parent = child;
                }

                entries[parent] = last;
                return first;
            }
        }

        public static bool TryGetNextWaypoint(RobotController robot, Vector2 start, Vector2 destination,
            out Vector2 waypoint)
        {
            waypoint = start;
            if (robot == null)
            {
                return false;
            }

            sbyte[] walkable = new sbyte[CellCount];
            int startIndex = FindNearestWalkable(robot, walkable, start, 3);
            int goalIndex = FindNearestWalkable(robot, walkable, destination, 4);
            if (startIndex < 0 || goalIndex < 0)
            {
                return false;
            }

            float[] costs = new float[CellCount];
            int[] parents = new int[CellCount];
            bool[] closed = new bool[CellCount];
            for (int i = 0; i < CellCount; i++)
            {
                costs[i] = float.PositiveInfinity;
                parents[i] = -1;
            }

            MinHeap open = new MinHeap();
            costs[startIndex] = 0f;
            open.Push(startIndex, Heuristic(startIndex, goalIndex));
            bool reached = false;
            while (open.Count > 0)
            {
                QueueEntry currentEntry = open.Pop();
                int current = currentEntry.Index;
                if (closed[current])
                {
                    continue;
                }

                closed[current] = true;
                if (current == goalIndex)
                {
                    reached = true;
                    break;
                }

                int x = current % GridSize;
                int z = current / GridSize;
                for (int i = 0; i < NeighborX.Length; i++)
                {
                    int nx = x + NeighborX[i];
                    int nz = z + NeighborZ[i];
                    if (nx < 0 || nx >= GridSize || nz < 0 || nz >= GridSize)
                    {
                        continue;
                    }

                    int next = ToIndex(nx, nz);
                    if (closed[next] || !IsWalkable(robot, walkable, next))
                    {
                        continue;
                    }

                    bool diagonal = NeighborX[i] != 0 && NeighborZ[i] != 0;
                    if (diagonal &&
                        (!IsWalkable(robot, walkable, ToIndex(x + NeighborX[i], z)) ||
                         !IsWalkable(robot, walkable, ToIndex(x, z + NeighborZ[i]))))
                    {
                        continue;
                    }

                    float nextCost = costs[current] + (diagonal ? 1.41421356f : 1f);
                    if (nextCost >= costs[next])
                    {
                        continue;
                    }

                    parents[next] = current;
                    costs[next] = nextCost;
                    open.Push(next, nextCost + Heuristic(next, goalIndex));
                }
            }

            if (!reached)
            {
                return false;
            }

            List<int> path = new List<int>();
            int cursor = goalIndex;
            path.Add(cursor);
            while (cursor != startIndex)
            {
                cursor = parents[cursor];
                if (cursor < 0)
                {
                    return false;
                }

                path.Add(cursor);
            }

            path.Reverse();
            Vector2 furthest = CellPosition(path[0]);
            for (int i = 1; i < path.Count; i++)
            {
                Vector2 candidate = CellPosition(path[i]);
                Vector2 delta = candidate - start;
                float distance = delta.magnitude;
                if (distance > 0.95f)
                {
                    break;
                }

                if (distance < 0.01f)
                {
                    continue;
                }

                Vector3 direction = new Vector3(delta.x / distance, 0f, delta.y / distance);
                float clearanceProbeDistance = distance + PathSafetyMargin;
                float clearance = robot.GetTravelClearance(direction, clearanceProbeDistance);
                if (clearance < clearanceProbeDistance - 0.01f)
                {
                    break;
                }

                furthest = candidate;
            }

            if (path.Count > 1 && (furthest - start).sqrMagnitude < 0.0001f)
            {
                return false;
            }

            waypoint = furthest;
            return true;
        }

        private static int FindNearestWalkable(RobotController robot, sbyte[] walkable,
            Vector2 point, int maxRadius)
        {
            int centerX = Mathf.Clamp(Mathf.RoundToInt((point.x - GridMin) / GridStep), 0, GridSize - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt((point.y - GridMin) / GridStep), 0, GridSize - 1);
            for (int radius = 0; radius <= maxRadius; radius++)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                for (int z = centerZ - radius; z <= centerZ + radius; z++)
                {
                    for (int x = centerX - radius; x <= centerX + radius; x++)
                    {
                        if (x < 0 || x >= GridSize || z < 0 || z >= GridSize)
                        {
                            continue;
                        }

                        if (radius > 0 && Mathf.Max(Mathf.Abs(x - centerX), Mathf.Abs(z - centerZ)) != radius)
                        {
                            continue;
                        }

                        int index = ToIndex(x, z);
                        if (!IsWalkable(robot, walkable, index))
                        {
                            continue;
                        }

                        Vector2 delta = CellPosition(index) - point;
                        float distance = delta.sqrMagnitude;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = index;
                        }
                    }
                }

                if (best >= 0)
                {
                    return best;
                }
            }

            return -1;
        }

        private static bool IsWalkable(RobotController robot, sbyte[] walkable, int index)
        {
            if (walkable[index] == 0)
            {
                walkable[index] = robot.IsPositionClear(CellPosition(index), PathSafetyMargin)
                    ? (sbyte)1 : (sbyte)-1;
            }

            return walkable[index] > 0;
        }

        private static float Heuristic(int a, int b)
        {
            int dx = Mathf.Abs(a % GridSize - b % GridSize);
            int dz = Mathf.Abs(a / GridSize - b / GridSize);
            int diagonal = Mathf.Min(dx, dz);
            return diagonal * 1.41421356f + Mathf.Max(dx, dz) - diagonal;
        }

        private static int ToIndex(int x, int z)
        {
            return z * GridSize + x;
        }

        private static Vector2 CellPosition(int index)
        {
            int x = index % GridSize;
            int z = index / GridSize;
            return new Vector2(GridMin + x * GridStep, GridMin + z * GridStep);
        }
    }
}
