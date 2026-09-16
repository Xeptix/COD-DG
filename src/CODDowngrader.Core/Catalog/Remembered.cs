using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Steam;

namespace CODDowngrader.Catalog;

/// <summary>Where a remembered manifest came from, which decides what it can tell a history.</summary>
public enum RememberedSource
{
    /// <summary>Steam's manifest cache had it, and <see cref="RememberedManifest.Time"/> is when that build was made.</summary>
    Built,

    /// <summary>Steam's content log fetched it, at <see cref="RememberedManifest.Time"/>.</summary>
    Fetched,

    /// <summary>Pasted from SteamDB's manifests table, with the time SteamDB first saw it.</summary>
    SteamDb,

    /// <summary>Steam had it installed.</summary>
    Installed,

    /// <summary>Steam's product info named it as the latest.</summary>
    Latest,

    /// <summary>Named by hand: pasted, or given with --manifest.</summary>
    Named,
}

public sealed record RememberedManifest(uint Depot, ulong Manifest, RememberedSource Source, DateTimeOffset? Time = null);

/// <summary>
/// Every Call of Duty manifest the tool has learned on this PC, kept in <c>remembered.txt</c> in the tool's folder so it outlives
/// Steam deleting its manifest cache and rotating its log, and so manifests pasted from SteamDB are there next time. One manifest
/// per line: depot, manifest, source, and a time where the source has one. Rows are only ever added, and a line that cannot be
/// read is skipped. --no-remember stops a run adding any; what is remembered already is still used.
/// </summary>
public sealed class Remembered
{
    const string Header = "# COD Downgrader remembers every Call of Duty manifest it learns on this PC: depot, manifest, source, time.";

    /// <summary>Whether this run adds what it learns. Set once, from --no-remember.</summary>
    public static bool Writing { get; set; } = true;

    public static string DefaultPath => Path.Combine(AppState.Folder, "remembered.txt");

    readonly string _path;
    readonly bool _writing;
    readonly List<RememberedManifest> _rows = new();
    readonly HashSet<(uint, ulong, RememberedSource, long)> _keys = new();

    Remembered(string path, bool writing)
    {
        _path = path;
        _writing = writing;
    }

    public IReadOnlyList<RememberedManifest> Rows => _rows;

    public static Remembered Load(string? path = null, bool? writing = null)
    {
        var remembered = new Remembered(path ?? DefaultPath, writing ?? Writing);
        try
        {
            if (File.Exists(remembered._path))
                foreach (var line in File.ReadLines(remembered._path))
                    if (Parse(line) is { } row) remembered.Keep(row);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience: a file that cannot be read is the same as none.
        }
        return remembered;
    }

    /// <summary>Adds the rows not already remembered, and writes them to the file unless this run does not remember.</summary>
    /// <summary>Jobs running together each append to the same file: one at a time.</summary>
    static readonly object FileGate = new();

    public void Learn(IEnumerable<RememberedManifest> rows)
    {
        List<RememberedManifest> added;
        lock (_rows) added = rows.Where(r => r.Depot != 0 && r.Manifest != 0 && Keep(r)).ToList();
        if (added.Count == 0 || !_writing) return;
        lock (FileGate)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = added.Select(Format).ToList();
            if (!File.Exists(_path)) lines.Insert(0, Header);
            File.AppendAllLines(_path, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    bool Keep(RememberedManifest row)
    {
        if (!_keys.Add((row.Depot, row.Manifest, row.Source, row.Time?.UtcTicks ?? 0))) return false;
        _rows.Add(row);
        return true;
    }

    public static string Format(RememberedManifest row) =>
        string.Join(' ', new[]
        {
            row.Depot.ToString(CultureInfo.InvariantCulture),
            row.Manifest.ToString(CultureInfo.InvariantCulture),
            row.Source.ToString().ToLowerInvariant(),
            row.Time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "",
        }).TrimEnd();

    public static RememberedManifest? Parse(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || words[0].StartsWith('#')
            || !uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depot)
            || !ulong.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var manifest)
            || !Enum.TryParse<RememberedSource>(words[2], ignoreCase: true, out var source))
            return null;
        DateTimeOffset? time = null;
        if (words.Length > 3)
        {
            if (!DateTimeOffset.TryParse(words[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return null;
            time = parsed.ToUniversalTime();
        }
        return new RememberedManifest(depot, manifest, source, time);
    }

    /// <summary>
    /// A history's input with what is remembered added, each row as what it was: build times as cached manifests, fetch times
    /// as the log's, SteamDB's first-seen times as list rows (the built-in list's row wins for a manifest both have). Rows of
    /// depots the input does not have are left out.
    /// </summary>
    public BuildInput Into(BuildInput input)
    {
        var depots = new HashSet<uint>(input.DepotIds);
        var mine = _rows.Where(r => depots.Contains(r.Depot) && r.Time is not null).ToList();
        if (mine.Count == 0) return input;

        var cached = input.Cached.ToList();
        var cachedIds = cached.Select(c => (c.DepotId, c.ManifestId)).ToHashSet();
        cached.AddRange(mine.Where(r => r.Source == RememberedSource.Built && cachedIds.Add((r.Depot, r.Manifest)))
            .Select(r => new CachedManifest(r.Depot, r.Manifest, "", r.Time!.Value, 0)));

        var seen = input.Seen.ToList();
        var seenIds = seen.Select(s => (s.DepotId, s.ManifestId, s.Time.UtcTicks)).ToHashSet();
        seen.AddRange(mine.Where(r => r.Source == RememberedSource.Fetched && seenIds.Add((r.Depot, r.Manifest, r.Time!.Value.UtcTicks)))
            .Select(r => new ManifestSeen(r.Time!.Value, r.Depot, r.Manifest)));
        seen.Sort((a, b) => a.Time.CompareTo(b.Time));

        var listed = input.Listed is null ? new Dictionary<uint, IReadOnlyList<ListedManifest>>() : new Dictionary<uint, IReadOnlyList<ListedManifest>>(input.Listed);
        foreach (var group in mine.Where(r => r.Source == RememberedSource.SteamDb).GroupBy(r => r.Depot))
        {
            var rows = listed.GetValueOrDefault(group.Key)?.ToList() ?? new List<ListedManifest>();
            var have = rows.Select(r => r.ManifestId).ToHashSet();
            rows.AddRange(group.Where(r => have.Add(r.Manifest)).Select(r => new ListedManifest(r.Manifest, r.Time!.Value)));
            listed[group.Key] = rows.OrderBy(r => r.FirstSeen).ToList();
        }

        return new BuildInput
        {
            DepotIds = input.DepotIds,
            Installed = input.Installed,
            Latest = input.Latest,
            Cached = cached,
            Seen = seen,
            Listed = listed,
            ListedUpdatesFrom = input.ListedUpdatesFrom,
        };
    }

    /// <summary>
    /// A history with the remembered manifests it does not know added to each depot's known manifests. They carry no time a
    /// history can place, so they make no build; they are there to pick from.
    /// </summary>
    public BuildHistoryResult Known(BuildHistoryResult history)
    {
        var depots = new Dictionary<uint, IReadOnlyList<KnownManifest>>(history.Depots);
        foreach (var group in _rows.Where(r => history.Depots.ContainsKey(r.Depot)).GroupBy(r => r.Depot))
        {
            var known = depots[group.Key].ToList();
            var have = known.Select(k => k.ManifestId).ToHashSet();
            known.AddRange(group.Where(r => have.Add(r.Manifest))
                .Select(r => new KnownManifest(r.Depot, r.Manifest, null, null, false, false, Remembered: true)));
            depots[group.Key] = known;
        }
        return history with { Depots = depots };
    }
}
