using Nox.CCK.Microphone;
using Nox.Desktop.Runtime;
using Nox.Microphone;
using Nox.Microphone.Players;
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
				.GetMod("microphone")
				?.GetInstance<IMicrophoneAPI>();

		// ── Public API ────────────────────────────────────────────────────────

		/// <summary>Bind this connector to a session's local player voice.</summary>
		public void Bind(ILocalPlayerVoice localVoice) {
			Unbind();
			_voice = localVoice;
			StartMic();
			_voice.Speak = MicrophoneSettings.Mute ? SpeakMode.Muted : _initialSpeak;
			MicrophoneSettings.OnMuteChanged.AddListener(OnMuteChanged);
			MicrophoneSettings.OnCurrentMicrophoneChanged.AddListener(OnMicChanged);
		}

		/// <summary>Detach from the current local player voice and stop the microphone.</summary>
		public void Unbind() {
			if (_voice == null) return;
			MicrophoneSettings.OnMuteChanged.RemoveListener(OnMuteChanged);
			MicrophoneSettings.OnCurrentMicrophoneChanged.RemoveListener(OnMicChanged);
			_voice.Speak = SpeakMode.Muted;
			_voice.Audio = null;
			_voice       = null;
			StopMic();
		}

		// ── Microphone lifecycle ──────────────────────────────────────────────

		private void StartMic() {
			_microphone?.Stop("voice");
			var micApi = MicrophoneAPI;
			if (micApi == null) return;

			var name = MicrophoneSettings.CurrentMicrophone;
			_microphone = string.IsNullOrEmpty(name) ? null : micApi.Get(name);
			_microphone ??= micApi.GetCurrent() ?? micApi.GetDefault();

			if (_microphone == null || _voice == null) return;
			var clip = _microphone.Start("voice");
			_voice.Audio = clip != null ? new MicrophoneAudio(clip, _microphone) : null;
		}

		private void StopMic() {
			_microphone?.Stop("voice");
			_microphone = null;
		}

		// ── Settings listeners ────────────────────────────────────────────────

		private void OnMuteChanged(bool muted) {
			if (_voice != null)
				_voice.Speak = muted ? SpeakMode.Muted : _initialSpeak;
		}

		private void OnMicChanged(string _, string __) {
			if (_voice != null) StartMic();
		}

		// ── Unity lifecycle ───────────────────────────────────────────────────

		private void OnDestroy() => Unbind();
	}
}
