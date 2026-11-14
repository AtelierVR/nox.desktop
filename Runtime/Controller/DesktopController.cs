using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars.Controllers;
using Nox.CCK.Nameplate;
using Nox.CCK.Utils;
using Nox.Controllers;
using Nox.Desktop.Connectors;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// Desktop controller proxy. Split across partials, mirroring <c>nox.xr</c>:
	/// avatar handling in <c>DesktopController.Avatar.cs</c>, the <see cref="IController"/>
	/// surface (parts, abilities, camera) in <c>DesktopController.Parts.cs</c> and the
	/// nameplate binding in <c>DesktopController.Nameplate.cs</c>.
	/// </summary>
	public partial class DesktopController : MonoBehaviour, IController, IControllerAvatar, INoxObject, INameplateHolder {
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

		public DesktopPlayer player;
		public EventSystem eventSystem;

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controllers")
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
		/// <param name="cancellationToken">
		/// Owns the creation: the proxy and everything it instantiates (its menus) must not be built
		/// once the caller is gone. Cancelled, the half-built proxy is dropped.
		/// </param>
		/// <returns></returns>
		static async internal UniTask<bool> Make(CancellationToken cancellationToken = default) {
			if (!IsBetterThanCurrent() || cancellationToken.IsCancellationRequested)
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

			try {
				await desktop.Menu.Generate(cancellationToken);
				cancellationToken.ThrowIfCancellationRequested();

				if (!await ControllerAPI.SetCurrent(desktop)) {
					Logger.LogError("Failed to set Desktop proxy as current");						desktop.Menu.Dispose();					instance.Destroy();
					return false;
				}
			} catch (OperationCanceledException) {
				desktop.Menu.Dispose();
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

			desktop.gameObject.name = $"[{desktop.GetType().Name}] {ComponentExtension.GetId(desktop)}";
			desktop.gameObject.DontDestroyOnLoad();
			return true;
		}

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		/// <summary>
		/// Disposes the proxy and completes once its avatar is destroyed: the avatar writes what it has to
		/// persist while it is disposed, and that write must end before the mods (and their table) go away.
		/// </summary>
		public UniTask Dispose() {
			DisposeNameplate();
			DisposeEyes();
			Menu.Dispose();

			var avatar = avatarLoader != null
				? avatarLoader.Dispose()
				: UniTask.CompletedTask;

			// Deferred: the avatar is a child of this object and finishes its write on this frame.
			gameObject.Destroy();

			return avatar;
		}

		private void Awake() {
			SetupNameplate();
			ResolveEyes();
		}

		private void Update() {
			// The nameplate mod may be loaded after this proxy was created.
			if (!_nameplate.IsAlive())
				SetupNameplate();

			UpdateNameplate();
			HandleZoomInput();
		}

		private void HandleZoomInput() {
			// A menu, or a window that lost the focus: the wheel does not zoom the view.
			if (DesktopInput.Blocked)
				return;

			// Vérifier si la souris n'est pas sur l'UI
			if (EventSystem.current && EventSystem.current.IsPointerOverGameObject())
				return;

			// An object is carried: the wheel moves it nearer or farther, it must not zoom the view.
			if (player && player.IsHolding)
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
