using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CODDowngrader.Builds;
using CODDowngrader.Catalog;
using CODDowngrader.Patching;

namespace CODDowngrader.App;

/// <summary>A build made on this PC: a downgrade written into a game, a folder applied to one, a whole build downloaded, or a patch folder saved.</summary>
public sealed class MadeBuild
{
    /// <summary>ingame, apply, download or patch, as the commands are named.</summary>
    public string Kind { get; set; } = "";

    /// <summary>When it was made. For a build written into a game, the time its record keeps, which is how the two are matched.</summary>
    public DateTimeOffset Made { get; set; }

    public uint AppId { get; set; }
    public string Game { get; set; } = "";
    public string Build { get; set; } = "";

    /// <summary>The part of the build, such as "content only"; null for all of it.</summary>
    public string? Part { get; set; }

    /// <summary>Where it is: the game's install folder for a downgrade or a folder applied, the folder a download or a patch went into.</summary>
    public string Folder { get; set; } = "";

    /// <summary>A patch's starting build, or the folder a build was applied from.</summary>
    public string? From { get; set; }

    /// <summary>The build as a shared build, kept so it can be shared after its folder has gone.</summary>
    public string Shared { get; set; } = "";

    [JsonIgnore]
    public string Title => Part is null || Build.EndsWith(Part, StringComparison.Ordinal) ? Build : $"{Build}, {Part}";

    /// <summary>What was done, in words.</summary>
    [JsonIgnore]
    public string What => Kind switch
    {
        "ingame" => "Put into the game",
        "apply" => "Put into the game from a folder",
        "download" => "Downloaded into a folder of its own",
        "patch" => From is { Length: > 0 } from ? $"Saved as a patch folder, from {from}" : "Saved as a patch folder",
        _ => Kind,
    };
}

[JsonSerializable(typeof(List<MadeBuild>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class MadeJson : JsonSerializerContext
{
}

/// <summary>
/// Every build made on this PC, kept in builds.json in the tool's folder so they can be found and shared again: whichever way
/// the tool ran, a download, a downgrade, a patch folder or a folder applied adds one when it finishes. --no-remember adds none.
/// </summary>
public static class MadeBuilds
{
    public static string DefaultPath => Path.Combine(AppState.Folder, "builds.json");

    /// <summary>
    /// The builds, oldest first. Downgrades written into games that the list does not have (made before it existed) are added,
    /// with <paramref name="library"/> naming their games.
    /// </summary>
    public static List<MadeBuild> Load(GameLibrary? library = null, string? path = null, string? appliedFolder = null, bool? writing = null)
    {
        path ??= DefaultPath;
        var builds = Read(path);

        var added = false;
        foreach (var record in PatchStore.AllApplied(appliedFolder))
        {
            if (builds.Any(b => b.Kind is "ingame" or "apply" && Matches(b, record))) continue;
            var game = library?.Entries.FirstOrDefault(e => record.AppIds.Contains(e.AppId))
                       ?? new GameEntry { AppId = record.AppIds.FirstOrDefault(), Name = record.Game, Owners = new Dictionary<uint, uint>() };
            builds.Add(FromApplied(record, game, "ingame"));
            added = true;
        }
        builds.Sort((a, b) => a.Made.CompareTo(b.Made));
        if (added && (writing ?? Remembered.Writing)) Write(path, builds);
        return builds;
    }

    /// <summary>Adds a build to the list, unless this run remembers nothing new.</summary>
    public static void Add(MadeBuild build, string? path = null, bool? writing = null)
    {
        if (!(writing ?? Remembered.Writing)) return;
        path ??= DefaultPath;
        var builds = Read(path);
        builds.Add(build);
        Write(path, builds);
    }

    /// <summary>Takes a build off the list. Nothing it made is touched.</summary>
    public static void Remove(MadeBuild build, string? path = null)
    {
        path ??= DefaultPath;
        var builds = Read(path);
        builds.RemoveAll(b => b.Kind == build.Kind && b.AppId == build.AppId && PathRules.Comparer.Equals(b.Folder, build.Folder)
                              && Math.Abs((b.Made - build.Made).TotalSeconds) < 1);
        Write(path, builds);
    }

    /// <summary>
    /// The build a finished job made, from the record it left: the downgrade's record in the tool's folder, or the patch or
    /// download record in its folder. Null for a job that changed nothing, or whose record cannot be read.
    /// </summary>
    public static MadeBuild? FromJob(string kind, JsonObject result, GameEntry game, string? appliedFolder = null)
    {
        var written = result["written"] is JsonValue w && w.TryGetValue<int>(out var files) ? files : -1;
        var removed = result["removed"] is JsonValue r && r.TryGetValue<int>(out var gone) ? gone : 0;
        if (kind is "ingame" or "apply" or "patch" && written == 0 && removed == 0) return null;
        var folder = result["folder"] is JsonValue f && f.TryGetValue<string>(out var text) ? text : null;
        var shared = result["shared"] is JsonValue s && s.TryGetValue<string>(out var sharedText) ? sharedText : null;

        MadeBuild? made = kind switch
        {
            "ingame" or "apply" when game.Installed is { } app && PatchStore.AppliedTo(app.InstallDir, appliedFolder) is { } record
                => FromApplied(record, game, kind, kind == "apply" ? folder : null),
            "patch" when folder is not null && PatchStore.LoadPatch(folder) is { } patch => FromPatch(patch, game, folder),
            "download" when folder is not null && AppState.LoadRecord(folder, out _)?.For(game.AppId) is { Complete: true } part
                => FromDownload(part, game, folder),
            _ => null,
        };
        if (made is not null && shared is { Length: > 0 }) made.Shared = shared;
        return made;
    }

    public static MadeBuild FromApplied(AppliedRecord record, GameEntry game, string kind, string? from = null) => new()
    {
        Kind = kind,
        Made = record.Applied,
        AppId = game.AppId,
        Game = game.Name,
        Build = record.Build,
        Part = record.Part,
        Folder = record.InstallDir,
        From = from,
        Shared = SharedBuild.Of(record, game).Text(),
    };

    public static MadeBuild FromPatch(PatchRecord record, GameEntry game, string folder) => new()
    {
        Kind = "patch",
        Made = record.Created,
        AppId = game.AppId,
        Game = game.Name,
        Build = record.Build,
        Part = record.Part,
        Folder = folder,
        From = record.From,
        Shared = SharedBuild.Of(record, game).Text(),
    };

    public static MadeBuild FromDownload(DownloadPart part, GameEntry game, string folder) => new()
    {
        Kind = "download",
        Made = part.Finished ?? DateTimeOffset.Now,
        AppId = game.AppId,
        Game = game.Name,
        Build = part.Build,
        Folder = folder,
        Shared = SharedBuild.Of(part, game).Text(),
    };

    /// <summary>Whether a downgrade on the list is the one written into its game now: the latest change there.</summary>
    public static bool InGame(MadeBuild build, string? appliedFolder = null) =>
        build.Kind is "ingame" or "apply" && PatchStore.AppliedTo(build.Folder, appliedFolder) is { } record && Matches(build, record);

    /// <summary>Whether a download or a patch on the list is still whole in its folder, to be put into its game.</summary>
    public static bool FolderHolds(MadeBuild build) => build.Kind switch
    {
        "patch" => PatchStore.LoadPatch(build.Folder) is { Complete: true },
        "download" => AppState.LoadRecord(build.Folder, out _)?.For(build.AppId) is { Complete: true },
        _ => false,
    };

    /// <summary>Where a build stands now: still in the game or the folder, changed since, or gone.</summary>
    public static string StateOf(MadeBuild build, GameLibrary? library = null, string? appliedFolder = null)
    {
        switch (build.Kind)
        {
            case "ingame" or "apply":
                if (PatchStore.AppliedTo(build.Folder, appliedFolder) is not { } record || !Matches(build, record))
                    return "Taken out of the game since, or replaced by a later downgrade.";
                if (library is null) return "In the game now.";
                return PatchApplier.StateOf(record, library.FolderManifests(record.InstallDir)) switch
                {
                    DowngradeState.Intact => "In the game now.",
                    DowngradeState.Unfinished => "Writing it into the game stopped part way.",
                    DowngradeState.FilesChanged => "In the game, and Steam has put back some of its own files since.",
                    _ => "In the game, and Steam has updated the game since.",
                };
            case "patch":
                return PatchStore.LoadPatch(build.Folder) is { Complete: true } ? "The patch folder is there." : "The patch folder is not there any more.";
            default:
                return AppState.LoadRecord(build.Folder, out _)?.For(build.AppId) is { Complete: true }
                    ? "The folder is there."
                    : "The folder is not there any more.";
        }
    }

    static bool Matches(MadeBuild build, AppliedRecord record) =>
        PathRules.Comparer.Equals(PathRules.Normalize(build.Folder), PathRules.Normalize(record.InstallDir))
        && Math.Abs((build.Made - record.Applied).TotalSeconds) < 1;

    static List<MadeBuild> Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), MadeJson.Default.ListMadeBuild) ?? new List<MadeBuild>() : new List<MadeBuild>();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<MadeBuild>();
        }
    }

    static void Write(string path, List<MadeBuild> builds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(builds, MadeJson.Default.ListMadeBuild));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The list is a convenience: failing to keep it never fails the build it lists.
        }
    }
}
