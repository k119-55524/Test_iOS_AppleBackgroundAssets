using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
using Apple.BackgroundAssets;
#endif

namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Scene controller for testing Apple Background Assets download, progress reporting,
	/// persistent local storage, and Addressables sprite loading.
	/// </summary>
	public class BackgroundAssetsTestController : MonoBehaviour
	{
		[Header("Identifiers")]
		[Tooltip("ID of the Apple Asset Pack configured in ba-package / App Store Connect.")]
		[SerializeField] private string assetPackId = "test_textures";

		[Tooltip("Addressables label assigned to the test sprites.")]
		[SerializeField] private string addressableLabel = "test_textures";

		[Tooltip("При нажатии «Загрузить» проверять и докачивать новую версию пака, даже если он уже скачан.")]
		[SerializeField] private bool checkForUpdates = true;

		[Header("UI")]
		[SerializeField] private Button downloadButton;
		[SerializeField] private Button removePackButton;
		[SerializeField] private Slider progressSlider;
		[SerializeField] private Text statusText;
		[SerializeField] private Image previewImage;
		[SerializeField] private Button prevButton;
		[SerializeField] private Button nextButton;
		[SerializeField] private Text previewCounterText;

		private readonly List<Sprite> _loadedSprites = new List<Sprite>();
		private AsyncOperationHandle<IList<Sprite>> _addressablesHandle;
		private int _currentIndex;
		private bool _isBusy;

		public IReadOnlyList<Sprite> LoadedSprites => _loadedSprites;

		private void Awake()
		{
			if (downloadButton != null)
				downloadButton.onClick.AddListener(OnDownloadClicked);

			if (removePackButton != null)
				removePackButton.onClick.AddListener(OnRemovePackClicked);

			if (prevButton != null)
				prevButton.onClick.AddListener(ShowPreviousSprite);

			if (nextButton != null)
				nextButton.onClick.AddListener(ShowNextSprite);

			UpdatePreviewDisplay();
		}

		private async void Start()
		{
			await RefreshInitialStateAsync();
		}

		private void OnDestroy()
		{
			if (downloadButton != null)
				downloadButton.onClick.RemoveListener(OnDownloadClicked);

			if (removePackButton != null)
				removePackButton.onClick.RemoveListener(OnRemovePackClicked);

			if (prevButton != null)
				prevButton.onClick.RemoveListener(ShowPreviousSprite);

			if (nextButton != null)
				nextButton.onClick.RemoveListener(ShowNextSprite);

			ReleaseLoadedAssets();
		}

		private async Task RefreshInitialStateAsync()
		{
			SetProgress(0f);

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
            bool isLocal = AssetPackManager.AssetPackIsAvailableLocally(assetPackId);
            if (isLocal)
            {
                SetStatus($"Пак '{assetPackId}' уже скачан локально. Загрузка спрайтов...");
                SetProgress(1f);
                await LoadSpritesFromAddressablesAsync();
            }
            else
            {
                SetStatus($"Пак '{assetPackId}' не скачан. Нажмите «Загрузить».");
            }
#else
			SetStatus("Editor mode: нажмите «Загрузить» для проверки Addressables.");
			await Task.CompletedTask;
#endif
		}

		private async void OnDownloadClicked()
		{
			if (_isBusy)
				return;

			SetBusy(true);
			try
			{
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
                await DownloadAppleAssetPackAsync();
#endif
				await LoadSpritesFromAddressablesAsync();
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
				SetStatus($"Ошибка: {ex.Message}");
			}
			finally
			{
				SetBusy(false);
			}
		}

		private async void OnRemovePackClicked()
		{
			if (_isBusy)
				return;

			SetBusy(true);
			try
			{
				ReleaseLoadedAssets();
				UpdatePreviewDisplay();

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
                SetStatus($"Удаление пака '{assetPackId}'...");
                // Дожидаемся, пока бандлы выгрузятся и закроют файлы пака.
                while (AppleBackgroundAssetBundleProvider.PendingUnloadCount > 0)
                    await Task.Yield();
                await AssetPackManager.RemoveAssetPackAsync(assetPackId);
#endif
				SetProgress(0f);
				SetStatus($"Пак '{assetPackId}' удалён.");
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
				SetStatus($"Ошибка удаления: {ex.Message}");
			}
			finally
			{
				SetBusy(false);
			}
		}

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
        private async Task DownloadAppleAssetPackAsync()
        {
            bool isLocal = AssetPackManager.AssetPackIsAvailableLocally(assetPackId);
            if (isLocal && !checkForUpdates)
            {
                SetProgress(1f);
                SetStatus($"Пак '{assetPackId}' уже скачан.");
                return;
            }

            SetProgress(isLocal ? 1f : 0f);
            SetStatus("Получение манифеста Apple...");
            AssetPackManifest manifest = await AssetPackManager.GetManifestAsync();

            AssetPack assetPack = null;
            foreach (AssetPack pack in manifest.GetAllAssetPacks())
            {
                if (pack.GetId() == assetPackId)
                {
                    assetPack = pack;
                    break;
                }
            }

            if (assetPack == null)
            {
                if (isLocal)
                {
                    SetStatus($"Пак '{assetPackId}' не найден в манифесте сервера, используется локальная копия.");
                    return;
                }
                throw new InvalidOperationException(
                    $"Пак '{assetPackId}' отсутствует в манифесте. Проверьте, что .aar загружен в App Store Connect " +
                    "или запущен ba-serve и на устройстве задан URL Override.");
            }

            Debug.Log($"[BackgroundAssetsTest] Pack '{assetPackId}': version {assetPack.GetVersion()}, " +
                      $"download size {assetPack.GetDownloadSize() / (1024f * 1024f):0.0} MB, local: {isLocal}");

            bool monitoringActive = true;
            _ = MonitorDownloadProgressAsync(() => monitoringActive);

            try
            {
                SetStatus(isLocal
                    ? $"Проверка обновлений пака '{assetPackId}'..."
                    : $"Скачивание пака '{assetPackId}'...");
                await AssetPackManager.EnsureLocalAvailabilityOfAssetPackAsync(assetPack, checkForUpdates);
                SetProgress(1f);
                SetStatus($"Пак '{assetPackId}' доступен локально.");
            }
            finally
            {
                monitoringActive = false;
            }
        }

        private async Task MonitorDownloadProgressAsync(Func<bool> isActive)
        {
            try
            {
                await foreach (AssetPackManager.DownloadStatusUpdate update in AssetPackManager.DownloadStatusUpdatesAsync(assetPackId))
                {
                    if (!isActive() || this == null)
                        break;

                    switch (update)
                    {
                        case AssetPackManager.DownloadStatusUpdate.Began:
                            SetStatus($"Начата загрузка '{assetPackId}'...");
                            break;
                        case AssetPackManager.DownloadStatusUpdate.Paused:
                            SetStatus($"Загрузка '{assetPackId}' на паузе...");
                            break;
                        case AssetPackManager.DownloadStatusUpdate.Downloading downloading:
                            float progress = (float)downloading.Progress;
                            SetProgress(progress);
                            SetStatus($"Скачивание: {progress * 100f:0}%");
                            break;
                        case AssetPackManager.DownloadStatusUpdate.Finished:
                            SetProgress(1f);
                            SetStatus("Скачивание завершено.");
                            return;
                        case AssetPackManager.DownloadStatusUpdate.Failed failed:
                            SetStatus($"Ошибка загрузки: {failed.Error}");
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BackgroundAssetsTest] Progress monitor ended: {ex.Message}");
            }
        }
#endif

		private async Task LoadSpritesFromAddressablesAsync()
		{
			ReleaseLoadedAssets();

			SetStatus($"Загрузка Addressables ({addressableLabel})...");
			_addressablesHandle = Addressables.LoadAssetsAsync<Sprite>(addressableLabel, null);

			while (!_addressablesHandle.IsDone)
			{
#if UNITY_EDITOR || !(UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
				SetProgress(_addressablesHandle.PercentComplete);
#endif
				await Task.Yield();
			}

			if (_addressablesHandle.Status != AsyncOperationStatus.Succeeded)
			{
				SetStatus($"Ошибка Addressables: {_addressablesHandle.OperationException?.Message}");
				return;
			}

			_loadedSprites.AddRange(_addressablesHandle.Result);
			_loadedSprites.Sort((a, b) =>
			{
				int numA = int.TryParse(a != null ? a.name : string.Empty, out int va) ? va : int.MaxValue;
				int numB = int.TryParse(b != null ? b.name : string.Empty, out int vb) ? vb : int.MaxValue;
				return numA.CompareTo(numB);
			});

			_currentIndex = 0;
			UpdatePreviewDisplay();

			SetProgress(1f);
			SetStatus($"Готово! Загружено спрайтов: {_loadedSprites.Count}");
		}

		public void ShowPreviousSprite()
		{
			if (_loadedSprites.Count == 0)
				return;

			_currentIndex = (_currentIndex - 1 + _loadedSprites.Count) % _loadedSprites.Count;
			UpdatePreviewDisplay();
		}

		public void ShowNextSprite()
		{
			if (_loadedSprites.Count == 0)
				return;

			_currentIndex = (_currentIndex + 1) % _loadedSprites.Count;
			UpdatePreviewDisplay();
		}

		private void UpdatePreviewDisplay()
		{
			int count = _loadedSprites.Count;
			if (count == 0)
			{
				_currentIndex = 0;
				if (previewImage != null)
					previewImage.sprite = null;
				if (previewCounterText != null)
					previewCounterText.text = "0 / 0";
			}
			else
			{
				_currentIndex = Mathf.Clamp(_currentIndex, 0, count - 1);
				var sprite = _loadedSprites[_currentIndex];
				if (previewImage != null)
				{
					previewImage.sprite = sprite;
					previewImage.preserveAspect = true;
				}
				if (previewCounterText != null)
				{
					string spriteName = sprite != null ? sprite.name : "?";
					previewCounterText.text = $"{_currentIndex + 1} / {count} ({spriteName})";
				}
			}

			UpdateNavButtons();
		}

		private void UpdateNavButtons()
		{
			bool canBrowse = !_isBusy && _loadedSprites.Count > 1;
			if (prevButton != null)
				prevButton.interactable = canBrowse;
			if (nextButton != null)
				nextButton.interactable = canBrowse;
		}

		private void ReleaseLoadedAssets()
		{
			_loadedSprites.Clear();
			if (_addressablesHandle.IsValid())
			{
				Addressables.Release(_addressablesHandle);
				_addressablesHandle = default;
			}
		}

		private void SetBusy(bool busy)
		{
			_isBusy = busy;
			if (downloadButton != null)
				downloadButton.interactable = !busy;
			if (removePackButton != null)
				removePackButton.interactable = !busy;
			UpdateNavButtons();
		}

		private void SetProgress(float value)
		{
			if (progressSlider != null)
				progressSlider.value = Mathf.Clamp01(value);
		}

		private void SetStatus(string message)
		{
			Debug.Log($"[BackgroundAssetsTest] {message}");
			if (statusText != null)
				statusText.text = message;
		}
	}
}
