#if UNITY_IOS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace AppleBackgroundAssetsTest.EditorTools
{
	/// <summary>
	/// Правки Xcode-проекта после сборки из Unity, чтобы не делать их руками каждый раз:
	///  1. Фаза «Embed ExtensionKit Extensions» (Downloader.appex) переносится выше run-script фаз Unity —
	///     иначе Xcode 15+ падает с «Cycle inside Unity-iPhone».
	///  2. Таргету Downloader прописывается Team ID и автоматическая подпись (из Player Settings).
	///  3. UnityRuntime.framework убирается из Embed Frameworks (фикс Unity 6: ошибки «Invalid Bundle … minimum OS Version»
	///     и «Upload Symbols Failed» при загрузке в App Store Connect). Взято из рабочего проекта TF1 (XcodeSettingsPostProcesser).
	///  4. Из Info.plist удаляются UIRequiredDeviceCapabilities и UIApplicationExitsOnSuspend — как в TF1,
	///     чтобы список поддерживаемых устройств не сужался по сравнению с прошлыми версиями приложения.
	///  5. Включается доступ к Documents через «Файлы» (UIFileSharingEnabled) — чтобы забирать app_log.txt.
	/// Запускается после плагинов Apple (у них PostProcessBuild(10)).
	/// </summary>
	public static class XcodeSettingsPostProcesser
	{
		const string LogPrefix = "[XcodeSettingsPostProcesser] ";
		const string DownloaderTargetName = "Downloader";
		const string EmbedPhaseName = "Embed ExtensionKit Extensions";

		[PostProcessBuild(1000)]
		public static void OnPostProcessBuild(BuildTarget target, string projectPath)
		{
			if (target != BuildTarget.iOS)
				return;

			string pbxPath = PBXProject.GetPBXProjectPath(projectPath);
			var project = new PBXProject();
			project.ReadFromFile(pbxPath);
			string mainTargetGuid = project.GetUnityMainTargetGuid();
			SetupDownloaderSigning(project);
			RemoveUnityRuntimeFromBuild(project, mainTargetGuid);
			project.WriteToFile(pbxPath);

			string text = File.ReadAllText(pbxPath);
			string fixedText = MoveEmbedPhaseBeforeScripts(text, mainTargetGuid);
			fixedText = RemoveUnityRuntimeEmbedLines(fixedText);
			if (fixedText != text)
				File.WriteAllText(pbxPath, fixedText);

			CleanupInfoPlist(projectPath);
		}

		static void SetupDownloaderSigning(PBXProject project)
		{
			string guid = project.TargetGuidByName(DownloaderTargetName);
			if (string.IsNullOrEmpty(guid))
			{
				Debug.LogWarning(LogPrefix + $"Target '{DownloaderTargetName}' not found — signing not changed.");
				return;
			}

			string team = PlayerSettings.iOS.appleDeveloperTeamID;
			if (string.IsNullOrEmpty(team))
			{
				Debug.LogWarning(LogPrefix + "Player Settings → iOS → Signing Team ID is empty — set it to sign Downloader automatically.");
				return;
			}

			project.SetBuildProperty(guid, "DEVELOPMENT_TEAM", team);
			if (PlayerSettings.iOS.appleEnableAutomaticSigning)
				project.SetBuildProperty(guid, "CODE_SIGN_STYLE", "Automatic");
			Debug.Log(LogPrefix + $"Downloader signing: team {team}, automatic = {PlayerSettings.iOS.appleEnableAutomaticSigning}.");
		}

		static string MoveEmbedPhaseBeforeScripts(string text, string mainTargetGuid)
		{
			var shellPhaseGuids = new HashSet<string>(
				Regex.Matches(text, @"([0-9A-Fa-f]{16,}) /\* [^*]*? \*/ = \{\s*isa = PBXShellScriptBuildPhase;")
					.Cast<Match>().Select(m => m.Groups[1].Value));

			Match targetMatch = Regex.Match(text,
				Regex.Escape(mainTargetGuid) + @" /\* [^*]*? \*/ = \{\s*isa = PBXNativeTarget;.*?buildPhases = \((?<phases>.*?)\);",
				RegexOptions.Singleline);
			if (!targetMatch.Success)
			{
				Debug.LogWarning(LogPrefix + "Main target build phases not found in project.pbxproj — phase order not changed.");
				return text;
			}

			Group phases = targetMatch.Groups["phases"];
			List<string> lines = phases.Value.Split('\n').ToList();
			int embedIndex = lines.FindIndex(l => l.Contains("/* " + EmbedPhaseName + " */"));
			if (embedIndex < 0)
			{
				Debug.Log(LogPrefix + $"Phase '{EmbedPhaseName}' not found — nothing to reorder.");
				return text;
			}

			int firstScriptIndex = lines.FindIndex(l =>
			{
				Match m = Regex.Match(l, @"^\s*([0-9A-Fa-f]{16,}) /\*");
				return m.Success && shellPhaseGuids.Contains(m.Groups[1].Value);
			});
			if (firstScriptIndex < 0 || embedIndex < firstScriptIndex)
			{
				Debug.Log(LogPrefix + $"'{EmbedPhaseName}' is already before run-script phases.");
				return text;
			}

			string embedLine = lines[embedIndex];
			lines.RemoveAt(embedIndex);
			lines.Insert(firstScriptIndex, embedLine);
			Debug.Log(LogPrefix + $"Moved '{EmbedPhaseName}' before run-script phases (fixes 'Cycle inside Unity-iPhone').");
			return text.Substring(0, phases.Index) + string.Join("\n", lines) + text.Substring(phases.Index + phases.Length);
		}

		static void RemoveUnityRuntimeFromBuild(PBXProject project, string mainTargetGuid)
		{
			string guid = project.FindFileGuidByProjectPath("Frameworks/UnityRuntime.framework");
			if (string.IsNullOrEmpty(guid))
			{
				Debug.Log(LogPrefix + "UnityRuntime.framework is not in the project — nothing to remove.");
				return;
			}
			project.RemoveFileFromBuild(mainTargetGuid, guid);
			Debug.Log(LogPrefix + "Removed UnityRuntime.framework from Unity-iPhone build/embed phases.");
		}

		static string RemoveUnityRuntimeEmbedLines(string text)
		{
			// Страховка, как в TF1: построчно удаляем UnityRuntime из фазы Embed Frameworks, если что-то осталось.
			string[] lines = text.Split('\n');
			string[] filtered = lines.Where(l => !l.Contains("UnityRuntime.framework in Embed Frameworks")).ToArray();
			if (filtered.Length == lines.Length)
				return text;
			Debug.Log(LogPrefix + $"Removed {lines.Length - filtered.Length} leftover 'UnityRuntime.framework in Embed Frameworks' line(s).");
			return string.Join("\n", filtered);
		}

		static void CleanupInfoPlist(string projectPath)
		{
			string plistPath = Path.Combine(projectPath, "Info.plist");
			if (!File.Exists(plistPath))
			{
				Debug.LogWarning(LogPrefix + "Info.plist not found: " + plistPath);
				return;
			}

			var plist = new PlistDocument();
			plist.ReadFromString(File.ReadAllText(plistPath));
			bool changed = false;
			foreach (string key in new[] { "UIRequiredDeviceCapabilities", "UIApplicationExitsOnSuspend" })
			{
				if (plist.root.values.Remove(key))
				{
					changed = true;
					Debug.Log(LogPrefix + $"Info.plist: removed {key}.");
				}
			}
			// Тестовая сборка: папка Documents (там app_log.txt) видна в «Файлы → На iPhone → TrueFear1»,
			// чтобы лог можно было забрать, даже если приложение падает при запуске.
			plist.root.SetBoolean("UIFileSharingEnabled", true);
			plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
			changed = true;
			Debug.Log(LogPrefix + "Info.plist: UIFileSharingEnabled + LSSupportsOpeningDocumentsInPlace (логи доступны в «Файлах»).");

			if (changed)
				File.WriteAllText(plistPath, plist.WriteToString());
		}
	}
}
#endif
