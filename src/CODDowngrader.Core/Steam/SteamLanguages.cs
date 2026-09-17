using Microsoft.Win32;

namespace CODDowngrader.Steam;

/// <summary>Steam's language codes, as product info names a depot's language and the client names the one it downloads.</summary>
public static class SteamLanguages
{
    /// <summary>Every language Steam has, in the order Steam's own language menu lists them, with the names it gives them.</summary>
    static readonly (string Code, string Name)[] Known =
    {
        ("english", "English"),
        ("arabic", "Arabic"),
        ("bulgarian", "Bulgarian"),
        ("schinese", "Simplified Chinese"),
        ("tchinese", "Traditional Chinese"),
        ("czech", "Czech"),
        ("danish", "Danish"),
        ("dutch", "Dutch"),
        ("finnish", "Finnish"),
        ("french", "French"),
        ("german", "German"),
        ("greek", "Greek"),
        ("hungarian", "Hungarian"),
        ("indonesian", "Indonesian"),
        ("italian", "Italian"),
        ("japanese", "Japanese"),
        ("koreana", "Korean"),
        ("norwegian", "Norwegian"),
        ("polish", "Polish"),
        ("portuguese", "Portuguese - Portugal"),
        ("brazilian", "Portuguese - Brazil"),
        ("romanian", "Romanian"),
        ("russian", "Russian"),
        ("spanish", "Spanish - Spain"),
        ("latam", "Spanish - Latin America"),
        ("swedish", "Swedish"),
        ("thai", "Thai"),
        ("turkish", "Turkish"),
        ("ukrainian", "Ukrainian"),
        ("vietnamese", "Vietnamese"),
    };

    public const string English = "english";

    /// <summary>What Steam calls a language, or the code itself for one this list does not have.</summary>
    public static string Name(string code) =>
        Known.FirstOrDefault(k => k.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code;

    /// <summary>Where a language goes in a list: English first, then as Steam's menu has them, then any others.</summary>
    public static int Order(string code)
    {
        for (var i = 0; i < Known.Length; i++)
            if (Known[i].Code.Equals(code, StringComparison.OrdinalIgnoreCase)) return i;
        return Known.Length;
    }

    /// <summary>
    /// The language the Steam client downloads new games in: HKCU\Software\Valve\Steam Language on Windows, and the same key
    /// in ~/.steam/registry.vdf elsewhere. Null when neither says.
    /// </summary>
    public static string? ClientLanguage()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Clean(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "Language", null) as string);

            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "registry.vdf");
            if (!File.Exists(file)) return null;
            var steam = KvNode.Load(file)["Registry"]?["HKCU"]?["Software"]?["Valve"]?["Steam"];
            return Clean(steam?.Get("language"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }
}
