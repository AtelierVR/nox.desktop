using System;
using Nox.Interactions;
using UnityEngine;

namespace Nox.Desktop.Runtime.Interaction {
	[Serializable]
	public struct DesktopRay {
		public Camera view;
		public LayerMask layers;
		public float max;
		public float min;
		public float clearance;

		public static DesktopRay Default
			=> new() {
				layers = ~0,
				max = 5f,
				min = 0.4f,
				clearance = 0.35f
			};
	}

	[Serializable]
	public struct DesktopGrabInput {
		public GrabInputMode mode;
		public float duration;
		public bool scroll;
		public float step;
		public bool uiGate;

		public static DesktopGrabInput Default
			=> new() {
				duration = 0.1f,
				scroll = true,
				step = 0.25f,
				uiGate = true
			};
	}
}
