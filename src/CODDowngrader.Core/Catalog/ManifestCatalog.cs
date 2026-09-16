using System.Globalization;

namespace CODDowngrader.Catalog;

/// <summary>A public manifest of a depot, when SteamDB first saw it, and its size on disk once a PC's export has shown it.</summary>
public sealed record ListedManifest(ulong ManifestId, DateTimeOffset FirstSeen, ulong? Size = null);

/// <summary>
/// The built-in manifest list, Catalog/manifests.txt: every manifest of every depot of the Call of Duty
/// games, with when SteamDB first saw it, copied from SteamDB by hand and kept with tools/catalog.py.
/// It names and dates builds no file on this PC remembers.
/// </summary>
public sealed class ManifestCatalog
{
    public const string ResourceName = "manifests.txt";

    static readonly Lazy<ManifestCatalog> BuiltInList = new(LoadBuiltIn);

    ManifestCatalog(DateOnly? updated, DateTimeOffset? historyFrom, Dictionary<uint, IReadOnlyList<ListedManifest>> depots,
        Dictionary<uint, IReadOnlyDictionary<uint, uint>> apps)
    {
        Updated = updated;
        HistoryFrom = historyFrom;
        Depots = depots;
        Apps = apps;
    }

    public static ManifestCatalog Empty { get; } = new(null, null, new(), new());

    public static ManifestCatalog BuiltIn => BuiltInList.Value;

    /// <summary>When rows were last added.</summary>
    public DateOnly? Updated { get; }

    /// <summary>
    /// Updates SteamDB first saw before this are not used. Its rows from before then include manifests that
    /// were never the public build, and first-seen dates long after a build went up.
    /// </summary>
    public DateTimeOffset? HistoryFrom { get; }

    /// <summary>Each depot's public manifests, oldest first.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<ListedManifest>> Depots { get; }

    /// <summary>The depots a download of each app is made of, mapped to the app that owns them.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyDictionary<uint, uint>> Apps { get; }

    /// <summary>
    /// Depots never part of a download of a game Steam has not installed, though product info does not rule them out: Modern
    /// Warfare 2's German low-violence content has no language and no low-violence flag.
    /// </summary>
    public IReadOnlySet<uint> NotDefault { get; private init; } = new HashSet<uint>();

    /// <summary>Depot names, as SteamDB shows them.</summary>
    public IReadOnlyDictionary<uint, string> Names { get; private init; } = new Dictionary<uint, string>();

    static ManifestCatalog LoadBuiltIn()
    {
        using var stream = typeof(ManifestCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidDataException($"{ResourceName} is not built into this exe.");
        using var reader = new StreamReader(stream);
        return Parse(reader);
    }

    public static ManifestCatalog Parse(TextReader reader)
    {
        var inv = CultureInfo.InvariantCulture;
        DateOnly? updated = null;
        DateTimeOffset? historyFrom = null;
        var apps = new Dictionary<uint, IReadOnlyDictionary<uint, uint>>();
        var hidden = new HashSet<(uint, ulong)>();
        var notDefault = new HashSet<uint>();
        var names = new Dictionary<uint, string>();
        var rows = new Dictionary<uint, List<(ListedManifest Manifest, int Line)>>();

        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            var cut = line.IndexOf('#');
            var words = (cut < 0 ? line : line[..cut]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            try
            {
                switch (words[0])
                {
                    case "updated":
                        updated = DateOnly.ParseExact(words[1], "yyyy-MM-dd", inv);
                        break;
                    case "history-from":
                        historyFrom = DateTimeOffset.ParseExact(words[1], "yyyy-MM-dd", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                        break;
                    case "app":
                        var app = uint.Parse(words[1], NumberStyles.None, inv);
                        var depots = new Dictionary<uint, uint>();
                        foreach (var word in words.Skip(2))
                        {
                            var at = word.IndexOf('@');
                            var depot = uint.Parse(at < 0 ? word : word[..at], NumberStyles.None, inv);
                            depots[depot] = at < 0 ? app : uint.Parse(word[(at + 1)..], NumberStyles.None, inv);
                        }
                        apps[app] = depots;
                        break;
                    case "not-public":
                        hidden.Add((uint.Parse(words[1], NumberStyles.None, inv), ulong.Parse(words[2], NumberStyles.None, inv)));
                        break;
                    case "not-default":
                        notDefault.Add(uint.Parse(words[1], NumberStyles.None, inv));
                        break;
                    case "name":
                        var text = (cut < 0 ? line : line[..cut]).Trim();
                        names[uint.Parse(words[1], NumberStyles.None, inv)] = text.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries)[2];
                        break;
                    default:
                        var id = uint.Parse(words[0], NumberStyles.None, inv);
                        var seen = DateTimeOffset.ParseExact(words[2], "yyyy-MM-dd'T'HH:mm:ss'Z'", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                        ulong? size = words.Length > 3 ? ulong.Parse(words[3], NumberStyles.None, inv) : null;
                        if (!rows.TryGetValue(id, out var list)) rows[id] = list = new();
                        list.Add((new ListedManifest(ulong.Parse(words[1], NumberStyles.None, inv), seen, size), number));
                        break;
                }
            }
            catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException)
            {
                throw new InvalidDataException($"{ResourceName} line {number}: {line.Trim()}", e);
            }
        }

        // Rows are written newest first, so of two first seen in the same second the earlier line is newer.
        var ordered = rows.ToDictionary(
            r => r.Key,
            r => (IReadOnlyList<ListedManifest>)r.Value
                .Where(x => !hidden.Contains((r.Key, x.Manifest.ManifestId)))
                .OrderBy(x => x.Manifest.FirstSeen)
                .ThenByDescending(x => x.Line)
                .Select(x => x.Manifest)
                .ToList());
        return new ManifestCatalog(updated, historyFrom, ordered, apps) { NotDefault = notDefault, Names = names };
    }
}
