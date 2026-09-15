using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Controllers;
using Nox.Audio;
using Nox.Sessions;
using Nox.UI;
using Nox.Users;

namespace Nox.Desktop.Runtime {
	public class Client : IClientModInitializer {
		internal static IClientModCoreAPI CoreAPI;

		internal static IControllerAPI ControllerAPI
			=> CoreAPI.ModAPI
				.GetMod("controllers")
				.GetInstance<IControllerAPI>();

		internal static ISessionAPI SessionAPI
			=> CoreAPI.ModAPI
				.GetMod("session")
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
				.GetMod("microphone")
				.GetInstance<IMicrophoneAPI>();

		public async UniTask OnInitializeClientAsync(IClientModCoreAPI api) {
			CoreAPI = api;
			Keybindings.Rebind();

			// Le desktop est le contrôleur de repli. Quand un contrôleur de priorité
			// supérieure (XR) prend la main, ce proxy est détruit ; s'il disparaît ensuite
			// (sortie de VR, casque débranché) plus personne ne pilote le joueur. On le
			// recrée donc dès que le contrôleur courant devient null.
			ControllerAPI.OnCurrentChanged.AddListener(OnCurrentControllerChanged);

			await DesktopController.Make();
		}

		public async UniTask OnDisposeClientAsync() {
			// Se désabonner AVANT de retirer le contrôleur : sinon la disparition du
			// contrôleur courant relancerait Make() pendant le teardown.
			ControllerAPI?.OnCurrentChanged.RemoveListener(OnCurrentControllerChanged);

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

			DesktopController.Make().Forget();
		}
	}
}