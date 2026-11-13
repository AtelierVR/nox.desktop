using System.Linq;
using Nox.Avatars;
using Nox.Avatars.Parameters;
using Nox.CCK;

namespace Nox.Desktop.Runtime {
	/// <summary>
	/// Eye height of the avatar, exposed as the <c>eye_height</c> ability.
	/// <para>
	/// The parameter is resolved when an avatar is attached (see <c>AvatarLoaderConnector</c>), and again when
	/// the avatar descriptor announces a module or when its parameter module announces a parameter: nothing is
	/// looked up per call, since the abilities are read every frame (see the scene follow overlay).
	/// </para>
	/// </summary>
	public partial class DesktopController {
		/// <summary>Name of the parameter carrying the eye height above the feet (see <c>ScaleAvatarModule</c>).</summary>
		private const string EyeHeightParameterName = "Height/Eye";

		/// <summary>Parameter module of the loaded avatar, the owner of the eye height parameter.</summary>
		private IParameterModule _eyeParameterModule;

		/// <summary>
		/// Eye height parameter of the loaded avatar. Only the reference is cached: the value follows the scale
		/// of the avatar, so it is read live.
		/// </summary>
		private IParameter _eyeHeightParameter;

		/// <summary>
		/// Height of the avatar eyes above its feet: where the player camera is, and what the scene follow
		/// overlay pivots at. Falls back to the collider height while no avatar reports its eye height.
		/// </summary>
		private float EyeHeight
			=> _eyeHeightParameter != null
				? _eyeHeightParameter.Value.ToFloat()
				: player ? player.Height : 0f;

		/// <summary>Descriptor of the loaded avatar: the avatar the controller already holds owns it.</summary>
		private IAvatarDescriptor Descriptor
			=> GetAvatar()?.Descriptor;

		/// <summary>
		/// Reads the avatar and binds what carries the eye height, then follows the modules of its descriptor and
		/// the parameters of the module. Called when an avatar is attached, and once when the proxy starts.
		/// </summary>
		public void ResolveEyes() {
			var descriptor = Descriptor;
			if (descriptor == null) {
				UnbindEyes();
				return;
			}

			// Taken again rather than tracked: the listeners are unique per descriptor, so releasing them first is
			// what keeps a re-resolution from registering them twice.
			descriptor.OnModuleRegistered.RemoveListener(OnModuleRegistered);
			descriptor.OnModuleRegistered.AddListener(OnModuleRegistered);
			descriptor.OnModuleUnregistered.RemoveListener(OnModuleUnregistered);
			descriptor.OnModuleUnregistered.AddListener(OnModuleUnregistered);

			BindEyes();
		}

		/// <summary>Binds the parameter module of the avatar and the eye height parameter it carries.</summary>
		private void BindEyes() {
			var module = Descriptor?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (module == _eyeParameterModule) {
				ResolveEyeHeightParameter();
				return;
			}

			UnbindEyes();

			_eyeParameterModule = module;
			if (_eyeParameterModule == null)
				return;

			_eyeParameterModule.OnRegistred.AddListener(OnParameterRegistered);
			_eyeParameterModule.OnUnRegistred.AddListener(OnParameterUnregistered);

			ResolveEyeHeightParameter();
		}

		/// <summary>A module joined the avatar: the parameter module it carries may be the one to read.</summary>
		private void OnModuleRegistered(IAvatarModule module) {
			if (module is IParameterModule)
				BindEyes();
		}

		/// <summary>A module left the avatar: the cached module must not outlive it.</summary>
		private void OnModuleUnregistered(IAvatarModule module) {
			if (module is IParameterModule parameterModule && parameterModule == _eyeParameterModule)
				UnbindEyes();
		}

		/// <summary>Binds the eye height parameter of the current module, if the module exposes it.</summary>
		private void ResolveEyeHeightParameter()
			=> _eyeHeightParameter = _eyeParameterModule?.GetParameter(EyeHeightParameterName);

		/// <summary>A parameter joined the module: the eye height may be one of them.</summary>
		private void OnParameterRegistered(IParameter parameter) {
			if (parameter != null && parameter.Name == EyeHeightParameterName)
				_eyeHeightParameter = parameter;
		}

		/// <summary>A parameter left the module: the cached reference must not outlive it.</summary>
		private void OnParameterUnregistered(IParameter parameter) {
			if (parameter == null || _eyeHeightParameter == null || _eyeHeightParameter.Equals(parameter))
				ResolveEyeHeightParameter();
		}

		/// <summary>Drops the parameter module and its listeners.</summary>
		private void UnbindEyes() {
			if (_eyeParameterModule == null)
				return;

			_eyeParameterModule.OnRegistred.RemoveListener(OnParameterRegistered);
			_eyeParameterModule.OnUnRegistred.RemoveListener(OnParameterUnregistered);
			_eyeParameterModule = null;

			ResolveEyeHeightParameter();
		}

		/// <summary>Stops following the avatar, its modules and its parameters.</summary>
		private void DisposeEyes() {
			var descriptor = Descriptor;
			if (descriptor != null) {
				descriptor.OnModuleRegistered.RemoveListener(OnModuleRegistered);
				descriptor.OnModuleUnregistered.RemoveListener(OnModuleUnregistered);
			}

			UnbindEyes();
		}
	}
}
