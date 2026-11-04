using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using RobotEngine.Sim;
using RobotEngine.World;

namespace RobotEngine.Robot
{
    public sealed class MoveResult
    {
        public bool Collided;
        public float Moved;

        public MoveResult(bool collided, float moved)
        {
            Collided = collided;
            Moved = moved;
        }
    }

    /// <summary>
    /// Wheeled robot body. All motion is executed by coroutines driven by the engine API at the
    /// spec speeds: 1 m/s translation, 90 deg/s turn, 0.5 m pickup reach within +/-30 deg.
    /// </summary>
    public sealed class RobotController : MonoBehaviour
    {
        public const float MoveSpeed = 1f;
        public const float TurnSpeed = 90f;
        public const float ReachDistance = 0.5f;
        public const float ReachAngle = 30f;
        public const float BackoffDistance = 0.1f;
    private const float FixedStep = 1f / 60f;

        // Slightly smaller than the visual capsule so horizontal casts never graze the floor plane.
        private const float CastRadius = 0.24f;
        private const float CastHeight = 0.66f;
        private const float CastCenterHeight = 0.35f;
        private const float CastSkin = 0.02f;

        private readonly List<Transform> wheels = new List<Transform>();
        private Transform holdPoint;
        private GameObject visualsGroup;
        private Transform heldObject;

        public Transform HeldObject
        {
            get { return heldObject; }
        }

        public void Build()
        {
            // All cosmetic meshes live under one group so a custom GLB model can replace them.
            visualsGroup = new GameObject("ProceduralBody");
            visualsGroup.transform.SetParent(transform, false);
            Transform root = visualsGroup.transform;

            Material shellMaterial = CreateMaterial("RobotShellMaterial", new Color(0.13f, 0.15f, 0.19f));
            shellMaterial.SetFloat("_Smoothness", 0.45f);
            Material trimMaterial = CreateMaterial("RobotTrimMaterial", new Color(0.55f, 0.58f, 0.62f));
            trimMaterial.SetFloat("_Smoothness", 0.6f);
            Material darkMaterial = CreateMaterial("RobotDarkMaterial", new Color(0.05f, 0.05f, 0.06f));
            Material wheelMaterial = CreateMaterial("RobotWheelMaterial", new Color(0.04f, 0.04f, 0.05f));

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "RobotBody";
            body.transform.SetParent(root);
            body.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.35f, 0.5f);
            body.GetComponent<Renderer>().sharedMaterial = shellMaterial;

            // Low chassis under the shell for a grounded, vehicle-like silhouette.
            GameObject chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chassis.name = "Chassis";
            chassis.transform.SetParent(root);
            chassis.transform.localPosition = new Vector3(0f, 0.14f, -0.03f);
            chassis.transform.localScale = new Vector3(0.44f, 0.16f, 0.44f);
            chassis.GetComponent<Renderer>().sharedMaterial = trimMaterial;

            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "Visor";
            visor.transform.SetParent(root);
            visor.transform.localPosition = new Vector3(0f, 0.42f, 0.12f);
            visor.transform.localScale = new Vector3(0.3f, 0.1f, 0.1f);
            visor.GetComponent<Renderer>().sharedMaterial = darkMaterial;

            CreateWheel("WheelLeft", -0.26f, wheelMaterial, root);
            CreateWheel("WheelRight", 0.26f, wheelMaterial, root);
            CreateCaster("CasterFront", new Vector3(0f, 0.1f, 0.24f), root);
            CreateCaster("CasterRear", new Vector3(0f, 0.1f, -0.24f), root);

            // Visible camera mast so the audience can see the robot's eye.
            GameObject cameraMast = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cameraMast.name = "CameraMast";
            cameraMast.transform.SetParent(root);
            cameraMast.transform.localPosition = new Vector3(0f, 0.52f, 0.28f);
            cameraMast.transform.localScale = new Vector3(0.14f, 0.1f, 0.16f);
            cameraMast.GetComponent<Renderer>().sharedMaterial = darkMaterial;

            GameObject lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lens.name = "CameraLens";
            lens.transform.SetParent(cameraMast.transform);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.09f);
            lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            lens.transform.localScale = new Vector3(0.5f, 0.2f, 0.5f);
            lens.GetComponent<Renderer>().sharedMaterial = CreateMaterial("RobotLensMaterial", new Color(0.15f, 0.35f, 0.6f));

            // Antenna + beacon for character.
            GameObject antenna = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            antenna.name = "Antenna";
            antenna.transform.SetParent(root);
            antenna.transform.localPosition = new Vector3(0f, 0.68f, -0.16f);
            antenna.transform.localScale = new Vector3(0.15f, 0.22f, 0.15f);
            antenna.GetComponent<Renderer>().sharedMaterial = trimMaterial;
            GameObject beacon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            beacon.name = "Beacon";
            beacon.transform.SetParent(root);
            beacon.transform.localPosition = new Vector3(0f, 0.8f, -0.16f);
            beacon.transform.localScale = new Vector3(0.09f, 0.09f, 0.09f);
            Material beaconMaterial = CreateMaterial("RobotBeaconMaterial", new Color(0.9f, 0.25f, 0.1f));
            beaconMaterial.SetFloat("_Smoothness", 0.7f);
            beacon.GetComponent<Renderer>().sharedMaterial = beaconMaterial;

            CapsuleCollider collider = gameObject.AddComponent<CapsuleCollider>();
            collider.radius = 0.25f;
            collider.height = 0.7f;
            collider.center = new Vector3(0f, 0.35f, 0f);

            holdPoint = new GameObject("HoldPoint").transform;
            holdPoint.SetParent(transform);
            holdPoint.localPosition = new Vector3(0f, 0.3f, 0.5f);

            LoadCustomModelIfPresent();
        }

        /// <summary>If a robot.glb is provided, load it and hide the procedural meshes.</summary>
        private async void LoadCustomModelIfPresent()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string[] candidates =
            {
                Path.Combine(projectRoot, "Assets", "StreamingAssets", "robot.glb"),
                Path.Combine(projectRoot, "robot.glb"),
            };
            string path = candidates.FirstOrDefault(File.Exists);
            if (path == null)
            {
                return;
            }

            try
            {
                GLTFast.GltfImport importer = new GLTFast.GltfImport();
                bool loaded = await importer.Load(new Uri(path));
                if (!loaded)
                {
                    Debug.LogWarning("robot.glb failed to load; keeping the procedural robot");
                    return;
                }

                await importer.InstantiateMainSceneAsync(transform);
                if (visualsGroup != null)
                {
                    visualsGroup.SetActive(false);
                }

                Debug.Log("Custom robot model loaded from " + path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("robot.glb import error: " + ex.Message + " - keeping the procedural robot");
            }
        }

        /// <summary>Drives forward (positive) or backward (negative) at MoveSpeed.</summary>
        public IEnumerator MoveRoutine(float signedDistance, Action<MoveResult> done)
        {
            float remaining = Mathf.Abs(signedDistance);
            float direction = Mathf.Sign(signedDistance);
            bool collided = false;
            float moved = 0f;
            float accumulator = 0f;

            // Fixed-step integration: the simulation advances only in FixedStep
            // chunks while this op runs, and never between ops.
            while (remaining > 0.0005f)
            {
                accumulator += Time.deltaTime;
                while (accumulator >= FixedStep && remaining > 0.0005f)
                {
                    accumulator -= FixedStep;
                    float step = Mathf.Min(MoveSpeed * FixedStep, remaining);
                    Vector3 travel = transform.forward * direction;
                    float castDistance;
                    if (CastBody(travel, step, out castDistance))
                    {
                        float advance = Mathf.Max(castDistance - CastSkin, 0f);
                        transform.position += travel * advance;
                        moved += advance;
                        collided = true;
                        remaining = 0f;
                        break;
                    }

                    transform.position += travel * step;
                    moved += step;
                    remaining -= step;
                    SpinWheels(step);
                }

                yield return null;
            }

            if (collided)
            {
                yield return BackoffRoutine(transform.forward * direction);
            }

            done(new MoveResult(collided, moved));
        }

        /// <summary>Turns in place at TurnSpeed; positive degrees turn right per the API spec.</summary>
        public IEnumerator TurnRoutine(float signedDegrees, Action<float> done)
        {
            float remaining = Mathf.Abs(signedDegrees);
            float direction = Mathf.Sign(signedDegrees);
            float turned = 0f;
            float accumulator = 0f;

            while (remaining > 0.01f)
            {
                accumulator += Time.deltaTime;
                while (accumulator >= FixedStep && remaining > 0.01f)
                {
                    accumulator -= FixedStep;
                    float step = Mathf.Min(TurnSpeed * FixedStep, remaining);
                    transform.Rotate(0f, step * direction, 0f, Space.Self);
                    turned += step;
                    remaining -= step;
                }

                yield return null;
            }

            done(turned);
        }

        /// <summary>
        /// Attaches the nearest fetchable object within reach (0.5 m plus its radius) and within
        /// +/-30 deg of the heading. Returns success/failure only; object identity never leaves
        /// the engine.
        /// </summary>
        public bool TryPickup()
        {
            Transform target = FindPickupTarget();
            if (target == null)
            {
                return false;
            }

            heldObject = target;
            Rigidbody body = target.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = true;
            }

            // Disable colliders while carried so casts never treat the payload as an obstacle.
            foreach (Collider collider in target.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }

            target.SetParent(holdPoint);
            StartCoroutine(SnapRoutine(target));
            return true;
        }

        /// <summary>Snap animation: pull the object from its grabbed pose onto the hold point.</summary>
        private IEnumerator SnapRoutine(Transform target)
        {
            Vector3 startPosition = target.localPosition;
            Quaternion startRotation = target.localRotation;
            float elapsed = 0f;
            const float duration = 0.15f;

            while (elapsed < duration && target.parent == holdPoint)
            {
                elapsed += Time.deltaTime;
                float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / duration));
                target.localPosition = Vector3.Lerp(startPosition, Vector3.zero, eased);
                target.localRotation = Quaternion.Slerp(startRotation, Quaternion.identity, eased);
                yield return null;
            }

            if (target.parent == holdPoint)
            {
                target.localPosition = Vector3.zero;
                target.localRotation = Quaternion.identity;
            }
        }

        /// <summary>Releases the held object, lets it settle, then reports zone landing.</summary>
        public IEnumerator DropRoutine(Action<bool> done)
        {
            if (heldObject == null)
            {
                done(false);
                yield break;
            }

            Transform dropped = heldObject;
            heldObject = null;
            dropped.SetParent(null, true);
            foreach (Collider collider in dropped.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = true;
            }

            Rigidbody body = dropped.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.isKinematic = false;
            }

            yield return new WaitForSeconds(0.6f);

            if (body != null)
            {
                body.isKinematic = true;
            }

            done(SceneRegistry.IsInsideDropZone(dropped.position));
        }

        /// <summary>Quiet release used by /reset; SceneRegistry restores the original pose.</summary>
        public void ReleaseHeldQuiet()
        {
            if (heldObject != null)
            {
                heldObject.SetParent(null, true);
                heldObject = null;
            }
        }

        private Transform FindPickupTarget()
        {
            Vector2 forward = new Vector2(transform.forward.x, transform.forward.z).normalized;
            Transform best = null;
            float bestDistance = float.MaxValue;

            foreach (GameObject candidate in SceneRegistry.GetFetchables())
            {
                if (candidate.transform == heldObject)
                {
                    continue;
                }

                Collider candidateCollider = candidate.GetComponent<Collider>();
                if (candidateCollider == null)
                {
                    continue;
                }

                Vector3 offset = candidate.transform.position - transform.position;
                Vector2 flat = new Vector2(offset.x, offset.z);
                float distance = flat.magnitude;
                Bounds bounds = candidateCollider.bounds;
                float reach = ReachDistance + Mathf.Max(bounds.extents.x, bounds.extents.z);
                if (distance > reach)
                {
                    continue;
                }

                float angle = Vector2.SignedAngle(forward, flat.normalized);
                if (Mathf.Abs(angle) > ReachAngle)
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate.transform;
                }
            }

            return best;
        }

        private IEnumerator BackoffRoutine(Vector3 travelDirection)
        {
            Vector3 back = -travelDirection;
            float backed = 0f;

            while (backed < BackoffDistance)
            {
                float step = Mathf.Min(MoveSpeed * Time.deltaTime, BackoffDistance - backed);
                float castDistance;
                if (CastBody(back, step, out castDistance))
                {
                    break;
                }

                transform.position += back * step;
                backed += step;
                SpinWheels(-step);
                yield return null;
            }
        }

        private bool CastBody(Vector3 direction, float distance, out float castDistance)
        {
            Vector3 center = transform.position + new Vector3(0f, CastCenterHeight, 0f);
            float halfHeight = Mathf.Max(CastHeight * 0.5f - CastRadius, 0.01f);
            Vector3 bottom = center - transform.up * halfHeight;
            Vector3 top = center + transform.up * halfHeight;

            RaycastHit[] hits = Physics.CapsuleCastAll(bottom, top, CastRadius, direction, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            castDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider.transform.IsChildOf(transform))
                {
                    continue;
                }

                if (hits[i].distance < castDistance)
                {
                    castDistance = hits[i].distance;
                }
            }

            return castDistance <= distance;
        }

        private void SpinWheels(float travel)
        {
            for (int i = 0; i < wheels.Count; i++)
            {
                wheels[i].Rotate(Vector3.right, -travel * 360f, Space.Self);
            }
        }

        private void CreateWheel(string wheelName, float x, Material material, Transform parent)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = wheelName;
            wheel.transform.SetParent(parent);
            wheel.transform.localPosition = new Vector3(x, 0.28f, 0f);
            wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wheel.transform.localScale = new Vector3(0.12f, 0.06f, 0.12f);
            wheel.GetComponent<Renderer>().sharedMaterial = material;
            wheels.Add(wheel.transform);
        }

        private void CreateCaster(string casterName, Vector3 localPosition, Transform parent)
        {
            GameObject caster = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            caster.name = casterName;
            caster.transform.SetParent(parent);
            caster.transform.localPosition = localPosition;
            caster.transform.localScale = new Vector3(0.16f, 0.16f, 0.16f);
            caster.GetComponent<Renderer>().sharedMaterial =
                CreateMaterial("RobotCasterMaterial", new Color(0.1f, 0.1f, 0.11f));
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
    }
}
