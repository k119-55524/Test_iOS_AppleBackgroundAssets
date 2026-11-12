using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

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
		private int _lastLoggedProgressStep = -1;

		private const string Tag = "BackgroundAssetsTest";

		public IReadOnlyList<Sprite> LoadedSprites => _loadedSprites;

		private void Awake()
		{
			// Все исключения операций Addressables/ResourceManager — в лог с именем операции.
			UnityEngine.ResourceManagement.ResourceManager.ExceptionHandler = (op, ex) =>
				LogManager.LogException("ResourceManager", ex, op.IsValid() ? op.DebugName : "operation");

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
			try
			{
				await InitializeAddressablesAsync();
				await RefreshInitialStateAsync();
			}
			catch (Exception ex)
			{
				LogManager.LogException(Tag, ex, "Инициализация");
				SetStatus($"Ошибка инициализации: {LogManager.ShortMessage(ex)}", log: false);
			}
		}

		private async Task InitializeAddressablesAsync()
		{
			LogManager.Log("Addressables", $"InitializeAsync... RuntimePath={Addressables.RuntimePath}");
			var sw = Stopwatch.StartNew();
			var init = Addressables.InitializeAsync(false);
			await init.Task;

			if (init.Status == AsyncOperationStatus.Succeeded)
			{
				var ids = new List<string>();
				foreach (var locator in Addressables.ResourceLocators)
					ids.Add(locator.LocatorId);
				LogManager.Log("Addressables", $"Инициализировано за {sw.ElapsedMilliseconds} мс, локаторы: {string.Join(", ", ids)}");

				var locations = Addressables.LoadResourceLocationsAsync(addressableLabel, typeof(Sprite));
				await locations.Task;
				int count = locations.Status == AsyncOperationStatus.Succeeded ? locations.Result.Count : -1;
				LogManager.Log("Addressables", $"Метка '{addressableLabel}': {count} спрайтов в каталоге");
				Addressables.Release(locations);
			}
			else
			{
				LogManager.Error("Addressables", "Инициализация не удалась");
				LogManager.LogException("Addressables", init.OperationException, "InitializeAsync");
			}

			Addressables.Release(init);
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
            LogManager.Log("AssetPack", $"Старт: пак '{assetPackId}' локально доступен = {isLocal}");
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

			LogManager.Log("UI", "Нажата кнопка «Загрузить»");
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
				LogManager.LogException(Tag, ex, "Загрузка пака / спрайтов");
				SetStatus($"Ошибка: {LogManager.ShortMessage(ex)}", log: false);
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

			LogManager.Log("UI", "Нажата кнопка «Удалить пак»");
			SetBusy(true);
			try
			{
				ReleaseLoadedAssets();
				UpdatePreviewDisplay();

#if !UNITY_EDITOR && (UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_STANDALONE_OSX)
                SetStatus($"Удаление пака '{assetPackId}'...");
                // Дожидаемся, пока бандлы выгрузятся и закроют файлы пака.
                var removeSw = Stopwatch.StartNew();
                if (AppleBackgroundAssetBundleProvider.PendingUnloadCount > 0)
                    LogManager.Log("AssetPack", $"Ожидание выгрузки бандлов: {AppleBackgroundAssetBundleProvider.PendingUnloadCount}");
                while (AppleBackgroundAssetBundleProvider.PendingUnloadCount > 0)
                    await Task.Yield();
                LogManager.Log("AssetPack", $"RemoveAssetPackAsync('{assetPackId}')...");
                await AssetPackManager.RemoveAssetPackAsync(assetPackId);
                LogManager.Log("AssetPack", $"Пак удалён за {removeSw.ElapsedMilliseconds} мс, локально доступен = " +
                                            AssetPackManager.AssetPackIsAvailableLocally(assetPackId));
#endif
				SetProgress(0f);
				SetStatus($"Пак '{assetPackId}' удалён.");
			}
			catch (Exception ex)
			{
				LogManager.LogException(Tag, ex, "Удаление пака");
				SetStatus($"Ошибка удаления: {LogManager.ShortMessage(ex)}", log: false);
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
            var manifestSw = Stopwatch.StartNew();
            AssetPackManifest manifest = await AssetPackManager.GetManifestAsync();
            AssetPack[] allPacks = manifest.GetAllAssetPacks();
            var packIds = new List<string>();
            foreach (AssetPack p in allPacks)
                packIds.Add(p.GetId());
            LogManager.Log("AssetPack", $"Манифест получен за {manifestSw.ElapsedMilliseconds} мс, паков: {allPacks.Length} [{string.Join(", ", packIds)}]");

            AssetPack assetPack = null;
            foreach (AssetPack pack in allPacks)
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

            LogManager.Log("AssetPack", $"Pack '{assetPackId}': version {assetPack.GetVersion()}, " +
                      $"download size {assetPack.GetDownloadSize() / (1024f * 1024f):0.0} MB, local: {isLocal}");

            bool monitoringActive = true;
            _lastLoggedProgressStep = -1;
            _ = MonitorDownloadProgressAsync(() => monitoringActive);
            var downloadSw = Stopwatch.StartNew();
            LogManager.Log("AssetPack", $"EnsureLocalAvailabilityOfAssetPackAsync(shouldUpdate={checkForUpdates})...");

            try
            {
                SetStatus(isLocal
                    ? $"Проверка обновлений пака '{assetPackId}'..."
                    : $"Скачивание пака '{assetPackId}'...");
                await AssetPackManager.EnsureLocalAvailabilityOfAssetPackAsync(assetPack, checkForUpdates);
                LogManager.Log("AssetPack", $"Пак доступен локально, заняло {downloadSw.Elapsed.TotalSeconds:0.0} с");
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
                            SetStatus($"Скачивание: {progress * 100f:0}%", log: false);
                            int step = Mathf.FloorToInt(progress * 10f);
                            if (step != _lastLoggedProgressStep)
                            {
                                _lastLoggedProgressStep = step;
                                LogManager.Log("AssetPack", $"Прогресс: {progress * 100f:0}%");
                            }
                            break;
                        case AssetPackManager.DownloadStatusUpdate.Finished:
                            SetProgress(1f);
                            SetStatus("Скачивание завершено.");
                            return;
                        case AssetPackManager.DownloadStatusUpdate.Failed failed:
                            SetStatus($"Ошибка загрузки: {failed.Error}", log: false);
                            LogManager.LogException("AssetPack", failed.Error, "DownloadStatusUpdate.Failed");
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.LogException("AssetPack", ex, "Мониторинг прогресса");
            }
        }
#endif

		private async Task LoadSpritesFromAddressablesAsync()
		{
			ReleaseLoadedAssets();

			SetStatus($"Загрузка Addressables ({addressableLabel})...");
			var loadSw = Stopwatch.StartNew();
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
				SetStatus($"Ошибка Addressables: {LogManager.ShortMessage(_addressablesHandle.OperationException)}", log: false);
				LogManager.LogException("Addressables", _addressablesHandle.OperationException, $"LoadAssetsAsync<Sprite>('{addressableLabel}')");
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
			LogManager.Log("Addressables", $"LoadAssetsAsync<Sprite>('{addressableLabel}') за {loadSw.ElapsedMilliseconds} мс");
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
				LogManager.Log("Addressables", "Release спрайтов (бандл будет выгружен)");
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

		private void SetStatus(string message, bool log = true)
		{
			if (log)
				LogManager.Log(Tag, message);
			if (statusText != null)
				statusText.text = message;
		}
	}
}
