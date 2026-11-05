/// <summary>Colour-blob analysis over camera pixels - C# port of agent/local_vision.py,
/// used by the internal brain. Pixels only, exactly like the API's /look path.</summary>
using System.Collections.Generic;
using UnityEngine;

namespace RobotEngine.Sim
{
    public sealed class Blob
    {
        public string Colour;
        public string Shape;
        public float Fill;
        public float Aspect;
        public float Cx;
        public float Cy;
        public int TopY;
        public int W;
        public int H;
        public int Area;
    }

    public static class RobotVision
    {
        public const int FrameWidth = 640;
        public const int FrameHeight = 480;
        private const int Cell = 8;
        private const int GridW = FrameWidth / Cell;   // 80
        private const int GridH = FrameHeight / Cell;  // 60
        private const int SkyRows = 130;

        // Hue bands in [0..1] (RGBToHSV), matching the Python detector.
        private static readonly Dictionary<string, List<Vector2>> Bands = new Dictionary<string, List<Vector2>>
        {
            { "red", new List<Vector2> { new Vector2(350f / 360f, 1f), new Vector2(0f, 10f / 360f) } },
            { "yellow", new List<Vector2> { new Vector2(40f / 360f, 70f / 360f) } },
            { "green", new List<Vector2> { new Vector2(100f / 360f, 150f / 360f) } },
            { "blue", new List<Vector2> { new Vector2(210f / 360f, 250f / 360f) } },
            { "cyan", new List<Vector2> { new Vector2(160f / 360f, 205f / 360f) } },
        };

        public static List<Blob> Analyze(Color32[] pixels, int width, int height)
        {
            List<Blob> blobs = new List<Blob>();
            bool[] matched = new bool[GridW * GridH];

            foreach (KeyValuePair<string, List<Vector2>> pair in Bands)
            {
                bool[] mask = new bool[width * height];
                for (int y = SkyRows; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        Color32 pixel = pixels[y * width + x];
                        Color color = pixel;
                        float hue, sat, val;
                        Color.RGBToHSV(color, out hue, out sat, out val);
                        if (sat <= 0.314f || val <= 0.235f)
                        {
                            continue;
                        }

                        foreach (Vector2 band in pair.Value)
                        {
                            bool inBand = band.x <= band.y
                                ? (hue >= band.x && hue <= band.y)
                                : (hue >= band.x || hue <= band.y);
                            if (inBand)
                            {
                                mask[y * width + x] = true;
                                break;
                            }
                        }
                    }
                }

                // Downsample to the 8 px grid with 35% coverage.
                bool[] grid = new bool[GridW * GridH];
                for (int gy = 0; gy < GridH; gy++)
                {
                    for (int gx = 0; gx < GridW; gx++)
                    {
                        int hits = 0;
                        for (int py = 0; py < Cell; py++)
                        {
                            int row = gy * Cell + py;
                            for (int px = 0; px < Cell; px++)
                            {
                                if (mask[row * width + gx * Cell + px])
                                {
                                    hits++;
                                }
                            }
                        }

                        grid[gy * GridW + gx] = hits > Cell * Cell * 0.35f;
                    }
                }

                foreach (List<int> group in Components(grid))
                {
                    if (group.Count < 3)
                    {
                        continue;
                    }

                    int gx0 = GridW, gx1 = 0, gy0 = GridH, gy1 = 0;
                    float sumX = 0f, sumY = 0f;
                    foreach (int cell in group)
                    {
                        int cy = cell / GridW;
                        int cx = cell % GridW;
                        if (cx < gx0) gx0 = cx;
                        if (cx > gx1) gx1 = cx;
                        if (cy < gy0) gy0 = cy;
                        if (cy > gy1) gy1 = cy;
                        sumX += cx;
                        sumY += cy;
                    }

                    int x0 = gx0 * Cell, x1 = (gx1 + 1) * Cell;
                    int y0 = gy0 * Cell, y1 = (gy1 + 1) * Cell;
                    int bw = x1 - x0, bh = y1 - y0;

                    int pixelCount = 0;
                    for (int y = y0; y < y1 && y < height; y++)
                    {
                        for (int x = x0; x < x1 && x < width; x++)
                        {
                            if (mask[y * width + x])
                            {
                                pixelCount++;
                            }
                        }
                    }

                    float fill = pixelCount / (float)(bw * bh);
                    float aspect = bw / (float)Mathf.Max(1, bh);
                    string shape = fill >= 0.68f && aspect >= 0.8f && aspect <= 1.25f
                        ? "round"
                        : (aspect >= 2f || fill < 0.58f ? "tapered" : "boxy");

                    blobs.Add(new Blob
                    {
                        Colour = pair.Key,
                        Shape = shape,
                        Fill = fill,
                        Aspect = aspect,
                        Cx = (sumX / group.Count) * Cell + Cell / 2f,
                        Cy = (sumY / group.Count) * Cell + Cell / 2f,
                        TopY = y0,
                        W = bw,
                        H = bh,
                        Area = group.Count,
                    });
                }
            }

            blobs.Sort((a, b) => b.Area.CompareTo(a.Area));
            return blobs;
        }

        private static List<List<int>> Components(bool[] grid)
        {
            List<List<int>> components = new List<List<int>>();
            bool[] visited = new bool[grid.Length];
            Stack<int> stack = new Stack<int>();
            for (int start = 0; start < grid.Length; start++)
            {
                if (!grid[start] || visited[start])
                {
                    continue;
                }

                List<int> group = new List<int>();
                stack.Push(start);
                visited[start] = true;
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    group.Add(cell);
                    int cy = cell / GridW;
                    int cx = cell % GridW;
                    TryVisit(grid, visited, stack, cy - 1, cx);
                    TryVisit(grid, visited, stack, cy + 1, cx);
                    TryVisit(grid, visited, stack, cy, cx - 1);
                    TryVisit(grid, visited, stack, cy, cx + 1);
                }

                components.Add(group);
            }

            return components;
        }

        private static void TryVisit(bool[] grid, bool[] visited, Stack<int> stack, int cy, int cx)
        {
            if (cy < 0 || cy >= GridH || cx < 0 || cx >= GridW)
            {
                return;
            }

            int cell = cy * GridW + cx;
            if (grid[cell] && !visited[cell])
            {
                visited[cell] = true;
                stack.Push(cell);
            }
        }
    }
}
