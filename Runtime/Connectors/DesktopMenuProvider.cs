using System;
using Cysharp.Threading.Tasks;
using Nox.CCK.UI;
using Nox.CCK.Utils;
using Nox.Desktop.Runtime;
using Nox.UI;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Connectors {
	public class DesktopMenuProvider : MonoBehaviour, IMenuProvider, IRadialMenuProvider, IDisposable {

		public IMenu Menu;
		[Header("Standard Menu")]
		public RectTransform Container;
		public GameObject ContainerParent;

		public IRadialMenu RadialMenu;
		[Header("Radial Menu")]
		public RectTransform RadialContainer;
		public GameObject RadialContainerParent;

		[Header("Input")]
		public DesktopPlayerControllerLink ControllerLink;
		public float LongPressDuration = 0.4f;


		// IMenuProvider (menu standard)
		RectTransform IMenuProvider.Container
			=> Container;

		bool IMenuProvider.Active {
			get => ContainerParent != null && ContainerParent.activeSelf;
			set => ContainerParent?.SetActive(value);
		}

		// IRadialMenuProvider (menu radial)
		RectTransform IRadialMenuProvider.Container
			=> RadialContainer;

		bool IRadialMenuProvider.Active {
			get => RadialContainerParent != null && RadialContainerParent.activeSelf;
			set => RadialContainerParent?.SetActive(value);
		}

		private bool _mainDown;
		private float _mainDownTime;
		private bool _longPressFired;
		private bool _suppressToggle;
		private bool _radialWasOpen;

		/// <summary>
		/// Vrai si le radial est présent à l'écran : on vérifie l'état logique ET
		/// l'état du conteneur (couverture de la fenêtre d'animation de fermeture).
		/// </summary>
		private bool IsRadialOpen
			=> RadialMenu != null && RadialMenu.Active;

		private void Update() {
			// Détecte les changements d'état du radial (ex. fermé via son élément
			// Close, pas par la touche) pour restaurer le mouvement de la tête et
			// l'état du curseur.
			var isOpen = IsRadialOpen;
			if (isOpen != _radialWasOpen) {
				_radialWasOpen = isOpen;
				RefreshInputState();
			}

			if (!_mainDown || _longPressFired)
				return;

			if (Time.time - _mainDownTime >= LongPressDuration)
				OpenRadial();
		}

		public async UniTask<bool> Generate() {
			if (Container != null) {
				Menu = await Client.UiAPI.Make(this);

				if (Menu == null) {
					Logger.LogError("Failed to create menu");
				} else {
					Menu.Active = false;
				}
			}

			if (RadialContainer != null) {
				RadialMenu = await Client.UiAPI.MakeRadial(this);

				if (RadialMenu == null) {
					Logger.LogError("Failed to create radial menu");
				} else {
					SetupRadialMenu(RadialMenu);
					RadialMenu.Active = false;
				}
			}

			Keybindings.KeyEvent.AddListener(OnKey);

			return true;
		}

		private void OnKey(string key, float @new, float old) {
			if (key != "main")
				return;

			if (@new > 0.5f && old <= 0.5f) {
				// Appui.
				if (IsRadialOpen) {
					// Le radial est ouvert : cet appui le ferme et ne bascule PAS le menu standard.
					_suppressToggle = true;
					CloseRadial();
					return;
				}

				_suppressToggle = false;
				_mainDown       = true;
				_mainDownTime   = Time.time;
				_longPressFired = false;
			} else if (@new <= 0.5f && old > 0.5f) {
				// Relâchement.
				_mainDown = false;

				if (_suppressToggle) {
					// Ce relâchement suit la fermeture du radial : ne pas basculer le menu.
					_suppressToggle = false;
					return;
				}

				if (_longPressFired) {
					// Le radial a été ouvert par appui long : il reste ouvert.
					_longPressFired = false;
					return;
				}

				ToggleMenu(); // Appui court : bascule le menu standard.
			}
		}

		public void Dispose() {
			Keybindings.KeyEvent.RemoveListener(OnKey);
			Menu?.Dispose();
			Menu = null;
			RadialMenu?.Dispose();
			RadialMenu = null;
		}

		private void ToggleMenu() {
			if (Menu == null)
				return;

			Menu.Active = !Menu.Active;
			RefreshInputState();
		}

		private void OpenRadial() {
			if (RadialMenu == null)
				return;

			// Menu standard ouvert : on n'ouvre pas le radial.
			if (Menu != null && Menu.Active)
				return;

			_longPressFired   = true;
			RadialMenu.Active = true;
			RefreshInputState();
		}

		private void CloseRadial() {
			if (RadialMenu == null)
				return;

			RadialMenu.Active = false;
			RefreshInputState();
		}

		private void RefreshInputState() {
			var standardOpen = Menu != null && Menu.Active;
			var radialOpen   = RadialMenu != null && RadialMenu.Active;
			var anyOpen      = standardOpen || radialOpen;

			// Radial seul : curseur masqué, téléporté au centre du radial par le
			// RadialViewportProvider (ne peut pas sortir de la fenêtre).
			if (radialOpen && !standardOpen) {
				Cursor.lockState = CursorLockMode.None;
				Cursor.visible   = false;
			} else {
				Cursor.lockState = anyOpen ? CursorLockMode.None : CursorLockMode.Locked;
				Cursor.visible   = standardOpen;
			}

			if (ControllerLink != null)
				ControllerLink.canInput = !anyOpen;
		}

		/// <summary>
		/// Configure le provider de sélection du menu radial :
		/// trouve ou crée un RadialViewportProvider (souris) et l'assigne
		/// comme sélection du menu radial.
		/// </summary>
		private void SetupRadialMenu(IRadialMenu radial) {
			if (radial is not Component component)
				return;

			var viewport = component.GetOrAddComponent<RadialViewportProvider>();
			// Le curseur sera téléporté au centre du radial (position du pivot).
			viewport.center = RadialContainer;
			radial.Selection = viewport;
		}
	}
}