using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using Nox.Controllers;
using Nox.Microphone;
using Nox.Sessions;
using Nox.UI;
using Nox.Users;

namespace Nox.Desktop.Runtime {
	public class Client : IClientModInitializer {
		internal static IClientModCoreAPI CoreAPI;

		internal static IControllerAPI ControllerAPI
			=> CoreAPI.ModAPI
				.GetMod("controller")
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
			await DesktopController.Make();
		}

		public async UniTask OnDisposeClientAsync() {
			if (ControllerAPI.Current is DesktopController)
				await ControllerAPI.SetCurrent(null);
			Keybindings.Clear();
			CoreAPI = null;
		}
	}
}