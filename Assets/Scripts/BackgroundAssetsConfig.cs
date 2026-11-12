namespace AppleBackgroundAssetsTest
{
	/// <summary>
	/// Общие настройки Apple Background Assets — единственное место, где задаётся ID пака.
	/// Используется и при сборке (BackgroundAssetsBuild создаёт .aar с этим ID),
	/// и в рантайме (BackgroundAssetsTestController запрашивает пак по этому ID).
	/// </summary>
	public static class BackgroundAssetsConfig
	{
		// ID Apple asset pack: попадает в манифест .aar (assetPackID) и в App Store Connect,
		// по нему же игра запрашивает пак в рантайме (AssetPackManager).
		//
		// ВАЖНО — правила для ID (проверено на практике):
		//  • Только латинские буквы, цифры и дефис: [A-Za-z0-9-].
		//    Подчёркивание «_», точка «.» и пробелы App Store Connect НЕ принимает.
		//    Симптом в Transporter при загрузке .aar:
		//      "There is an error with a URL parameter (400) Found invalid values: <ID>"
		//      "Не удалось получить список пакетов … для Apple ID … (-19243)"
		//    (BackgroundAssetsBuild дополнительно проверяет ID и остановит сборку, если он неверный.)
		//  • ID нельзя менять после того, как пак загружен и используется: новый ID = новый пак в App Store Connect.
		//    Заархивированный в App Store Connect пак удаляется со всеми версиями, а его ID больше нельзя использовать.
		//  • Для тестов используйте отдельные ID (TF1-Test…), чтобы не занимать боевые имена.
		//
		// Схема имён: "TF1-" + название пака, например
		//   TF1-Part1, TF1-Part2, TF1-Part3, TF1-Part4-Admissions, TF1-BonusMaterials
		// (по Addressables-группам TF1; на Android/Play Asset Delivery имена другие — там «_» допустимо).
		public const string AssetPackId = "TF1-Test-Package";
	}
}
