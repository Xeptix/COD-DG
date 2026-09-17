using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CODDowngrader.Steam;

namespace CODDowngrader.Gui;

/// <summary>
/// A language a build can be in, as the language list shows it: its flag, its name, and whether it is the one the game is
/// installed in or, for a game that is not, the one Steam downloads for this PC.
/// </summary>
public sealed record LanguageChoice(string Code, string Name, Bitmap? Flag, bool IsDefault, bool IsInstalled = false)
{
    public string Note => IsInstalled ? "installed" : IsDefault ? "what Steam downloads for you" : "";
    public bool HasFlag => Flag is not null;
}

/// <summary>The flags drawn by tools/make_flags.py, one per Steam language code, loaded once each.</summary>
public static class Flags
{
    static readonly Dictionary<string, Bitmap?> Loaded = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Of(string code)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(code, out var known)) return known;
            Bitmap? flag = null;
            try
            {
                var uri = new Uri($"avares://CODDowngrader/Assets/Flags/{code.ToLowerInvariant()}.png");
                if (AssetLoader.Exists(uri))
                {
                    using var stream = AssetLoader.Open(uri);
                    flag = new Bitmap(stream);
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
            {
            }
            Loaded[code] = flag;
            return flag;
        }
    }

    public static LanguageChoice Choice(string code, string? defaultCode, string? installedCode = null) =>
        new(code, SteamLanguages.Name(code), Of(code), string.Equals(code, defaultCode, StringComparison.OrdinalIgnoreCase),
            string.Equals(code, installedCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>The languages Steam has for a game, as the lists show them. Empty when it has one or none.</summary>
    public static List<LanguageChoice> For(App.GameLibrary library, App.GameEntry game)
    {
        var codes = library.Languages(game);
        if (codes.Count < 2) return new List<LanguageChoice>();
        var installed = game.Installed is not null ? library.LanguageOf(game.InLanguageOf ?? game) : null;
        var steamDefault = library.DefaultLanguage(game);
        return codes.Select(code => Choice(code, steamDefault, installed)).ToList();
    }
}
