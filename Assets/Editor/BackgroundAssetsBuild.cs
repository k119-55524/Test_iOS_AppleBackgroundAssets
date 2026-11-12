using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AppleBackgroundAssetsTest.EditorTools
{
	/// <summary>
	/// Собирает Addressables-контент и превращает бандлы групп, использующих
	/// <see cref="AppleBackgroundAssetBundleProvider"/>, в Apple asset pack:
	///   Library/BackgroundAssetPacks/TF1-Test-Package/              — промежуточное: бандлы (корень пространства имён пака)
	///   Library/BackgroundAssetPacks/TF1-Test-Package.manifest.json — промежуточное: манифест для ba-package
	///   Library/BackgroundAssetPacks/package.sh                  — пересобрать .aar вручную
	///   Builds/BackgroundAssetPacks/TF1-Test-Package.aar            — результат: архив для Transporter (только на macOS)
	///   Builds/BackgroundAssetPacks/serve.sh                     — локальный mock-сервер ba-serve
	/// Ничего запускать вручную не нужно: всё выполняется автоматически при Build / Build And Run для iOS.
	/// </summary>
	public static class BackgroundAssetsBuild
	{
		/// <summary>ID пака — берётся из BackgroundAssetsConfig (единственное место, где он задаётся).</summary>
		public const string AssetPackId = BackgroundAssetsConfig.AssetPackId;

		/// <summary>Политика загрузки: onDemand | prefetch | essential.</summary>
		public const string DownloadPolicy = "onDemand";

		/// <summary>Готовый .aar (относительно корня проекта; Builds/ в .gitignore).</summary>
		public const string OutputFolderName = "Builds/BackgroundAssetPacks";

		/// <summary>Промежуточные файлы: бандлы и манифест (Library/ — кэш Unity, в .gitignore).</summary>
		public const string StagingFolderName = "Library/BackgroundAssetPacks";

		const string HostingConfigurationPath = "Assets/Apple Hosting Configuration.asset";

		static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
		public static string OutputRoot => Path.Combine(ProjectRoot, OutputFolderName);
		public static string StagingRoot => Path.Combine(ProjectRoot, StagingFolderName);
		public static string PackStagingDir => Path.Combine(StagingRoot, AssetPackId);
		public static string ManifestPath => Path.Combine(StagingRoot, AssetPackId + ".manifest.json");
		public static string ArchivePath => Path.Combine(OutputRoot, AssetPackId + ".aar");

		static Func<AddressableAssetSettings, AddressablesPlayerBuildResult> s_previousOverride;

		[InitializeOnLoadMethod]
		static void RegisterPlayerBuildOverride()
		{
			// Вызывается Addressables в начале сборки плеера (если BuildAddressablesWithPlayerBuild включён).
			Func<AddressableAssetSettings, AddressablesPlayerBuildResult> self = BuildForPlayer;
			var current = AddressablesPlayerBuildProcessor.BuildAddressablesOverride;
			if (current != null && !current.Equals(self))
				s_previousOverride = current;
			AddressablesPlayerBuildProcessor.BuildAddressablesOverride = self;

			// В редакторе Background Assets нет — Play Mode всегда через Asset Database.
			EditorApplication.delayCall += () =>
			{
				var settings = AddressableAssetSettingsDefaultObject.Settings;
				if (settings == null)
					return;
				int fastMode = settings.DataBuilders.FindIndex(b => b is BuildScriptFastMode);
				if (fastMode >= 0 && settings.ActivePlayModeDataBuilderIndex != fastMode)
					settings.ActivePlayModeDataBuilderIndex = fastMode;
			};
		}

		static AddressablesPlayerBuildResult BuildForPlayer(AddressableAssetSettings settings)
		{
			if (!IsAppleTarget(EditorUserBuildSettings.activeBuildTarget))
			{
				// Не-Apple платформы собираем как обычно (или чужим override, если он был).
				if (s_previousOverride != null)
					return s_previousOverride(settings);
				AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult defaultResult);
				return defaultResult;
			}

			Debug.Log("[BackgroundAssets] Player build: configuring Apple Build Profile, building Addressables + asset pack.");
			Debug.Log("[BackgroundAssets] " + EnsureAppleBuildProfile());
			return BuildAddressablesAndPack(runPackager: true);
		}

		public static AddressablesPlayerBuildResult BuildAddressablesAndPack(bool runPackager)
		{
			AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
			if (settings == null)
				return new AddressablesPlayerBuildResult { Error = "AddressableAssetSettings not found." };

			BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
			bool appleTarget = IsAppleTarget(target);
			List<AddressableAssetGroup> groups = GetBackgroundAssetGroups(settings);

			// Удаляем старые бандлы, чтобы в пак не попали файлы от прошлых сборок.
			if (appleTarget)
			{
				foreach (string dir in groups.Select(g => GetBuildDir(settings, g)).Distinct())
				{
					if (Directory.Exists(dir))
						Directory.Delete(dir, true);
				}
			}

			AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
			if (!string.IsNullOrEmpty(result.Error))
			{
				Debug.LogError($"[BackgroundAssets] Addressables build failed: {result.Error}");
				return result;
			}

			if (!appleTarget)
			{
				Debug.Log($"[BackgroundAssets] Active target is {target}: asset pack is not built (only iOS/tvOS/visionOS/macOS).");
				return result;
			}

			if (groups.Count == 0)
			{
				Debug.LogWarning("[BackgroundAssets] No Addressables groups use AppleBackgroundAssetBundleProvider — nothing to pack.");
				return result;
			}

			try
			{
				StagePack(settings, groups, result, target);
				WriteHelperScripts();
				if (runPackager)
					RunBaPackage();
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
				result.Error = "Asset pack staging failed: " + ex.Message;
			}

			return result;
		}

		static void StagePack(AddressableAssetSettings settings, List<AddressableAssetGroup> groups,
			AddressablesPlayerBuildResult result, BuildTarget target)
		{
			if (!System.Text.RegularExpressions.Regex.IsMatch(AssetPackId, "^[A-Za-z0-9-]+$"))
				throw new InvalidOperationException($"Asset pack ID '{AssetPackId}' is invalid: App Store Connect allows only letters, digits and hyphens.");

			var groupDirs = groups.Select(g => NormalizeDir(GetBuildDir(settings, g))).Distinct().ToList();

			var bundlePaths = new List<string>();
			foreach (AddressablesPlayerBuildResult.BundleBuildResult bundle in result.AssetBundleBuildResults)
			{
				string full = Path.GetFullPath(Path.IsPathRooted(bundle.FilePath)
					? bundle.FilePath
					: Path.Combine(ProjectRoot, bundle.FilePath));

				bool fromGroup = bundle.SourceAssetGroup != null && groups.Contains(bundle.SourceAssetGroup);
				bool inGroupDir = groupDirs.Any(d => full.StartsWith(d, StringComparison.OrdinalIgnoreCase));
				if ((fromGroup || inGroupDir) && File.Exists(full) && !bundlePaths.Contains(full))
					bundlePaths.Add(full);
			}

			if (bundlePaths.Count == 0)
				throw new InvalidOperationException("Addressables build produced no bundles for the Background Assets groups.");

			// Провайдер ищет бандл в паке только по имени файла — имена должны быть уникальны.
			var duplicate = bundlePaths.GroupBy(p => Path.GetFileName(p)).FirstOrDefault(g => g.Count() > 1);
			if (duplicate != null)
				throw new InvalidOperationException($"Duplicate bundle file name '{duplicate.Key}' — Background Assets uses a flat namespace.");

			if (Directory.Exists(PackStagingDir))
				Directory.Delete(PackStagingDir, true);
			Directory.CreateDirectory(PackStagingDir);
			Directory.CreateDirectory(OutputRoot);
			if (File.Exists(ArchivePath))
				File.Delete(ArchivePath);

			long totalBytes = 0;
			var fileNames = new List<string>();
			foreach (string src in bundlePaths)
			{
				string name = Path.GetFileName(src);
				File.Copy(src, Path.Combine(PackStagingDir, name), true);
				totalBytes += new FileInfo(src).Length;
				fileNames.Add(name);
			}

			File.WriteAllText(ManifestPath, BuildManifestJson(fileNames, ToApplePlatform(target)), new UTF8Encoding(false));

			Debug.Log($"[BackgroundAssets] Staged asset pack '{AssetPackId}': {fileNames.Count} bundle(s), " +
			          $"{totalBytes / (1024f * 1024f):0.0} MB → {PackStagingDir}\n" + string.Join("\n", fileNames));
		}

		static string BuildManifestJson(List<string> fileNames, string platform)
		{
			string policy = DownloadPolicy == "onDemand"
				? "\"onDemand\": {}"
				: $"\"{DownloadPolicy}\": {{ \"installationEventTypes\": [\"firstInstallation\", \"subsequentUpdate\"] }}";

			var sb = new StringBuilder();
			sb.AppendLine("{");
			sb.AppendLine($"  \"assetPackID\": \"{AssetPackId}\",");
			sb.AppendLine($"  \"downloadPolicy\": {{ {policy} }},");
			sb.AppendLine("  \"fileSelectors\": [");
			for (int i = 0; i < fileNames.Count; i++)
				sb.AppendLine($"    {{ \"file\": \"{fileNames[i]}\" }}{(i < fileNames.Count - 1 ? "," : "")}");
			sb.AppendLine("  ],");
			sb.AppendLine($"  \"platforms\": [\"{platform}\"]");
			sb.AppendLine("}");
			return sb.ToString();
		}

		static void WriteHelperScripts()
		{
			string package = "#!/bin/sh\n" +
			                 "# Собирает .aar из подготовленной папки пака (нужен Xcode 26+).\n" +
			                 "# Промежуточные файлы — здесь (Library/BackgroundAssetPacks), результат — в Builds/BackgroundAssetPacks.\n" +
			                 "set -e\n" +
			                 "cd \"$(dirname \"$0\")/" + AssetPackId + "\"\n" +
			                 "OUT=\"../../../" + OutputFolderName + "\"\n" +
			                 "mkdir -p \"$OUT\"\n" +
			                 "xcrun ba-package ../" + AssetPackId + ".manifest.json -o \"$OUT/" + AssetPackId + ".aar\"\n" +
			                 "echo \"Created $(cd \"$OUT\" && pwd)/" + AssetPackId + ".aar\"\n";

			string serve = "#!/bin/sh\n" +
			               "# Локальный mock-сервер Background Assets.\n" +
			               "# Использование: ./serve.sh [host]  (по умолчанию — IP интерфейса en0).\n" +
			               "# host должен совпадать с именем/IP в SSL-сертификате (см. README.md).\n" +
			               "set -e\n" +
			               "cd \"$(dirname \"$0\")\"\n" +
			               "HOST=\"${1:-$(ipconfig getifaddr en0)}\"\n" +
			               "echo \"URL Override на устройстве: https://$HOST\"\n" +
			               "xcrun ba-serve --host \"$HOST\" -p 443 " + AssetPackId + ".aar\n";

			File.WriteAllText(Path.Combine(StagingRoot, "package.sh"), package.Replace("\r\n", "\n"), new UTF8Encoding(false));
			File.WriteAllText(Path.Combine(OutputRoot, "serve.sh"), serve.Replace("\r\n", "\n"), new UTF8Encoding(false));
		}

		static void RunBaPackage()
		{
			if (Application.platform != RuntimePlatform.OSXEditor)
			{
				Debug.Log("[BackgroundAssets] Not on macOS — .aar is not created. Copy Library/BackgroundAssetPacks to a Mac and run ./package.sh (.aar goes to Builds/BackgroundAssetPacks).");
				return;
			}

			var psi = new ProcessStartInfo("xcrun", $"ba-package \"{ManifestPath}\" -o \"{ArchivePath}\"")
			{
				WorkingDirectory = PackStagingDir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
			};

			try
			{
				using (Process process = Process.Start(psi))
				{
					string stdout = process.StandardOutput.ReadToEnd();
					string stderr = process.StandardError.ReadToEnd();
					process.WaitForExit();

					if (process.ExitCode == 0 && File.Exists(ArchivePath))
						Debug.Log($"[BackgroundAssets] ba-package OK → {ArchivePath}\n{stdout}");
					else
						Debug.LogError($"[BackgroundAssets] ba-package failed (exit {process.ExitCode}).\n{stdout}\n{stderr}");
				}
			}
			catch (Exception ex)
			{
				Debug.LogError($"[BackgroundAssets] Could not run 'xcrun ba-package' (Xcode 26+ required): {ex.Message}");
			}
		}

		public static List<AddressableAssetGroup> GetBackgroundAssetGroups(AddressableAssetSettings settings)
		{
			string providerName = typeof(AppleBackgroundAssetBundleProvider).FullName;
			return settings.groups
				.Where(g => g != null)
				.Where(g =>
				{
					var schema = g.GetSchema<BundledAssetGroupSchema>();
					return schema != null && schema.IncludeInBuild &&
					       schema.AssetBundleProviderType.ClassName == providerName;
				})
				.ToList();
		}

		static string GetBuildDir(AddressableAssetSettings settings, AddressableAssetGroup group)
		{
			string path = group.GetSchema<BundledAssetGroupSchema>().BuildPath.GetValue(settings);
			return Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path);
		}

		static string NormalizeDir(string dir)
		{
			string full = Path.GetFullPath(dir);
			return full.EndsWith(Path.DirectorySeparatorChar.ToString()) ? full : full + Path.DirectorySeparatorChar;
		}

		public static bool IsAppleTarget(BuildTarget target) =>
			target == BuildTarget.iOS || target == BuildTarget.tvOS ||
			target == BuildTarget.VisionOS || target == BuildTarget.StandaloneOSX;

		static string ToApplePlatform(BuildTarget target)
		{
			switch (target)
			{
				case BuildTarget.tvOS: return "tvOS";
				case BuildTarget.VisionOS: return "visionOS";
				case BuildTarget.StandaloneOSX: return "macOS";
				default: return "iOS";
			}
		}

		/// <summary>
		/// Создаёт DefaultAppleBuildProfile и подключает Apple Hosting Configuration к шагу Apple.BackgroundAssets.
		/// Работает только в Unity на macOS (ограничение плагинов Apple).
		/// </summary>
		static string EnsureAppleBuildProfile()
		{
#if UNITY_EDITOR_OSX && (UNITY_IOS || UNITY_TVOS || UNITY_STANDALONE_OSX || UNITY_VISIONOS)
			Apple.Core.AppleBuildProfile profile = Apple.Core.AppleBuildProfile.DefaultProfile();
			profile.ResolveBuildSteps();

			Apple.BackgroundAssets.BuildStep step = profile.buildSteps.Values.OfType<Apple.BackgroundAssets.BuildStep>().FirstOrDefault();
			if (step == null)
				throw new InvalidOperationException("Apple.BackgroundAssets build step not found in Apple Build Profile.");

			step.IsEnabled = true;
			if (step.HostingConfiguration == null)
			{
				step.HostingConfiguration = AssetDatabase.LoadAssetAtPath<Apple.BackgroundAssets.HostingConfiguration>(HostingConfigurationPath);
				if (step.HostingConfiguration == null)
				{
					var hosting = ScriptableObject.CreateInstance<Apple.BackgroundAssets.AppleHostingConfiguration>();
					AssetDatabase.CreateAsset(hosting, HostingConfigurationPath);
					step.HostingConfiguration = hosting;
				}
			}

			EditorUtility.SetDirty(step);
			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssets();
			return $"Apple Build Profile OK: hosting={step.HostingConfiguration.GetType().Name}, " +
			       $"downloader={step.DownloaderExtensionBundleId}, appGroup={step.AppGroupId}";
#else
			Debug.LogWarning("[BackgroundAssets] Сборка не на macOS: плагины Apple не добавят Downloader-расширение " +
			                 "и ключи Background Assets в Xcode-проект. Собирайте iOS в Unity на Mac.");
			return "Apple Build Profile skipped (not macOS editor).";
#endif
		}
	}
}
