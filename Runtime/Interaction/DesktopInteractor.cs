using System;
using System.Collections.Generic;
using Nox.CCK.Interaction;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Nox.Desktop.Runtime.Interaction {
	/// <summary>How a far object reaches the player once grabbed.</summary>
	/// <remarks>
	/// The values are ordered so the already-serialized ones stay stable (<see cref="Instant"/> is 0,
	/// <see cref="Smooth"/> is 1): a scene saved before <see cref="None"/> existed keeps its mode.
	/// </remarks>
	public enum GrabPullMode {
		/// <summary>Snaps in front of the player at once, like the default distance grab of AutoHand.</summary>
		Instant,

		/// <summary>Travels to the hold distance at <c>pullSpeed</c>.</summary>
		Smooth,

		/// <summary>
		/// Never brought to the hand: the object stays at the distance it was grabbed at and is carried
		/// along the ray, exactly where the view points. Only the mouse wheel changes that distance.
		/// </summary>
		None
	}

	/// <summary>
	/// Desktop near/far interaction: a ray from the view takes a shared grabbable and carries it at the
	/// distance it was grabbed at, the mouse wheel moving it nearer or farther.
	/// <para>
	/// It is the desktop counterpart of an XR near/far ray: it only knows
	/// <see cref="IGrabbable"/>, so the same prop is grabbed here and in XR. The
	/// object follows the <see cref="GrabPoint"/> of this component from its own <c>LateUpdate</c>;
	/// nothing is re-parented.
	/// </para>
	/// <para>
	/// It runs in <c>LateUpdate</c> at execution order 1050: after the avatar camera was placed (order 1000)
	/// and before the grabbable (order 1100) reads the grab point. The carried object is therefore drawn
	/// with the view of the current frame, never one frame behind it.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/Desktop/Interaction/Desktop Interactor")]
	[DefaultExecutionOrder(1050)]
	[DisallowMultipleComponent]
	public class DesktopInteractor : MonoBehaviour, IGrabber, IGrabberBody, IGrabberRenderPose {
		[Header("Ray")]
		[SerializeField, Tooltip("Camera the ray is cast from. Wired on the prefab; falls back to Camera.main when empty.")]
		private Camera view;

		[SerializeField, Tooltip("Layers the ray can grab.")]
		private LayerMask layers = ~0;

		[SerializeField, Min(0f), Tooltip("Longest distance the ray reaches, in metres. 0 = no limit: the grabbables decide with their own range.")]
		private float maxDistance;

		[SerializeField, Min(0.01f), Tooltip("Closest distance a grabbed object can be pulled to, in metres.")]
		private float minDistance = 0.4f;

		[SerializeField, Min(0f), Tooltip("Radius of the cast: 0 is a thin ray, above 0 makes a near grab more forgiving.")]
		private float castRadius = 0f;

		[Header("Input")]
		[SerializeField, Tooltip("Hold: the object is carried while the mouse button is pressed. Toggle: a click takes it, the next click releases it.")]
		private GrabInputMode inputMode = GrabInputMode.Hold;

		[SerializeField, Tooltip("Mouse wheel moves a carried object nearer or farther.")]
		private bool scrollAdjustDistance = true;

		[SerializeField, Min(0f), Tooltip("Metres the wheel moves a carried object per notch.")]
		private float scrollStep = 0.25f;

		[SerializeField, Tooltip("Do not start a grab while the pointer is over a UI element.")]
		private bool ignorePointerOverUi = true;

		[Header("Pull")]
		[SerializeField, Tooltip("Instant: the object lands in front of the player at once (AutoHand default). Smooth: it travels at pullSpeed. None: it stays at the distance it was grabbed at and is never brought to the hand.")]
		private GrabPullMode pullMode = GrabPullMode.Instant;

		[SerializeField, Min(0f), Tooltip("Distance in front of the view the grabbed object settles at, in metres.")]
		private float holdDistance = 0.6f;

		[SerializeField, Min(0f), Tooltip("Speed (metres per second) a Smooth pull travels at.")]
		private float pullSpeed = 12f;

		[Header("Body")]
		[SerializeField, Tooltip("Colliders of the player's own body, wired on the prefab (its capsule). A carried object ignores them, so a prop pulled in front of the view cannot collide with - or push - the player.")]
		private Collider[] bodyColliders = Array.Empty<Collider>();

		/// <summary>Transform a carried object follows: on the ray, at the distance it was grabbed at.</summary>
		private Transform _grabPoint;

		/// <summary>Object currently carried, or <c>null</c>.</summary>
		private Nox.CCK.Interaction.IGrabbable _held;

		/// <summary>Object the ray is currently over, or <c>null</c>.</summary>
		private Nox.CCK.Interaction.IGrabbable _hover;

		/// <summary>Hit of the ray under the pointer, reused by the grab of the same frame.</summary>
		private RaycastHit _hoverHit;

		/// <summary>Surface this pointer closed on, while it carries an object.</summary>
		private GrabContact _contact;
		private bool _hasHoverHit;

		/// <summary>Object currently carrying the hover outline, or <c>null</c>.</summary>
		private Component _highlighted;

		/// <summary>Distance of the carried object along the ray.</summary>
		private float _distance = 2f;

		/// <summary>Distance the carried object is pulled towards (the hold distance, or the last wheel value).</summary>
		private float _targetDistance = 2f;

		/// <summary>Whether the object being carried asked to stay at the distance it was grabbed at.</summary>
		private bool _keepsDistance;

		/// <summary>Camera the ray is cast from.</summary>
		public Camera View {
			get {
				if (!view)
					view = ResolveCamera();

				return view;
			}
			set => view = value;
		}

		/// <summary>Longest distance the ray reaches, in metres (0 = no limit). A grabbable may be nearer than that (see its own range).</summary>
		public float MaxDistance {
			get => maxDistance;
			set => maxDistance = Mathf.Max(0f, value);
		}

		/// <summary>Distance the ray and the carried object are allowed to use: no limit while <see cref="maxDistance"/> is 0.</summary>
		private float Reach
			=> maxDistance > 0f ? maxDistance : float.PositiveInfinity;

		/// <summary>Object currently carried, or <c>null</c>.</summary>
		public Nox.CCK.Interaction.IGrabbable Held
			=> _held;

		/// <summary>How a far object reaches the player once grabbed.</summary>
		public GrabPullMode PullMode {
			get => pullMode;
			set => pullMode = value;
		}

		/// <summary>
		/// Whether a grabbed object is brought in front of the player: every mode but
		/// <see cref="GrabPullMode.None"/>, which keeps it at the distance it was grabbed at.
		/// </summary>
		public bool PullsToPlayer
			=> pullMode != GrabPullMode.None;

		/// <summary>Whether an object is currently carried.</summary>
		public bool IsHolding
			=> _held != null;

		/// <summary>Object the ray is currently over, or <c>null</c>.</summary>
		public IGrabbable Hovered
			=> _hover;

		/// <summary>Pose a carried object is attached to while grabbed.</summary>
		public Transform GrabPoint
			=> _grabPoint;

		/// <summary>A pointer has no side: a grabbable cannot pick a grip authored for a hand through it.</summary>
		public Handedness Handedness
			=> Handedness.None;

		/// <summary>Surface the pointer hit when it took the object, or an invalid contact when it grabbed blind.</summary>
		public GrabContact Contact
			=> _contact;

		/// <summary>The pointer holds the object at its grab point: that is where the point it touched is put.</summary>
		public Vector3 HoldingPoint
			=> _grabPoint ? _grabPoint.position : transform.position;

		/// <summary>Contact of a grab from the point a ray closed on, with the collider of that point.</summary>
		private static GrabContact ContactAt(Nox.CCK.Interaction.IGrabbable grabbable, Vector3 point, Vector3 normal)
			=> new() {
				Collider = ColliderOf(grabbable, point),
				Point = point,
				Normal = normal
			};

		/// <summary>Collider of the object around a point, or <c>null</c> when it has none there.</summary>
		private static Collider ColliderOf(Nox.CCK.Interaction.IGrabbable grabbable, Vector3 point) {
			var colliders = grabbable?.GrabColliders;
			if (colliders == null)
				return null;

			var best = float.MaxValue;
			var found = default(Collider);

			for (var i = 0; i < colliders.Count; i++) {
				var collider = colliders[i];
				if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy)
					continue;

				if (collider is MeshCollider mesh && !mesh.convex)
					continue;

				var distance = (collider.ClosestPoint(point) - point).sqrMagnitude;
				if (distance >= best)
					continue;

				best = distance;
				found = collider;
			}

			return found;
		}

		/// <summary>
		/// The ray follows the camera, which the avatar only places on its eyes during the render pass: the carried
		/// object is posed once per frame, once that pose is known (see <see cref="OnViewPlaced"/>).
		/// </summary>
		public bool PosesAtRender
			=> _owner && _owner.HasViewSync;

		/// <summary>Desktop player owning this interactor, when it was created by one.</summary>
		private DesktopPlayer _owner;

		/// <summary>Binds the desktop player that owns this interactor, so it follows the body and the view it drives.</summary>
		public void Bind(DesktopPlayer owner) {
			if (_owner && _owner != owner)
				_owner.ViewPlaced -= OnViewPlaced;

			_owner = owner;

			if (_owner && isActiveAndEnabled) {
				_owner.ViewPlaced -= OnViewPlaced;
				_owner.ViewPlaced += OnViewPlaced;
			}
		}

		/// <summary>
		/// Colliders of the desktop body: the ones of the owning player, or the ones serialized on the prefab.
		/// A prop pulled in front of the view ignores them and cannot collide with - or push - the player.
		/// </summary>
		public IReadOnlyList<Collider> BodyColliders
			=> _owner
				? _owner.BodyColliders
				: bodyColliders;

		private Camera ResolveCamera() {
			if (!view)
				view = Camera.main;

			return view;
		}

		private void Awake() {
			var node = new GameObject($"{name} Grab Point");
			node.transform.SetParent(transform, false);
			_grabPoint = node.transform;
			_grabPoint.gameObject.hideFlags = HideFlags.DontSave;
		}

		private void OnDisable() {
			if (_owner)
				_owner.ViewPlaced -= OnViewPlaced;

			ClearHighlight();
			Release();
		}

		private void OnEnable() {
			if (_owner) {
				_owner.ViewPlaced -= OnViewPlaced;
				_owner.ViewPlaced += OnViewPlaced;
			}
		}

		/// <summary>
		/// Places the grab point with the pose the frame is drawn from, then lets the carried object follow it. The
		/// camera of the desktop player is the head part of its rig, and the avatar puts it on its eyes after
		/// <c>LateUpdate</c>: aiming with the pose of <c>LateUpdate</c> leaves the carried object beside the cursor
		/// (and far below it once the view is pitched up).
		/// </summary>
		private void OnViewPlaced() {
			if (_held == null)
				return;

			var view = View;
			if (!view)
				return;

			PlaceGrabPoint(view);

			if (_held is IGrabFollower follower)
				follower.FollowGrabPose();
		}

		private void LateUpdate() {
			var mouse = Mouse.current;
			if (mouse == null)
				return;

			var camera = View;
			if (!camera)
				return;

			// A menu is open, or the window lost the focus: no hover, no grab and no wheel. What is carried stays
			// where it is (the view cannot move either), and its motion is measured again when the input comes back.
			if (DesktopInput.Blocked) {
				ClearHighlight();
				return;
			}

			var pressedThisFrame = mouse.leftButton.wasPressedThisFrame;
			var releasedThisFrame = mouse.leftButton.wasReleasedThisFrame;

			if (_held != null) {
				var heldMode = ModeOf(_held);

				if (heldMode == GrabInputMode.Hold && releasedThisFrame)
					Release();
				else if (heldMode == GrabInputMode.Toggle && pressedThisFrame)
					Release();
				else
					UpdateGrabPoint(camera, mouse);

				return;
			}

			Hover(camera);
			UpdateHighlight();

			// Hold: a held button takes whatever it is over, so aiming after pressing still grabs.
			// Toggle: only the press edge starts a grab, the next press releases it.
			var wantsGrab = ModeOf(_hover) == GrabInputMode.Toggle
				? pressedThisFrame
				: mouse.leftButton.isPressed;

			if (!wantsGrab)
				return;

			// With a locked cursor the pointer position is not meaningful for the UI: only guard against
			// the menu (unlocked cursor) swallowing the click.
			if (ignorePointerOverUi
				&& Cursor.lockState != CursorLockMode.Locked
				&& EventSystem.current
				&& EventSystem.current.IsPointerOverGameObject())
				return;

			TryGrab(camera);
		}

		/// <summary>
		/// How a grab of this object starts and stops: the object may decide for itself
		/// (see <see cref="IGrabInputMode"/>), otherwise the interactor uses its own input mode.
		/// </summary>
		private GrabInputMode ModeOf(Nox.CCK.Interaction.IGrabbable target)
			=> target is IGrabInputMode state
				? state.InputMode
				: inputMode;

		/// <summary>Keeps the hover outline in sync with the object under the ray.</summary>
		private void UpdateHighlight() {
			var target = _hover as Component;
			if (ReferenceEquals(target, _highlighted))
				return;

			if (_highlighted)
				GrabbableHighlight.Set(_highlighted, false);

			_highlighted = target;

			if (_highlighted)
				GrabbableHighlight.Set(_highlighted, true);
		}

		/// <summary>Removes the hover outline, if any.</summary>
		private void ClearHighlight() {
			if (_highlighted)
				GrabbableHighlight.Set(_highlighted, false);

			_highlighted = null;
		}

		/// <summary>Casts the ray from the view and remembers what it is over.</summary>
		private void Hover(Camera camera) {
			_hover = null;

			var ray = new Ray(camera.transform.position, camera.transform.forward);
			_hasHoverHit = Cast(ray, out _hoverHit);

			if (!_hasHoverHit)
				return;

			var candidate = _hoverHit.collider.GetComponentInParent<Nox.CCK.Interaction.IGrabbable>();
			if (candidate == null || !candidate.CanGrab(this))
				return;

			_hover = candidate;
		}

		/// <summary>Starts carrying the hovered object, then brings it in front of the player (see the pull mode).</summary>
		private void TryGrab(Camera camera) {
			var target = _hover;

			// The hover of this frame already asked the object whether it can be taken, and cast the ray.
			if (target == null || !_hasHoverHit)
				return;

			// The grab point is taken on the pivot of the object: it then rides the view exactly where the
			// cursor points, instead of hanging off the surface that was hit.
			var pivot = target.Target;
			var origin = pivot
				? pivot.position
				: _hoverHit.point;
			var distance = pivot
				? Vector3.Distance(camera.transform.position, origin)
				: _hoverHit.distance;

			_distance = Mathf.Clamp(distance, minDistance, Reach);

			// A panel (a menu) is not a prop: it stays across the room and follows the pointer, so the pull
			// does not decide where it lands and the wheel is the only thing moving it.
			_keepsDistance = target is IGrabAtDistance keep && keep.KeepsGrabDistance;

			// Bring a far object in front of the player, but never push away one that is already close.
			_targetDistance = _keepsDistance || !PullsToPlayer
				? _distance
				: Mathf.Clamp(Mathf.Min(distance, holdDistance), minDistance, Reach);

			// AutoHand grabs a distance object directly in the hand: the same happens here on the next
			// LateUpdate, the object jumping to the hold distance instead of travelling to it.
			if (!_keepsDistance && pullMode == GrabPullMode.Instant)
				_distance = _targetDistance;

			// The grab point lands on the object and keeps the view orientation: the object stays where
			// it is when Free mode captures its pose, instead of jumping to the camera.
			_grabPoint.SetPositionAndRotation(origin, camera.transform.rotation);

			// Where the pointer closed on the object: a prop can pose a hand on that point of its surface.
			_contact = ContactAt(target, _hoverHit.point, _hoverHit.normal);

			_held = target;
			target.OnGrab(this);
		}

		/// <summary>Moves the carried object along the ray (wheel changes the distance).</summary>
		private void UpdateGrabPoint(Camera camera, Mouse mouse) {
			if (scrollAdjustDistance) {
				var scroll = mouse.scroll.ReadValue().y;
				if (Mathf.Abs(scroll) > 0.01f) {
					_targetDistance = Mathf.Clamp(_targetDistance + Mathf.Sign(scroll) * scrollStep, minDistance, Reach);

					// The wheel moves the object right away: only the pull of a grab travels towards the hold
					// distance, and it must not swallow the distance the player just asked for.
					_distance = _targetDistance;
				}
			}

			if (!_keepsDistance && pullMode == GrabPullMode.Smooth)
				_distance = Mathf.MoveTowards(_distance, _targetDistance, pullSpeed * Time.deltaTime);

			PlaceGrabPoint(camera);
		}

		/// <summary>Puts the grab point on the ray of the view, at the current distance.</summary>
		private void PlaceGrabPoint(Camera camera) {
			var ray = new Ray(camera.transform.position, camera.transform.forward);

			_grabPoint.SetPositionAndRotation(ray.GetPoint(_distance), camera.transform.rotation);
		}

		/// <summary>Stops carrying the object, leaving it where it is.</summary>
		public void Release() {
			var target = _held;
			if (target == null)
				return;

			_held = null;
			_contact = default;

			// The throw belongs to the grabbable: it measures the motion of the grab point by itself
			// (see GrabbableObject). The pull that brings a far object in front of the player is not a throw,
			// so the motion is cancelled while it is still travelling towards the view.
			var pulling = !_keepsDistance && PullsToPlayer && Mathf.Abs(_distance - _targetDistance) > 0.001f;

			_keepsDistance = false;
			_distance = minDistance;
			_targetDistance = minDistance;
			target.OnRelease(this);

			if (!pulling)
				return;

			var body = target.Body;
			if (!body || body.isKinematic)
				return;

			body.linearVelocity = Vector3.zero;
			body.angularVelocity = Vector3.zero;
		}

		/// <summary>Ray or sphere cast, depending on <see cref="castRadius"/>.</summary>
		private bool Cast(Ray ray, out RaycastHit hit) {
			var origin = ray.origin;
			var direction = ray.direction;

			return castRadius > 0f
				? Physics.SphereCast(origin, castRadius, direction, out hit, Reach, layers, QueryTriggerInteraction.Ignore)
				: Physics.Raycast(origin, direction, out hit, Reach, layers, QueryTriggerInteraction.Ignore);
		}
	}
}
