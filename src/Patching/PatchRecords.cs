using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CODDowngrader.App;

namespace CODDowngrader.Patching;

/// <summary>"COD Downgrader patch.json" in a patch folder: the builds it goes between, the files in it, and what it removes.</summary>
public sealed class PatchRecord
{
    public const string FileName = "COD Downgrader patch.json";

    public string Tool { get; set; } = "";
    public List<uint> AppIds { get; set; } = new();
    public string Game { get; set; } = "";

    /// <summary>The build the patch turns the game into.</summary>
    public string Build { get; set; } = "";

    /// <summary>The build the patch is made from.</summary>
    public string From { get; set; } = "";

    public DateTimeOffset Created { get; set; }

    /// <summary>Every file is downloaded and checked.</summary>
    public bool Complete { get; set; }

    public Dictionary<string, string> BaseManifests { get; set; } = new();
    public Dictionary<string, string> TargetManifests { get; set; } = new();
    public Dictionary<string, string> Owners { get; set; } = new();
    public List<PatchWrite> Files { get; set; } = new();
    public List<PatchRemove> Remove { get; set; } = new();
    public bool RemovesKnown { get; set; }

    public PatchPlan Plan() => new(Files, Remove, RemovesKnown);
}

/// <param name="HadOriginal">The game had a file at this path before, which the backup holds when one was kept.</param>
/// <param name="WrittenUtc">The file's last-write time once written; null until then.</param>
public sealed record AppliedFile(string Name, ulong Size, bool HadOriginal, DateTime? WrittenUtc);

/// <summary>
/// A build written into an installed game, kept in the tool's own folder: what Undo puts back, and what shows
/// that Steam has changed the game since.
/// </summary>
public sealed class AppliedRecord
{
    public string Tool { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public List<uint> AppIds { get; set; } = new();
    public string Game { get; set; } = "";
    public string Build { get; set; } = "";
    public DateTimeOffset Applied { get; set; }

    /// <summary>False while files are being written, so a run that stopped part way can still be undone.</summary>
    public bool Complete { get; set; }

    /// <summary>Steam's installed manifest of every depot in the folder when the build was written.</summary>
    public Dictionary<string, string> SteamManifests { get; set; } = new();

    public Dictionary<string, string> TargetManifests { get; set; } = new();
    public Dictionary<string, string> Owners { get; set; } = new();
    public List<AppliedFile> Written { get; set; } = new();
    public List<string> Removed { get; set; } = new();

    /// <summary>Where the files it replaced and removed went; null when they were deleted.</summary>
    public string? Backup { get; set; }
}

[JsonSerializable(typeof(PatchRecord))]
[JsonSerializable(typeof(AppliedRecord))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class PatchJson : JsonSerializerContext
{
}

/// <summary>Depot, app and manifest IDs as JSON strings, so no JSON reader rounds a 64-bit manifest ID.</summary>
public static class IdMap
{
    public static Dictionary<string, string> Write(IEnumerable<KeyValuePair<uint, ulong>> map) =>
        map.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value.ToString(CultureInfo.InvariantCulture));

    public static Dictionary<string, string> Write(IEnumerable<KeyValuePair<uint, uint>> map) =>
        map.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value.ToString(CultureInfo.InvariantCulture));

    public static Dictionary<uint, ulong> Manifests(Dictionary<string, string> map)
    {
        var result = new Dictionary<uint, ulong>();
        foreach (var (key, value) in map)
            if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var depot)
                && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest)) result[depot] = manifest;
        return result;
    }

    public static Dictionary<uint, uint> Owners(Dictionary<string, string> map)
    {
        var result = new Dictionary<uint, uint>();
        foreach (var (key, value) in map)
            if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var depot)
                && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var app)) result[depot] = app;
        return result;
    }
}

public static class PatchStore
{
    public static string AppliedFolder => Path.Combine(AppState.Folder, "applied");

    public static PatchRecord? LoadPatch(string folder) =>
        Load(Path.Combine(folder, PatchRecord.FileName), PatchJson.Default.PatchRecord);

    public static void SavePatch(string folder, PatchRecord record)
    {
        record.Tool = $"COD Downgrader {AppState.Version}";
        Write(Path.Combine(folder, PatchRecord.FileName), JsonSerializer.Serialize(record, PatchJson.Default.PatchRecord));
    }

    /// <summary>The downgrade written into the game installed in <paramref name="installDir"/>, if there is one.</summary>
    public static AppliedRecord? AppliedTo(string installDir, string? stateFolder = null) =>
        Load(AppliedPath(installDir, stateFolder), PatchJson.Default.AppliedRecord);

    public static void SaveApplied(AppliedRecord record, string? stateFolder = null)
    {
        record.Tool = $"COD Downgrader {AppState.Version}";
        var path = AppliedPath(record.InstallDir, stateFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Write(path, JsonSerializer.Serialize(record, PatchJson.Default.AppliedRecord));
    }

    public static void DeleteApplied(AppliedRecord record, string? stateFolder = null) =>
        File.Delete(AppliedPath(record.InstallDir, stateFolder));

    /// <summary>One record per install folder, named by a hash of the folder's path however it is spelled.</summary>
    static string AppliedPath(string installDir, string? stateFolder)
    {
        var key = PathRules.Normalize(installDir);
        if (OperatingSystem.IsWindows()) key = key.ToUpperInvariant();
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(stateFolder ?? AppliedFolder, name + ".json");
    }

    static T? Load<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void Write(string path, string json)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
