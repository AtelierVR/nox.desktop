using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Controllers;
using Nox.Avatars.Players;
using Nox.Avatars.Runtime.Network;
using Nox.CCK;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Network;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using Nox.Controllers;
using Nox.Desktop.Connectors;
using Nox.Users;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Nox.Desktop.Runtime {
	public class DesktopController : MonoBehaviour, IController, IControllerAvatar, INoxObject {
		private static int DefaultPriority
			=> Config.Load().Get("settings.controller.desktop_priority", IController.DefaultPriority);

		private const string DefaultId = "desktop";

		[Header("Zoom Settings")]
		[SerializeField]
		private float zoomSpeed = 2f;

		[SerializeField]
		private float minZoom = 2f;

		[SerializeField]
		private float maxZoom = 60f;

		private float _currentZoom = 60f;

		public DesktopMenuProvider Menu;
		public AvatarLoaderConnector avatarLoader;
		public AvatarSyncConnector avatarSync;

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controller")
				.GetInstance<IControllerAPI>();

		/// <summary>
		/// Check if the current proxy is better than Desktop proxy.
		/// </summary>
		/// <returns></returns>
		private static bool IsBetterThanCurrent() {
			var controller = ControllerAPI.Current;
			return controller == null
				|| controller.GetPriority() < DefaultPriority
				|| controller.GetId() == DefaultId;
		}

		/// <summary>
		/// Check if the current proxy is the Desktop proxy.
		/// </summary>
		/// <returns></returns>
		private static bool IsCurrent() {
			var controller = ControllerAPI.Current;
			return controller != null
				&& controller.GetId() == DefaultId;
		}

		/// <summary>
		/// Remove the current proxy if it is the Desktop proxy.
		/// </summary>
		static async internal UniTask<bool> Remove() {
			if (!IsCurrent())
				return false;
			return await ControllerAPI.SetCurrent(null);
		}

		/// <summary>
		/// Create the Desktop proxy if it is not already created.
		/// </summary>
		/// <returns></returns>
		static async internal UniTask<bool> Make() {
			if (!IsBetterThanCurrent())
				return false;

			var prefab = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("desktop_proxy.prefab");
			if (!prefab) {
				Logger.LogError("Failed to load desktop proxy prefab");
				return false;
			}

			var instance = Instantiate(prefab);
			var desktop  = instance.GetComponent<DesktopController>();

			if (!desktop) {
				Logger.LogError("Failed to get desktop proxy component");
				instance.Destroy();
				return false;
			}

			await desktop.Menu.Generate();

			if (!await ControllerAPI.SetCurrent(desktop)) {
				Logger.LogError("Failed to set Desktop proxy as current");
				instance.Destroy();
				return false;
			}

			if (desktop.avatarLoader == null) {
				Logger.LogError("Desktop avatar loader is not configured in the prefab");
			} else {
				if (desktop.avatarLoader.GetAvatar() == null)
					desktop.SetupAvatar().Forget();
				desktop.avatarLoader.StartUserTracking();
			}

			desktop.gameObject.name = $"[{desktop.GetType().Name}_{desktop.GetEntityId().GetHashCode()}]";
			DontDestroyOnLoad(desktop);
			return true;
		}

		public UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> onProgress = null)
			=> avatarLoader != null ? avatarLoader.SetAvatar(identifier, onProgress) : UniTask.FromResult<IRuntimeAvatar>(null);

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		public DesktopPlayer player;
		public EventSystem eventSystem;

		public void Dispose() {
			Menu.Dispose();
			avatarLoader?.Dispose();
			Destroy(gameObject);
		}

		private async UniTask SetupAvatar() {
			if (avatarLoader == null || avatarLoader.GetAvatar() != null) {
				Logger.LogDebug("Avatar already set for DesktopController");
				return;
			}

			if (Client.AvatarAPI == null) {
				Logger.LogWarning("AvatarAPI not available yet, skipping avatar setup");
				return;
			}

			Logger.LogDebug("Creating avatar");

			var avatarParameters = new Dictionary<string, object> {
				["source"]  = this,
				["desktop"] = true,
				["local"]   = true
			};
			var avatar = await Client.AvatarAPI.LoadLoading(avatarParameters);
			if (avatar == null) {
				Logger.LogError("Failed to create avatar for DesktopController");
				return;
			}

			await avatarLoader.SetAvatar(avatar);

			avatarLoader.LoadAvatarFromUser(Client.UserAPI?.Current);
		}

		[NoxPublic(NoxAccess.Method)]
		public Camera GetCamera()
			=> player.headCamera;

		public EventSystem GetEventSystem()
			=> eventSystem;

		[NoxPublic(NoxAccess.Method)]
		public Collider GetCollider()
			=> player.bodyCollider;

		public UniTask Restore(IController controller) {
			foreach (var ability in controller.GetAbilities())
				SetAbilities(ability.Key, ability.Value);

			if (controller is IControllerAvatar ca) {
				var identifier = ca.GetAvatar().Identifier;
				if (identifier.IsValid())
					avatarLoader?.SetAvatar(identifier).Forget();
			}

			return UniTask.CompletedTask;
		}

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

		// ReSharper disable Unity.PerformanceAnalysis
		public void SetPart(ushort index, TransformObject tr) {
			if (!Parts.TryGetValue(index, out var part))
				return;

			Logger.LogDebug($"Set part {index}");
			if (!tr.IsSamePosition(part.position))
				part.position = tr.GetPosition();
			if (!tr.IsSameRotation(part.rotation))
				part.rotation = tr.GetRotation();

			if (!part.TryGetComponent<Rigidbody>(out var rb))
				return;

			if (rb && !tr.IsSameVelocity(rb.linearVelocity))
				rb.linearVelocity = tr.GetVelocity();
			if (rb && !tr.IsSameAngular(rb.angularVelocity))
				rb.angularVelocity = tr.GetAngular();
		}

		public IRuntimeAvatar GetAvatar()
			=> avatarLoader?.GetAvatar();

		public UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar)
			=> avatarLoader != null ? avatarLoader.SetAvatar(runtimeAvatar) : UniTask.FromResult(false);

		private void Update() {
			HandleZoomInput();
		}

		private void HandleZoomInput() {
			// Vérifier si la souris n'est pas sur l'UI
			if (EventSystem.current && EventSystem.current.IsPointerOverGameObject())
				return;

			// Gérer le zoom avec la molette de la souris
			var scrollInput = Mouse.current?.scroll.ReadValue().y / 120f ?? 0f;
			if (!(Mathf.Abs(scrollInput) > 0.01f))
				return;

			// Calculer le nouveau zoom
			_currentZoom -= scrollInput * zoomSpeed * 10f;
			_currentZoom =  Mathf.Clamp(_currentZoom, minZoom, maxZoom);

			// Appliquer le zoom à la caméra
			if (player?.headCamera)
				player.headCamera.fieldOfView = _currentZoom;
		}


	}
}