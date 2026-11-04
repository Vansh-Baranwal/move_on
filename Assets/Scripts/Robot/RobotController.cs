using System;
using System.Collections;
using System.Collections.Generic;
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

        // Slightly smaller than the visual capsule so horizontal casts never graze the floor plane.
        private const float CastRadius = 0.24f;
        private const float CastHeight = 0.66f;
        private const float CastCenterHeight = 0.35f;
        private const float CastSkin = 0.02f;

        private readonly List<Transform> wheels = new List<Transform>();
        private Transform holdPoint;
        private Transform heldObject;

        public Transform HeldObject
        {
            get { return heldObject; }
        }

        public void Build()
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "RobotBody";
            body.transform.SetParent(transform);
            body.transform.localPosition = new Vector3(0f, 0.35f, 0f);
            body.transform.localScale = new Vector3(0.5f, 0.35f, 0.5f);
            body.GetComponent<Renderer>().sharedMaterial = CreateMaterial("RobotBodyMaterial", new Color(0.12f, 0.14f, 0.18f));

            Material wheelMaterial = CreateMaterial("RobotWheelMaterial", new Color(0.03f, 0.03f, 0.04f));
            CreateWheel("WheelLeft", -0.26f, wheelMaterial);
            CreateWheel("WheelRight", 0.26f, wheelMaterial);

            CapsuleCollider collider = gameObject.AddComponent<CapsuleCollider>();
            collider.radius = 0.25f;
            collider.height = 0.7f;
            collider.center = new Vector3(0f, 0.35f, 0f);

            holdPoint = new GameObject("HoldPoint").transform;
            holdPoint.SetParent(transform);
            holdPoint.localPosition = new Vector3(0f, 0.3f, 0.5f);
        }

        /// <summary>Drives forward (positive) or backward (negative) at MoveSpeed.</summary>
        public IEnumerator MoveRoutine(float signedDistance, Action<MoveResult> done)
        {
            float remaining = Mathf.Abs(signedDistance);
            float direction = Mathf.Sign(signedDistance);
            bool collided = false;
            float moved = 0f;

            while (remaining > 0.0005f)
            {
                float step = Mathf.Min(MoveSpeed * Time.deltaTime, remaining);
                Vector3 travel = transform.forward * direction;
                float castDistance;
                if (CastBody(travel, step, out castDistance))
                {
                    float advance = Mathf.Max(castDistance - CastSkin, 0f);
                    transform.position += travel * advance;
                    moved += advance;
                    collided = true;
                    break;
                }

                transform.position += travel * step;
                moved += step;
                remaining -= step;
                SpinWheels(step);
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

            while (remaining > 0.01f)
            {
                float step = Mathf.Min(TurnSpeed * Time.deltaTime, remaining);
                transform.Rotate(0f, step * direction, 0f, Space.Self);
                turned += step;
                remaining -= step;
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
            target.localPosition = Vector3.zero;
            target.localRotation = Quaternion.identity;
            return true;
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

        private void CreateWheel(string wheelName, float x, Material material)
        {
            GameObject wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = wheelName;
            wheel.transform.SetParent(transform);
            wheel.transform.localPosition = new Vector3(x, 0.28f, 0f);
            wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wheel.transform.localScale = new Vector3(0.12f, 0.06f, 0.12f);
            wheel.GetComponent<Renderer>().sharedMaterial = material;
            wheels.Add(wheel.transform);
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
