using System.Globalization;
using System.Text.RegularExpressions;
using CODDowngrader.App;
using CODDowngrader.Steam;

namespace CODDowngrader.Patching;

/// <summary>
/// File lists of builds Steam never cached: the manifests DepotDownloader fetched for this tool, kept in
/// %LOCALAPPDATA%\COD Downgrader\manifests so a later patch does not fetch them again.
/// </summary>
public static partial class ManifestLists
{
    public static string CacheFolder => Path.Combine(AppState.Folder, "manifests");

    static string SavedName(uint depot, ulong manifest) => $"{depot}_{manifest}.manifest";

    static string ListingName(uint depot, ulong manifest) => $"manifest_{depot}_{manifest}.txt";

    /// <summary>A file list from the tool's cache, or from what DepotDownloader left in <paramref name="folder"/>.</summary>
    public static IReadOnlyList<ManifestFile>? Find(uint depot, ulong manifest, string? folder = null) =>
        Read(CacheFolder, Path.Combine(CacheFolder, SavedName(depot, manifest)), depot, manifest, keep: false)
        ?? (folder is null ? null : Read(folder, Path.Combine(folder, ".DepotDownloader", SavedName(depot, manifest)), depot, manifest, keep: true));

    static IReadOnlyList<ManifestFile>? Read(string folder, string saved, uint depot, ulong manifest, bool keep)
    {
        var listing = Path.Combine(folder, ListingName(depot, manifest));
        if (DepotManifest.TryLoadFiles(saved) is { } files)
        {
            if (keep)
            {
                Keep(saved, SavedName(depot, manifest), move: false);
                // DepotDownloader writes its listing beside the files as well; it has no place in a patch folder.
                if (File.Exists(listing)) Keep(listing, ListingName(depot, manifest), move: true);
            }
            return files;
        }

        try
        {
            if (!File.Exists(listing)) return null;
            var parsed = ParseListing(File.ReadLines(listing));
            if (keep) Keep(listing, ListingName(depot, manifest), move: true);
            return parsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*(\d+)\s+\d+\s+([0-9a-fA-F]{40})\s+([0-9a-fA-F]+)\s+(\S.*?)\s*$")]
    private static partial Regex ListingRowRegex();

    /// <summary>The listing DepotDownloader's -manifest-only writes: size, chunk count, SHA-1, flags in hex and name on each file's line.</summary>
    public static IReadOnlyList<ManifestFile> ParseListing(IEnumerable<string> lines)
    {
        var files = new List<ManifestFile>();
        foreach (var line in lines)
        {
            var row = ListingRowRegex().Match(line);
            if (!row.Success || !ulong.TryParse(row.Groups[1].Value, out var size)) continue;
            if (!uint.TryParse(row.Groups[3].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var flags)) flags = 0;

            // A folder is listed with no content: a zero SHA, where an empty file has the SHA-1 of nothing.
            var sha = row.Groups[2].Value.ToUpperInvariant();
            var isFolder = (flags & 0x40) != 0 || (size == 0 && sha.All(c => c == '0'));
            files.Add(new ManifestFile(ManifestFile.NormalizeName(row.Groups[4].Value), size, isFolder ? flags | 0x40u : flags, isFolder ? null : sha));
        }
        return files;
    }

    /// <summary>Puts a fetched list in the tool's cache. The listing is moved, so it does not stay behind in a patch folder.</summary>
    static void Keep(string path, string name, bool move)
    {
        try
        {
            var cached = Path.Combine(CacheFolder, name);
            Directory.CreateDirectory(CacheFolder);
            if (move) File.Move(path, cached, overwrite: true);
            else if (!File.Exists(cached)) File.Copy(path, cached);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache only saves fetching the list again.
        }
    }
}
