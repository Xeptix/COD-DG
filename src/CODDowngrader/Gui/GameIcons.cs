using Avalonia.Media.Imaging;
using CODDowngrader.App;

namespace CODDowngrader.Gui;

/// <summary>
/// The icons of the installed games: the icon of the exe Steam starts for each, or, for a game Steam starts some other way
/// (the Black Ops III Mod Tools start from a .bat), the icon Steam keeps for it in its library cache.
/// </summary>
public static class GameIcons
{
    /// <summary>Every installed game's icon as image bytes, read off the window's thread. Games with neither are left out.</summary>
    public static Dictionary<uint, byte[]> Read(GameLibrary library)
    {
        var icons = new Dictionary<uint, byte[]>();
        foreach (var game in library.Entries.Where(e => e.Installed is not null))
        {
            if (game.LaunchExe() is { } exe && ExeIcon.Read(exe) is { } ico)
            {
                icons[game.AppId] = ico;
                continue;
            }
            try
            {
                if (library.SteamIcon(game) is { } cached) icons[game.AppId] = File.ReadAllBytes(cached);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return icons;
    }

    /// <summary>An .ico or a .jpg as something the window can draw; null when it cannot be decoded.</summary>
    public static Bitmap? Bitmap(byte[]? ico)
    {
        if (ico is null) return null;
        try
        {
            using var stream = new MemoryStream(ico);
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
