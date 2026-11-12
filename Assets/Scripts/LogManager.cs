using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Синглтон-лог. Собирает всё, что пишется через Debug.Log* (включая ошибки Unity/Addressables)
	/// и необработанные исключения, показывает в панели справа и дублирует в файл
	/// (persistentDataPath/app_log.txt), чтобы лог пережил падение приложения.
	///
	/// Кнопка «Поделиться» открывает системное меню iOS (Почта, Discord, Telegram, AirDrop, Файлы)
	/// с файлами лога — плагин Assets/Plugins/iOS/LogShare.mm.
	///
	/// Уровни: [I] инфо, [W] предупреждение, [E] ошибка, [EX] исключение (с полным стеком и inner-цепочкой).
	///
	/// API:
	///   LogManager.Log / Warning / Error(tag, message)
	///   LogManager.LogException(tag, ex, context)                 — подробный отчёт об исключении
	///   LogManager.Guard(tag, context, () => ...)                  — выполнить и залогировать исключение
	///   await LogManager.GuardAsync(tag, context, async () => ...) — то же для async
	///   LogManager.ShortMessage(ex)                                — читаемый текст ошибки для UI
	/// </summary>
	public sealed class LogManager : MonoBehaviour
	{
		public enum Level { Info, Warning, Error, Exception }

		private struct Entry
		{
			public Level Level;
			public string Text;
		}

		private const int MaxEntries = 5000;
		private const int MaxDisplayChars = 12000;
		private const int FontSize = 24;
		private const string LogFileName = "app_log.txt";
		private const string PrevLogFileName = "app_log_prev.txt";

		private static LogManager s_instance;
		private static readonly object s_lock = new object();
		private static readonly Queue<Entry> s_pending = new Queue<Entry>();
		private static StreamWriter s_file;
		private static string s_logDir;
		private static int s_warnings;
		private static int s_errors;

		[ThreadStatic] private static bool t_suppressCapture;

		private readonly List<Entry> _entries = new List<Entry>();
		private readonly StringBuilder _sb = new StringBuilder();

		private Text _logText;
		private Text _titleText;
		private Text _copyLabel;
		private Text _prevLabel;
		private Text _filterLabel;
		private ScrollRect _scroll;
		private bool _dirty;
		private bool _errorsOnly;
		private float _labelsResetAt;

		public static LogManager Instance => s_instance;

		// ======================= API =======================

		public static void Log(string tag, string message) => Debug.Log($"[{tag}] {message}");
		public static void Warning(string tag, string message) => Debug.LogWarning($"[{tag}] {message}");
		public static void Error(string tag, string message) => Debug.LogError($"[{tag}] {message}");

		/// <summary>Подробный отчёт об исключении: тип, сообщение, стек, все inner-исключения.</summary>
		public static void LogException(string tag, Exception ex, string context = null)
		{
			if (ex == null)
				return;

			string header = string.IsNullOrEmpty(context) ? $"[{tag}]" : $"[{tag}] {context}:";
			string text = header + " " + Describe(ex);

			Enqueue(Level.Exception, text);

			// В консоль Xcode/Unity тоже, но без повторного захвата этим же логом.
			t_suppressCapture = true;
			try { Debug.LogError(text); }
			finally { t_suppressCapture = false; }
		}

		/// <summary>Выполняет действие; исключение логируется и не пробрасывается. Возвращает true при успехе.</summary>
		public static bool Guard(string tag, string context, Action action)
		{
			try
			{
				action();
				return true;
			}
			catch (Exception ex)
			{
				LogException(tag, ex, context);
				return false;
			}
		}

		/// <summary>Async-версия Guard.</summary>
		public static async Task<bool> GuardAsync(string tag, string context, Func<Task> action)
		{
			try
			{
				await action();
				return true;
			}
			catch (Exception ex)
			{
				LogException(tag, ex, context);
				return false;
			}
		}

		/// <summary>Короткое читаемое сообщение (для статуса в UI). Учитывает Apple.BackgroundAssets.Error.</summary>
		public static string ShortMessage(Exception ex)
		{
			if (ex == null)
				return "";
			while (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
				ex = agg.InnerException;
			return $"{BestMessage(ex)} ({ex.GetType().Name})";
		}

		/// <summary>Полное описание исключения со стеком и цепочкой inner.</summary>
		public static string Describe(Exception ex)
		{
			var sb = new StringBuilder();
			int depth = 0;
			while (ex != null && depth < 8)
			{
				if (depth > 0)
					sb.Append("\n   ── inner: ");
				sb.Append(ex.GetType().FullName).Append(": ").Append(BestMessage(ex));

				if (!string.IsNullOrEmpty(ex.StackTrace))
				{
					foreach (string frame in ex.StackTrace.Split('\n'))
					{
						if (!string.IsNullOrWhiteSpace(frame))
							sb.Append("\n      ").Append(frame.Trim());
					}
				}

				if (ex is AggregateException agg && agg.InnerExceptions.Count > 1)
				{
					for (int i = 0; i < agg.InnerExceptions.Count; i++)
						sb.Append($"\n   ── aggregate[{i}]: ").Append(Describe(agg.InnerExceptions[i]).Replace("\n", "\n   "));
					break;
				}

				ex = ex.InnerException;
				depth++;
			}
			return sb.ToString();
		}

		private static string BestMessage(Exception ex)
		{
			// Apple.BackgroundAssets.Error хранит описание только в ToString(), Message у него стандартный.
			string message = ex.Message;
			if (string.IsNullOrEmpty(message) || message.StartsWith("Exception of type"))
			{
				try
				{
					string s = ex.ToString();
					if (!string.IsNullOrEmpty(s))
						message = s.Split('\n')[0].Trim();
				}
				catch
				{
					// ignore
				}
			}
			return message;
		}

		public string GetFullText(bool errorsOnly = false)
		{
			var sb = new StringBuilder();
			foreach (Entry e in _entries)
			{
				if (errorsOnly && e.Level == Level.Info)
					continue;
				sb.Append(e.Text).Append('\n');
			}
			return sb.ToString();
		}

		// ======================= Lifecycle =======================

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
		private static void Bootstrap()
		{
			if (s_instance != null)
				return;

			var go = new GameObject("[LogManager]");
			DontDestroyOnLoad(go);
			go.AddComponent<LogManager>();
		}

		private void Awake()
		{
			if (s_instance != null && s_instance != this)
			{
				Destroy(gameObject);
				return;
			}

			s_instance = this;
			OpenLogFile();

			Application.logMessageReceivedThreaded += OnUnityLog;
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			Application.lowMemory += OnLowMemory;
			SceneManager.sceneLoaded += OnSceneLoaded;

			Log("App", $"Старт: {Application.productName} {Application.version} ({Application.identifier})");
			Log("App", $"Unity {Application.unityVersion}, {SystemInfo.deviceModel}, {SystemInfo.operatingSystem}");
			Log("App", $"Экран {Screen.width}x{Screen.height}, RAM {SystemInfo.systemMemorySize} MB, лог-файл: {Path.Combine(s_logDir, LogFileName)}");
			if (File.Exists(Path.Combine(s_logDir, PrevLogFileName)))
				Log("App", "Лог предыдущего запуска сохранён — кнопка «Пред. сессия» копирует его в буфер");
		}

		private void OnDestroy()
		{
			if (s_instance != this)
				return;

			Application.logMessageReceivedThreaded -= OnUnityLog;
			AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
			TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
			Application.lowMemory -= OnLowMemory;
			SceneManager.sceneLoaded -= OnSceneLoaded;
			s_instance = null;

			lock (s_lock)
			{
				s_file?.Dispose();
				s_file = null;
			}
		}

		private void OnApplicationPause(bool paused) => Log("App", paused ? "Приложение свернуто" : "Приложение развернуто");

		private void OnApplicationQuit() => Log("App", "Выход из приложения");

		private static void OnLowMemory() => Warning("App", "Система сообщает о нехватке памяти (lowMemory)");

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			Log("App", $"Сцена загружена: {scene.name}");
			if (_logText == null)
				BuildUI();
			_dirty = true;
		}

		// ======================= Capture =======================

		// Вызывается из любого потока.
		private static void OnUnityLog(string message, string stackTrace, LogType type)
		{
			if (t_suppressCapture)
				return;

			Level level;
			switch (type)
			{
				case LogType.Warning: level = Level.Warning; break;
				case LogType.Exception: level = Level.Exception; break;
				case LogType.Error:
				case LogType.Assert: level = Level.Error; break;
				default: level = Level.Info; break;
			}

			string text = message;
			if (level != Level.Info && level != Level.Warning && !string.IsNullOrEmpty(stackTrace))
			{
				int maxFrames = level == Level.Exception ? 30 : 6;
				string[] frames = stackTrace.Split('\n');
				for (int i = 0, n = 0; i < frames.Length && n < maxFrames; i++)
				{
					if (string.IsNullOrWhiteSpace(frames[i]))
						continue;
					text += "\n      " + frames[i].Trim();
					n++;
				}
			}

			Enqueue(level, text);
		}

		private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			LogException("Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
				e.IsTerminating ? "Необработанное исключение (приложение будет закрыто)" : "Необработанное исключение");
		}

		private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
		{
			LogException("Unhandled", e.Exception, "Необработанное исключение в Task (fire-and-forget)");
			e.SetObserved();
		}

		private static void Enqueue(Level level, string text)
		{
			string tag;
			switch (level)
			{
				case Level.Warning: tag = "[W] "; break;
				case Level.Error: tag = "[E] "; break;
				case Level.Exception: tag = "[EX]"; break;
				default: tag = "[I] "; break;
			}

			var entry = new Entry { Level = level, Text = $"{DateTime.Now:HH:mm:ss.fff} {tag} {text}" };

			lock (s_lock)
			{
				s_pending.Enqueue(entry);
				if (level == Level.Warning) s_warnings++;
				else if (level >= Level.Error) s_errors++;

				// Сразу в файл (AutoFlush), чтобы строка сохранилась даже при падении.
				try { s_file?.WriteLine(entry.Text); }
				catch { /* ignore */ }
			}
		}

		private static void OpenLogFile()
		{
			try
			{
				s_logDir = Application.persistentDataPath;
				string current = Path.Combine(s_logDir, LogFileName);
				string prev = Path.Combine(s_logDir, PrevLogFileName);
				if (File.Exists(current))
				{
					if (File.Exists(prev))
						File.Delete(prev);
					File.Move(current, prev);
				}
				lock (s_lock)
					s_file = new StreamWriter(new FileStream(current, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
						new UTF8Encoding(false)) { AutoFlush = true };
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[LogManager] Не удалось открыть лог-файл: {ex.Message}");
			}
		}

		// ======================= Update / render =======================

		private void Update()
		{
			lock (s_lock)
			{
				while (s_pending.Count > 0)
				{
					_entries.Add(s_pending.Dequeue());
					_dirty = true;
				}
			}

			if (_entries.Count > MaxEntries)
				_entries.RemoveRange(0, _entries.Count - MaxEntries);

			if (_labelsResetAt > 0f && Time.unscaledTime >= _labelsResetAt)
			{
				_labelsResetAt = 0f;
				if (_copyLabel != null) _copyLabel.text = "Копировать";
				if (_prevLabel != null) _prevLabel.text = "Пред. сессия";
			}

			if (_dirty && _logText != null)
				RefreshText();
		}

		private void RefreshText()
		{
			_dirty = false;

			int warnings, errors;
			lock (s_lock)
			{
				warnings = s_warnings;
				errors = s_errors;
			}
			_titleText.text = $"Лог   <color=#FFD54F>W: {warnings}</color>   <color=#FF6E6E>E: {errors}</color>" +
			                  (_errorsOnly ? "   <color=#90CAF9>(только W/E)</color>" : "");

			// Хвост лога (ограничение UI Text по вершинам).
			int start = _entries.Count;
			int chars = 0;
			int hidden = 0;
			while (start > 0)
			{
				Entry e = _entries[start - 1];
				if (_errorsOnly && e.Level == Level.Info)
				{
					start--;
					continue;
				}
				if (chars + e.Text.Length + 1 > MaxDisplayChars)
					break;
				chars += e.Text.Length + 1;
				start--;
			}
			for (int i = 0; i < start; i++)
				if (!_errorsOnly || _entries[i].Level != Level.Info)
					hidden++;

			_sb.Clear();
			if (hidden > 0)
				_sb.Append($"<color=#9E9E9E>… ещё {hidden} строк выше — полный лог копируется кнопкой</color>\n");

			for (int i = start; i < _entries.Count; i++)
			{
				Entry e = _entries[i];
				if (_errorsOnly && e.Level == Level.Info)
					continue;

				// '<' в тексте могут сломать rich text — заменяем только при отображении.
				string safe = e.Text.Replace("<", "‹").Replace(">", "›");
				switch (e.Level)
				{
					case Level.Warning: _sb.Append("<color=#FFD54F>").Append(safe).Append("</color>"); break;
					case Level.Error: _sb.Append("<color=#FF6E6E>").Append(safe).Append("</color>"); break;
					case Level.Exception: _sb.Append("<color=#FF5252><b>").Append(safe).Append("</b></color>"); break;
					default: _sb.Append(safe); break;
				}
				_sb.Append('\n');
			}

			bool stickToBottom = _scroll == null || _scroll.verticalNormalizedPosition <= 0.02f ||
			                     _scroll.content.rect.height <= _scroll.viewport.rect.height;

			_logText.text = _sb.ToString();

			if (stickToBottom && _scroll != null)
			{
				Canvas.ForceUpdateCanvases();
				_scroll.verticalNormalizedPosition = 0f;
			}
		}

		// ======================= Buttons =======================

		private void CopyToClipboard()
		{
			GUIUtility.systemCopyBuffer = GetFullText(_errorsOnly);
			_copyLabel.text = $"Скопировано ({_entries.Count})";
			_labelsResetAt = Time.unscaledTime + 2f;
		}

		private void CopyPreviousSession()
		{
			string prev = Path.Combine(s_logDir ?? Application.persistentDataPath, PrevLogFileName);
			if (File.Exists(prev))
			{
				GUIUtility.systemCopyBuffer = File.ReadAllText(prev);
				_prevLabel.text = "Скопировано";
			}
			else
			{
				_prevLabel.text = "Нет файла";
			}
			_labelsResetAt = Time.unscaledTime + 2f;
		}

#if UNITY_IOS && !UNITY_EDITOR
		[DllImport("__Internal")]
		private static extern void LogShare_ShareFiles(string[] paths, int count, string subject);
#endif

		/// <summary>Системное меню «Поделиться» с файлами лога (текущий + предыдущий запуск).</summary>
		private void ShareLog()
		{
			try
			{
				string dir = s_logDir ?? Application.persistentDataPath;
				string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
				string shareDir = Path.Combine(Application.temporaryCachePath, "LogShare");
				if (Directory.Exists(shareDir))
					Directory.Delete(shareDir, true);
				Directory.CreateDirectory(shareDir);

				var files = new List<string>();

				// Снимок текущего лога (файл открыт на запись, читаем с FileShare.ReadWrite).
				string current = Path.Combine(dir, LogFileName);
				if (File.Exists(current))
				{
					string snapshot = Path.Combine(shareDir, $"log_{stamp}.txt");
					using (var src = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
					using (var dst = File.Create(snapshot))
						src.CopyTo(dst);
					files.Add(snapshot);
				}

				string prev = Path.Combine(dir, PrevLogFileName);
				if (File.Exists(prev))
				{
					string prevCopy = Path.Combine(shareDir, $"log_{stamp}_previous_session.txt");
					File.Copy(prev, prevCopy, true);
					files.Add(prevCopy);
				}

				if (files.Count == 0)
				{
					Warning("LogManager", "Нечего отправлять: файлы лога не найдены");
					return;
				}

				Log("LogManager", $"Поделиться: {files.Count} файл(а)");
				string subject = $"Log {Application.productName} {Application.version} {stamp}";

#if UNITY_IOS && !UNITY_EDITOR
				LogShare_ShareFiles(files.ToArray(), files.Count, subject);
#elif UNITY_EDITOR
				UnityEditor.EditorUtility.RevealInFinder(files[0]);
#else
				Application.OpenURL("file://" + shareDir);
#endif
			}
			catch (System.Exception ex)
			{
				LogException("LogManager", ex, "Поделиться логом");
			}
		}

		private void ClearLog()
		{
			_entries.Clear();
			lock (s_lock)
			{
				s_warnings = 0;
				s_errors = 0;
			}
			_dirty = true;
			Log("App", "Лог очищен (в файле остаётся полный)");
		}

		private void ToggleFilter()
		{
			_errorsOnly = !_errorsOnly;
			_filterLabel.text = _errorsOnly ? "Показать всё" : "Только W/E";
			_dirty = true;
		}

		// ======================= UI =======================

		private void BuildUI()
		{
			Canvas canvas = FindAnyObjectByType<Canvas>();
			if (canvas == null)
			{
				var canvasGo = new GameObject("LogCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
				canvas = canvasGo.GetComponent<Canvas>();
				canvas.renderMode = RenderMode.ScreenSpaceOverlay;
				var scaler = canvasGo.GetComponent<CanvasScaler>();
				scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
				scaler.referenceResolution = SafeAreaRoot.ReferenceResolution;
				scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
			}

			Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

			// Внутрь рамки UI (безопасная зона + сохранение пропорций).
			RectTransform uiRoot = SafeAreaRoot.Ensure(canvas);
			RectTransform panel = CreateRect("LogPanel", uiRoot, new Vector2(0.51f, 0.02f), new Vector2(0.99f, 0.98f));
			panel.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);

			// Строка 1: заголовок со счётчиками.
			RectTransform titleRow = CreateRect("TitleRow", panel, new Vector2(0f, 0.955f), new Vector2(1f, 1f), 6f);
			_titleText = CreateText("Title", titleRow, font, "Лог", 30, TextAnchor.MiddleLeft);
			_titleText.supportRichText = true;

			// Строка 2: кнопки.
			RectTransform buttons = CreateRect("Buttons", panel, new Vector2(0f, 0.91f), new Vector2(1f, 0.955f), 4f);
			_copyLabel = CreateButton("CopyButton", buttons, font, "Копировать", 0.00f, 0.19f, CopyToClipboard);
			CreateButton("ShareButton", buttons, font, "Поделиться", 0.20f, 0.40f, ShareLog);
			CreateButton("ClearButton", buttons, font, "Очистить", 0.41f, 0.58f, ClearLog);
			_filterLabel = CreateButton("FilterButton", buttons, font, "Только W/E", 0.59f, 0.79f, ToggleFilter);
			_prevLabel = CreateButton("PrevButton", buttons, font, "Пред. сессия", 0.80f, 1.00f, CopyPreviousSession);

			// Прокручиваемая область.
			RectTransform viewport = CreateRect("Viewport", panel, new Vector2(0f, 0f), new Vector2(1f, 0.91f), 6f);
			viewport.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.04f);
			viewport.gameObject.AddComponent<RectMask2D>();

			RectTransform content = CreateRect("Content", viewport, new Vector2(0f, 1f), new Vector2(1f, 1f));
			content.pivot = new Vector2(0.5f, 1f);
			content.sizeDelta = Vector2.zero;

			_logText = content.gameObject.AddComponent<Text>();
			_logText.font = font;
			_logText.fontSize = FontSize;
			_logText.color = new Color(0.85f, 1f, 0.85f, 1f);
			_logText.alignment = TextAnchor.UpperLeft;
			_logText.horizontalOverflow = HorizontalWrapMode.Wrap;
			_logText.verticalOverflow = VerticalWrapMode.Overflow;
			_logText.supportRichText = true;
			_logText.raycastTarget = false;

			var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			_scroll = viewport.gameObject.AddComponent<ScrollRect>();
			_scroll.viewport = viewport;
			_scroll.content = content;
			_scroll.horizontal = false;
			_scroll.vertical = true;
			_scroll.movementType = ScrollRect.MovementType.Clamped;
			_scroll.scrollSensitivity = 40f;
		}

		private static RectTransform CreateRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, float padding = 0f)
		{
			var go = new GameObject(name, typeof(RectTransform));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = anchorMin;
			rt.anchorMax = anchorMax;
			rt.offsetMin = new Vector2(padding, padding);
			rt.offsetMax = new Vector2(-padding, -padding);
			return rt;
		}

		private static Text CreateText(string name, Transform parent, Font font, string text, int size, TextAnchor alignment)
		{
			RectTransform rt = CreateRect(name, parent, Vector2.zero, Vector2.one, 2f);
			var t = rt.gameObject.AddComponent<Text>();
			t.font = font;
			t.text = text;
			t.fontSize = size;
			t.alignment = alignment;
			t.color = Color.white;
			t.resizeTextForBestFit = true;
			t.resizeTextMinSize = 10;
			t.resizeTextMaxSize = size;
			t.raycastTarget = false;
			return t;
		}

		private static Text CreateButton(string name, Transform parent, Font font, string label,
			float xMin, float xMax, UnityEngine.Events.UnityAction onClick)
		{
			RectTransform rt = CreateRect(name, parent, new Vector2(xMin, 0f), new Vector2(xMax, 1f));
			var image = rt.gameObject.AddComponent<Image>();
			image.color = new Color(0.25f, 0.45f, 0.85f, 1f);
			var button = rt.gameObject.AddComponent<Button>();
			button.targetGraphic = image;
			button.onClick.AddListener(onClick);
			return CreateText("Label", rt, font, label, 24, TextAnchor.MiddleCenter);
		}
	}
}
