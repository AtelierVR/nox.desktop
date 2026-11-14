using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Controllers;
using Nox.Audio;
using Nox.Nameplate;
using Nox.Sessions;
using Nox.UI;
using Nox.Users;

namespace Nox.Desktop.Runtime {
	public class Client : IClientModInitializer {
		internal static IClientModCoreAPI CoreAPI;

		private static CancellationTokenSource _lifetime;

		internal static CancellationToken Lifetime
			=> _lifetime?.Token ?? CancellationToken.None;

		internal static IControllerAPI ControllerAPI
			=> CoreAPI.ModAPI
				.GetMod("controllers")
				.GetInstance<IControllerAPI>();

		internal static ISessionAPI SessionAPI
			=> CoreAPI.ModAPI
				.GetMod("sessions")
				.GetInstance<ISessionAPI>();

		internal static IUiAPI UiAPI
			=> CoreAPI.ModAPI
				.GetMod("ui")
				.GetInstance<IUiAPI>();

		internal static IAvatarAPI AvatarAPI
			=> CoreAPI.ModAPI
				.GetMod("avatar")
				.GetInstance<IAvatarAPI>();

		internal static IUserAPI UserAPI
			=> CoreAPI.ModAPI
				.GetMod("users")
				.GetInstance<IUserAPI>();

		internal static IMicrophoneAPI MicrophoneAPI
			=> CoreAPI.ModAPI
				.GetMod("audio")
				.GetInstance<IMicrophoneAPI>();

		/// <summary>Audio API of the audio mod (channels, deafen state).</summary>
		internal static IAudioAPI AudioAPI
			=> CoreAPI.ModAPI
				.GetMod("audio")
				.GetInstance<IAudioAPI>();

		/// <summary>
		/// API of the optional <c>nox.nameplate</c> mod: null when the mod is not loaded.
		/// </summary>
		internal static INameplateAPI NameplateAPI
			=> CoreAPI?.ModAPI?
				.GetMod("nameplate")?
				.GetInstance<INameplateAPI>();

		public async UniTask OnInitializeClientAsync(IClientModCoreAPI api) {
			CoreAPI = api;

			_lifetime?.Cancel();
			_lifetime = new CancellationTokenSource();

			Keybindings.Rebind();
			ControllerAPI.OnCurrentChanged.AddListener(OnCurrentControllerChanged);

			await DesktopController.Make(Lifetime);
		}

		public async UniTask OnDisposeClientAsync() {
			ControllerAPI?.OnCurrentChanged.RemoveListener(OnCurrentControllerChanged);

			var lifetime = _lifetime;
			_lifetime = null;
			lifetime?.Cancel();

			if (ControllerAPI.Current is DesktopController)
				await ControllerAPI.SetCurrent(null);
			Keybindings.Clear();
			CoreAPI = null;
		}

		/// <summary>
		/// Aucun contrôleur courant : on rétablit le proxy desktop pour que le jeu ait
		/// toujours une caméra et des entrées.
		/// </summary>
		private static void OnCurrentControllerChanged(IController controller) {
			if (controller != null)
				return;

			DesktopController.Make(Lifetime).Forget();
		}
	}
}
