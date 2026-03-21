using System.Collections;
using UnityEngine;
using Nox.UI;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;

namespace Nox.Desktop.Runtime {
	[RequireComponent(typeof(Rigidbody)), RequireComponent(typeof(CapsuleCollider))]
	public class DesktopPlayer : MonoBehaviour {
		[Header("Desktop Player")]
		public Camera headCamera;

		public Transform       forwardFollow;
		public CapsuleCollider bodyCollider;

		[Header("Movement")]
		public bool useMovement = true;

		public float maxMoveSpeed     = 2.3f;
		public float moveAcceleration = 100000f;
		public float jumpForce        = 5f;
		public float sprintMultiplier = 1.5f;
		public float airControl       = 0.3f;
		public float movementDeadzone = 0.2f;

		[Header("Height")]
		public float heightOffset = 0f;

		public bool    crouching                = false;
		public float   crouchHeight             = 0.6f;
		public float   heightSmoothSpeed        = 10f;
		public bool    autoAdjustColliderHeight = true;
		public Vector2 minMaxHeight             = new Vector2(0.5f, 2.5f);

		[Header("Grounding")]
		public bool useGrounding = true;

		public float     maxStepHeight              = 0.3f;
		public float     groundingPenetrationOffset = 0.1f;
		public float     maxStepAngle               = 45f;
		public LayerMask groundLayerMask            = -1;
		public float     groundedDrag               = 10000f;
		public float     flyingDrag                 = 4f;

		[Header("Flying")]
		public bool mayFly = false;

		public  float      flySpeed         = 5f;
		public  float      flyAcceleration  = 20f;
		public  float      verticalFlySpeed = 3f; // Private fields
		public  Rigidbody  body;
		private Vector3    moveDirection;
		private Vector3    flyDirection;
		private float      turningAxis;
		private bool       isGrounded  = false;
		private bool       isFlying    = false;
		private bool       isSprinting = false;
		private bool       lastCrouching;
		private RaycastHit lastGroundHit;
		private bool       tempDisableGrounding  = false;
		private bool       isGroundedWhileFlying = false;
		private RaycastHit lastFlyingGroundHit;

		public virtual void Awake() {
			body = GetComponent<Rigidbody>();
			if (body.collisionDetectionMode == CollisionDetectionMode.Discrete)
				body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
			if (forwardFollow == null)
				forwardFollow = headCamera.transform;

			bodyCollider = GetComponent<CapsuleCollider>();
		}

		public virtual void Start() {
			if (headCamera == null) {
				Logger.LogError("DesktopPlayer: headCamera is not assigned!");
				return;
			}

			// Configuration will be applied manually when needed
		}

		/// <summary>Sets move direction for this fixedupdate</summary>
		public virtual void Move(Vector2 axis, bool useDeadzone = true, bool useRelativeDirection = true) {
			if (!useMovement) return;

			// Apply deadzone
			if (useDeadzone && axis.magnitude < movementDeadzone) {
				moveDirection = Vector3.zero;
				return;
			}

			Vector3 forward = useRelativeDirection ? forwardFollow.forward : Vector3.forward;
			Vector3 right   = useRelativeDirection ? forwardFollow.right : Vector3.right;

			// Flatten vectors to avoid unwanted vertical movement
			forward.y = 0;
			right.y   = 0;
			forward.Normalize();
			right.Normalize();

			moveDirection = (forward * axis.y + right * axis.x).normalized;
		}

		/// <summary>Sets sprint state</summary>
		public virtual void SetSprinting(bool sprinting) {
			isSprinting = sprinting;
		}

		/// <summary>Sets turning axis for smooth turning</summary>
		public virtual void Turn(float axis) {
			if (!useMovement) return;
			turningAxis = axis;
		}

		public virtual void Jump() {
			if (!isGrounded) return;
			DisableGrounding(0.1f);
			body.useGravity = true;
			body.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
		}

		public virtual void ToggleFlying() {
			if (!mayFly) return;
			isFlying = !isFlying;
			// When flying, disable grounding and gravity
			// When not flying, enable grounding and let Ground() manage gravity
			if (isFlying) 
				body.useGravity = false;
			
			// Gravity will be managed by Ground() method
			flyDirection    = Vector3.zero;
		}

		/// <summary>Sets fly direction including vertical movement</summary>
		public virtual void Fly(Vector3 direction) {
			if (!isFlying || !mayFly) return;
			flyDirection = direction;
		}

		public virtual void SetCrouching(bool crouch) {
			crouching = crouch;
		}

		protected virtual void FixedUpdate() {
			if (!useMovement) return;
			UpdateRigidbody();
			Ground();
			CheckGroundWhileFlying(); // Always check ground even when flying
			UpdatePlayerHeight();
		}

		protected virtual void UpdateRigidbody() {
			var move = moveDirection;
			var yVel = body.linearVelocity.y;

			if (isFlying) {
				// Flying movement with vertical control
				Vector3 targetVelocity = move * flySpeed;

				// Add vertical movement for flying
				if (flyDirection.y != 0) {
					targetVelocity.y = flyDirection.y * verticalFlySpeed;
				}

				// Use MoveTowards to respect speed limits
				var newVel = Vector3.MoveTowards(body.linearVelocity, targetVelocity, flyAcceleration * Time.fixedDeltaTime);
				if (newVel.magnitude > flySpeed && flyDirection.y == 0) {
					Vector3 horizontal = new Vector3(newVel.x, 0, newVel.z);
					if (horizontal.magnitude > flySpeed) {
						horizontal = horizontal.normalized * flySpeed;
						newVel     = new Vector3(horizontal.x, newVel.y, horizontal.z);
					}
				}

				body.linearVelocity = newVel;
			} else {
				// Ground movement - exactly
				if (move != Vector3.zero) {
					// Calculate current move speed with sprint modifier
					float currentMaxSpeed = maxMoveSpeed;
					if (isSprinting && !crouching) {
						currentMaxSpeed *= sprintMultiplier;
					}

					// Use MoveTowards to smoothly reach target velocity
					var newVel = Vector3.MoveTowards(body.linearVelocity, move * currentMaxSpeed, moveAcceleration * Time.fixedDeltaTime);

					// Ensure we don't exceed max speed
					if (newVel.magnitude > currentMaxSpeed) {
						newVel = newVel.normalized * currentMaxSpeed;
					}

					// Preserve vertical velocity
					newVel.y            = yVel;
					body.linearVelocity = newVel;
				}
			}

			// Apply drag
			UpdateDrag();
			
			// Handle turning
			if (Mathf.Abs(turningAxis) > 0.001f) {
				transform.Rotate(0, turningAxis * 90f * Time.fixedDeltaTime, 0);
			}
		}

		protected virtual void UpdateDrag() {
			// Apply drag
			if (moveDirection.magnitude <= movementDeadzone && isGrounded) {
				// Strong drag when grounded and not moving (like foot strength)
				body.linearVelocity *= (Mathf.Clamp01(1 - groundedDrag * Time.fixedDeltaTime));
			} else if (!useGrounding || isFlying) {
				// Flying drag when not using grounding or actually flying
				body.linearVelocity *= (Mathf.Clamp01(1 - flyingDrag * Time.fixedDeltaTime));
			}
			// No extra drag when moving on ground to maintain smooth movement
		}

		RaycastHit[] hitsNonAlloc = new RaycastHit[128];

		protected virtual void Ground() {
			isGrounded    = false;
			lastGroundHit = new RaycastHit();

			if (!tempDisableGrounding && useGrounding && !isFlying) {
				float highestPoint = -1;
				float scale        = transform.lossyScale.x > transform.lossyScale.z ? transform.lossyScale.x : transform.lossyScale.z;

				// Calculate points
				var point1 = scale * bodyCollider.center + transform.position + scale * bodyCollider.height / 2f * -Vector3.up + (maxStepHeight + scale * bodyCollider.radius * 2)         * Vector3.up;
				var point2 = scale * bodyCollider.center + transform.position + (scale * bodyCollider.height                                            / 2f + groundingPenetrationOffset) * -Vector3.up;

				// First pass with larger radius
				var radius   = scale * bodyCollider.radius * 2 + Physics.defaultContactOffset * 2;
				int hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);

				CheckGroundHits();

				if (!isGrounded && hitCount > 0) {
					// Second pass with smaller radius
					radius   = scale * bodyCollider.radius;
					hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);
					CheckGroundHits();
				}

				void CheckGroundHits() {
					for (int i = 0; i < hitCount; i++) {
						var hit = hitsNonAlloc[i];

						if (hit.collider != bodyCollider) {
							if (hit.point.y >= point2.y && hit.point.y <= point2.y + maxStepHeight + groundingPenetrationOffset) {
								float stepAngle = Vector3.Angle(hit.normal, Vector3.up);
								float dist      = hit.point.y - transform.position.y;

								if (stepAngle < maxStepAngle && dist > highestPoint) {
									isGrounded    = true;
									highestPoint  = dist;
									lastGroundHit = hit;
								}
							}
						}
					}
				}

				if (isGrounded) {
					// Zero out vertical velocity since we're grounded
					body.linearVelocity = new Vector3(body.linearVelocity.x, 0, body.linearVelocity.z);

					// Position the body to stick to ground
					body.position      = new Vector3(body.position.x, lastGroundHit.point.y, body.position.z);
					transform.position = body.position;
				} // Manage gravity

				body.useGravity = !isGrounded;
			}
		}

		protected virtual void CheckGroundWhileFlying() {
			// Always check ground detection, even while flying
			isGroundedWhileFlying = false;
			lastFlyingGroundHit   = new RaycastHit();

			if (!tempDisableGrounding && useGrounding) {
				float highestPoint = -1;
				float scale        = transform.lossyScale.x > transform.lossyScale.z ? transform.lossyScale.x : transform.lossyScale.z;

				// Calculate points
				var point1 = scale * bodyCollider.center + transform.position + scale * bodyCollider.height / 2f * -Vector3.up + (maxStepHeight + scale * bodyCollider.radius * 2)         * Vector3.up;
				var point2 = scale * bodyCollider.center + transform.position + (scale * bodyCollider.height                                            / 2f + groundingPenetrationOffset) * -Vector3.up;

				// Use a smaller radius for flying ground detection
				var radius   = scale * bodyCollider.radius;
				int hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);

				for (int i = 0; i < hitCount; i++) {
					var hit = hitsNonAlloc[i];

					if (hit.collider != bodyCollider) {
						if (hit.point.y >= point2.y && hit.point.y <= point2.y + maxStepHeight + groundingPenetrationOffset) {
							float stepAngle = Vector3.Angle(hit.normal, Vector3.up);
							float dist      = hit.point.y - transform.position.y;

							if (stepAngle < maxStepAngle && dist > highestPoint) {
								isGroundedWhileFlying = true;
								highestPoint          = dist;
								lastFlyingGroundHit   = hit;
							}
						}
					}
				}
			}
		}

		protected virtual void UpdatePlayerHeight() {
			if (crouching != lastCrouching) {
				lastCrouching = crouching;
			}

			if (autoAdjustColliderHeight) {
				float targetHeight = crouching ? crouchHeight : minMaxHeight.y;
				targetHeight = Mathf.Clamp(targetHeight, minMaxHeight.x, minMaxHeight.y);

				bodyCollider.height = Mathf.Lerp(bodyCollider.height, targetHeight, Time.deltaTime * heightSmoothSpeed);

				// Adjust center to keep feet on ground
				bodyCollider.center = new Vector3(0, bodyCollider.height * 0.5f + heightOffset, 0);
			}
		}

		public bool IsGrounded() {
			// Return true if grounded normally OR if flying and ground is detected
			return isGrounded || (isFlying && isGroundedWhileFlying);
		}

		public bool IsFlying() {
			return isFlying;
		}

		public bool MayFly() {
			return mayFly;
		}

		public bool IsSprinting() {
			return isSprinting;
		}

		public Vector3 GetMoveDirection() {
			return moveDirection;
		}

		public float GetCurrentMoveSpeed() {
			float speed = maxMoveSpeed;
			if (isSprinting && !crouching) {
				speed *= sprintMultiplier;
			}

			return speed;
		}

		public void SetMayFly(bool value) {
			mayFly = value;
			if (!mayFly && isFlying) {
				ToggleFlying();
			}
		}

		/// <summary>
		/// Force la position du Transform et du Rigidbody, en ignorant temporairement le grounding
		/// </summary>
		/// <param name="position">Nouvelle position à appliquer</param>
		/// <param name="disableGroundingDuration">Durée en secondes pour désactiver le grounding (par défaut 0.1s)</param>
		public void SetPosition(Vector3 position, float disableGroundingDuration = 0.1f) {
			// Désactiver temporairement le grounding pour éviter qu'il corrige la position
			DisableGrounding(disableGroundingDuration);
			
			// Appliquer la nouvelle position au Transform et au Rigidbody
			transform.position = position;
			body.position = position;
			
			// Réinitialiser la vitesse verticale pour éviter des comportements étranges
			if (body.useGravity && !isFlying) {
				body.linearVelocity = new Vector3(body.linearVelocity.x, 0, body.linearVelocity.z);
			}
		}
		
		public void DisableGrounding(float seconds) {
			if (_disableGroundingRoutine != null)
				StopCoroutine(_disableGroundingRoutine);
			_disableGroundingRoutine = StartCoroutine(DisableGroundingSecondsRoutine(seconds));
		}

		private Coroutine _disableGroundingRoutine;

		IEnumerator DisableGroundingSecondsRoutine(float seconds) {
			tempDisableGrounding = true;
			isGrounded           = false;
			yield return new WaitForSeconds(seconds);
			tempDisableGrounding = false;
		}
	}
}