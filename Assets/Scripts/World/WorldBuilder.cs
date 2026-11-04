using System.Collections.Generic;
using UnityEngine;
using RobotEngine.Sim;

namespace RobotEngine.World
{
    /// <summary>
    /// Flat test room: four walls, 4-6 high-contrast primitives (two sharing a
    /// colour, two sharing a shape) and a marked drop zone. Build(0) uses the
    /// curated demo layout; Build(seed) generates a deterministic random layout
    /// that still passes the LayoutValidator clearances.
    /// </summary>
    public sealed class WorldBuilder : MonoBehaviour
    {
        private const float RoomSize = 10f;
        private const float WallHeight = 2.5f;
        private const float WallThickness = 0.2f;
        private const float DropZoneSize = 1.25f;
        private const int MaxLayoutAttempts = 300;

        private static readonly string[] ShapePlan = { "cube", "cube", "sphere", "cylinder", "cone" };
        private static readonly string[] ColourPalette = { "red", "blue", "yellow", "green" };

        private int currentSeed;

        public void Build(int seed)
        {
            currentSeed = seed;
            System.Random random = new System.Random(seed == 0 ? 20261104 : seed);

            Transform root = transform;
            Material floorMaterial = CreateMaterial("Floor", new Color(0.35f, 0.35f, 0.35f));
            Material wallMaterial = CreateMaterial("Wall", new Color(0.48f, 0.48f, 0.48f));
            Material dropZoneMaterial = CreateUnlitMaterial("DropZone", new Color(1f, 0f, 0.8f));
            Material borderMaterial = CreateUnlitMaterial("DropZoneBorder", new Color(0.08f, 0.08f, 0.08f));

            CreatePrimitive("Floor", PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f),
                new Vector3(RoomSize, 0.1f, RoomSize), floorMaterial, root);
            CreatePrimitive("WallNorth", PrimitiveType.Cube, new Vector3(0f, WallHeight / 2f, RoomSize / 2f),
                new Vector3(RoomSize, WallHeight, WallThickness), wallMaterial, root);
            CreatePrimitive("WallSouth", PrimitiveType.Cube, new Vector3(0f, WallHeight / 2f, -RoomSize / 2f),
                new Vector3(RoomSize, WallHeight, WallThickness), wallMaterial, root);
            CreatePrimitive("WallEast", PrimitiveType.Cube, new Vector3(RoomSize / 2f, WallHeight / 2f, 0f),
                new Vector3(WallThickness, WallHeight, RoomSize), wallMaterial, root);
            CreatePrimitive("WallWest", PrimitiveType.Cube, new Vector3(-RoomSize / 2f, WallHeight / 2f, 0f),
                new Vector3(WallThickness, WallHeight, RoomSize), wallMaterial, root);

            Vector2 robotStart = new Vector2(-3.4f, -3.4f);
            LayoutPlan plan = GeneratePlan(random);
            LayoutValidator.Validate(plan.Objects, robotStart, plan.ZoneCenter, DropZoneSize, RoomSize);

            for (int i = 0; i < plan.Objects.Length; i++)
            {
                Material material = CreateMaterial("Colour" + plan.Colours[i], ColourOf(plan.Colours[i]));
                GameObject fetchable = CreateShape("obj_" + (i + 1), plan.Objects[i], plan.Colours[i], material, root);
                PrepareFetchable(fetchable);
            }

            // The drop zone is purely visual: colliders removed so the robot can drive onto it.
            Vector3 zoneCenter = new Vector3(plan.ZoneCenter.x, 0.012f, plan.ZoneCenter.y);
            StripCollider(CreatePrimitive("DropZone", PrimitiveType.Cube, zoneCenter,
                new Vector3(DropZoneSize, 0.025f, DropZoneSize), dropZoneMaterial, root));
            StripCollider(CreatePrimitive("DropZoneBorderNorth", PrimitiveType.Cube,
                zoneCenter + new Vector3(0f, 0.015f, DropZoneSize / 2f),
                new Vector3(DropZoneSize + 0.12f, 0.04f, 0.08f), borderMaterial, root));
            StripCollider(CreatePrimitive("DropZoneBorderSouth", PrimitiveType.Cube,
                zoneCenter + new Vector3(0f, 0.015f, -DropZoneSize / 2f),
                new Vector3(DropZoneSize + 0.12f, 0.04f, 0.08f), borderMaterial, root));
            StripCollider(CreatePrimitive("DropZoneBorderEast", PrimitiveType.Cube,
                zoneCenter + new Vector3(DropZoneSize / 2f, 0.015f, 0f),
                new Vector3(0.08f, 0.04f, DropZoneSize), borderMaterial, root));
            StripCollider(CreatePrimitive("DropZoneBorderWest", PrimitiveType.Cube,
                zoneCenter + new Vector3(-DropZoneSize / 2f, 0.015f, 0f),
                new Vector3(0.08f, 0.04f, DropZoneSize), borderMaterial, root));
            SceneRegistry.SetDropZone(new Vector3(plan.ZoneCenter.x, 0f, plan.ZoneCenter.y), DropZoneSize);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.42f, 0.42f);
            RenderSettings.ambientIntensity = 1f;
            GameObject lightObject = new GameObject("WorldLight");
            lightObject.transform.SetParent(root);
            lightObject.transform.rotation = Quaternion.Euler(50f, -35f, 0f);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.65f;
        }

        /// <summary>Tears down the current room and rebuilds it from a seed.</summary>
        public void Rebuild(int seed)
        {
            SceneRegistry.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            Build(seed);
        }

        private LayoutPlan GeneratePlan(System.Random random)
        {
            if (currentSeed == 0)
            {
                return CuratedPlan();
            }

            // Two shapes share a colour; the cubes guarantee two share a shape.
            int pairA = random.Next(ShapePlan.Length);
            int pairB = (pairA + 1 + random.Next(ShapePlan.Length - 1)) % ShapePlan.Length;
            int sharedColour = random.Next(ColourPalette.Length);
            List<int> remaining = new List<int>();
            for (int i = 0; i < ColourPalette.Length; i++)
            {
                if (i != sharedColour)
                {
                    remaining.Add(i);
                }
            }

            string[] colours = new string[ShapePlan.Length];
            for (int i = 0; i < ShapePlan.Length; i++)
            {
                if (i == pairA || i == pairB)
                {
                    colours[i] = ColourPalette[sharedColour];
                }
                else
                {
                    int pick = random.Next(remaining.Count);
                    colours[i] = ColourPalette[remaining[pick]];
                    remaining.RemoveAt(pick);
                }
            }

            for (int attempt = 0; attempt < MaxLayoutAttempts; attempt++)
            {
                LayoutValidator.LayoutObject[] objects = new LayoutValidator.LayoutObject[ShapePlan.Length];
                for (int i = 0; i < ShapePlan.Length; i++)
                {
                    float height = ShapePlan[i] switch
                    {
                        "cube" => 0.3f + (float)random.NextDouble() * 0.3f,
                        "sphere" => 0.2f + (float)random.NextDouble() * 0.2f,
                        "cylinder" => 0.5f + (float)random.NextDouble() * 0.4f,
                        _ => 0.15f + (float)random.NextDouble() * 0.2f,
                    };
                    Vector2 position = new Vector2(
                        -3.8f + (float)random.NextDouble() * 7.6f,
                        -3.8f + (float)random.NextDouble() * 7.6f);
                    objects[i] = new LayoutValidator.LayoutObject(position, height, ShapePlan[i]);
                }

                Vector2 zoneCenter = new Vector2(
                    1.5f + (float)random.NextDouble() * 2.3f,
                    -3.8f + (float)random.NextDouble() * 2.3f);
                try
                {
                    LayoutValidator.Validate(objects, new Vector2(-3.4f, -3.4f), zoneCenter, DropZoneSize, RoomSize);
                    return new LayoutPlan(objects, colours, zoneCenter);
                }
                catch (System.InvalidOperationException)
                {
                    // Overlapping or unreachable sample: resample.
                }
            }

            return CuratedPlan();
        }

        private static LayoutPlan CuratedPlan()
        {
            return new LayoutPlan(
                new[]
                {
                    new LayoutValidator.LayoutObject(new Vector2(-2.2f, -1.7f), 0.4f, "cube"),
                    new LayoutValidator.LayoutObject(new Vector2(1.7f, -1.3f), 0.6f, "cube"),
                    new LayoutValidator.LayoutObject(new Vector2(-1.5f, 1.5f), 0.3f, "sphere"),
                    new LayoutValidator.LayoutObject(new Vector2(0f, -0.1f), 0.8f, "cylinder"),
                    new LayoutValidator.LayoutObject(new Vector2(2.8f, 0.2f), 0.2f, "cone"),
                },
                new[] { "red", "blue", "red", "yellow", "green" },
                new Vector2(3.1f, -3.1f));
        }

        private static GameObject CreateShape(string objectName, LayoutValidator.LayoutObject spec,
            string colourName, Material material, Transform parent)
        {
            switch (spec.Shape)
            {
                case "cube":
                    return CreatePrimitive(objectName, PrimitiveType.Cube,
                        new Vector3(spec.Position.x, spec.Height / 2f, spec.Position.y),
                        new Vector3(spec.Height + 0.15f, spec.Height, spec.Height + 0.15f), material, parent);
                case "sphere":
                    return CreatePrimitive(objectName, PrimitiveType.Sphere,
                        new Vector3(spec.Position.x, spec.Height / 2f, spec.Position.y),
                        new Vector3(spec.Height, spec.Height, spec.Height), material, parent);
                case "cylinder":
                    return CreatePrimitive(objectName, PrimitiveType.Cylinder,
                        new Vector3(spec.Position.x, spec.Height / 2f, spec.Position.y),
                        new Vector3(0.55f, spec.Height / 2f, 0.55f), material, parent);
                default:
                    return CreateCone(objectName,
                        new Vector3(spec.Position.x, spec.Height / 2f, spec.Position.y),
                        0.7f, spec.Height, material, parent);
            }
        }

        private static Color ColourOf(string name)
        {
            switch (name)
            {
                case "blue": return new Color(0.02f, 0.18f, 0.95f);
                case "yellow": return new Color(1f, 0.72f, 0.01f);
                case "green": return new Color(0.02f, 0.78f, 0.12f);
                default: return new Color(0.92f, 0.02f, 0.02f);
            }
        }

        private struct LayoutPlan
        {
            public LayoutValidator.LayoutObject[] Objects;
            public string[] Colours;
            public Vector2 ZoneCenter;

            public LayoutPlan(LayoutValidator.LayoutObject[] objects, string[] colours, Vector2 zoneCenter)
            {
                Objects = objects;
                Colours = colours;
                ZoneCenter = zoneCenter;
            }
        }

        private static void PrepareFetchable(GameObject fetchable)
        {
            Rigidbody body = fetchable.AddComponent<Rigidbody>();
            body.isKinematic = true;
            MeshCollider meshCollider = fetchable.GetComponent<MeshCollider>();
            if (meshCollider != null)
            {
                meshCollider.convex = true;
            }

            SceneRegistry.RegisterFetchable(fetchable);
        }

        private static void StripCollider(GameObject visual)
        {
            Destroy(visual.GetComponent<Collider>());
        }

        private static GameObject CreatePrimitive(string objectName, PrimitiveType type, Vector3 position,
            Vector3 scale, Material material, Transform parent)
        {
            GameObject created = GameObject.CreatePrimitive(type);
            created.name = objectName;
            created.transform.SetParent(parent);
            created.transform.SetPositionAndRotation(position, Quaternion.identity);
            created.transform.localScale = scale;
            created.GetComponent<Renderer>().sharedMaterial = material;
            return created;
        }

        private static GameObject CreateCone(string objectName, Vector3 position, float diameter, float height,
            Material material, Transform parent)
        {
            GameObject created = new GameObject(objectName);
            created.transform.SetParent(parent);
            created.transform.SetPositionAndRotation(position, Quaternion.identity);
            MeshFilter meshFilter = created.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = ConeMesh.Create(24);
            created.transform.localScale = new Vector3(diameter, height, diameter);
            created.AddComponent<MeshRenderer>().sharedMaterial = material;
            created.AddComponent<MeshCollider>().sharedMesh = meshFilter.sharedMesh;
            return created;
        }

        private static Material CreateMaterial(string materialName, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            Material material = new Material(shader)
            {
                name = materialName,
                color = color
            };
            material.SetFloat("_Smoothness", 0f);
            material.SetFloat("_SpecularHighlights", 0f);
            material.SetColor("_SpecColor", Color.black);
            material.DisableKeyword("_SPECULARHIGHLIGHTS_ON");
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            return material;
        }

        private static Material CreateUnlitMaterial(string materialName, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            return new Material(shader)
            {
                name = materialName,
                color = color
            };
        }
    }
}
