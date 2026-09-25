using Cysharp.Threading.Tasks;
using Nox.CCK.Nameplate;
using Nox.Nameplate;
using UnityEngine;
using Transform = UnityEngine.Transform;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// Nameplate binding for the desktop controller: the controller owns a dedicated anchor above
	/// the player's head and asks <c>nox.nameplate</c> for an <see cref="INameplate"/>.
	/// <para>
	/// That plate is only the controller's <b>handle</b> on the nameplate system: it displays nothing
	/// (a player never sees their own name and no session can influence it) and carries the
	/// client-wide visibility driven by the menu provider through <c>Keys.Visible</c>.
	/// </para>
	/// </summary>
	public partial class DesktopController {

		/// <summary>Height of the plate anchor above the head camera.</summary>
		private const float NameplateAnchorHeight = 0.35f;

		/// <summary>Dedicated transform handed to the nameplate mod (above the local head). Assigned in the prefab.</summary>
		[Header("Nameplate Settings")]
		public Transform NameplateAnchor;

		/// <summary>The plate owned by this controller (local and private to it).</summary>
		public INameplate Nameplate
			=> _nameplate;

		private INameplate _nameplate;
		private bool       _creatingNameplate;

		private void SetupNameplate() {
			if (_nameplate.IsAlive() || _creatingNameplate)
				return;

			var api = Client.NameplateAPI;
			if (api == null)
				return;

			if (NameplateAnchor == null) {
				Logger.LogWarning("Desktop nameplate anchor is not assigned in the prefab");
				return;
			}

			UpdateNameplate();

			CreateNameplateAsync(api, NameplateAnchor).Forget();
		}

		/// <summary>
		/// Keeps the plate anchor above the head, expressed in the proxy's local space: the height is
		/// <see cref="DesktopPlayer.Height"/> (the body collider height, which tracks the avatar height
		/// and the crouch) plus <see cref="NameplateAnchorHeight"/>, while x/z stay at 0 so the plate
		/// remains centred on the proxy and never drifts or tilts with the head rotation.
		/// </summary>
		private void UpdateNameplate() {
			if (NameplateAnchor == null || player == null)
				return;

			// Height is expressed in world space; bring it back into the proxy's local space so the
			// anchor stays correct whatever scale/rotation the proxy is nested under.
			var height = transform.InverseTransformVector(Vector3.up * player.Height).y;
			NameplateAnchor.localPosition = new Vector3(0f, height + NameplateAnchorHeight, 0f);
		}

		private async UniTaskVoid CreateNameplateAsync(INameplateAPI api, Transform anchor) {
			_creatingNameplate = true;
			try {
				var plate = await api.Instantiate(anchor);
				if (!plate.IsAlive())
					return;

				// The proxy may have been destroyed while instantiating.
				if (!this || !gameObject) {
					plate.Dispose();
					return;
				}

				_nameplate = plate;

				// The plate displays nothing on its own: it is only this controller's handle on the
				// nameplate system, carrying the client-wide visibility driven by the menu provider.					
				// Its badges carry the local platform/engine (hidden while no user is bound).
				plate.SetClientBadges();			
			} finally {
				_creatingNameplate = false;
			}
		}

		private void DisposeNameplate() {
			if (_nameplate.IsAlive())
				_nameplate.Dispose();
			_nameplate = null;
		}
	}
}
