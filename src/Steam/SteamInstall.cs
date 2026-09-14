using Microsoft.Win32;

namespace CODDowngrader.Steam;

public sealed record InstalledDepot(uint DepotId, ulong ManifestId, ulong Size, uint? DlcAppId);

public sealed record InstalledApp(
    uint AppId,
    string Name,
    string Library,
    string InstallDir,
    uint BuildId,
    uint TargetBuildId,
    DateTimeOffset? LastUpdated,
    string? Language,
    IReadOnlyDictionary<uint, InstalledDepot> Depots,
    IReadOnlyDictionary<uint, uint> SharedDepots)
{
    /// <summary>Steam has a newer build queued but has not applied it: the files on disk are still this build.</summary>
    public bool UpdatePending => TargetBuildId != 0 && TargetBuildId != BuildId;

    public IReadOnlyDictionary<uint, ulong> Manifests => Depots.ToDictionary(d => d.Key, d => d.Value.ManifestId);
}

/// <summary>A manifest file Steam left in its depotcache folder.</summary>
public sealed record CachedManifest(uint DepotId, ulong ManifestId, string Path, DateTimeOffset Created, ulong SizeOnDisk);

/// <summary>A Steam client on this machine: its folder, its libraries, and what is installed in them.</summary>
public sealed class SteamInstall
{
    SteamInstall(string root, IReadOnlyList<string> libraries, IReadOnlyList<string> missing)
    {
        Root = root;
        Libraries = libraries;
        MissingLibraries = missing;
    }

    public string Root { get; }

    public IReadOnlyList<string> Libraries { get; }

    /// <summary>Libraries Steam lists that are not reachable right now, such as an unplugged drive.</summary>
    public IReadOnlyList<string> MissingLibraries { get; }

    public string AppInfoPath => Path.Combine(Root, "appcache", "appinfo.vdf");

    public static SteamInstall? Find(string? root = null)
    {
        foreach (var candidate in root is not null ? new[] { root } : CandidateRoots())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try
            {
                full = ResolveLink(Path.GetFullPath(candidate));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }
            if (Directory.Exists(Path.Combine(full, "steamapps"))) return Open(full);
        }
        return null;
    }

    static IEnumerable<string> CandidateRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? "";
            yield return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string ?? "";
            yield return @"C:\Program Files (x86)\Steam";
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        yield return Path.Combine(home, "Library", "Application Support", "Steam");
    }

    static string ResolveLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is not null ? info.ResolveLinkTarget(true)?.FullName ?? path : path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    static SteamInstall Open(string root)
    {
        var listed = new List<string>();
        foreach (var file in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
        {
            if (!File.Exists(file)) continue;
            try
            {
                foreach (var entry in KvNode.Load(file)["libraryfolders"]?.Children ?? new List<KvNode>())
                {
                    // Current format is "0" { "path" "..." }; the old one was "1" "D:\\SteamLibrary".
                    var path = entry.Value ?? entry.Get("path");
                    if (uint.TryParse(entry.Key, out _) && !string.IsNullOrWhiteSpace(path)) listed.Add(path);
                }
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        listed.Add(root);

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var libraries = new List<string>();
        var missing = new List<string>();
        foreach (var path in listed.Select(p => p.Length > 3 ? p.TrimEnd('\\', '/') : p).Distinct(comparer))
        {
            if (Directory.Exists(Path.Combine(path, "steamapps"))) libraries.Add(path);
            else missing.Add(path);
        }
        // The registry spells Steam's own folder in lower case; the library list has it as it is on disk.
        var spelled = libraries.FirstOrDefault(l => comparer.Equals(l, root.TrimEnd('\\', '/'))) ?? root;
        return new SteamInstall(spelled, libraries, missing);
    }

    public IReadOnlyList<InstalledApp> InstalledApps()
    {
        var apps = new Dictionary<uint, InstalledApp>();
        foreach (var library in Libraries)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var acf in files)
            {
                var app = ReadAppManifest(acf, library);
                if (app is not null) apps.TryAdd(app.AppId, app);
            }
        }
        return apps.Values.ToList();
    }

    public static InstalledApp? ReadAppManifest(string acfPath, string library)
    {
        KvNode? state;
        try
        {
            state = KvNode.Load(acfPath)["AppState"];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (state is null || !uint.TryParse(state.Get("appid"), out var appId)) return null;

        var depots = new Dictionary<uint, InstalledDepot>();
        foreach (var node in state["InstalledDepots"]?.Children ?? new List<KvNode>())
        {
            if (!uint.TryParse(node.Key, out var depotId)) continue;
            depots[depotId] = new InstalledDepot(depotId, node.GetUInt64("manifest"), node.GetUInt64("size"),
                uint.TryParse(node.Get("dlcappid"), out var dlc) ? dlc : null);
        }

        // Depots this app uses from another app, as depot -> owning app: a Multiplayer app's share of the
        // campaign's content, or Steamworks redistributables (app 228980).
        var shared = new Dictionary<uint, uint>();
        foreach (var node in state["SharedDepots"]?.Children ?? new List<KvNode>())
        {
            if (uint.TryParse(node.Key, out var depotId) && uint.TryParse(node.Value, out var owner)) shared[depotId] = owner;
        }

        var updated = long.TryParse(state.Get("LastUpdated"), out var t) && t > 0
            ? DateTimeOffset.FromUnixTimeSeconds(t)
            : (DateTimeOffset?)null;

        return new InstalledApp(
            appId,
            state.Get("name") ?? $"App {appId}",
            library,
            Path.Combine(library, "steamapps", "common", state.Get("installdir") ?? ""),
            uint.TryParse(state.Get("buildid"), out var build) ? build : 0,
            uint.TryParse(state.Get("TargetBuildID"), out var target) ? target : 0,
            updated,
            state["UserConfig"]?.Get("language"),
            depots,
            shared);
    }

    /// <summary>
    /// Manifests Steam kept in depotcache for the given depots. Steam leaves the previous build's
    /// manifests behind after an update, often for weeks, and each one records when it was built.
    /// </summary>
    public IReadOnlyList<CachedManifest> CachedManifests(IReadOnlySet<uint> depotIds)
    {
        var folders = new[] { Path.Combine(Root, "depotcache") }
            .Concat(Libraries.Select(l => Path.Combine(l, "steamapps", "depotcache")))
            .Where(Directory.Exists);

        var found = new Dictionary<(uint, ulong), CachedManifest>();
        foreach (var folder in folders)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.manifest"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var cut = name.IndexOf('_');
                    if (cut <= 0
                        || !uint.TryParse(name.AsSpan(0, cut), out var depot)
                        || !ulong.TryParse(name.AsSpan(cut + 1), out var gid)
                        || !depotIds.Contains(depot)
                        || found.ContainsKey((depot, gid))) continue;

                    try
                    {
                        var header = DepotManifest.ReadHeader(file);
                        if (header.DepotId == depot && header.ManifestId == gid)
                            found[(depot, gid)] = new CachedManifest(depot, gid, file, header.Created, header.SizeOnDisk);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found.Values.ToList();
    }
}
