using Nox.Avatars;
using Nox.Avatars.Camera;
using Nox.Avatars.Parameters;
using Nox.CCK;
using Nox.CCK.Utils;
using Nox.Desktop.Runtime;
using UnityEngine;

namespace Nox.Desktop.Connectors {
	/// <summary>
	/// Keeps the desktop player in sync with the avatar: parameters, and the camera.
	/// <para>
	/// Runs after every other script: the last writer of the head bone is the rig
	/// (<c>RigManagerModule.LateUpdate</c> → <c>RigBuilderRig.LateUpdate</c>), and placing the camera before it
	/// would leave it one frame behind for everything that reads it during the frame.
	/// </para>
	/// </summary>
	[DefaultExecutionOrder(1000)]
	public class AvatarSyncConnector : MonoBehaviour {
		public DesktopPlayer player;
		public AvatarLoaderConnector avatarLoader;

		private const string GroundedName = "Grounded";

		/// <summary>
		/// Locomotion vector. Writing it also writes <c>Velocity/X</c>, <c>Velocity/Y</c>, <c>Velocity/Z</c> and
		/// <c>Velocity/Magnitude</c> (see <c>Vector3ParameterProvider</c>): its components are never set on their own.
		/// </summary>
		private const string VelocityName = "Velocity";

		private const string HeadPositionName = "Tracking/Head/Position";
		private const string HeadRotationName = "Tracking/Head/Rotation";
		private const string HeightName       = "Height";
		private const string HeightEyeName    = "Height/Eye";

		/// <summary>Descriptor of the followed avatar: its module events drive the binding below.</summary>
		private IAvatarDescriptor _descriptor;

		/// <summary>Camera module of the avatar, and the head bone it exposes: the point the camera follows.</summary>
		private ICameraModule _cameraModule;
		private Transform _cameraAnchor;

		/// <summary>Parameter module of the avatar: its parameter events fill the slots below.</summary>
		private IParameterModule _parameterModule;

		/// <summary>
		/// Watched parameters, taken from the registration events of the parameter module (null while the avatar
		/// does not carry them). The frame loop only touches these, instead of walking every parameter of the
		/// avatar and comparing its name on each frame.
		/// </summary>
		private IParameter _grounded;
		private IParameter _velocity;
		private IParameter _headPosition;
		private IParameter _headRotation;
		private IParameter _height;

		/// <summary>
		/// Offset of the camera in the frame of the head, in world units: the point the camera is placed on. Cached
		/// because <see cref="Nox.CCK.Utils.CameraChop"/> scales the head bone to zero while the player camera
		/// renders, which makes <see cref="ICameraModule.GetOffset"/> return zero in that window.
		/// </summary>
		private Vector3 _cameraOffset;

		private void Start()
			=> BindAvatar();

		private void LateUpdate() {
			RefreshCameraOffset();
			SynchronizeCamera();

			// The head pose has no event to follow (the rig writes it every frame), the locomotion has one
			// (see OnGroundedChanged and OnVelocityChanged): only the head is read here.
			SynchronizeHeadParameters();
			SynchronizeHeight();
		}

		private void OnEnable() {
			RenderOrder.Subscribe(OnBeginCameraRendering, RenderPhase.Input);

			if (player)
				player.HasViewSync = true;

			if (avatarLoader)
				avatarLoader.AvatarChanged.AddListener(OnAvatarChanged);

			SubscribePlayer();

			// OnDisable dropped the binding: a re-enabled connector must take the avatar back, since no avatar
			// change is announced for it.
			BindAvatar();
		}

		private void OnDisable() {
			RenderOrder.Unsubscribe(OnBeginCameraRendering);

			if (player)
				player.HasViewSync = false;

			if (avatarLoader)
				avatarLoader.AvatarChanged.RemoveListener(OnAvatarChanged);

			UnsubscribePlayer();
			UnbindAvatar();
		}

		/// <summary>Follows the locomotion of the player from its own announcements, not from a frame poll.</summary>
		private void SubscribePlayer() {
			if (!player)
				return;

			player.OnGroundedChanged.RemoveListener(WriteGrounded);
			player.OnGroundedChanged.AddListener(WriteGrounded);
			player.OnVelocityChanged.RemoveListener(WriteVelocity);
			player.OnVelocityChanged.AddListener(WriteVelocity);
		}

		private void UnsubscribePlayer() {
			if (!player)
				return;

			player.OnGroundedChanged.RemoveListener(WriteGrounded);
			player.OnVelocityChanged.RemoveListener(WriteVelocity);
		}

		/// <summary>Another avatar was loaded (or reloaded): its modules replace the ones that were followed.</summary>
		private void OnAvatarChanged(IRuntimeAvatar avatar)
			=> BindAvatar();

		/// <summary>
		/// Follows the descriptor of the current avatar: the modules it announces are bound as they arrive and
		/// released as they leave, so nothing is resolved per frame.
		/// </summary>
		private void BindAvatar() {
			var descriptor = avatarLoader?.GetAvatar()?.Descriptor;
			if (ReferenceEquals(descriptor, _descriptor))
				return;

			UnbindAvatar();

			_descriptor = descriptor;
			if (_descriptor == null)
				return;

			// Released first rather than tracked: the listeners are unique per descriptor, so this is harmless and
			// covers a descriptor that was already followed.
			_descriptor.OnModuleRegistered.RemoveListener(OnModuleRegistered);
			_descriptor.OnModuleRegistered.AddListener(OnModuleRegistered);
			_descriptor.OnModuleUnregistered.RemoveListener(OnModuleUnregistered);
			_descriptor.OnModuleUnregistered.AddListener(OnModuleUnregistered);

			// The modules are usually already there (only their changes are announced): seed the binding with what
			// the descriptor carries, the events keep it up to date afterwards.
			foreach (var module in _descriptor.GetModules<IAvatarModule>())
				OnModuleRegistered(module);
		}

		/// <summary>Stops following the avatar, its modules and its parameters.</summary>
		private void UnbindAvatar() {
			var descriptor = _descriptor;
			if (descriptor != null) {
				descriptor.OnModuleRegistered.RemoveListener(OnModuleRegistered);
				descriptor.OnModuleUnregistered.RemoveListener(OnModuleUnregistered);
			}

			_descriptor = null;

			UnbindParameters();

			_cameraModule = null;
			_cameraAnchor = null;
			_cameraOffset = Vector3.zero;
		}

		/// <summary>A module joined the avatar: the camera and the parameters are the ones to follow.</summary>
		private void OnModuleRegistered(IAvatarModule module) {
			if (module is ICameraModule cameraModule) {
				_cameraModule = cameraModule;
				_cameraAnchor = cameraModule.GetAnchor();
				_cameraOffset = Vector3.zero;
				RefreshCameraOffset();
			}

			if (module is IParameterModule parameterModule)
				BindParameters(parameterModule);
		}

		/// <summary>A module left the avatar: the cached references must not outlive it.</summary>
		private void OnModuleUnregistered(IAvatarModule module) {
			if (ReferenceEquals(module, _cameraModule)) {
				_cameraModule = null;
				_cameraAnchor = null;
				_cameraOffset = Vector3.zero;
				return;
			}

			if (ReferenceEquals(module, _parameterModule))
				UnbindParameters();
		}

		/// <summary>Binds the parameter module and follows the parameters it registers.</summary>
		private void BindParameters(IParameterModule module) {
			if (ReferenceEquals(module, _parameterModule)) {
				SeedParameters();
				return;
			}

			UnbindParameters();

			_parameterModule = module;
			module.OnRegistred.RemoveListener(OnParameterRegistered);
			module.OnRegistred.AddListener(OnParameterRegistered);
			module.OnUnRegistred.RemoveListener(OnParameterUnregistered);
			module.OnUnRegistred.AddListener(OnParameterUnregistered);

			SeedParameters();
		}

		/// <summary>Drops the parameter module, its listeners and every watched parameter.</summary>
		private void UnbindParameters() {
			var module = _parameterModule;
			if (module != null) {
				module.OnRegistred.RemoveListener(OnParameterRegistered);
				module.OnUnRegistred.RemoveListener(OnParameterUnregistered);
			}

			_parameterModule = null;

			_grounded = null;
			_velocity = null;
			_headPosition = null;
			_headRotation = null;
			_height = null;
		}

		/// <summary>
		/// Takes the watched parameters the module already carries. A module registers its parameters before or
		/// after this binding: the events cover the other direction.
		/// </summary>
		private void SeedParameters() {
			var module = _parameterModule;
			if (module == null)
				return;

			_grounded = module.GetParameter(GroundedName);
			_velocity = module.GetParameter(VelocityName);
			_headPosition = module.GetParameter(HeadPositionName);
			_headRotation = module.GetParameter(HeadRotationName);

			ResolveHeight();

			// A parameter that just joined has no value from this connector yet: give it one now instead of
			// waiting for the next change.
			WriteGrounded(player && player.IsGrounded());
			WriteVelocity(player ? player.LocomotionVelocity : Vector3.zero);
			SynchronizeHeadParameters();
		}

		/// <summary>Eye height of the avatar: the first of the two names the parameter module carries.</summary>
		private void ResolveHeight()
			=> _height = _parameterModule?.GetParameter(HeightName)
				?? _parameterModule?.GetParameter(HeightEyeName);

		/// <summary>A parameter joined the module: kept only when this connector drives it.</summary>
		private void OnParameterRegistered(IParameter parameter) {
			if (parameter == null)
				return;

			switch (parameter.Name) {
				case GroundedName:
					_grounded = parameter;
					WriteGrounded(player && player.IsGrounded());
					break;

				case VelocityName:
					_velocity = parameter;
					WriteVelocity(player ? player.LocomotionVelocity : Vector3.zero);
					break;

				case HeadPositionName:
					_headPosition = parameter;
					SynchronizeHeadParameters();
					break;

				case HeadRotationName:
					_headRotation = parameter;
					SynchronizeHeadParameters();
					break;

				case HeightName:
				case HeightEyeName:
					ResolveHeight();
					break;

				default:
					return;
			}
		}

		/// <summary>A parameter left the module: the cached reference must not outlive it.</summary>
		private void OnParameterUnregistered(IParameter parameter) {
			if (parameter == null)
				return;

			if (ReferenceEquals(_grounded, parameter)) _grounded = null;
			if (ReferenceEquals(_velocity, parameter)) _velocity = null;
			if (ReferenceEquals(_headPosition, parameter)) _headPosition = null;
			if (ReferenceEquals(_headRotation, parameter)) _headRotation = null;

			if (ReferenceEquals(_height, parameter))
				ResolveHeight();
		}

		/// <summary>
		/// Re-reads the world offset of the camera from the head, as long as the head is readable: while
		/// <see cref="Nox.CCK.Utils.CameraChop"/> hides it the bone scale is zero, so the module answers
		/// <see cref="Vector3.zero"/>. The last valid offset is kept for the render pass.
		/// </summary>
		private void RefreshCameraOffset() {
			if (_cameraModule == null || _cameraAnchor == null)
				return;

			if (_cameraAnchor.lossyScale.y <= 0.0001f)
				return;

			_cameraOffset = _cameraModule.GetOffset();
		}

		/// <summary>
		/// Puts the camera on the point of the camera module one last time, right before the frame is drawn. The
		/// camera is the head part of the desktop controller (see <c>DesktopController.Parts</c>), so the avatar
		/// rig writes into it after <see cref="LateUpdate"/>: placing it there is what the player actually sees.
		/// </summary>
		private void OnBeginCameraRendering(Camera camera) {
			if (camera == null || player == null || camera != player.headCamera)
				return;

			SynchronizeCamera();

			// The pose of the view is now the one the frame is drawn with: everything that aims with it can update.
			player.NotifyViewPlaced();
		}

		/// <summary>The player landed or left the ground: only the grounded parameter changed.</summary>
		private void WriteGrounded(bool grounded) {
			if (_grounded == null)
				return;

			if ((bool)_grounded.Value == grounded)
				return;

			_grounded.Value = grounded;
		}

		/// <summary>
		/// Velocity of the body announced by the player, expressed in the frame of the view so a locomotion blend
		/// tree makes the legs follow the player. Writing the vector also writes <c>Velocity/X</c>, <c>/Y</c>,
		/// <c>/Z</c> and <c>/Magnitude</c> (see <c>Vector3ParameterProvider</c>).
		/// </summary>
		private void WriteVelocity(Vector3 worldVelocity) {
			if (_velocity == null)
				return;

			var localVelocity = transform.InverseTransformDirection(worldVelocity);
			if (Vector3.Distance(_velocity.Value.ToVector3(), localVelocity) < 0.0001f)
				return;

			_velocity.Value = localVelocity;
		}

		/// <summary>
		/// Head pose of the view. Unlike the locomotion it has no event to follow (the rig writes the head every
		/// frame), so it is read from <see cref="LateUpdate"/>, once the camera of the frame is placed.
		/// </summary>
		private void SynchronizeHeadParameters() {
			if (!player)
				return;

			var head = player.headCamera;
			if (!head)
				return;

			if (_headPosition != null) {
				var position = head.transform.position;
				if (Vector3.Distance(_headPosition.Value.ToVector3(), position) >= 0.001f)
					_headPosition.Value = position;
			}

			if (_headRotation != null) {
				var rotation = head.transform.rotation;
				if (Quaternion.Angle(_headRotation.Value.ToQuaternion(), rotation) >= 0.001f)
					_headRotation.Value = rotation;
			}
		}

		/// <summary>
		/// Maximum height of the player: the height parameter of the avatar when it carries one, the head above the
		/// player otherwise.
		/// </summary>
		private void SynchronizeHeight() {
			if (!player)
				return;

			var head = player.headCamera;

			float maxHeight;
			if (_height != null)
				maxHeight = _height.Value.ToFloat();
			else if (head)
				maxHeight = head.transform.position.y - player.transform.position.y;
			else
				maxHeight = 1.7f;

			if (!Mathf.Approximately(player.minMaxHeight.y, maxHeight))
				player.minMaxHeight = new Vector2(player.minMaxHeight.x, maxHeight);
		}

		/// <summary>
		/// Puts the camera on the point the camera module of the avatar describes: the head bone plus its offset,
		/// applied in the frame of the head — the same point the magenta circle of the scale gizmo marks.
		/// <para>
		/// Called from <see cref="LateUpdate"/> and again from <see cref="OnBeginCameraRendering"/>: the head bone
		/// is written by the rig after <see cref="LateUpdate"/>, so the render is the only moment where the pose
		/// of the frame is final.
		/// </para>
		/// <para>
		/// The module, the head bone and the offset are read when an avatar is loaded (see <see cref="Start"/> and
		/// <see cref="RefreshCameraOffset"/>) but the position itself is rebuilt here every frame, from the pose of
		/// the frame: caching it would put the camera one frame behind the head.
		/// </para>
		/// </summary>
		private void SynchronizeCamera() {
			// A destroyed anchor reads as null: the avatar is gone, so there is nothing to follow until the next
			// one is loaded (and BindAvatar is called again).
			if (!player || !player.headCamera || _cameraModule == null || _cameraAnchor == null)
				return;

			// TransformDirection only uses the rotation: the zero scale CameraChop writes on the head during the
			// render does not affect the offset, and the position is unaffected as well.
			player.headCamera.transform.position = _cameraAnchor.position + _cameraAnchor.TransformDirection(_cameraOffset);
		}
	}
}
