using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Вписывает весь UI в экран с сохранением пропорций (2:1, альбомная — безопасная зона современных iPhone в альбомной ориентации ≈ 1.97:1):
	///   Canvas → SafeArea (Screen.safeArea, без выреза/полоски «Домой») → Frame (AspectRatioFitter, FitInParent).
	/// Все элементы UI переносятся во Frame и растягиваются по его размеру (у них якоря в долях).
	/// Создаётся автоматически при старте, на сцене ничего настраивать не нужно.
	/// </summary>
	[RequireComponent(typeof(RectTransform))]
	public sealed class SafeAreaRoot : MonoBehaviour
	{
		/// <summary>
		/// Пропорции рамки UI (ширина / высота), под iPhone в альбомной ориентации.
		/// Всё внутри рамки подстроится само.
		/// </summary>
		public const float Aspect = 2f / 1f;

		/// <summary>Размер рамки в единицах Canvas (от него считаются размеры шрифтов).</summary>
		public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 960f);

		private RectTransform _frame;
		private Rect _lastSafeArea;
		private Vector2Int _lastScreenSize;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		private static void AutoSetup()
		{
			Canvas canvas = FindAnyObjectByType<Canvas>();
			if (canvas != null)
				Ensure(canvas);
		}

		/// <summary>Возвращает (и при необходимости создаёт) рамку UI для Canvas.</summary>
		public static RectTransform Ensure(Canvas canvas)
		{
			SafeAreaRoot existing = canvas.GetComponentInChildren<SafeAreaRoot>(true);
			if (existing != null)
				return existing._frame;

			// Масштаб Canvas: рамка всегда ≈ 1920×960 единиц, независимо от разрешения экрана.
			var scaler = canvas.GetComponent<CanvasScaler>();
			if (scaler != null)
			{
				scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
				scaler.referenceResolution = ReferenceResolution;
				scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
			}

			// SafeArea — безопасная зона экрана.
			var safeGo = new GameObject("SafeArea", typeof(RectTransform));
			var safe = (RectTransform)safeGo.transform;
			safe.SetParent(canvas.transform, false);

			// Frame — максимальный прямоугольник нужных пропорций внутри SafeArea, по центру.
			var frameGo = new GameObject("Frame", typeof(RectTransform));
			var frame = (RectTransform)frameGo.transform;
			frame.SetParent(safe, false);
			frame.anchorMin = Vector2.zero;
			frame.anchorMax = Vector2.one;
			frame.offsetMin = Vector2.zero;
			frame.offsetMax = Vector2.zero;
			var fitter = frameGo.AddComponent<AspectRatioFitter>();
			fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
			fitter.aspectRatio = Aspect;

			// Переносим все текущие элементы UI во Frame, сохраняя порядок отрисовки.
			var children = new List<Transform>();
			foreach (Transform child in canvas.transform)
			{
				if (child != safe)
					children.Add(child);
			}
			foreach (Transform child in children)
				child.SetParent(frame, false);

			safe.SetAsLastSibling();

			var root = safeGo.AddComponent<SafeAreaRoot>();
			root._frame = frame;
			root.Apply();
			return frame;
		}

		private void Update()
		{
			if (Screen.safeArea != _lastSafeArea || Screen.width != _lastScreenSize.x || Screen.height != _lastScreenSize.y)
				Apply();
		}

		private void Apply()
		{
			_lastSafeArea = Screen.safeArea;
			_lastScreenSize = new Vector2Int(Screen.width, Screen.height);

			if (Screen.width <= 0 || Screen.height <= 0)
				return;

			var rt = (RectTransform)transform;
			Vector2 min = _lastSafeArea.position;
			Vector2 max = _lastSafeArea.position + _lastSafeArea.size;
			rt.anchorMin = new Vector2(min.x / Screen.width, min.y / Screen.height);
			rt.anchorMax = new Vector2(max.x / Screen.width, max.y / Screen.height);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;

			Debug.Log($"[UI] Экран {Screen.width}x{Screen.height}, safe area {_lastSafeArea}, рамка UI {Aspect:0.00}:1");
		}
	}
}
