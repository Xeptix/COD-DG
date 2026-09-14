using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CODDowngrader.App;

public sealed class Settings
{
    /// <summary>The Steam account name DepotDownloader has a saved login for. Never a password: it only ever sees those itself.</summary>
    public string? SteamAccount { get; set; }

    public string? LastDestinationRoot { get; set; }
}

/// <summary>
/// "COD Downgrader.json" in a download folder: which builds of which games the folder holds. It is
/// written before anything is copied or downloaded, so a stopped download can be recognised and carried
/// on, a different build is never written over this one without asking, and files copied from the
/// install are still known when the download finishes in a later attempt.
/// </summary>
public sealed class DownloadRecord
{
    public string Tool { get; set; } = "";
    public List<DownloadPart> Downloads { get; set; } = new();

    public DownloadPart? For(uint appId) => Downloads.FirstOrDefault(d => d.AppId == appId);
}

public sealed class DownloadPart
{
    public uint AppId { get; set; }
    public string Game { get; set; } = "";
    public string Build { get; set; } = "";
    public bool Complete { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Finished { get; set; }

    /// <summary>Depot to manifest. Strings, so no JSON reader rounds a 64-bit ID.</summary>
    public Dictionary<string, string> Manifests { get; set; } = new();

    /// <summary>Depots the signed-in account could not download.</summary>
    public List<string> Skipped { get; set; } = new();

    /// <summary>Depot to the files copied into this folder from the installed game for it, not yet checked against the download.</summary>
    public Dictionary<string, List<string>> CopiedFromInstall { get; set; } = new();

    public Dictionary<uint, ulong> ManifestMap() => Manifests
        .Where(kv => uint.TryParse(kv.Key, out _) && ulong.TryParse(kv.Value, out _))
        .ToDictionary(kv => uint.Parse(kv.Key, CultureInfo.InvariantCulture), kv => ulong.Parse(kv.Value, CultureInfo.InvariantCulture));

    public void SetManifests(IEnumerable<KeyValuePair<uint, ulong>> manifests) =>
        Manifests = manifests.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value.ToString(CultureInfo.InvariantCulture));

    public bool SameManifests(IReadOnlyDictionary<uint, ulong> manifests)
    {
        var mine = ManifestMap();
        return mine.Count == manifests.Count && manifests.All(kv => mine.TryGetValue(kv.Key, out var m) && m == kv.Value);
    }
}

[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(DownloadRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class StateJson : JsonSerializerContext
{
}

/// <summary>The tool's own folder: %LOCALAPPDATA%\COD Downgrader, or $XDG_DATA_HOME/cod-downgrader.</summary>
public static class AppState
{
    public const string RecordFileName = "COD Downgrader.json";

    public static string Version { get; } =
        (typeof(AppState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0")
        .Split('+')[0];

    public static string Folder { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "COD Downgrader")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cod-downgrader");

    public static string LogsFolder => Path.Combine(Folder, "logs");

    static string SettingsPath => Path.Combine(Folder, "settings.json");

    public static Settings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), StateJson.Default.Settings) ?? new Settings()
                : new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public static void SaveSettings(Settings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, StateJson.Default.Settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them never stops a download.
        }
    }

    /// <summary>The folder's record; null when there is none. <paramref name="unreadable"/> is set when a record exists but cannot be read.</summary>
    public static DownloadRecord? LoadRecord(string folder, out bool unreadable)
    {
        unreadable = false;
        var path = Path.Combine(folder, RecordFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllText(path), StateJson.Default.DownloadRecord);
            if (record is { Downloads.Count: > 0 }) return record;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        unreadable = true;
        return null;
    }

    public static void SaveRecord(string folder, DownloadRecord record)
    {
        record.Tool = $"COD Downgrader {Version}";
        var path = Path.Combine(folder, RecordFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, StateJson.Default.DownloadRecord));
        File.Move(temp, path, overwrite: true);
    }
}
