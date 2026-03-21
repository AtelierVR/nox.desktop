using System;
using Nox.KeyBindings;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// A static class that manages key bindings for the player system.
	/// </summary>
	public static class Keybindings {
		/// <summary>
		/// A collection of key bindings used by the player system.
		/// </summary>
		private static readonly (string, string, string, Action<float>, float)[] Keys = {
			("nox.movement", "forward", "<Keyboard>/w", value => SetValue("forward", value), 0f),
			("nox.movement", "backward", "<Keyboard>/s", value => SetValue("backward", value), 0f),
			("nox.movement", "left", "<Keyboard>/a", value => SetValue("left", value), 0f),
			("nox.movement", "right", "<Keyboard>/d", value => SetValue("right", value), 0f),
			("nox.movement", "jump", "<Keyboard>/space", value => SetValue("jump", value), 0f),
			("nox.movement", "crouch", "<Keyboard>/leftShift", value => SetValue("crouch", value), 0f),
			("nox.movement", "sprint", "<Keyboard>/leftCtrl", value => SetValue("sprint", value), 0f),
			("nox.ui", "main", "<Keyboard>/tab", value => SetValue("main", value), 0f)
		};

		static readonly internal UnityEvent<string, float, float> KeyEvent = new();

		/// <summary>
		/// Gets the current movement vector based on the key bindings.
		/// </summary>
		/// <returns></returns>
		public static Vector3 GetMovement()
			=> new(
				GetValue("right") - GetValue("left"),
				GetValue("jump") - GetValue("crouch"),
				GetValue("forward") - GetValue("backward")
			);

		/// <summary>
		/// Gets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		private static float GetValue(string key) {
			for (var i = 0; i < Keys.Length; i++)
				if (Keys[i].Item2 == key) return Keys[i].Item5;
			return 0f;
		}

		/// <summary>
		/// Checks if a specific key binding is pressed.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool IsPressed(string key)
			=> GetValue(key) > 0.1f;

		/// <summary>
		/// Sets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		private static void SetValue(string key, float value) {
			var index = Array.FindIndex(Keys, k => k.Item2 == key);
			if (index == -1) return;
			var keyTuple = Keys[index];
			var oldValue = keyTuple.Item5;
			keyTuple.Item5 = value;
			Keys[index]    = keyTuple;
			KeyEvent.Invoke(key, value, oldValue);
		}

		/// <summary>
		/// Gets the key binding manager instance from the player system.
		/// </summary>
		private static IKeyBindingManager Keybinding
			=> Client
				.CoreAPI.ModAPI
				.GetMod("keybinding")
				?.GetInstance<IKeyBindingManager>();

		/// <summary>
		/// Rebinds all key bindings defined in the Keys array.
		/// </summary>
		public static void Rebind() {
			foreach (var key in Keys)
				Rebind(key.Item2);
		}

		/// <summary>
		/// Rebinds a specific key binding by its ID.
		/// </summary>
		/// <param name="id"></param>
		private static void Rebind(string id) {
			var found = false;
			(string, string, string, Action<float>, float) key = default;
			for (var i = 0; i < Keys.Length; i++) {
				if (Keys[i].Item2 != id) continue;
				key   = Keys[i];
				found = true;
				break;
			}
			if (!found) return;
			var keybinding = Keybinding.AddKeyBinding(key.Item2, key.Item3, key.Item1);
			if (keybinding == null) {
				Logger.LogError($"Failed to add or get key binding for {id}");
				return;
			}

			keybinding.AddListener(key.Item4);
		}

		/// <summary>
		/// Clears all key bindings defined in the Keys array.
		/// </summary>
		public static void Clear() {
			foreach (var key in Keys) {
				var keybinding = Keybinding.GetKeyBinding(key.Item2, key.Item1);
				keybinding.RemoveListener(key.Item4);
				if (keybinding.GetListenerCount() == 0)
					Keybinding.RemoveKeyBinding(keybinding.GetId(), keybinding.GetCategory());
			}
		}
	}
}