using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Nox.Desktop.Runtime {
	public class DesktopCrosshair : MonoBehaviour {
		public Image         image;

		public Sprite hoverSprite;
		public Sprite normalSprite;

		private bool _isHoveringUI = false;

		private void Start() {
			if (image && normalSprite)
				image.sprite = normalSprite;
		}

		void Update() {
			var currentHoverState = EventSystem.current && EventSystem.current.IsPointerOverGameObject();
			if (currentHoverState == _isHoveringUI) return;
			_isHoveringUI = currentHoverState;
			UpdateCrosshairSprite();
		}

		private void UpdateCrosshairSprite() {
			if (!image) return;
			image.sprite = _isHoveringUI switch {
				true when hoverSprite   => hoverSprite,
				false when normalSprite => normalSprite,
				_                       => image.sprite
			};
		}
	}
}