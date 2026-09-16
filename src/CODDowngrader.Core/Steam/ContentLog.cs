using System.Globalization;
using System.Text.RegularExpressions;

namespace CODDowngrader.Steam;

/// <summary>A manifest Steam fetched, and when. Steam only fetches the manifest a depot is on, or is about to be on.</summary>
public sealed record ManifestSeen(DateTimeOffset Time, uint DepotId, ulong ManifestId);

/// <summary>Steam noticing an app update: "AppID 42690 config changed : updated depots 42682,42691".</summary>
public sealed record DepotsUpdated(DateTimeOffset Time, uint AppId, IReadOnlyList<uint> DepotIds);

/// <summary>
/// Steam's logs\content_log.txt (and the rotated content_log.previous.txt). Every manifest request is
/// logged with its depot and manifest ID, which is the only record of an old build once Steam has
/// deleted the manifest file itself. Timestamps are the machine's local time.
/// </summary>
public sealed partial class ContentLog
{
    public List<ManifestSeen> Manifests { get; } = new();

    public List<DepotsUpdated> Updates { get; } = new();

    /// <summary>The earliest timestamp in the log: nothing before this point was recorded.</summary>
    public DateTimeOffset? Start { get; private set; }

    [GeneratedRegex(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"/depot/(\d+)/manifest/(\d+)/")]
    private static partial Regex ManifestRegex();

    [GeneratedRegex(@"AppID (\d+) config changed : updated depots ([\d,]+)")]
    private static partial Regex UpdatedDepotsRegex();

    public static ContentLog Load(string steamRoot)
    {
        var log = new ContentLog();
        foreach (var name in new[] { "content_log.previous.txt", "content_log.txt" })
        {
            var path = Path.Combine(steamRoot, "logs", name);
            if (!File.Exists(path)) continue;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                log.ParseLines(ReadLines(reader));
            }
            catch (IOException)
            {
                // Steam holds the log open; a failed read only means less history.
            }
        }
        return log;
    }

    static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    public void ParseLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var stamp = TimestampRegex().Match(line);
            if (!stamp.Success) continue;
            if (!DateTime.TryParseExact(stamp.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var local)) continue;

            var time = new DateTimeOffset(local);
            if (Start is null || time < Start) Start = time;

            var manifest = ManifestRegex().Match(line);
            if (manifest.Success)
            {
                // A request that failed says so on the same line; only a delivered manifest counts.
                if (line.Contains("HTTP response", StringComparison.Ordinal) && !line.Contains(" 200 ", StringComparison.Ordinal)) continue;
                if (uint.TryParse(manifest.Groups[1].Value, out var depot) && ulong.TryParse(manifest.Groups[2].Value, out var gid))
                    Manifests.Add(new ManifestSeen(time, depot, gid));
                continue;
            }

            var updated = UpdatedDepotsRegex().Match(line);
            if (updated.Success && uint.TryParse(updated.Groups[1].Value, out var app))
            {
                var depots = updated.Groups[2].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => uint.TryParse(d, out var id) ? id : 0)
                    .Where(id => id != 0)
                    .ToList();
                Updates.Add(new DepotsUpdated(time, app, depots));
            }
        }
    }
}
