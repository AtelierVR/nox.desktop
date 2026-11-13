using System;
using System.Collections.Generic;
using Nox.CCK.Interactions;
using Nox.Interactions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Nox.Desktop.Runtime.Interaction {
	[AddComponentMenu("Nox/Desktop/Interaction/Desktop Grabber")]
	[DefaultExecutionOrder(2000)]
	[DisallowMultipleComponent]
	public class DesktopGrabber : GrabberBase, IGrabberBody {
		[Header("Ray")]
		[SerializeField] private DesktopRay ray = DesktopRay.Default;

		[Header("Input")]
		[SerializeField] private DesktopGrabInput input = DesktopGrabInput.Default;

		[Header("Body")]
		[SerializeField] private Collider[] bodyColliders = Array.Empty<Collider>();

		private DesktopPlayer _owner;
		private Transform _rayPoint;
		private IGrabbable _hovered;
		private RaycastHit _hoverHit;
		private float _distance = 2f;
		private int _tickedFrame = -1;
		private int _viewFrame = -1;
		private float _pendingScroll;
		private float _holdMinDistance;

		public Camera View {
			get {
				if (!ray.view)
					ray.view = Camera.main;

				return ray.view;
			}
			set => ray.view = value;
		}

		public DesktopGrabInput Input {
			get => input;
			set => input = value;
		}

		public float MaxDistance {
			get => ray.max;
			set => ray.max = Mathf.Max(0f, value);
		}

		public float MinDistance {
			get => ray.min;
			set => ray.min = Mathf.Max(0f, value);
		}

		public float HeadClearance {
			get => ray.clearance;
			set => ray.clearance = Mathf.Max(0f, value);
		}

		public float Distance {
			get => _distance;
			set => _distance = Mathf.Max(ray.min, value);
		}

		public IGrabbable Hovered => _hovered;

		public bool IsHolding => HeldGrabbable != null;

		public IReadOnlyList<Collider> BodyColliders => _owner ? _owner.BodyColliders : bodyColliders;

		public void Bind(DesktopPlayer owner) {
			if (_owner != null)
				_owner.ViewPlaced -= OnViewPlaced;

			_owner = owner;

			if (_owner != null && isActiveAndEnabled)
				_owner.ViewPlaced += OnViewPlaced;
		}

		protected override void Awake() {
			base.Awake();

			SetKind(GrabKind.Indirect);

			TargetLayers = ray.layers;

			var node = new GameObject($"{name} Grab Point");
			node.transform.SetParent(transform, false);
			node.hideFlags = HideFlags.DontSave;

			_rayPoint = node.transform;
			SetGrabPoint(_rayPoint);
		}

		private void OnEnable() {
			if (_owner != null)
				_owner.ViewPlaced += OnViewPlaced;
		}

		private void OnDisable() {
			if (_owner != null)
				_owner.ViewPlaced -= OnViewPlaced;
		}

		private void OnDestroy() {
			if (_owner != null)
				_owner.ViewPlaced -= OnViewPlaced;

			if (_rayPoint)
				Destroy(_rayPoint.gameObject);
		}

		private void LateUpdate() {
			if (Time.frameCount - _viewFrame <= 1)
				return;

			Tick();
		}

		private void Update() {
			var mouse = Mouse.current;
			if (mouse == null) return;

			var raw = mouse.scroll.ReadValue().y;
			if (Mathf.Approximately(raw, 0f)) return;

			_pendingScroll += Mathf.Abs(raw) >= 10f ? raw / 120f : raw;
		}

		private void OnViewPlaced() {
			_viewFrame = Time.frameCount;
			Tick();
		}

		private void Tick() {
			if (_tickedFrame == Time.frameCount)
				return;

			_tickedFrame = Time.frameCount;

			if (DesktopInput.Blocked)
				return;

			var mouse = Mouse.current;
			var pressed = mouse != null && mouse.leftButton.wasPressedThisFrame;
			var released = mouse != null && mouse.leftButton.wasReleasedThisFrame;

			if (HeldGrabbable != null) {
				var release = input.mode == GrabInputMode.Hold ? released : pressed;

				if (release) {
					TryReleaseHeld(InteractionConstants.DefaultThrowMultiplier);
					ResetScroll();
					return;
				}

				Scroll();
				PlaceRayPoint();
				Follow();
				return;
			}

			_pendingScroll = 0f;

			Hover();
			PlaceRayPoint();

			var wantsGrab = input.mode == GrabInputMode.Toggle
				? pressed
				: mouse != null && mouse.leftButton.isPressed;

			if (!wantsGrab) return;

			if (input.uiGate && Cursor.lockState != CursorLockMode.Locked && PointerOverUi()) return;

			GrabHovered();
		}

		public override bool TryFindTarget(out IGrabbable grabbable, out RaycastHit contact) {
			grabbable = null;

			var origin = View ? View.transform : transform;
			var reach = ray.max > 0f ? ray.max : 1000f;

			if (!Physics.Raycast(origin.position, origin.forward, out contact, reach, TargetLayers, QueryTriggerInteraction.Ignore))
				return false;

			grabbable = contact.collider.GetComponentInParent<IGrabbable>();
			return grabbable != null && grabbable.CanBeGrabbedBy(this);
		}

		private void Hover() {
			if (!TryFindTarget(out var grabbable, out var hit)) {
				if (CurrentTarget != null)
					ClearTarget(CurrentTarget);

				_hovered = null;
				return;
			}

			_hovered = grabbable;
			_hoverHit = hit;

			SetTarget(grabbable, hit);
		}

		private void GrabHovered() {
			if (_hovered == null) return;

			_distance = GrabDistance(_hovered, View ? View.transform : transform, _hoverHit.distance);
			_holdMinDistance = ray.min;
			PlaceRayPoint();

			if (!TryGrab(_hovered, _hoverHit, input.duration)) return;

			Follow();
			RefreshHoldDistance();
		}

		private void Scroll() {
			var notches = _pendingScroll;
			_pendingScroll = 0f;

			if (!input.scroll || Mathf.Approximately(notches, 0f)) return;
			if (HeldGrabbable is not IGrabHold held || held.Hold.fixedDistance) return;

			var min = Mathf.Max(ray.min, _holdMinDistance);
			var distance = _distance + Mathf.Clamp(notches, -3f, 3f) * input.step;

			if (ray.max > 0f)
				distance = Mathf.Min(distance, Mathf.Max(min, ray.max));

			distance = Mathf.Max(min, distance);

			if (Mathf.Approximately(distance, _distance)) return;

			_distance = distance;

			if (HeldGrabbable is GrabbableBase grabbable)
				grabbable.Motion.Clear();
		}

		private void ResetScroll() {
			_pendingScroll = 0f;
			_holdMinDistance = ray.min;
			_distance = ray.min;
		}

		private void PlaceRayPoint() {
			if (!_rayPoint) return;

			var origin = View ? View.transform : transform;
			var distance = Mathf.Max(_distance, _holdMinDistance);

			_rayPoint.SetPositionAndRotation(origin.position + origin.forward * distance, origin.rotation);
		}

		private void RefreshHoldDistance() {
			_holdMinDistance = HeldGrabbable != null && ray.clearance > 0f
				? Mathf.Max(ray.min, HoldClearance(HeldGrabbable, View ? View.transform : transform))
				: ray.min;
		}

		private float HoldClearance(IGrabbable grabbable, Transform origin) {
			if (ray.clearance <= 0f || !_rayPoint)
				return ray.min;

			if (!TryGetBounds(grabbable, out var bounds))
				return Mathf.Max(ray.min, ray.clearance);

			var forward = origin.forward;
			var halfExtent = Mathf.Abs(bounds.extents.x * forward.x)
				+ Mathf.Abs(bounds.extents.y * forward.y)
				+ Mathf.Abs(bounds.extents.z * forward.z);
			var offset = Vector3.Dot(bounds.center - _rayPoint.position, forward);

			return Mathf.Max(ray.min, ray.clearance + halfExtent - offset);
		}

		private void Follow() {
			if (HeldGrabbable is GrabbableBase grabbable)
				grabbable.FollowHeld(Time.deltaTime);
		}

		private float GrabDistance(IGrabbable grabbable, Transform origin, float hitDistance) {
			var distance = MinHeadDistance(grabbable, origin, hitDistance);

			if (grabbable is not IGrabHold held || !held.Hold.atHoldDistance)
				return distance;

			var target = held.Hold.distance > 0f
				? held.Hold.distance
				: ray.min;

			return Mathf.Min(distance, Mathf.Max(ray.min, target));
		}

		private float MinHeadDistance(IGrabbable grabbable, Transform origin, float hitDistance) {
			if (grabbable is not GrabbableBase concrete || (concrete.Hold.snap & GrabSnap.Position) == 0)
				return Mathf.Max(ray.min, hitDistance);

			if (!TryGetBounds(grabbable, out var bounds))
				return Mathf.Max(ray.min, hitDistance);

			return Mathf.Max(ray.min, Vector3.Dot(bounds.center - origin.position, origin.forward));
		}

		private static bool TryGetBounds(IGrabbable grabbable, out Bounds bounds) {
			bounds = default;
			var found = false;

			var colliders = grabbable is IGrabColliders source
				? source.GrabColliders
				: null;
			if (colliders != null)
				for (var i = 0; i < colliders.Count; i++) {
					var collider = colliders[i];
					if (!collider || !collider.enabled) continue;

					if (found)
						bounds.Encapsulate(collider.bounds);
					else {
						bounds = collider.bounds;
						found = true;
					}
				}

			if (found) return true;

			var target = grabbable.Target;
			if (!target) return false;

			var renderers = target.GetComponentsInChildren<Renderer>();
			for (var i = 0; i < renderers.Length; i++) {
				if (!renderers[i].enabled) continue;

				if (found)
					bounds.Encapsulate(renderers[i].bounds);
				else {
					bounds = renderers[i].bounds;
					found = true;
				}
			}

			return found;
		}

		private static bool PointerOverUi() {
			return EventSystem.current && EventSystem.current.IsPointerOverGameObject();
		}
	}
}
