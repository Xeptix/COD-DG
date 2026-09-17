using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CODDowngrader.Steam;

namespace CODDowngrader.Gui;

/// <summary>A language a download can be in, as the language list shows it: its flag, its name, and whether it is Steam's for this PC.</summary>
public sealed record LanguageChoice(string Code, string Name, Bitmap? Flag, bool IsDefault)
{
    public string Note => IsDefault ? "what Steam downloads for you" : "";
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

    public static LanguageChoice Choice(string code, string? defaultCode) =>
        new(code, SteamLanguages.Name(code), Of(code), string.Equals(code, defaultCode, StringComparison.OrdinalIgnoreCase));
}
