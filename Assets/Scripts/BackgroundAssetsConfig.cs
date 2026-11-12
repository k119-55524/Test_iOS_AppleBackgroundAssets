namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Общие настройки Apple Background Assets — единственное место, где задаётся ID пака.
	/// Используется и при сборке (BackgroundAssetsBuild создаёт .aar с этим ID),
	/// и в рантайме (BackgroundAssetsTestController запрашивает пак по этому ID).
	/// </summary>
	public static class BackgroundAssetsConfig
	{
		/// <summary>ID Apple asset pack (в манифесте .aar и в App Store Connect).</summary>
		public const string AssetPackId = "test_textures";
	}
}
