using System;
using Nox.CCK.Audio;
using Nox.Desktop.Runtime;
using Nox.Audio;
using Nox.Audio.Players;
using Nox.CCK.Mods.Events;
using UnityEngine;

namespace Nox.Desktop.Connectors {
	/// <summary>
	/// Connector placed on the Desktop controller prefab.
	/// Owns the microphone lifecycle and binds it to the session's local player.
	/// <para>
	/// Call <see cref="Bind"/> when a session becomes current and
	/// <see cref="Unbind"/> when it is left or the controller is disposed.
	/// </para>
	/// </summary>
	public class MicrophoneConnector : MonoBehaviour {
		[SerializeField]
		[Tooltip("Speak mode applied when bound and not muted.")]
		private SpeakMode _initialSpeak = SpeakMode.Normal;

		private IMicrophone       _microphone;
		private ILocalPlayerVoice _voice;

		private static IMicrophoneAPI MicrophoneAPI
			=> Client.CoreAPI.ModAPI
				.GetMod("audio")
				?.GetInstance<IMicrophoneAPI>();
		
		private EventSubscription[] _events = Array.Empty<EventSubscription>();

		// ── Public API ────────────────────────────────────────────────────────

		/// <summary>Bind this connector to a session's local player voice.</summary>
		public void Bind(ILocalPlayerVoice localVoice) {
			Unbind();

			_voice = localVoice;
			StartMicrophone();

			_events = new[] {
				Client.CoreAPI.EventAPI.Subscribe("audio.current_microphone_changed", OnCurrentChanged),
				Client.CoreAPI.EventAPI.Subscribe("audio.microphone_mute_changed", OnMuteChanged)
			};
		}

		/// <summary>Detach from the current local player voice and stop the microphone.</summary>
		public void Unbind() {
			if (_voice == null) return;

			foreach (var ev in _events)
				Client.CoreAPI.EventAPI.Unsubscribe(ev);

			_voice.Speak = SpeakMode.Muted;
			_voice.Audio = null;
			_voice       = null;

			StopMicrophone();
		}

		// ── Microphone lifecycle ──────────────────────────────────────────────
		
		private void StartMicrophone() {
			if (_voice == null)
				return;

			StopMicrophone();

			_microphone ??= MicrophoneAPI.Current
				?? MicrophoneAPI.Default;

			if (_microphone == null)
				return;

			_voice.Speak = MicrophoneAPI.Current.IsMuted
				? SpeakMode.Muted
				: _initialSpeak;

			var clip = _microphone.Start("voice");
			_voice.Audio = clip
				? new CapturedMicrophone(clip, _microphone)
				: null;
		}

		private void StopMicrophone() {
			_microphone?.Stop("voice");
			_microphone = null;
		}

		// ── Settings listeners ────────────────────────────────────────────────

		private void OnMuteChanged(EventData context) {
			if (_voice == null)
				return;

			var microphone = !context.TryGet<IMicrophone>(0, out var mic)
				? throw new ArgumentException("Argument 0 is not a Microphone")
				: mic;

			if (microphone != _microphone)
				return;

			var muted = !context.TryGet<bool>(1, out var mut)
				? throw new ArgumentException("Argument 1 is not a bool")
				: mut;

			_voice.Speak = muted
				? SpeakMode.Muted
				: _initialSpeak;
		}

		private void OnCurrentChanged(EventData context) {
			if (_voice == null)
				return;

			StartMicrophone();
		}

		// ── Unity lifecycle ───────────────────────────────────────────────────

		public void OnDestroy()
			=> Unbind();
	}
}
