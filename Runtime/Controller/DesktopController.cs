using Cysharp.Threading.Tasks;
using Nox.Avatars.Controllers;
using Nox.CCK;
using Nox.CCK.Nameplate;
using Nox.CCK.Utils;
using Nox.Controllers;
using Nox.Desktop.Connectors;
using Nox.Audio.Players;
using Nox.Sessions;
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
		[SerializeField] public MicrophoneConnector microphone;

		public DesktopPlayer player;
		public EventSystem eventSystem;

		private ISessionAPI _sessionApi;

		/// <summary>
		/// Get the proxy mod API.
		/// </summary>
		private static IControllerAPI ControllerAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("controllers")
				.GetInstance<IControllerAPI>();

		private static ISessionAPI SessionAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("session")
				?.GetInstance<ISessionAPI>();

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

		[NoxPublic(NoxAccess.Method)]
		public string GetId()
			=> DefaultId;

		[NoxPublic(NoxAccess.Method)]
		public int GetPriority()
			=> DefaultPriority;

		public void Dispose() {
			DisposeNameplate();
			_sessionApi?.OnCurrentChanged.RemoveListener(OnSessionChanged);
			microphone?.Unbind();
			Menu.Dispose();
			avatarLoader?.Dispose();
			Destroy(gameObject);
		}

		private void Awake() {
			SetupNameplate();

			_sessionApi = SessionAPI;
			if (_sessionApi == null) return;
			_sessionApi.OnCurrentChanged.AddListener(OnSessionChanged);
			if (_sessionApi.Current != null && _sessionApi.TryGet(_sessionApi.Current, out var current))
				OnSessionChanged(null, current);
		}

		private void OnSessionChanged(ISession old, ISession next) {
			if (microphone == null) return;
			microphone.Unbind();
			if (next?.LocalPlayer is ILocalPlayerVoice voice)
				microphone.Bind(voice);
		}

		private void Update() {
			// The nameplate mod may be loaded after this proxy was created.
			if (!_nameplate.IsAlive())
				SetupNameplate();

			UpdateNameplate();
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
