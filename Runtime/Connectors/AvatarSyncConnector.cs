using System.Linq;
using Nox.Avatars.Camera;
using Nox.Avatars.Parameters;
using Nox.CCK;
using Nox.Desktop.Runtime;
using UnityEngine;

namespace Nox.Desktop.Connectors {
	public class AvatarSyncConnector : MonoBehaviour {
		public DesktopPlayer player;
		public AvatarLoaderConnector avatarLoader;

		// ReSharper disable Unity.PerformanceAnalysis
		private void Update() {
			SynchronizeParametersAvatar();
		}

		private void LateUpdate() {
			SynchronizeCamera();
		}

		// ReSharper disable Unity.PerformanceAnalysis
		private void SynchronizeParametersAvatar() {
			var avatar = avatarLoader?.GetAvatar();
			var parameterModule = avatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null)
				return;

			var parameters = parameterModule.GetParameters();
			foreach (var param in parameters) {
				var n = param.Name;
				switch (n) {
					case "Grounded": {
						var grounded = player.IsGrounded();
						var value    = (bool)param.Value;
						if (value == grounded)
							continue;
						param.Value = grounded;
						break;
					}
					case "VelocityX": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Value.ToFloat();
						if (Mathf.Approximately(value, localVelocity.x))
							continue;
						param.Value = localVelocity.x;
						break;
					}
					case "VelocityY": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Value.ToFloat();
						if (Mathf.Approximately(value, localVelocity.y))
							continue;
						param.Value = localVelocity.y;
						break;
					}
					case "VelocityZ": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Value.ToFloat();
						if (Mathf.Approximately(value, localVelocity.z))
							continue;
						param.Value = localVelocity.z;
						break;
					}
					case "Velocity": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Value.ToVector3();
						if (value == localVelocity)
							continue;
						param.Value = localVelocity;
						break;
					}
					case "VelocityMagnitude": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var magnitude     = worldVelocity.magnitude;
						var value         = param.Value.ToFloat();
						if (Mathf.Approximately(value, magnitude))
							continue;
						param.Value = magnitude;
						break;
					}
					case "tracking/head/position": {
						var cPos  = player.headCamera.transform.position;
						var value = param.Value.ToVector3();
						if (Vector3.Distance(value, cPos) < 0.001f)
							continue;
						param.Value = cPos;
						break;
					}
					case "tracking/head/rotation": {
						var cRot  = player.headCamera.transform.rotation;
						var value = param.Value.ToQuaternion();
						if (Quaternion.Angle(value, cRot) < 0.001f)
							continue;
						param.Value = cRot;
						break;
					}
				}
			}

			var heightP = parameterModule.GetParameter("Height")
				?? parameterModule.GetParameter("EyeHeight");
			float maxHeight;
			if (heightP != null)
				maxHeight = heightP.Value.ToFloat();
			else if (player.headCamera)
				maxHeight = player.headCamera.transform.position.y - player.transform.position.y;
			else
				maxHeight = 1.7f;

			if (!Mathf.Approximately(player.minMaxHeight.y, maxHeight))
				player.minMaxHeight = new Vector2(player.minMaxHeight.x, maxHeight);
		}

		private void SynchronizeCamera() {
			var cameraModule = avatarLoader?.GetAvatar()?.Descriptor
				?.GetModules<ICameraModule>()
				.FirstOrDefault();

			if (cameraModule == null)
				return;

			var offset = cameraModule.GetOffset();
			var anchor = cameraModule.GetAnchor();
			anchor.GetPositionAndRotation(out var pos, out var rot);

			pos += anchor.TransformDirection(offset);

			player.headCamera.transform.position = pos;
		}
	}
}
