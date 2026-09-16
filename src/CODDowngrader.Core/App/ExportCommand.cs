using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.App;

/// <summary>export.json in an export: everything but the manifest files, which sit beside it in the zip.</summary>
public sealed class ExportFile
{
    public int Format { get; set; } = ExportCommand.FileFormat;
    public string Tool { get; set; } = "";
    public DateTimeOffset Exported { get; set; }
    public string? BuiltInList { get; set; }
    public DateTimeOffset? LogFrom { get; set; }
    public bool ProductInfoUnreadable { get; set; }
    public List<ExportApp> Apps { get; set; } = new();
    public List<ExportCached> Cached { get; set; } = new();
    public List<ExportFetch> Fetched { get; set; } = new();
    public List<ExportUpdate> Updates { get; set; } = new();
    public List<string> DownloadedLists { get; set; } = new();
}

public sealed record ExportApp(uint App, string Name, ExportProductInfo? ProductInfo, ExportInstalled? Installed);

public sealed record ExportProductInfo(uint? BuildId, List<ExportBranch> Branches, List<ExportDepot> Depots);

public sealed record ExportBranch(string Name, uint? BuildId, DateTimeOffset? Updated, bool Password);

public sealed record ExportDepot(uint Depot, string? Name, string? Os, string? Language, bool LowViolence, uint? DlcApp,
    uint? FromApp, string? SharedInstall, string? Manifest, ulong? Size, ulong? Download);

public sealed record ExportInstalled(uint BuildId, uint TargetBuildId, DateTimeOffset? LastUpdated, string? Language,
    List<ExportInstalledDepot> Depots, List<ExportShared> Shared);

public sealed record ExportInstalledDepot(uint Depot, string Manifest, ulong Size, uint? DlcApp);

public sealed record ExportShared(uint Depot, uint App);

/// <param name="Entry">The manifest file's name in the zip.</param>
public sealed record ExportCached(uint Depot, string Manifest, DateTimeOffset Created, ulong Size, string Entry);

public sealed record ExportFetch(DateTimeOffset Time, uint Depot, string Manifest);

public sealed record ExportUpdate(DateTimeOffset Time, uint App, List<uint> Depots);

[JsonSerializable(typeof(ExportFile))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class ExportJson : JsonSerializerContext
{
}

/// <summary>
/// --export: what this PC's Steam knows about the Call of Duty builds, in one zip, for checking and filling in the built-in list
/// with tools/catalog.py import. It holds Call of Duty app, depot and manifest IDs, dates, sizes, and the manifests Steam cached,
/// which list the games' files. No account name, Steam ID, folder path or server name goes in it.
/// </summary>
public static partial class ExportCommand
{
    public const int FileFormat = 1;
    public const string JsonEntry = "export.json";

    public static int Run(SteamInstall steam, string target) => Run(steam, target, quiet: false, out _);

    /// <param name="quiet">Say nothing: the caller reports it, as the command line's --json does.</param>
    /// <param name="written">The zip, once there is one.</param>
    public static int Run(SteamInstall steam, string target, bool quiet, out string? written)
    {
        written = null;
        string path;
        try
        {
            path = OutputPath(target);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (!quiet) Console.Error.WriteLine($"{target} cannot be used: {e.Message}");
            return 2;
        }
        if (File.Exists(path))
        {
            if (!quiet) Console.Error.WriteLine($"{path} already exists.");
            return 1;
        }

        var export = Write(GameLibrary.Load(steam), path, ManifestLists.CacheFolder);
        written = path;
        if (quiet) return 0;

        Console.WriteLine($"COD Downgrader {AppState.Version}: this PC's Call of Duty build information");
        Console.WriteLine($"  {export.Apps.Count(a => a.ProductInfo is not null)} games with Steam's product info, {export.Apps.Count(a => a.Installed is not null)} installed");
        Console.WriteLine($"  {export.Cached.Count} cached manifests, {export.Fetched.Count} manifest downloads in Steam's log" +
                          (export.LogFrom is { } from ? $" from {Format.Date(from)}" : ""));
        if (export.ProductInfoUnreadable) Console.WriteLine("  Steam's product info could not be read");
        Console.WriteLine($"  {path} ({Format.Size((ulong)new FileInfo(path).Length)})");
        Console.WriteLine("It holds Call of Duty app, depot and manifest IDs, dates, sizes and the games' file lists. No account name, Steam ID or folder path is in it.");
        return 0;
    }

    static string OutputPath(string target)
    {
        var name = $"COD Downgrader export {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.zip";
        if (string.IsNullOrWhiteSpace(target)) return Path.Combine(Environment.CurrentDirectory, name);
        var full = Path.GetFullPath(target.Trim().Trim('"'));
        if (Directory.Exists(full)) return Path.Combine(full, name);
        return Path.HasExtension(full) ? full : full + ".zip";
    }

    [GeneratedRegex(@"^(?:manifest_)?(\d+)_(\d+)\.(?:manifest|txt)$")]
    private static partial Regex DownloadedListRegex();

    /// <summary>Writes the export of <paramref name="library"/>, with the file lists DepotDownloader fetched into <paramref name="downloadedLists"/>.</summary>
    public static ExportFile Write(GameLibrary library, string path, string downloadedLists)
    {
        var inv = CultureInfo.InvariantCulture;
        var games = library.Entries.Where(e => e.Downgradable && (e.Info is not null || e.Installed is not null)).ToList();
        var appIds = games.Select(g => g.AppId).ToHashSet();
        var depots = new HashSet<uint>();
        foreach (var game in games)
        {
            depots.UnionWith(game.Owners.Keys);
            if (game.Info is not null) depots.UnionWith(game.Info.Depots.Where(d => !d.IsRedistributable).Select(d => d.DepotId));
            if (game.Installed is { } installed)
            {
                depots.UnionWith(installed.Depots.Keys);
                depots.UnionWith(installed.SharedDepots.Where(s => s.Value != GameLibrary.Redistributables).Select(s => s.Key));
            }
        }

        var export = new ExportFile
        {
            Tool = $"COD Downgrader {AppState.Version}",
            Exported = DateTimeOffset.UtcNow,
            BuiltInList = library.List.Updated?.ToString("yyyy-MM-dd", inv),
            LogFrom = library.Log.Start?.ToUniversalTime(),
            ProductInfoUnreadable = library.AppInfoProblem is not null,
            Apps = games.OrderBy(g => g.AppId)
                .Select(g => new ExportApp(g.AppId, g.Name, g.Info is null ? null : ProductInfo(g.Info), g.Installed is null ? null : Installed(g.Installed)))
                .ToList(),
            Fetched = library.Log.Manifests.Where(m => depots.Contains(m.DepotId) && m.ManifestId != 0)
                .Select(m => new ExportFetch(m.Time.ToUniversalTime(), m.DepotId, m.ManifestId.ToString(inv)))
                .ToList(),
            Updates = library.Log.Updates.Where(u => appIds.Contains(u.AppId))
                .Select(u => new ExportUpdate(u.Time.ToUniversalTime(), u.AppId, u.DepotIds.ToList()))
                .ToList(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".part";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var cached in library.Steam.CachedManifests(depots).OrderBy(c => c.DepotId).ThenBy(c => c.Created))
                {
                    var entry = $"manifests/{cached.DepotId}_{cached.ManifestId}.manifest";
                    if (Add(zip, cached.Path, entry))
                        export.Cached.Add(new ExportCached(cached.DepotId, cached.ManifestId.ToString(inv), cached.Created, cached.SizeOnDisk, entry));
                }

                if (Directory.Exists(downloadedLists))
                {
                    foreach (var file in Directory.EnumerateFiles(downloadedLists).OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var name = Path.GetFileName(file);
                        var match = DownloadedListRegex().Match(name);
                        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out var depot) || !depots.Contains(depot)) continue;
                        if (Add(zip, file, $"downloaded-lists/{name}")) export.DownloadedLists.Add($"downloaded-lists/{name}");
                    }
                }

                using var json = zip.CreateEntry(JsonEntry, CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(json, export, ExportJson.Default.ExportFile);
            }
            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        return export;
    }

    /// <summary>Copies a file Steam may have open into the zip. False when it cannot be read.</summary>
    static bool Add(ZipArchive zip, string path, string entry)
    {
        byte[] bytes;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        using var stream = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
        return true;
    }

    static ExportProductInfo ProductInfo(AppInfo info) => new(
        info.PublicBuildId,
        info.Branches.Select(b => new ExportBranch(b.Name, b.BuildId, b.Updated, b.PasswordRequired)).ToList(),
        info.Depots.Where(d => !d.IsRedistributable).OrderBy(d => d.DepotId)
            .Select(d => new ExportDepot(d.DepotId, d.Name, d.OsList, d.Language, d.LowViolence, d.DlcAppId, d.DepotFromApp, d.SharedInstall,
                d.PublicManifest?.ToString(CultureInfo.InvariantCulture), d.PublicSize, d.PublicDownload))
            .ToList());

    static ExportInstalled Installed(InstalledApp app) => new(
        app.BuildId,
        app.TargetBuildId,
        app.LastUpdated,
        app.Language,
        app.Depots.Values.Where(d => d.ManifestId != 0).OrderBy(d => d.DepotId)
            .Select(d => new ExportInstalledDepot(d.DepotId, d.ManifestId.ToString(CultureInfo.InvariantCulture), d.Size, d.DlcAppId))
            .ToList(),
        app.SharedDepots.Where(s => s.Value != GameLibrary.Redistributables).OrderBy(s => s.Key)
            .Select(s => new ExportShared(s.Key, s.Value))
            .ToList());
}
