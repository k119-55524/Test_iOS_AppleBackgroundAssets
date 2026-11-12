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
		// ID пака задаётся в одном месте — BackgroundAssetsConfig.AssetPackId.
		private static string assetPackId => BackgroundAssetsConfig.AssetPackId;

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
		private DownloadTrace _download;

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
            // Отметка ДО нативного вызова: если приложение упадёт внутри него, это будет последней строкой лога.
            LogManager.Log("AssetPack", $"Старт: AssetPackIsAvailableLocally('{assetPackId}')...");
            bool isLocal = AssetPackManager.AssetPackIsAvailableLocally(assetPackId);
            LogManager.Log("AssetPack", $"Старт: пак '{assetPackId}' локально доступен = {isLocal}");
            if (isLocal)
            {
                // Не грузим спрайты автоматически при старте: если с паком что-то не так (например, загрузка
                // была прервана), краш при старте не даст открыть приложение и достать лог. Загрузка — по кнопке.
                SetStatus($"Пак '{assetPackId}' уже скачан локально. Нажмите «Загрузить», чтобы показать спрайты.");
                SetProgress(1f);
                await Task.CompletedTask;
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
            LogManager.Log("AssetPack", "GetManifestAsync()...");
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

            float sizeMb = assetPack.GetDownloadSize() / (1024f * 1024f);
            LogManager.Log("AssetPack", $"Pack '{assetPackId}': version {assetPack.GetVersion()}, " +
                      $"download size {sizeMb:0.0} MB, local: {isLocal}");
            LogManager.Log("AssetPack", $"Сеть: {Application.internetReachability}");
            await LogPackStatusAsync(assetPack, "до загрузки");

            var downloadSw = Stopwatch.StartNew();
            _download = new DownloadTrace(downloadSw, sizeMb);
            bool monitoringActive = true;
            _ = MonitorDownloadProgressAsync(_download, () => monitoringActive);
            _ = DownloadWatchdogAsync(_download, assetPack, () => monitoringActive);
            LogManager.Log("AssetPack", $"EnsureLocalAvailabilityOfAssetPackAsync(shouldUpdate={checkForUpdates})...");

            try
            {
                SetStatus(isLocal
                    ? $"Проверка обновлений пака '{assetPackId}'..."
                    : $"Скачивание пака '{assetPackId}'...");
                await AssetPackManager.EnsureLocalAvailabilityOfAssetPackAsync(assetPack, checkForUpdates);
                LogManager.Log("AssetPack", $"Пак доступен локально, заняло {downloadSw.Elapsed.TotalSeconds:0.0} с " +
                                            $"(последнее событие: {_download.Describe()})");
                SetProgress(1f);
                SetStatus($"Пак '{assetPackId}' доступен локально.");
            }
            catch (Exception ex)
            {
                LogManager.Log("AssetPack", $"EnsureLocalAvailability завершился ошибкой через {downloadSw.Elapsed.TotalSeconds:0.0} с: " +
                                            $"{LogManager.ShortMessage(ex)}; последнее событие: {_download.Describe()}");
                await LogPackStatusAsync(assetPack, "после ошибки");
                throw;
            }
            finally
            {
                monitoringActive = false;
            }
        }

        /// <summary>Логирует каждое событие загрузки от системы (Began / Paused / Downloading / Finished / Failed).</summary>
        private async Task MonitorDownloadProgressAsync(DownloadTrace trace, Func<bool> isActive)
        {
            bool terminal = false;
            try
            {
                await foreach (AssetPackManager.DownloadStatusUpdate update in AssetPackManager.DownloadStatusUpdatesAsync(assetPackId))
                {
                    if (this == null)
                        break;

                    switch (update)
                    {
                        case AssetPackManager.DownloadStatusUpdate.Began:
                            trace.OnEvent("Began");
                            LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Began: загрузка начата/возобновлена");
                            SetStatus($"Начата загрузка '{assetPackId}'...", log: false);
                            break;

                        case AssetPackManager.DownloadStatusUpdate.Paused:
                            trace.OnEvent("Paused");
                            LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Paused: система приостановила загрузку " +
                                                        $"(сеть: {Application.internetReachability}, прогресс {trace.Progress * 100:0.00}%)");
                            SetStatus($"Загрузка '{assetPackId}' на паузе...", log: false);
                            break;

                        case AssetPackManager.DownloadStatusUpdate.Downloading downloading:
                            trace.OnProgress(downloading.Progress);
                            float progress = (float)downloading.Progress;
                            SetProgress(progress);
                            SetStatus($"Скачивание: {progress * 100f:0.0}%", log: false);
                            if (trace.ShouldLogProgress())
                                LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Прогресс: {downloading.Progress * 100:0.00}% " +
                                                            $"(~{downloading.Progress * trace.SizeMb:0.0} из {trace.SizeMb:0.0} MB, " +
                                                            $"событий: {trace.EventCount})");
                            break;

                        case AssetPackManager.DownloadStatusUpdate.Finished:
                            terminal = true;
                            trace.OnEvent("Finished");
                            LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Finished: скачивание завершено, событий: {trace.EventCount}");
                            SetProgress(1f);
                            SetStatus("Скачивание завершено.", log: false);
                            return;

                        case AssetPackManager.DownloadStatusUpdate.Failed failed:
                            terminal = true;
                            trace.OnEvent("Failed");
                            Exception failedSafe = LogManager.MakeSafe(failed.Error, "DownloadStatusUpdate.Failed");
                            LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Failed на {trace.Progress * 100:0.00}%: " +
                                                        LogManager.ShortMessage(failedSafe));
                            SetStatus($"Ошибка загрузки: {LogManager.ShortMessage(failedSafe)}", log: false);
                            LogManager.LogException("AssetPack", failedSafe, "DownloadStatusUpdate.Failed");
                            return;

                        default:
                            trace.OnEvent(update.GetType().Name);
                            LogManager.Log("AssetPack", $"[{trace.Elapsed:0.0} с] Неизвестное событие: {update}");
                            break;
                    }

                    if (!isActive())
                    {
                        LogManager.Log("AssetPack", "Мониторинг событий остановлен: EnsureLocalAvailability уже завершился.");
                        break;
                    }
                }

                if (!terminal)
                    LogManager.Log("AssetPack", $"Поток событий закрылся без Finished/Failed. Последнее: {trace.Describe()}");
            }
            catch (Exception ex)
            {
                LogManager.LogException("AssetPack", ex, "Мониторинг прогресса");
            }
        }

        /// <summary>
        /// «Сторож»: раз в 5 с пишет, что происходит, даже если система молчит —
        /// время с последнего события, прогресс, статус пака и сеть. Так видно, где именно загрузка встала.
        /// </summary>
        private async Task DownloadWatchdogAsync(DownloadTrace trace, AssetPack assetPack, Func<bool> isActive)
        {
            const int IntervalMs = 5000;
            const double StallWarningSec = 30;
            bool stallReported = false;
            try
            {
                while (true)
                {
                    await Task.Delay(IntervalMs);
                    if (!isActive() || this == null)
                        return;

                    string status = await DescribePackStatusAsync(assetPack);
                    LogManager.Log("AssetPack", $"[{trace.Elapsed:0} с] Сторож: {trace.Describe()}; статус пака: {status}; " +
                                                $"сеть: {Application.internetReachability}");

                    if (trace.SinceLastEvent >= StallWarningSec && !stallReported)
                    {
                        stallReported = true;
                        LogManager.Log("AssetPack", $"ВНИМАНИЕ: нет событий от системы {trace.SinceLastEvent:0} с " +
                                                    $"(застряло на {trace.Progress * 100:0.00}%).");
                    }
                    else if (trace.SinceLastEvent < StallWarningSec && stallReported)
                    {
                        stallReported = false;
                        LogManager.Log("AssetPack", "События снова пошли.");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.LogException("AssetPack", ex, "Сторож загрузки");
            }
        }

        private async Task LogPackStatusAsync(AssetPack assetPack, string when)
        {
            LogManager.Log("AssetPack", $"Статус пака ({when}): {await DescribePackStatusAsync(assetPack)}, " +
                                        $"локально доступен = {AssetPackManager.AssetPackIsAvailableLocally(assetPackId)}");
        }

        private static async Task<string> DescribePackStatusAsync(AssetPack assetPack)
        {
            try
            {
                AssetPack.Status s = await AssetPackManager.GetAssetPackStatusAsync(assetPack);
                var flags = new List<string>();
                if (s.IsDownloadAvailable()) flags.Add("DownloadAvailable");
                if (s.IsDownloading()) flags.Add("Downloading");
                if (s.IsDownloaded()) flags.Add("Downloaded");
                if (s.IsUpToDate()) flags.Add("UpToDate");
                if (s.IsUpdateAvailable()) flags.Add("UpdateAvailable");
                if (s.IsOutOfDate()) flags.Add("OutOfDate");
                if (s.IsObsolete()) flags.Add("Obsolete");
                return flags.Count == 0 ? "(нет флагов)" : string.Join(", ", flags);
            }
            catch (Exception ex)
            {
                return "ошибка запроса статуса: " + LogManager.ShortMessage(ex);
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

		/// <summary>Состояние текущей загрузки пака — для подробного лога.</summary>
		private sealed class DownloadTrace
		{
			private readonly Stopwatch _sw;
			private double _lastEventAt;
			private double _lastLoggedProgress = -1;
			private double _lastLoggedAt = double.MinValue;

			public DownloadTrace(Stopwatch sw, float sizeMb)
			{
				_sw = sw;
				SizeMb = sizeMb;
			}

			public float SizeMb { get; }
			public double Progress { get; private set; }
			public string LastEvent { get; private set; } = "нет событий";
			public int EventCount { get; private set; }
			public double Elapsed => _sw.Elapsed.TotalSeconds;
			public double SinceLastEvent => Elapsed - _lastEventAt;

			public void OnEvent(string name)
			{
				LastEvent = name;
				_lastEventAt = Elapsed;
				EventCount++;
			}

			public void OnProgress(double progress)
			{
				Progress = progress;
				OnEvent("Downloading");
			}

			/// <summary>Логировать каждый +1% и, если прогресс меняется медленно, не реже раза в 5 с.</summary>
			public bool ShouldLogProgress()
			{
				bool bigStep = Progress - _lastLoggedProgress >= 0.01;
				bool timeStep = Elapsed - _lastLoggedAt >= 5 && Progress != _lastLoggedProgress;
				if (!bigStep && !timeStep && _lastLoggedProgress >= 0)
					return false;
				_lastLoggedProgress = Progress;
				_lastLoggedAt = Elapsed;
				return true;
			}

			public string Describe() =>
				$"последнее событие {LastEvent} ({SinceLastEvent:0.0} с назад), прогресс {Progress * 100:0.00}%, " +
				$"событий {EventCount}, прошло {Elapsed:0.0} с";
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
