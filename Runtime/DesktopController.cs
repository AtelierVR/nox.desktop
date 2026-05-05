using System;
using System.Collections.Generic;
using Nox.CCK;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Camera;
using Nox.Avatars.Controllers;
using Nox.Avatars.Parameters;
using Nox.Avatars.Players;
using Nox.Avatars.Runtime.Network;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Network;
using Nox.CCK.Players;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
using Transform = UnityEngine.Transform;
using Nox.Controllers;
using Nox.Users;
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

		private DesktopController()
			=> _avatarParameters = new Dictionary<string, object> {
				["source"]  = this,
				["desktop"] = true,
				["local"]   = true
			};

		private readonly Dictionary<string, object> _avatarParameters;

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

			if (desktop._attachedRuntimeAvatar == null)
				desktop.SetupAvatar().Forget();

			desktop._onUserUpdate = Client.CoreAPI.EventAPI.Subscribe("user_update", desktop.OnUserUpdate);

			desktop.gameObject.name = $"[{desktop.GetType().Name}_{desktop.GetEntityId().GetHashCode()}]";
			DontDestroyOnLoad(desktop);
			return true;
		}

		private void OnUserUpdate(EventData context) {
			if (!context.TryGet(0, out ICurrentUser user) || user == null || !IsCurrent())
				return;
			LoadAvatarFromUser(user);
		}

		private void LoadAvatarFromUser(ICurrentUser user)
			=> SetAvatar(user.Avatar).Forget();

		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> onProgress = null) {
			var localPlayer = Client.SessionAPI.TryGet(Client.SessionAPI.Current, out var session)
				? session.LocalPlayer
				: null;
			var playerAvatar = localPlayer as ILocalPlayerAvatar;

			Logger.LogDebug($"Loading avatar for identifier {identifier.ToString() ?? "null"}");

			if (!identifier.IsValid()) {
				Logger.LogWarning($"Invalid avatar identifier: {identifier.ToString() ?? "null"}");
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new NullReferenceException("Invalid avatar identifier"));
				return null;
			}

			if (identifier.Equals(playerAvatar?.GetAvatar())) {
				Logger.LogDebug("Avatar identifier matches player identifier, no need to load.");
				if (playerAvatar != null)
					await playerAvatar.OnAvatarReady();
				return _attachedRuntimeAvatar;
			}

			if (identifier.Equals(_attachedRuntimeAvatar?.Identifier)) {
				Logger.LogDebug("Avatar identifier matches current avatar, no need to load.");
				if (playerAvatar != null)
					await playerAvatar.OnAvatarReady();
				return _attachedRuntimeAvatar;
			}

			_avatarLoadingCts?.Cancel();
			_avatarLoadingCts = new CancellationTokenSource();

			var version = identifier.GetVersion();
			if (version == ushort.MaxValue) {
				var avatarData = await Client.AvatarAPI.Fetch(identifier);
				if (avatarData != null && avatarData.Release != ushort.MaxValue)
					version = avatarData.Release;
			}

			var req = new AssetSearchRequest {
				Engines   = new[] { EngineExtensions.CurrentEngine.GetEngineName() },
				Platforms = new[] { PlatformExtensions.CurrentPlatform.GetPlatformName() },
				Versions  = new[] { version },
				Limit     = 1
			};

			var asset = (await Client.AvatarAPI.SearchAssets(identifier, req)
					.AttachExternalCancellation(_avatarLoadingCts.Token))
				.Items.FirstOrDefault();

			if (_avatarLoadingCts.IsCancellationRequested)
				return null;

			if (asset == null) {
				Logger.LogWarning($"Avatar asset not found for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_avatarParameters);
				err.Identifier = identifier;
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new NullReferenceException("Avatar asset not found"));
				return null;
			}

			if (!Client.AvatarAPI.HasInCache(asset.Hash)) {
				var download = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => onProgress?.Invoke($"Downloading avatar {identifier.ToString()}", p),
					token: _avatarLoadingCts.Token
				);
				await download.Start();
				if (_avatarLoadingCts.IsCancellationRequested)
					return null;
			}

			var avatar = await Client.AvatarAPI.LoadFromCache(
				asset.Hash,
				_avatarParameters,
				progress: p => onProgress?.Invoke($"Loading avatar {identifier.ToString()}", p),
				token: _avatarLoadingCts.Token
			);
			if (_avatarLoadingCts.IsCancellationRequested)
				return null;

			if (avatar == null && Client.AvatarAPI.HasInCache(asset.Hash)) {
				Logger.LogWarning($"Corrupt cache entry for avatar {identifier.ToString()}, re-downloading...");
				Client.AvatarAPI.RemoveFromCache(asset.Hash);
				var reDownload = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => onProgress?.Invoke($"Re-downloading avatar {identifier.ToString()}", p),
					token: _avatarLoadingCts.Token
				);
				await reDownload.Start();
				if (_avatarLoadingCts.IsCancellationRequested)
					return null;
				avatar = await Client.AvatarAPI.LoadFromCache(
					asset.Hash,
					_avatarParameters,
					progress: p => onProgress?.Invoke($"Loading avatar {identifier.ToString()}", p),
					token: _avatarLoadingCts.Token
				);
				if (_avatarLoadingCts.IsCancellationRequested)
					return null;
			}

			if (avatar == null) {
				Logger.LogError($"Failed to load avatar from cache for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_avatarParameters);
				err.Identifier = identifier;
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Failed to load avatar from cache"));
				return null;
			}

			Logger.LogDebug($"Avatar loaded: {identifier.ToString()}");
			avatar.Identifier = identifier;
			await SetAvatar(avatar);
			if (playerAvatar != null)
				await playerAvatar.OnAvatarReady();
			return avatar;
		}

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		public DesktopPlayer player;
		public EventSystem eventSystem;

		public void Dispose() {
			Client.CoreAPI.EventAPI.Unsubscribe(_onUserUpdate);
			Menu.Dispose();
			_onUserUpdate = null;
			_avatarLoadingCts?.Cancel();
			_avatarLoadingCts?.Dispose();
			_avatarLoadingCts = null;
			_attachedRuntimeAvatar?.Dispose();
			_attachedRuntimeAvatar = null;
			Destroy(gameObject);
		}

		private async UniTask SetupAvatar() {
			if (_attachedRuntimeAvatar != null) {
				Logger.LogDebug("Avatar already set for DesktopController");
				return;
			}

			if (Client.AvatarAPI == null) {
				Logger.LogWarning("AvatarAPI not available yet, skipping avatar setup");
				return;
			}

			Logger.LogDebug("Creating avatar");

			var avatar = await Client.AvatarAPI.LoadLoading(_avatarParameters);
			if (avatar == null) {
				Logger.LogError("Failed to create avatar for DesktopController");
				return;
			}

			await SetAvatar(avatar);

			LoadAvatarFromUser(Client.UserAPI?.Current);
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
					SetAvatar(identifier).Forget();
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
				{ "air_control", player.airControl }
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

		private IRuntimeAvatar _attachedRuntimeAvatar;
		private CancellationTokenSource _avatarLoadingCts;
		private EventSubscription _onUserUpdate;

		public IRuntimeAvatar GetAvatar()
			=> _attachedRuntimeAvatar;

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar) {
			Logger.LogDebug("Setting avatar for DesktopController");
			if (runtimeAvatar == _attachedRuntimeAvatar)
				return true;

			var old = _attachedRuntimeAvatar;
			_attachedRuntimeAvatar = runtimeAvatar;

			if (_attachedRuntimeAvatar == null) {
				Logger.LogWarning("Setting avatar to null, removing current avatar.");
				_attachedRuntimeAvatar = old;
				return false;
			}

			var root = _attachedRuntimeAvatar.Descriptor.Anchor;
			if (!root) {
				Logger.LogError("Avatar descriptor root is null, cannot set avatar.");
				_attachedRuntimeAvatar = old;
				return false;
			}

			root.name += $" {runtimeAvatar.Identifier.ToString()} Desktop";

			if (old != null)
				await old.Dispose();

			Logger.LogDebug($"Attaching avatar to {runtimeAvatar.Descriptor}", runtimeAvatar.Descriptor.Anchor);
			root.transform.SetParent(transform, false);
			root.transform.localPosition = Vector3.zero;
			root.transform.localRotation = Quaternion.identity;

			var parameterModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null) {
				Logger.LogWarning("Avatar has no parameter module, cannot configure tracking parameters.");
				return true;
			}

			// Attendre que l'Animator soit prêt avant de configurer les paramètres
			var animator = _attachedRuntimeAvatar?.Descriptor?.Animator;
			if (animator && !animator.runtimeAnimatorController) {
				Logger.LogDebug("Waiting for Animator to be ready...");
				await UniTask.WaitUntil(() => animator.runtimeAnimatorController);
			}

			var parameters = parameterModule.GetParameters();
			foreach (var param in parameters) {
				var n = param.GetName();
				switch (n) {
					case "rig/ik/head/target":
					case "tracking/left_hand/active":
					case "tracking/right_hand/active":
					case "tracking/left_foot/active":
					case "tracking/right_foot/active":
					case "tracking/right_toes/active":
					case "tracking/left_toes/active":
						param.Set(false);
						break;
					case "rig/ik/spine/position_weight":
					case "rig/ik/spine/hint_weight":
						param.Set(0f);
						break;
					case "tracking/head/active":
					case "IsLocal":
						param.Set(true);
						break;
				}
			}

			root.SetActive(true);

			Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _attachedRuntimeAvatar);

			return true;
		}

		private void Update() {
			HandleZoomInput();
			SynchronizeParametersAvatar();
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

		private void LateUpdate()
			=> UpdateCamera();


		// ReSharper disable Unity.PerformanceAnalysis
		private void SynchronizeParametersAvatar() {
			var parameterModule = _attachedRuntimeAvatar?.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null)
				return;

			var parameters = parameterModule.GetParameters();
			foreach (var param in parameters) {
				var n = param.GetName();
				switch (n) {
					case "Grounded": {
						var grounded = player.IsGrounded();
						var value    = param.Get().ToBool();
						if (value == grounded)
							continue;
						param.Set(grounded);
						break;
					}
					case "VelocityX": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.x))
							continue;
						param.Set(localVelocity.x);
						break;
					}
					case "VelocityY": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.y))
							continue;
						param.Set(localVelocity.y);
						break;
					}
					case "VelocityZ": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, localVelocity.z))
							continue;
						param.Set(localVelocity.z);
						break;
					}
					case "Velocity": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var localVelocity = transform.InverseTransformDirection(worldVelocity);
						var value         = param.Get().ToVector3();
						if (value == localVelocity)
							continue;
						param.Set(localVelocity);
						break;
					}
					case "VelocityMagnitude": {
						var worldVelocity = player.body?.linearVelocity ?? Vector3.zero;
						var magnitude     = worldVelocity.magnitude;
						var value         = param.Get().ToFloat();
						if (Mathf.Approximately(value, magnitude))
							continue;
						param.Set(magnitude);
						break;
					}
					case "tracking/head/rotation": {
						var cRot  = player.headCamera.transform.rotation;
						var value = param.Get().ToQuaternion();
						if (Quaternion.Angle(value, cRot) < 0.001f)
							continue;
						param.Set(cRot);
						break;
					}
				}
			}
		}

		private void UpdateCamera() {
			var cameraModule = _attachedRuntimeAvatar?.Descriptor
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