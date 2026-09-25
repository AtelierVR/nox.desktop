using System.Collections.Generic;
using System.Linq;
using Nox.CCK;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.Controllers;
using UnityEngine;
using UnityEngine.EventSystems;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// <see cref="IController"/> surface of the desktop proxy: camera, collider, event system,
	/// rig parts and the movement abilities exposed to other controllers/sessions.
	/// </summary>
	public partial class DesktopController {

		[NoxPublic(NoxAccess.Method)]
		public Camera GetCamera()
			=> player.headCamera;

		public EventSystem GetEventSystem()
			=> eventSystem;

		[NoxPublic(NoxAccess.Method)]
		public Collider GetCollider()
			=> player.bodyCollider;

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
				{ "height", player.Height }
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
				{ PlayerRig.Base.ToIndex(), transform },
				{ PlayerRig.Head.ToIndex(), player.headCamera.transform }
			};

		IReadOnlyDictionary<ushort, TransformObject> IController.GetParts()
			=> Parts
				.ToDictionary(
					p => p.Key,
					p => {
						var rb = p.Value.GetComponent<Rigidbody>();
						return new TransformObject(p.Value, rb);
					}
				);

		public bool TryGetPart(ushort index, out TransformObject tr) {
			if (!Parts.TryGetValue(index, out var part)) {
				tr = new TransformObject();
				return false;
			}

			var rb = part.TryGetComponent<Rigidbody>(out var rigid)
				? rigid
				: null;
			tr = new TransformObject(part, rb);

			return true;
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
