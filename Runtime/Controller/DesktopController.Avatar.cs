using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Controllers;
using Nox.CCK.Utils;
using Nox.Controllers;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// Avatar handling for the desktop controller: loading the placeholder avatar, attaching the
	/// announced one, and restoring the avatar when this proxy takes over another controller.
	/// </summary>
	public partial class DesktopController {

		private bool _settingUpAvatar;

		public UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> onProgress = null)
			=> avatarLoader != null ? avatarLoader.SetAvatar(identifier, onProgress) : UniTask.FromResult<IRuntimeAvatar>(null);

		public UniTask<IRuntimeAvatar> ReloadAvatar(Action<string, float> onProgress = null)
			=> avatarLoader != null ? avatarLoader.ReloadAvatar(onProgress) : UniTask.FromResult<IRuntimeAvatar>(null);

		public IRuntimeAvatar GetAvatar()
			=> avatarLoader?.GetAvatar();

		public UniTask<bool> SetAvatar(IRuntimeAvatar runtimeAvatar)
			=> avatarLoader != null ? avatarLoader.SetAvatar(runtimeAvatar) : UniTask.FromResult(false);

		private async UniTask SetupAvatar() {
			if (avatarLoader == null || avatarLoader.GetAvatar() != null) {
				Logger.LogDebug("Avatar already set for DesktopController");
				return;
			}

			// SetupAvatar peut être relancé (Make + flux user_update) ; deux passes
			// concurrentes s'annulent via _context.Cancel() dans le loader.
			if (_settingUpAvatar) {
				Logger.LogDebug("Avatar setup already in progress, skipping duplicate request.");
				return;
			}

			if (Client.AvatarAPI == null) {
				Logger.LogWarning("AvatarAPI not available yet, skipping avatar setup");
				return;
			}

			_settingUpAvatar = true;
			try {
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

				// Le proxy desktop est détruit dès qu'un contrôleur de priorité supérieure
				// (XR) prend la main : l'avatar en vol n'a plus de loader pour l'accueillir.
				if (!this || !gameObject || avatarLoader == null) {
					Logger.LogDebug("Desktop proxy was destroyed while loading the loading avatar, discarding it.");
					await avatar.Dispose();
					return;
				}

				await avatarLoader.SetAvatar(avatar);

				avatarLoader.LoadAvatarFromUser(Client.UserAPI?.Current);
			} finally {
				_settingUpAvatar = false;
			}
		}

		public UniTask Restore(IController controller) {
			foreach (var ability in controller.GetAbilities())
				SetAbilities(ability.Key, ability.Value);

			// Un loader fraîchement créé n'a pas encore d'avatar : le charger ici
			// ferait démarrer un chargement avant SetupAvatar(), qui en lancerait un
			// second en parallèle (annulations et avatars détruits en cascade).
			if (controller is IControllerAvatar ca && avatarLoader?.GetAvatar() != null) {
				var identifier = ca.GetAvatar()?.Identifier ?? Identifier.Invalid;
				if (identifier.IsValid())
					avatarLoader.SetAvatar(identifier).Forget();
			}

			return UniTask.CompletedTask;
		}
	}
}
