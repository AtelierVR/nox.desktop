using System;
using System.Collections;
using System.Collections.Generic;
using Nox.CCK.Utils;
using Nox.Desktop.Runtime.Interaction;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;

namespace Nox.Desktop.Runtime {
	[RequireComponent(typeof(Rigidbody)), RequireComponent(typeof(CapsuleCollider))]
	public class DesktopPlayer : MonoBehaviour {
		[Header("Desktop Player")]
		public Camera headCamera;

		public Transform forwardFollow;
		public CapsuleCollider bodyCollider;

		[Header("Interaction")]
		[SerializeField, Min(0f), Tooltip("Longest distance the grab ray reaches, in metres. 0 = no limit: each grabbable decides with its own range.")]
		private float maxGrabDistance;

		[Header("Interaction")]
		[SerializeField, Tooltip("Use the nox.interactions grabber (DesktopGrabber) instead of the legacy DesktopInteractor.")]
		private bool useInteractionGrabber = true;

		[Header("Body colliders")]
		[SerializeField, Tooltip("Extra colliders of the player's body (head, hands, avatar parts). bodyCollider is always added. A carried object ignores them all, so it can be brought into the body without colliding with - or pushing - it.")]
		private Collider[] extraBodyColliders = System.Array.Empty<Collider>();

		private readonly List<Collider> _bodyColliders = new();

		/// <summary>Colliders of the player's own body, handed to the interactor so a carried object ignores them.</summary>
		public IReadOnlyList<Collider> BodyColliders
			=> _bodyColliders;

		/// <summary>Near/far interaction ray of the desktop view (grabs the shared grabbables).</summary>
		public DesktopInteractor Interactor { get; private set; }

		/// <summary>Grabber of the nox.interactions system, when <c>useInteractionGrabber</c> is on.</summary>
		public DesktopGrabber Grabber { get; private set; }

		/// <summary>Whether the view currently carries an object, whichever grabber runs.</summary>
		public bool IsHolding
			=> Interactor
				? Interactor.IsHolding
				: Grabber != null && Grabber.IsHolding;

		/// <summary>Whether the view currently points at something it can take.</summary>
		public bool HasHover
			=> Interactor
			? Interactor.Hovered != null
			: Grabber != null && Grabber.Hovered != null;

		/// <summary>
		/// Raised once per frame, when the view camera has been placed where the frame is drawn from: the avatar
		/// moves it onto its eyes after <c>LateUpdate</c>, so anything aiming with the camera listens to this
		/// instead of walking the render pipeline.
		/// </summary>
		internal event Action ViewPlaced;

		/// <summary>Whether a connector places the view camera at render time (see <see cref="ViewPlaced"/>).</summary>
		internal bool HasViewSync { get; set; }

		/// <summary>Tells the listeners that the pose of the view camera is final for this frame.</summary>
		internal void NotifyViewPlaced()
			=> ViewPlaced?.Invoke();

		[Header("Movement")]
		public bool useMovement = true;

		public float maxMoveSpeed = 2.3f;
		public float moveAcceleration = 100000f;
		public float jumpForce = 5f;
		public float sprintMultiplier = 1.5f;
		public float airControl = 0.3f;
		public float movementDeadzone = 0.2f;

		[Header("Height")]
		public float heightOffset = 0f;

		public bool crouching = false;
		public float crouchHeight = 0.6f;
		public float heightSmoothSpeed = 10f;
		public bool autoAdjustColliderHeight = true;
		public Vector2 minMaxHeight = new(0.5f, 2.5f);

		[Header("Grounding")]
		public bool useGrounding = true;

		public float maxStepHeight = 0.3f;
		public float groundingPenetrationOffset = 0.1f;
		public float maxStepAngle = 45f;
		public LayerMask groundLayerMask = -1;
		public float groundedDrag = 10000f;
		public float flyingDrag = 4f;

		[Header("Flying")]
		public bool mayFly = false;

		public float flySpeed = 5f;
		public float flyAcceleration = 20f;
		public float verticalFlySpeed = 3f; // Private fields
		public Rigidbody body;
		private Vector3 _moveDirection;
		private Vector3 _flyDirection;
		private float _turningAxis;
		private bool _isGrounded = false;
		private bool _isFlying = false;
		private bool _isSprinting = false;
		private bool _lastCrouching;
		private RaycastHit _lastGroundHit;
		private bool _tempDisableGrounding = false;
		private bool _isGroundedWhileFlying = false;
		private RaycastHit _lastFlyingGroundHit;

		public virtual void Awake() {
			body = GetComponent<Rigidbody>();
			if (body.collisionDetectionMode == CollisionDetectionMode.Discrete)
				body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
			if (forwardFollow == null)
				forwardFollow = headCamera.transform;

			bodyCollider = GetComponent<CapsuleCollider>();
			RefreshBodyColliders();

			if (useInteractionGrabber) {
				Grabber = gameObject.GetOrAddComponent<DesktopGrabber>();
				Grabber.View = headCamera;
				Grabber.MaxDistance = maxGrabDistance;
				Grabber.Bind(this);
			}
			else {
				Interactor = gameObject.GetOrAddComponent<DesktopInteractor>();
				Interactor.View = headCamera;
				Interactor.MaxDistance = maxGrabDistance;
				Interactor.Bind(this);
			}
		}

		/// <summary>
		/// Collects the colliders of the body from the prefab fields, without walking the hierarchy. Call it
		/// again when the body gains colliders at runtime (a loaded avatar, a worn prop).
		/// </summary>
		public void RefreshBodyColliders() {
			_bodyColliders.Clear();

			if (bodyCollider)
				_bodyColliders.Add(bodyCollider);

			for (var i = 0; i < extraBodyColliders.Length; i++) {
				var collider = extraBodyColliders[i];

				if (collider && !_bodyColliders.Contains(collider))
					_bodyColliders.Add(collider);
			}
		}

		public virtual void Start() {
			if (headCamera == null) {
				Logger.LogError("DesktopPlayer: headCamera is not assigned!");
				return;
			}

			if (Interactor)
				Interactor.View = headCamera;

			if (Grabber)
				Grabber.View = headCamera;
		}

		/// <summary>Sets move direction for this fixedupdate</summary>
		public virtual void Move(Vector2 axis, bool useDeadzone = true, bool useRelativeDirection = true) {
			if (!useMovement)
				return;

			if (useDeadzone && axis.magnitude < movementDeadzone) {
				_moveDirection = Vector3.zero;
				return;
			}

			var forward = useRelativeDirection
				? forwardFollow.forward
				: Vector3.forward;
			var right = useRelativeDirection
				? forwardFollow.right
				: Vector3.right;

			forward.y = 0;
			right.y = 0;
			forward.Normalize();
			right.Normalize();

			_moveDirection = (forward * axis.y + right * axis.x).normalized;
		}

		/// <summary>Sets sprint state</summary>
		public virtual void SetSprinting(bool sprinting) {
			_isSprinting = sprinting;
		}

		/// <summary>Sets turning axis for smooth turning</summary>
		public virtual void Turn(float axis) {
			if (!useMovement)
				return;
			_turningAxis = axis;
		}

		public virtual void Jump() {
			if (!_isGrounded)
				return;
			DisableGrounding(0.1f);
			body.useGravity = true;
			body.AddForce(Vector3.up * jumpForce, ForceMode.VelocityChange);
		}

		public virtual void ToggleFlying() {
			if (!mayFly)
				return;
			_isFlying = !_isFlying;
			if (_isFlying)
				body.useGravity = false;

			_flyDirection = Vector3.zero;
		}

		/// <summary>Sets fly direction including vertical movement</summary>
		public virtual void Fly(Vector3 direction) {
			if (!_isFlying || !mayFly)
				return;
			_flyDirection = direction;
		}

		public virtual void SetCrouching(bool crouch)
			=> crouching = crouch;

		protected virtual void FixedUpdate() {
			if (!useMovement)
				return;
			UpdateRigidbody();
			Ground();

			if (_isFlying)
				CheckGroundWhileFlying();

			UpdatePlayerHeight();
		}

		protected virtual void UpdateRigidbody() {
			var move = _moveDirection;
			var yVel = body.linearVelocity.y;

			if (_isFlying) {
				var targetVelocity = move * flySpeed;

				if (_flyDirection.y != 0)
					targetVelocity.y = _flyDirection.y * verticalFlySpeed;

				var newVel = Vector3.MoveTowards(body.linearVelocity, targetVelocity, flyAcceleration * Time.fixedDeltaTime);
				if (newVel.magnitude > flySpeed && _flyDirection.y == 0) {
					var horizontal = new Vector3(newVel.x, 0, newVel.z);
					if (horizontal.magnitude > flySpeed) {
						horizontal = horizontal.normalized * flySpeed;
						newVel = new Vector3(horizontal.x, newVel.y, horizontal.z);
					}
				}

				body.linearVelocity = newVel;
			} else if (move != Vector3.zero) {
				var currentMaxSpeed = maxMoveSpeed;
				if (_isSprinting && !crouching)
					currentMaxSpeed *= sprintMultiplier;

				var newVel = Vector3.MoveTowards(body.linearVelocity, move * currentMaxSpeed, moveAcceleration * Time.fixedDeltaTime);

				if (newVel.magnitude > currentMaxSpeed)
					newVel = newVel.normalized * currentMaxSpeed;

				newVel.y = yVel;
				body.linearVelocity = newVel;
			}

			UpdateDrag();

			if (Mathf.Abs(_turningAxis) > 0.001f)
				transform.Rotate(0, _turningAxis * 90f * Time.fixedDeltaTime, 0);
		}

		protected virtual void UpdateDrag() {
			if (_moveDirection.magnitude <= movementDeadzone && _isGrounded) {
				body.linearVelocity *= Mathf.Clamp01(1 - groundedDrag * Time.fixedDeltaTime);
			} else if (!useGrounding || _isFlying)
				body.linearVelocity *= Mathf.Clamp01(1 - flyingDrag * Time.fixedDeltaTime);
		}

		readonly RaycastHit[] hitsNonAlloc = new RaycastHit[128];

		protected virtual void Ground() {
			_isGrounded = false;
			_lastGroundHit = new RaycastHit();

			if (!_tempDisableGrounding && useGrounding && !_isFlying) {
				float highestPoint = -1;
				var scale = transform.lossyScale.x > transform.lossyScale.z ? transform.lossyScale.x : transform.lossyScale.z;

				var point1 = scale * bodyCollider.center
					+ transform.position
					+ scale * bodyCollider.height / 2f * -Vector3.up
					+ (maxStepHeight + scale * bodyCollider.radius * 2) * Vector3.up;
				var point2 = scale * bodyCollider.center
					+ transform.position
					+ (scale * bodyCollider.height / 2f + groundingPenetrationOffset) * -Vector3.up;

				var radius = scale * bodyCollider.radius * 2 + Physics.defaultContactOffset * 2;
				var hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);

				CheckGroundHits();

				if (!_isGrounded && hitCount > 0) {
					radius = scale * bodyCollider.radius;
					hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);
					CheckGroundHits();
				}

				void CheckGroundHits() {
					for (var i = 0; i < hitCount; i++) {
						var hit = hitsNonAlloc[i];

						if (hit.collider != bodyCollider && hit.collider.attachedRigidbody != body)
							if (hit.point.y >= point2.y && hit.point.y <= point2.y + maxStepHeight + groundingPenetrationOffset) {
								var stepAngle = Vector3.Angle(hit.normal, Vector3.up);
								var dist = hit.point.y - transform.position.y;

								if (stepAngle < maxStepAngle && dist > highestPoint) {
									_isGrounded = true;
									highestPoint = dist;
									_lastGroundHit = hit;
								}
							}
					}
				}

				if (_isGrounded) {
					body.linearVelocity = new Vector3(body.linearVelocity.x, 0, body.linearVelocity.z);

					body.position = new Vector3(body.position.x, _lastGroundHit.point.y, body.position.z);
					transform.position = body.position;
				}

				body.useGravity = !_isGrounded;
			}
		}

		protected virtual void CheckGroundWhileFlying() {
			_isGroundedWhileFlying = false;
			_lastFlyingGroundHit = new RaycastHit();

			if (!_tempDisableGrounding && useGrounding) {
				float highestPoint = -1;
				var scale = transform.lossyScale.x > transform.lossyScale.z
					? transform.lossyScale.x
					: transform.lossyScale.z;

				var point1 = scale * bodyCollider.center
					+ transform.position
					+ scale * bodyCollider.height / 2f * -Vector3.up
					+ (maxStepHeight + scale * bodyCollider.radius * 2) * Vector3.up;
				var point2 = scale * bodyCollider.center
					+ transform.position
					+ (scale * bodyCollider.height / 2f + groundingPenetrationOffset) * -Vector3.up;

				var radius = scale * bodyCollider.radius;
				var hitCount = Physics.SphereCastNonAlloc(point1, radius, -Vector3.up, hitsNonAlloc, Vector3.Distance(point1, point2) + scale * bodyCollider.radius * 4, groundLayerMask, QueryTriggerInteraction.Ignore);

				for (var i = 0; i < hitCount; i++) {
					var hit = hitsNonAlloc[i];

					if (hit.collider != bodyCollider && hit.collider.attachedRigidbody != body)
						if (hit.point.y >= point2.y && hit.point.y <= point2.y + maxStepHeight + groundingPenetrationOffset) {
							var stepAngle = Vector3.Angle(hit.normal, Vector3.up);
							var dist = hit.point.y - transform.position.y;

							if (stepAngle < maxStepAngle && dist > highestPoint) {
								_isGroundedWhileFlying = true;
								highestPoint = dist;
								_lastFlyingGroundHit = hit;
							}
						}
				}
			}
		}

		protected virtual void UpdatePlayerHeight() {
			if (crouching != _lastCrouching)
				_lastCrouching = crouching;

			if (autoAdjustColliderHeight) {
				var targetHeight = crouching
					? crouchHeight
					: minMaxHeight.y;
				targetHeight = Mathf.Clamp(targetHeight, minMaxHeight.x, minMaxHeight.y);

				bodyCollider.height = Mathf.Lerp(bodyCollider.height, targetHeight, Time.deltaTime * heightSmoothSpeed);

				bodyCollider.center = new Vector3(0, bodyCollider.height * 0.5f + heightOffset, 0);
			}
		}

		public bool IsGrounded()
			=> _isGrounded || (_isFlying && _isGroundedWhileFlying);

		public bool IsFlying()
			=> _isFlying;

		public bool MayFly()
			=> mayFly;

		public bool IsSprinting()
			=> _isSprinting;

		public Vector3 GetMoveDirection()
			=> _moveDirection;

		public float GetCurrentMoveSpeed() {
			var speed = maxMoveSpeed;
			if (_isSprinting && !crouching)
				speed *= sprintMultiplier;
			return speed;
		}

		public void SetMayFly(bool value) {
			mayFly = value;
			if (!mayFly && _isFlying)
				ToggleFlying();
		}

		/// <summary>
		/// Force la position du Transform et du Rigidbody, en ignorant temporairement le grounding
		/// </summary>
		/// <param name="position">Nouvelle position à appliquer</param>
		/// <param name="disableGroundingDuration">Durée en secondes pour désactiver le grounding (par défaut 0.1s)</param>
		public void SetPosition(Vector3 position, float disableGroundingDuration = 0.1f) {
			DisableGrounding(disableGroundingDuration);

			transform.position = position;
			body.position = position;

			if (body.useGravity && !_isFlying)
				body.linearVelocity = new Vector3(body.linearVelocity.x, 0, body.linearVelocity.z);
		}

		public void DisableGrounding(float seconds) {
			if (_disableGroundingRoutine != null)
				StopCoroutine(_disableGroundingRoutine);
			_disableGroundingRoutine = StartCoroutine(DisableGroundingSecondsRoutine(seconds));
		}

		private Coroutine _disableGroundingRoutine;

		IEnumerator DisableGroundingSecondsRoutine(float seconds) {
			_tempDisableGrounding = true;
			_isGrounded = false;
			yield return new WaitForSeconds(seconds);
			_tempDisableGrounding = false;
		}

		public float Height
			=> bodyCollider.height * transform.lossyScale.y;
	}
}
