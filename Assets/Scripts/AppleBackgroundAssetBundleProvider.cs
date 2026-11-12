
using System;
using System.IO;
using UnityEngine;
using System.ComponentModel;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
using Apple.BackgroundAssets;
using Microsoft.Win32.SafeHandles;
#endif

namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Custom Addressables AssetBundleProvider that loads bundles from downloaded Apple Background Assets packs
	/// on Apple platforms, and falls back to the standard AssetBundleProvider in the Unity Editor.
	/// </summary>
	[DisplayName("Apple Background Assets Bundle Provider")]
	public class AppleBackgroundAssetBundleProvider : AssetBundleProvider
	{
		private static int s_pendingUnloads;

		/// <summary>
		/// Количество бандлов, которые сейчас выгружаются (UnloadAsync ещё не завершён и файл пака открыт).
		/// Перед удалением пака стоит дождаться нуля.
		/// </summary>
		public static int PendingUnloadCount => s_pendingUnloads;
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
        private sealed class BackgroundAssetBundleResource : IAssetBundleResource
        {
            private AssetBundle _assetBundle;
            private FileStream _stream;
            private AssetBundleCreateRequest _loadRequest;
            private ProvideHandle _provideHandle;
            private bool _completed;

            public void Start(ProvideHandle provideHandle)
            {
                _provideHandle = provideHandle;
                string bundleFileName = Path.GetFileName(provideHandle.Location.InternalId);

                try
                {
                    UnityEngine.Debug.Log($"[BAProvider] OpenFile('{bundleFileName}')");
                    SafeFileHandle fileHandle = AssetPackManager.OpenFile(bundleFileName, null);
                    _stream = new FileStream(fileHandle, FileAccess.Read, 4096, false);
                    UnityEngine.Debug.Log($"[BAProvider] Файл открыт, {_stream.Length / (1024f * 1024f):0.0} MB, LoadFromStreamAsync...");
                    _loadRequest = AssetBundle.LoadFromStreamAsync(_stream);

                    provideHandle.SetProgressCallback(() => _loadRequest != null ? _loadRequest.progress : 0f);
                    provideHandle.SetWaitForCompletionCallback(WaitForCompletion);
                    _loadRequest.completed += OnLoadCompleted;
                }
                catch (Exception ex)
                {
                    LogManager.LogException("BAProvider", ex, $"Не удалось открыть '{bundleFileName}' из пака");
                    DisposeStream();
                    _completed = true;
                    provideHandle.Complete<BackgroundAssetBundleResource>(null, false, ex);
                }
            }

            private void OnLoadCompleted(UnityEngine.AsyncOperation op)
            {
                if (_completed)
                    return;

                _completed = true;
                _assetBundle = _loadRequest != null ? _loadRequest.assetBundle : null;
                _loadRequest = null;

                if (_assetBundle != null)
                {
                    UnityEngine.Debug.Log($"[BAProvider] Бандл загружен: {_assetBundle.name}");
                    _provideHandle.Complete(this, true, null);
                }
                else
                {
                    UnityEngine.Debug.LogError($"[BAProvider] LoadFromStreamAsync вернул null (файл повреждён или бандл из другой сборки): {_provideHandle.Location?.InternalId}");
                    DisposeStream();
                    string path = _provideHandle.Location?.InternalId ?? "<null>";
                    _provideHandle.Complete<BackgroundAssetBundleResource>(
                        null,
                        false,
                        new FileLoadException($"Failed to load AssetBundle from Background Assets: '{path}'."));
                }
            }

            private bool WaitForCompletion()
            {
                if (_loadRequest != null && !_loadRequest.isDone)
                {
                    UnityEngine.Debug.Log("[BAProvider] WaitForCompletion: синхронное ожидание загрузки бандла");
                    _assetBundle = _loadRequest.assetBundle;
                    OnLoadCompleted(_loadRequest);
                }

                return _completed;
            }

            public AssetBundle GetAssetBundle()
            {
                return _assetBundle;
            }

            public void Unload()
            {
                if (_assetBundle != null)
                {
                    UnityEngine.Debug.Log($"[BAProvider] Выгрузка бандла: {_assetBundle.name}");
                    AssetBundleUnloadOperation unloadOp = _assetBundle.UnloadAsync(true);
                    _assetBundle = null;
                    if (unloadOp != null)
                    {
                        s_pendingUnloads++;
                        unloadOp.completed += _ =>
                        {
                            DisposeStream();
                            s_pendingUnloads--;
                            UnityEngine.Debug.Log($"[BAProvider] Бандл выгружен, файл пака закрыт (в очереди выгрузки: {s_pendingUnloads})");
                        };
                        return;
                    }
                }

                DisposeStream();
                UnityEngine.Debug.Log("[BAProvider] Unload: файл пака закрыт");
            }

            private void DisposeStream()
            {
                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                }
            }
        }
#endif

		public override void Provide(ProvideHandle providerInterface)
		{
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
            var resource = new BackgroundAssetBundleResource();
            resource.Start(providerInterface);
#else
			base.Provide(providerInterface);
#endif
		}

		public override void Release(IResourceLocation location, object asset)
		{
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
            if (asset is BackgroundAssetBundleResource resource)
            {
                resource.Unload();
                return;
            }
#endif
			base.Release(location, asset);
		}
	}
}
