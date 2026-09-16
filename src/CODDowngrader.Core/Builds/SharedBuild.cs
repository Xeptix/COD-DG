using System.Globalization;
using System.Text;
using CODDowngrader.App;
using CODDowngrader.Cli;
using CODDowngrader.Jobs;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Builds;

/// <summary>
/// A build as one person hands it to another: the game, what the build is called, every depot's manifest, which of its files,
/// and whether the games sharing the install folder go along. Plain lines in the words of the command line's options, so it
/// reads in a chat message and pastes back in whole. It holds no game files: whoever opens it downloads them from Steam, with
/// an account that owns the game.
/// </summary>
/// <param name="Only">content or binaries; null for every file.</param>
/// <param name="Files">The files chosen one by one, instead of <paramref name="Only"/>.</param>
public sealed record SharedBuild(uint AppId, string Game, string Title, IReadOnlyDictionary<uint, ulong> Manifests,
    string? Only = null, IReadOnlyList<string>? Files = null, bool Siblings = false)
{
    public const string Header = "COD Downgrader shared build";

    public IReadOnlyList<string> FileNames => Files ?? Array.Empty<string>();

    /// <summary>The part of the build in words, as a downgrade's title names it; null for all of it.</summary>
    public string? PartLabel => FileNames.Count > 0
        ? FileNames.Count == 1 ? "1 chosen file" : $"{FileNames.Count} chosen files"
        : Only switch
        {
            "content" => "content only",
            "binaries" => "exes and DLLs only",
            _ => null,
        };

    public string Text()
    {
        var text = new StringBuilder();
        text.Append(Header).Append('\n');
        text.Append("game ").Append(AppId.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(OneLine(Game)).Append('\n');
        text.Append("title ").Append(OneLine(Title)).Append('\n');
        foreach (var (depot, manifest) in Manifests.OrderBy(m => m.Key))
            text.Append("manifest ").Append(depot.ToString(CultureInfo.InvariantCulture)).Append('=').Append(manifest.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (FileNames.Count > 0)
        {
            foreach (var name in FileNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) text.Append("file ").Append(OneLine(name)).Append('\n');
        }
        else if (Only is "content" or "binaries")
        {
            text.Append("only ").Append(Only).Append('\n');
        }
        if (Siblings) text.Append("siblings\n");
        return text.ToString();
    }

    /// <summary>A file name to save it under.</summary>
    public string FileName => Interactive.Safe($"COD Downgrader build - {Game} - {Title}") + ".txt";

    static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// A shared build from its text, as it was pasted: blank lines, a chat's code fences and indentation are passed over, and so
    /// are lines this version does not know. Null with <paramref name="error"/> set when it names no game or no manifest.
    /// </summary>
    public static SharedBuild? Read(string text, out string? error)
    {
        error = null;
        uint? app = null;
        var game = "";
        string? title = null;
        string? only = null;
        var siblings = false;
        var manifests = new Dictionary<uint, ulong>();
        var files = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Trim('`').Trim();
            if (line.Length == 0 || line.StartsWith(Header, StringComparison.OrdinalIgnoreCase)) continue;

            var cut = line.IndexOfAny(new[] { ' ', '\t' });
            var word = (cut < 0 ? line : line[..cut]).ToLowerInvariant();
            var rest = cut < 0 ? "" : line[(cut + 1)..].Trim();
            switch (word)
            {
                case "game":
                {
                    var space = rest.IndexOfAny(new[] { ' ', '\t' });
                    var id = space < 0 ? rest : rest[..space];
                    if (uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0)
                    {
                        app = parsed;
                        game = space < 0 ? "" : rest[(space + 1)..].Trim();
                    }
                    break;
                }
                case "title":
                    title = rest;
                    break;
                case "manifest":
                {
                    var parts = rest.Split(new[] { '=', ':', ' ', '\t' }, 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2
                        && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depot) && depot != 0
                        && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) && manifest != 0)
                        manifests[depot] = manifest;
                    break;
                }
                case "only":
                    only = rest.ToLowerInvariant() is "content" or "binaries" ? rest.ToLowerInvariant() : null;
                    break;
                case "file":
                    if (ManifestFile.NormalizeName(rest) is { Length: > 0 } name && !files.Contains(name, StringComparer.OrdinalIgnoreCase)) files.Add(name);
                    break;
                case "siblings":
                    siblings = true;
                    break;
            }
        }

        if (app is null || manifests.Count == 0)
        {
            error = app is null && manifests.Count == 0
                ? $"That is not a shared build: one starts with \"{Header}\" and names a game and its manifests."
                : app is null
                    ? "That shared build names no game."
                    : "That shared build names no manifests.";
            return null;
        }
        return new SharedBuild(app.Value, game, title is { Length: > 0 } ? title : "A shared build", manifests, files.Count > 0 ? null : only, files, siblings);
    }

    /// <summary>The words a part of a build is recorded under, as --only takes them: content or binaries, or null.</summary>
    public static string? OnlyOf(string? partLabel) => partLabel switch
    {
        "content only" => "content",
        "exes and DLLs only" => "binaries",
        _ => null,
    };

    /// <summary>
    /// A build as a job named it: its manifests, what it is called, and the part of it. Depots that belong to a game sharing the
    /// install folder are left out, since the one who opens it has <paramref name="siblings"/> for those.
    /// </summary>
    /// <param name="files">File names separated by commas, as --files takes them.</param>
    public static SharedBuild Of(GameEntry game, IReadOnlyDictionary<uint, ulong> manifests, string title, string? only, string? files, bool siblings)
    {
        var own = game.Owners.Count == 0 ? manifests : manifests.Where(m => game.Owners.ContainsKey(m.Key)).ToDictionary(m => m.Key, m => m.Value);
        if (own.Count == 0) own = manifests;
        var names = (files ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ManifestFile.NormalizeName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new SharedBuild(game.AppId, game.Name, title, own, names.Count > 0 ? null : only is "content" or "binaries" ? only : null, names, siblings);
    }

    /// <summary>A downgrade written into a game, as its record keeps it.</summary>
    public static SharedBuild Of(AppliedRecord record, GameEntry game) =>
        Of(game, IdMap.Manifests(record.TargetManifests), record.Build, OnlyOf(record.Part),
            record.Part == "chosen files" ? string.Join(",", record.Written.Select(w => w.Name)) : null,
            record.AppIds.Count > 1);

    /// <summary>A patch folder, as its record keeps it.</summary>
    public static SharedBuild Of(PatchRecord record, GameEntry game) =>
        Of(game, IdMap.Manifests(record.TargetManifests), record.Build, OnlyOf(record.Part),
            record.Part == "chosen files" ? string.Join(",", record.Files.Select(f => f.Name)) : null,
            siblings: false);

    /// <summary>A whole build downloaded into a folder of its own.</summary>
    public static SharedBuild Of(DownloadPart part, GameEntry game) =>
        Of(game, part.ManifestMap(), part.Build, only: null, files: null, siblings: false);
}

/// <summary>
/// A shared build as this PC takes it: the game, what a job needs to name the build, what it is called here, and what did not
/// carry over. A build this PC already knows by its manifests is named as this PC names it.
/// </summary>
public sealed record SharedTarget(GameEntry Game, JobSettings Settings, string Key, string Title, Build? Build, IReadOnlyList<string> Notes);

public static class SharedBuilds
{
    /// <summary>The game a shared build is for, here. Null with <paramref name="error"/> set when there is none to take it.</summary>
    public static GameEntry? GameOf(GameLibrary library, SharedBuild shared, out string? error)
    {
        error = null;
        var game = library.Entries.FirstOrDefault(e => e.AppId == shared.AppId);
        if (game is null)
        {
            error = $"That build is for Steam app {shared.AppId}{(shared.Game.Length > 0 ? $", {shared.Game}" : "")}, which is not a Call of Duty COD Downgrader knows.";
            return null;
        }
        if (!game.Downgradable)
        {
            error = $"{game.Name}: {game.Title!.NotDowngradable}";
            return null;
        }
        return game;
    }

    /// <summary>
    /// What a shared build comes to for <paramref name="game"/>: its manifests for the depots this game has here, named as one of
    /// the builds <paramref name="history"/> knows when every one of them matches it. Null with <paramref name="error"/> set when
    /// none of its depots is part of the game.
    /// </summary>
    public static SharedTarget? Resolve(GameEntry game, BuildHistoryResult history, SharedBuild shared, out string? error)
    {
        error = null;
        var depots = new HashSet<uint>(game.Owners.Keys);
        if (game.InstalledManifests is { } installed) depots.UnionWith(installed.Keys);
        if (game.LatestManifests is { } latest) depots.UnionWith(latest.Keys);

        var usable = depots.Count == 0
            ? new Dictionary<uint, ulong>(shared.Manifests)
            : shared.Manifests.Where(m => depots.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);
        if (usable.Count == 0)
        {
            error = $"None of the depots in that build is part of {game.Name} here.";
            return null;
        }

        var notes = new List<string>();
        var leftOut = shared.Manifests.Keys.Where(d => !usable.ContainsKey(d)).OrderBy(d => d).ToList();
        if (leftOut.Count > 0)
            notes.Add($"{Depots(leftOut)} {(leftOut.Count == 1 ? "is" : "are")} not part of {game.Name} here, so {(leftOut.Count == 1 ? "it is" : "they are")} left out.");

        var files = shared.FileNames.Count > 0 ? string.Join(",", shared.FileNames) : null;
        var index = -1;
        for (var i = 0; i < history.Builds.Count && index < 0; i++)
        {
            var build = history.Builds[i];
            if (build.Unknown.Count == 0 && usable.All(m => build.Manifests.TryGetValue(m.Key, out var have) && have == m.Value))
                index = i;
        }

        if (index >= 0)
        {
            var key = Selectors.Keys(history.Builds)[index];
            var settings = new JobSettings { Build = key, Only = shared.Only, Files = files, Siblings = shared.Siblings };
            return new SharedTarget(game, settings, key, Format.BuildTitles(game, history.Builds)[index], history.Builds[index], notes);
        }

        var keeps = game.Owners.Keys.Where(d => !usable.ContainsKey(d)).OrderBy(d => d).ToList();
        if (keeps.Count > 0)
            notes.Add($"{Depots(keeps)} {(keeps.Count == 1 ? "is" : "are")} not in that build, so {(keeps.Count == 1 ? "it keeps" : "they keep")} the build {(keeps.Count == 1 ? "it is" : "they are")} on here.");
        var named = new JobSettings
        {
            Manifests = usable.OrderBy(m => m.Key).Select(m => $"{m.Key.ToString(CultureInfo.InvariantCulture)}={m.Value.ToString(CultureInfo.InvariantCulture)}").ToList(),
            Label = shared.Title,
            Only = shared.Only,
            Files = files,
            Siblings = shared.Siblings,
        };
        return new SharedTarget(game, named, "shared", shared.Title, null, notes);
    }

    static string Depots(IReadOnlyList<uint> depots) =>
        depots.Count == 1 ? $"Depot {depots[0]}" : $"Depots {string.Join(", ", depots)}";
}
