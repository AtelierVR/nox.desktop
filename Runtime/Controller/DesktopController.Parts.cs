using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.Controllers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;
using System;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// <see cref="IController"/> surface of the desktop proxy: camera, collider, event system,
	/// rig parts and the movement abilities exposed to other controllers/sessions.
	/// </summary>
	public partial class DesktopController {

		[NoxPublic(NoxAccess.Method)]
		public Camera GetCamera()
			=> player.headCamera;

		/// <summary>Colliders of the body: the capsule and the extras of the player (avatar parts, worn props).</summary>
		public IReadOnlyList<Collider> GetColliders()
			=> player
				? player.BodyColliders 
				: Array.Empty<Collider>();

		/// <summary>Invoked when the body of the player gained or lost a collider.</summary>
		public UnityEvent<IController> OnCollidersChanged { get; } = new();

		public EventSystem GetEventSystem()
			=> eventSystem;

		[NoxPublic(NoxAccess.Method)]
		public Dictionary<string, object> GetAbilities()
			=> new() {
				{ "grounded", player.IsGrounded() },
				{ "immobilized", !player.useMovement },
				{ "crouching", player.crouching },
				{ "sprinting", player.IsSprinting() },
				{ "flying", player.IsFlying() },
				{ "may_fly", player.MayFly() },
				{ "max_move_speed", player.maxMoveSpeed },
				{ "move_acceleration", player.moveAcceleration },
				{ "jump_force", player.jumpForce },
				{ "fly_speed", player.flySpeed },
				{ "sprint_multiplier", player.sprintMultiplier },
				{ "air_control", player.airControl },
				{ "height", player.Height },
				{ "eye_height", EyeHeight }
			};

		[NoxPublic(NoxAccess.Method)]
		public void SetAbilities(string key, object value) {
			if (!GetAbilities().ContainsKey(key))
				return;
			switch (key) {
				case "immobilized":
					player.useMovement = !(bool)value;
					break;
				case "crouching":
					player.SetCrouching((bool)value);
					break;
				case "sprinting":
					player.SetSprinting((bool)value);
					break;
				case "flying":
					if ((bool)value != player.IsFlying())
						player.ToggleFlying();
					break;
				case "may_fly":
					player.SetMayFly((bool)value);
					break;
				case "max_move_speed":
					player.maxMoveSpeed = (float)value;
					break;
				case "move_acceleration":
					player.moveAcceleration = (float)value;
					break;
				case "jump_force":
					player.jumpForce = (float)value;
					break;
				case "fly_speed":
					player.flySpeed = (float)value;
					break;
				case "sprint_multiplier":
					player.sprintMultiplier = (float)value;
					break;
				case "air_control":
					player.airControl = (float)value;
					break;
			}
		}

		#region Parts

		private Dictionary<ushort, Transform> _parts;

		private Dictionary<ushort, Transform> Parts
			=> _parts ??= new Dictionary<ushort, Transform> {
				{ EntityRig.Base.ToIndex(), transform },
				{ EntityRig.Head.ToIndex(), player.headCamera.transform }
			};

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts()
			=> Parts
				.ToDictionary(
					p => p.Key,
					p => {
						var rb = p.Value.GetComponent<Rigidbody>();
						return p.Key == EntityRig.Head.ToIndex()
							? HeadPart(p.Value)
							: new TransformObject(p.Value, rb);
					}
				);

		public bool TryGetPart(ushort index, out TransformObject tr) {
			if (!Parts.TryGetValue(index, out var part)) {
				tr = new TransformObject();
				return false;
			}

			tr = index == EntityRig.Head.ToIndex()
				? HeadPart(part)
				: new TransformObject(part, part.TryGetComponent<Rigidbody>(out var rigid) ? rigid : null);

			return true;
		}

		/// <summary>
		/// The head of a desktop player only turns: it has no head tracking, so it must not carry a
		/// position. The camera sits in front of the neck, and the viewers would take that point as the
		/// head, which drags the head away from the body (the IK head target is written with it).
		/// </summary>
		private static TransformObject HeadPart(Transform head) {
			var tr = new TransformObject();
			tr.SetRotation(head.rotation);
			return tr;
		}

		// ReSharper disable Unity.PerformanceAnalysis
		public void SetPart(ushort index, TransformObject tr) {
			if (!Parts.TryGetValue(index, out var part))
				return;

			Logger.LogDebug($"Set part {index}");
			var hasRb = part.TryGetComponent<Rigidbody>(out var rb);

			if (tr.Flags.HasFlag(TransformFlags.Position) && !tr.IsSamePosition(part.position)) {
				part.position = tr.GetPosition();
				if (hasRb && rb)
					rb.position = tr.GetPosition();
				Physics.SyncTransforms();
			}

			if (tr.Flags.HasFlag(TransformFlags.Rotation) && !tr.IsSameRotation(part.rotation)) {
				part.rotation = tr.GetRotation();
				if (hasRb && rb)
					rb.rotation = tr.GetRotation();
			}

			if (tr.Flags.HasFlag(TransformFlags.Scale) && !tr.IsSameScale(part.localScale))
				part.localScale = tr.GetScale();

			if (!hasRb || !rb)
				return;

			if (tr.Flags.HasFlag(TransformFlags.Velocity) && !tr.IsSameVelocity(rb.linearVelocity))
				rb.linearVelocity = tr.GetVelocity();
			if (tr.Flags.HasFlag(TransformFlags.Angular) && !tr.IsSameAngular(rb.angularVelocity))
				rb.angularVelocity = tr.GetAngular();
		}

		#endregion
	}
}
