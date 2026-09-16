using System.Globalization;
using System.Text.RegularExpressions;

namespace CODDowngrader.Catalog;

/// <summary>A manifest found in pasted text, with the time SteamDB first saw it when the text had one.</summary>
public sealed record PastedManifest(ulong Manifest, DateTimeOffset? FirstSeen);

/// <summary>
/// Text pasted for one depot: a manifest ID on its own, or rows copied from SteamDB's manifests table, which a browser copies
/// as "3 September 2026 – 16:03:08 UTC", a relative date and the manifest ID on each line. Nothing is fetched: the text is
/// whatever the user copied from a page they opened.
/// </summary>
public static partial class SteamDbPaste
{
    static readonly string[] Months =
        { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

    [GeneratedRegex(@"(?<!\d)(\d{15,20})(?!\d)")]
    private static partial Regex ManifestRegex();

    [GeneratedRegex(@"(\d{1,2})\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{4})(?:\D{1,5}(\d{1,2}):(\d{2})(?::(\d{2}))?)?", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    /// <summary>Every manifest in the text, newest first where the text has dates, in the order written where it does not.</summary>
    public static IReadOnlyList<PastedManifest> Read(string text)
    {
        var found = new List<PastedManifest>();
        foreach (var line in text.Split('\n'))
        {
            var ids = ManifestRegex().Matches(line).Select(m => m.Groups[1].Value).ToList();
            if (ids.Count == 0) continue;
            // "depot manifest" and download_depot lines name a depot too; the manifest is the last long number.
            if (!ulong.TryParse(ids[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) || manifest == 0) continue;
            if (found.Any(f => f.Manifest == manifest)) continue;
            found.Add(new PastedManifest(manifest, Date(line)));
        }
        return found.Any(f => f.FirstSeen is not null)
            ? found.OrderByDescending(f => f.FirstSeen ?? DateTimeOffset.MinValue).ToList()
            : found;
    }

    /// <summary>
    /// The manifest a depot had at <paramref name="moment"/>: the newest one SteamDB first saw on or before it. With no dates in the
    /// text, a single manifest is taken as it is. Null when nothing fits.
    /// </summary>
    public static PastedManifest? At(IReadOnlyList<PastedManifest> pasted, DateTimeOffset? moment)
    {
        if (pasted.Count == 0) return null;
        if (pasted.All(p => p.FirstSeen is null)) return pasted.Count == 1 ? pasted[0] : null;
        if (moment is null) return pasted.Count == 1 ? pasted[0] : null;
        return pasted.Where(p => p.FirstSeen is { } seen && seen <= moment).MaxBy(p => p.FirstSeen);
    }

    static DateTimeOffset? Date(string line)
    {
        var match = DateRegex().Match(line);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = Array.IndexOf(Months, match.Groups[2].Value.ToLowerInvariant()) + 1;
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var hour = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var minute = match.Groups[5].Success ? int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
        var second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            // SteamDB writes UTC.
            return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
