using UnityEngine;

namespace RobotEngine.World
{
    public sealed class WorldBuilder : MonoBehaviour
    {
        private const float RoomSize = 10f;
        private const float WallHeight = 2.5f;
        private const float WallThickness = 0.2f;
        private const float DropZoneSize = 1.25f;

        public void Build()
        {
            Transform root = transform;
            Material floorMaterial = CreateMaterial("Floor", new Color(0.35f, 0.35f, 0.35f));
            Material wallMaterial = CreateMaterial("Wall", new Color(0.48f, 0.48f, 0.48f));
            Material redMaterial = CreateMaterial("Red", new Color(0.92f, 0.02f, 0.02f));
            Material blueMaterial = CreateMaterial("Blue", new Color(0.02f, 0.18f, 0.95f));
            Material yellowMaterial = CreateMaterial("Yellow", new Color(1f, 0.72f, 0.01f));
            Material greenMaterial = CreateMaterial("Green", new Color(0.02f, 0.78f, 0.12f));
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

            // Final layout: red cube 0.4 m, blue cube 0.6 m, red sphere 0.3 m,
            // yellow cylinder 0.8 m, and green cone 0.2 m high. Two reds, two cubes.
            LayoutValidator.LayoutObject[] layout =
            {
                new LayoutValidator.LayoutObject(new Vector2(-2.2f, -1.7f), 0.4f, "cube"),
                new LayoutValidator.LayoutObject(new Vector2(1.7f, -1.3f), 0.6f, "cube"),
                new LayoutValidator.LayoutObject(new Vector2(-1.5f, 1.5f), 0.3f, "sphere"),
                new LayoutValidator.LayoutObject(new Vector2(0f, -0.1f), 0.8f, "cylinder"),
                new LayoutValidator.LayoutObject(new Vector2(2.8f, 0.2f), 0.2f, "cone")
            };
            Vector2 robotStart = new Vector2(-3.4f, -3.4f);
            Vector2 dropZoneCenter = new Vector2(3.1f, -3.1f);
            LayoutValidator.Validate(layout, robotStart, dropZoneCenter, DropZoneSize, RoomSize);

            PrepareFetchable(CreatePrimitive("obj_1", PrimitiveType.Cube, Coordinate(layout[0].Position, layout[0].Height / 2f),
                new Vector3(0.55f, layout[0].Height, 0.55f), redMaterial, root));
            PrepareFetchable(CreatePrimitive("obj_2", PrimitiveType.Cube, Coordinate(layout[1].Position, layout[1].Height / 2f),
                new Vector3(0.65f, layout[1].Height, 0.65f), blueMaterial, root));
            PrepareFetchable(CreatePrimitive("obj_3", PrimitiveType.Sphere, Coordinate(layout[2].Position, layout[2].Height / 2f),
                new Vector3(layout[2].Height, layout[2].Height, layout[2].Height), redMaterial, root));
            PrepareFetchable(CreatePrimitive("obj_4", PrimitiveType.Cylinder, Coordinate(layout[3].Position, layout[3].Height / 2f),
                new Vector3(0.55f, layout[3].Height / 2f, 0.55f), yellowMaterial, root));
            PrepareFetchable(CreateCone("obj_5", Coordinate(layout[4].Position, layout[4].Height / 2f),
                0.7f, layout[4].Height, greenMaterial, root));

            // The drop zone is purely visual: colliders removed so the robot can drive onto it.
            Vector3 zoneCenter = new Vector3(dropZoneCenter.x, 0.012f, dropZoneCenter.y);
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
            SceneRegistry.SetDropZone(new Vector3(dropZoneCenter.x, 0f, dropZoneCenter.y), DropZoneSize);

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

        private static Vector3 Coordinate(Vector2 position, float height)
        {
            return new Vector3(position.x, height, position.y);
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
