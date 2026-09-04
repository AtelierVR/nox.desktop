using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Avatars;
using Nox.Avatars.Parameters;
using Nox.Avatars.Players;
using Nox.Avatars.Scale;
using Nox.CCK.Avatars;
using Nox.CCK.Mods.Events;
using Nox.CCK.Utils;
using Nox.Desktop.Runtime;
using Nox.Users;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Desktop.Connectors
{
	[RequireComponent(typeof(DesktopController))]
	public class AvatarLoaderConnector : MonoBehaviour
	{
		public DesktopPlayer player;

		private IRuntimeAvatar _runtime;
		private CancellationTokenSource _context;
		private EventSubscription _onUserUpdate;
		private Dictionary<string, object> _parameters;

		private void Awake()
			=> _parameters = new Dictionary<string, object>
			{
				["source"] = GetComponent<DesktopController>(),
				["desktop"] = true,
				["local"] = true
			};

		public void StartUserTracking()
		{
			_onUserUpdate = Client.CoreAPI.EventAPI.Subscribe("user_update", OnUserUpdate);
		}

		public void Dispose()
		{
			if (_onUserUpdate != null)
			{
				Client.CoreAPI.EventAPI.Unsubscribe(_onUserUpdate);
				_onUserUpdate = null;
			}
			_context?.Cancel();
			_context?.Dispose();
			_context = null;
			_runtime?.Dispose();
			_runtime = null;
		}

		public IRuntimeAvatar GetAvatar()
			=> _runtime;

		private void OnUserUpdate(EventData context)
		{
			if (!context.TryGet(0, out ICurrentUser user) || user == null)
				return;
			LoadAvatarFromUser(user);
		}

		public void LoadAvatarFromUser(ICurrentUser user)
		{
			if (user?.Avatar.IsValid() != true)
				return;
			SetAvatar(user.Avatar).Forget();
		}

		public async UniTask<bool> SetAvatar(IRuntimeAvatar runtime)
		{
			Logger.LogDebug("Setting avatar for DesktopController");

			if (!this || !gameObject)
			{
				Logger.LogError("AvatarLoaderConnector has been destroyed, cannot set avatar");
				return false;
			}

			if (runtime == _runtime)
				return true;

			var old = _runtime;
			_runtime = runtime;

			if (_runtime == null)
			{
				Logger.LogWarning("Setting avatar to null, removing current avatar.");
				_runtime = old;
				return false;
			}

			var descriptor = _runtime.Descriptor;
			if (descriptor == null)
			{
				Logger.LogError("Avatar descriptor is null, cannot set avatar.");
				_runtime = old;
				return false;
			}

			var root = descriptor.Anchor;
			if (!root)
			{
				Logger.LogError("Avatar descriptor root is null, cannot set avatar.");
				_runtime = old;
				return false;
			}

			root.name += $" {runtime.Identifier.ToString()} Desktop";

			if (old != null)
				await old.Dispose();

			if (!this)
				return false;

			Logger.LogDebug($"Attaching avatar to {runtime.Descriptor}", runtime.Descriptor.Anchor);
			root.transform.SetParent(transform, false);
			root.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

			var scaleModule = _runtime.Descriptor.GetModules<IScaleAvatarModule>().FirstOrDefault();
			player.minMaxHeight = new Vector2(player.minMaxHeight.x, scaleModule?.Height ?? 1.7f);

			var parameterModule = _runtime.Descriptor
				?.GetModules<IParameterModule>()
				.FirstOrDefault();

			if (parameterModule == null)
			{
				Logger.LogWarning("Avatar has no parameter module, cannot configure tracking parameters.");
				root.SetActive(true);
				Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _runtime);
				return true;
			}

			var animator = _runtime.Descriptor?.Animator;
			if (animator && !animator.runtimeAnimatorController)
			{
				Logger.LogDebug("Waiting for Animator to be ready...");
				await UniTask.WaitUntil(() => animator.runtimeAnimatorController);
			}

			var parameters = parameterModule.GetParameters() ?? Array.Empty<IParameter>();
			foreach (var param in parameters)
				switch (param.GetName())
				{
					case "rig/ik/head/target":
					case "tracking/left_hand/active":
					case "tracking/right_hand/active":
					case "tracking/left_foot/active":
					case "tracking/right_foot/active":
					case "tracking/right_toes/active":
					case "tracking/left_toes/active":
						param.Set(false);
						break;
					case "rig/ik/spine/position_weight":
					case "rig/ik/spine/hint_weight":
						param.Set(0f);
						break;
					case "tracking/head/active":
					case "IsLocal":
						param.Set(true);
						break;
				}

			root.SetActive(true);
			Client.CoreAPI.EventAPI.Emit("controller_avatar_changed", this, _runtime);
			return true;
		}

		public async UniTask<IRuntimeAvatar> SetAvatar(Identifier identifier, Action<string, float> onProgress = null, bool forceReload = false)
		{
			Logger.LogDebug($"Loading avatar for identifier {identifier.ToString() ?? "null"}");

			if (!this || !gameObject)
				return null;

			var playerAvatar = Client.SessionAPI.TryGet(Client.SessionAPI.Current, out var session)
				? session.LocalPlayer as ILocalPlayerAvatar
				: null;

			if (!identifier.IsValid())
			{
				Logger.LogWarning($"Invalid avatar identifier: {identifier.ToString() ?? "null"}");
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new NullReferenceException("Invalid avatar identifier"));
				return null;
			}

			if (!forceReload && _runtime.Arguments.ContainsKey("error") && identifier.Equals(_runtime.Identifier))
			{
				Logger.LogDebug("Avatar identifier matches current avatar, no need to load.");
				if (playerAvatar != null)
					await playerAvatar.OnAvatarReady();
				return _runtime;
			}

			_context?.Cancel();
			_context = new CancellationTokenSource();

			var version = identifier.GetVersion();
			if (version == ushort.MaxValue)
			{
				var avatarData = await Client.AvatarAPI.Fetch(identifier);
				version = avatarData.Release.Value;
			}

			var req = new AssetSearchRequest
			{
				Engines = new[] { EngineExtensions.CurrentEngine.GetEngineName() },
				Platforms = new[] { PlatformExtensions.CurrentPlatform.GetPlatformName() },
				Versions = new[] { version },
				Limit = 1
			};

			var asset = (await Client.AvatarAPI.SearchAssets(identifier, req)
					.AttachExternalCancellation(_context.Token))
				.Items.FirstOrDefault();

			if (_context.IsCancellationRequested)
				return null;

			if (asset == null)
			{
				Logger.LogWarning($"Avatar asset not found for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_parameters);
				err.Identifier = identifier;
				err.Arguments.Add("error", new[] { "asset_not_found" });
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new NullReferenceException("Avatar asset not found"));
				return null;
			}

			if (!Client.AvatarAPI.HasInCache(asset.Hash))
			{
				var download = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => onProgress?.Invoke($"Downloading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				await download.Start();
				if (_context.IsCancellationRequested)
					return null;
			}

			var avatar = await Client.AvatarAPI.LoadFromCache(
				asset.Hash,
				_parameters,
				progress: p => onProgress?.Invoke($"Loading avatar {identifier.ToString()}", p),
				token: _context.Token
			);
			if (_context.IsCancellationRequested)
				return null;

			if (avatar == null && Client.AvatarAPI.HasInCache(asset.Hash))
			{
				Logger.LogWarning($"Corrupt cache entry for avatar {identifier.ToString()}, re-downloading...");
				Client.AvatarAPI.RemoveFromCache(asset.Hash);
				var reDownload = Client.AvatarAPI.DownloadToCache(
					asset.Url,
					hash: asset.Hash,
					progress: p => onProgress?.Invoke($"Re-downloading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				await reDownload.Start();
				if (_context.IsCancellationRequested)
					return null;
				avatar = await Client.AvatarAPI.LoadFromCache(
					asset.Hash,
					_parameters,
					progress: p => onProgress?.Invoke($"Loading avatar {identifier.ToString()}", p),
					token: _context.Token
				);
				if (_context.IsCancellationRequested)
					return null;
			}

			if (avatar == null)
			{
				Logger.LogError($"Failed to load avatar from cache for identifier {identifier.ToString()}");
				var err = await Client.AvatarAPI.LoadError(_parameters);
				err.Identifier = identifier;
				err.Arguments.Add("error", new[] { "load_failed" });
				await SetAvatar(err);
				if (playerAvatar != null)
					await playerAvatar.OnAvatarFailed(new Exception("Failed to load avatar from cache"));
				return null;
			}

			Logger.LogDebug($"Avatar loaded: {identifier.ToString()}");
			avatar.Identifier = identifier;
			await SetAvatar(avatar);
			if (playerAvatar != null)
				await playerAvatar.OnAvatarReady();
			return avatar;
		}

		public async UniTask<IRuntimeAvatar> ReloadAvatar(Action<string, float> onProgress = null)
		{
			var identifier = _runtime?.Identifier ?? Identifier.Invalid;
			if (!identifier.IsValid())
			{
				Logger.LogWarning("Cannot reload avatar: current avatar identifier is invalid.");
				return null;
			}

			return await SetAvatar(identifier, onProgress, true);
		}
	}
}
