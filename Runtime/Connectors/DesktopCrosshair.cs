using Nox.Desktop.Runtime;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Nox.Desktop.Connectors {
	public class DesktopCrosshair : MonoBehaviour {
		public Image image;

		public Sprite hoverSprite;
		public Sprite normalSprite;

		/// <summary>Player whose ray says whether something can be taken, found on this object when empty.</summary>
		[SerializeField] private DesktopPlayer player;

		/// <summary>Whether the crosshair currently shows the hover sprite.</summary>
		private bool _isHovering;

		private void Start() {
			if (!player)
				player = GetComponent<DesktopPlayer>();

			if (image && normalSprite)
				image.sprite = normalSprite;
		}

		private void Update() {
			var hovering = Hovering();

			if (hovering == _isHovering)
				return;

			_isHovering = hovering;
			UpdateCrosshairSprite();
		}

		/// <summary>
		/// Whether the crosshair points at something the player can use: a UI element, or an object its ray can
		/// take (see <see cref="Nox.Desktop.Runtime.Interaction.DesktopInteractor.Hovered"/>).
		/// </summary>
		private bool Hovering() {
			if (player && player.HasHover)
				return true;

			return EventSystem.current && EventSystem.current.IsPointerOverGameObject();
		}

		private void UpdateCrosshairSprite() {
			if (!image)
				return;

			image.sprite = _isHovering switch {
				true when hoverSprite   => hoverSprite,
				false when normalSprite => normalSprite,
				_                       => image.sprite
			};
		}
	}
}
