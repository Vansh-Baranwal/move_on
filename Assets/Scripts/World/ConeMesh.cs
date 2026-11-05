using UnityEngine;

namespace RobotEngine.World
{
    internal static class ConeMesh
    {
        public static Mesh Create(int sides)
        {
            Mesh mesh = new Mesh { name = "GeneratedCone" };
            Vector3[] vertices = new Vector3[sides * 2 + 2];
            Vector2[] uv = new Vector2[vertices.Length];
            int[] triangles = new int[sides * 6];
            float radius = 0.5f;

            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                vertices[i] = new Vector3(x, -0.5f, z);
                vertices[sides + i] = new Vector3(x, -0.5f, z);
                uv[i] = new Vector2((float)i / sides, 0f);
                uv[sides + i] = uv[i];
            }

            int apex = sides * 2;
            int baseCenter = apex + 1;
            vertices[apex] = new Vector3(0f, 0.5f, 0f);
            vertices[baseCenter] = new Vector3(0f, -0.5f, 0f);
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                int sideOffset = i * 6;
                triangles[sideOffset] = i;
                triangles[sideOffset + 1] = next;
                triangles[sideOffset + 2] = apex;
                triangles[sideOffset + 3] = baseCenter;
                triangles[sideOffset + 4] = next + sides;
                triangles[sideOffset + 5] = i + sides;
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
