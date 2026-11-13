using UnityEngine;
using UnityEngine.Events;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// Whether the desktop input is suspended: a menu is open, or the window of the game does not have the
	/// focus.
	/// <para>
	/// Everything the mouse drives reads it - the look, the walk, the wheel of the camera and of the grab, the
	/// grabs themselves - so opening a menu or leaving the window stops the player instead of keeping the last
	/// input of the mouse alive, which would send the view spinning when the window comes back.
	/// </para>
	/// </summary>
	public static class DesktopInput {
		/// <summary>Whether a menu (standard or radial) is open.</summary>
		public static bool MenuOpen { get; private set; }

		/// <summary>Whether the window of the game has the focus.</summary>
		public static bool Focused { get; private set; } = true;

		/// <summary>Whether a menu is open or the window lost the focus: nothing the mouse drives runs.</summary>
		public static bool Blocked
			=> MenuOpen || !Focused;

		/// <summary>Raised when <see cref="Blocked"/> changes: <c>true</c> when the input is suspended.</summary>
		public static readonly UnityEvent<bool> OnBlockedChanged = new();

		/// <summary>Tells whether a menu is open (called by the menu provider).</summary>
		/// <param name="open">Whether a menu is open.</param>
		internal static void SetMenuOpen(bool open)
			=> Set(open, Focused);

		/// <summary>Tells whether the window of the game has the focus (called by the player controller).</summary>
		/// <param name="focused">Whether the window has the focus.</param>
		internal static void SetFocused(bool focused)
			=> Set(MenuOpen, focused);

		private static void Set(bool menuOpen, bool focused) {
			var blocked = menuOpen || !focused;
			var changed = Blocked != blocked;

			MenuOpen = menuOpen;
			Focused  = focused;

			if (changed)
				OnBlockedChanged.Invoke(blocked);
		}
	}
}
