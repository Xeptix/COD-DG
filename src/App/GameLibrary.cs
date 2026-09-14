using CODDowngrader.Builds;
using CODDowngrader.Catalog;
using CODDowngrader.Patching;
using CODDowngrader.Steam;
using CatalogGames = CODDowngrader.Catalog.Games;

namespace CODDowngrader.App;

public sealed class GameEntry
{
    public required uint AppId { get; init; }
    public required string Name { get; init; }
    public Game? Title { get; init; }
    public InstalledApp? Installed { get; init; }
    public AppInfo? Info { get; init; }

    /// <summary>
    /// Every depot a download of this game is made of, mapped to the app that owns it. That is the game's
    /// own depots plus the content it uses from the apps it shares an install folder with: Black Ops II
    /// Zombies runs on the campaign app's 202972, and Modern Warfare 3's campaign on Multiplayer's 42682.
    /// </summary>
    public required IReadOnlyDictionary<uint, uint> Owners { get; init; }

    /// <summary>The installed manifest of each depot, from the owning app's appmanifest.</summary>
    public IReadOnlyDictionary<uint, ulong>? InstalledManifests { get; init; }

    /// <summary>Steam's current public manifest of each depot, from the owning app's product info.</summary>
    public IReadOnlyDictionary<uint, ulong>? LatestManifests { get; init; }

    public IReadOnlyList<uint> DepotIds => Owners.Keys.OrderBy(d => d).ToList();

    public bool Downgradable => Title?.Downgradable ?? true;

    public uint OwnerOf(uint depot) => Owners.TryGetValue(depot, out var owner) ? owner : AppId;
}

/// <summary>Every Call of Duty this machine knows about, with what Steam has left behind for each.</summary>
public sealed class GameLibrary
{
    /// <summary>Steamworks Common Redistributables: DirectX and runtimes, shared by every game and never part of a download.</summary>
    public const uint Redistributables = 228980;

    readonly Dictionary<(uint, ulong), CachedManifest> _cached;
    readonly IReadOnlyDictionary<uint, AppInfo> _infos;
    readonly IReadOnlyDictionary<uint, InstalledApp> _installed;

    GameLibrary(SteamInstall steam, ContentLog log, ManifestCatalog list, IReadOnlyList<GameEntry> entries, IEnumerable<CachedManifest> cached,
        IReadOnlyDictionary<uint, AppInfo> infos, IReadOnlyDictionary<uint, InstalledApp> installed, string? appInfoProblem)
    {
        Steam = steam;
        Log = log;
        List = list;
        Entries = entries;
        _cached = cached.ToDictionary(c => (c.DepotId, c.ManifestId));
        _infos = infos;
        _installed = installed;
        AppInfoProblem = appInfoProblem;
    }

    public SteamInstall Steam { get; }
    public ContentLog Log { get; }

    /// <summary>The built-in manifest list.</summary>
    public ManifestCatalog List { get; }
    public IReadOnlyList<GameEntry> Entries { get; }
    public string? AppInfoProblem { get; }

    public static GameLibrary Load(SteamInstall steam)
    {
        var installed = steam.InstalledApps().ToDictionary(a => a.AppId);
        var games = installed.Values
            .Where(a => CatalogGames.Find(a.AppId) is not null || a.Name.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.AppId)
            .Concat(CatalogGames.All.Select(g => g.AppId))
            .ToHashSet();

        var wanted = new HashSet<uint>(games);
        foreach (var id in games)
        {
            if (!installed.TryGetValue(id, out var app)) continue;
            foreach (var owner in app.SharedDepots.Values)
                if (owner != Redistributables) wanted.Add(owner);
        }

        var infos = new Dictionary<uint, AppInfo>();
        string? problem = null;
        if (File.Exists(steam.AppInfoPath))
        {
            try
            {
                infos = AppInfoReader.Read(steam.AppInfoPath, wanted);
                var owners = infos.Values
                    .SelectMany(i => i.Depots)
                    .Where(d => d.IsBorrowed && !infos.ContainsKey(d.DepotFromApp!.Value))
                    .Select(d => d.DepotFromApp!.Value)
                    .ToHashSet();
                if (owners.Count > 0)
                    foreach (var (id, info) in AppInfoReader.Read(steam.AppInfoPath, owners)) infos[id] = info;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                problem = e.Message;
            }
        }
        else
        {
            problem = "Steam's appcache\\appinfo.vdf is not there.";
        }

        var list = ManifestCatalog.BuiltIn;
        var entries = new List<GameEntry>();
        foreach (var id in games)
        {
            installed.TryGetValue(id, out var app);
            infos.TryGetValue(id, out var info);
            var title = CatalogGames.Find(id);
            var owners = app is not null ? InstalledDepots(app, info) : DefaultDepots(id, info);
            if (app is null && owners.Count == 0) owners = ListedDepots(id, list);

            var installedManifests = new Dictionary<uint, ulong>();
            var latestManifests = new Dictionary<uint, ulong>();
            foreach (var (depot, owner) in owners)
            {
                if (app is not null && InstalledManifest(installed, app, depot, owner) is { } have) installedManifests[depot] = have;
                var ownerInfo = owner == id ? info : infos.GetValueOrDefault(owner);
                if (ownerInfo?.Depot(depot)?.PublicManifest is { } latest) latestManifests[depot] = latest;
            }

            entries.Add(new GameEntry
            {
                AppId = id,
                Name = title?.Name ?? app?.Name ?? info?.Name ?? $"App {id}",
                Title = title,
                Installed = app,
                Info = info,
                Owners = owners,
                InstalledManifests = app is not null ? installedManifests : null,
                LatestManifests = latestManifests.Count > 0 ? latestManifests : null,
            });
        }

        var ordered = entries
            .OrderBy(e => e.Installed is null)
            .ThenBy(e => CatalogIndex(e.AppId))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cached = steam.CachedManifests(new HashSet<uint>(ordered.SelectMany(e => e.Owners.Keys)));
        return new GameLibrary(steam, ContentLog.Load(steam.Root), list, ordered, cached, infos, installed, problem);
    }

    static int CatalogIndex(uint appId)
    {
        for (var i = 0; i < CatalogGames.All.Count; i++)
            if (CatalogGames.All[i].AppId == appId) return i;
        return int.MaxValue;
    }

    /// <summary>The installed depots, plus the depots Steam lists as shared from other apps, less the redistributables.</summary>
    static Dictionary<uint, uint> InstalledDepots(InstalledApp app, AppInfo? info)
    {
        var owners = app.Depots.Keys.ToDictionary(d => d, _ => app.AppId);
        foreach (var (depot, owner) in app.SharedDepots)
        {
            if (owner == app.AppId || owner == Redistributables || info?.Depot(depot)?.IsRedistributable == true) continue;
            owners.TryAdd(depot, owner);
        }
        return owners;
    }

    /// <summary>For a game that is not installed: its Windows English depots, its own and borrowed alike.</summary>
    static Dictionary<uint, uint> DefaultDepots(uint appId, AppInfo? info)
    {
        var owners = new Dictionary<uint, uint>();
        if (info is null) return owners;

        foreach (var d in info.Depots)
        {
            if (d.IsRedistributable || !d.ForWindows || d.LowViolence) continue;
            if (!string.IsNullOrEmpty(d.Language) && !d.Language.Equals("english", StringComparison.OrdinalIgnoreCase)) continue;
            if (d.IsBorrowed) owners[d.DepotId] = d.DepotFromApp!.Value;
            else if (d.PublicManifest is not null) owners[d.DepotId] = appId;
        }
        return owners;
    }

    /// <summary>For a game Steam has no depot list for on this PC: the depots the built-in list names for it.</summary>
    static Dictionary<uint, uint> ListedDepots(uint appId, ManifestCatalog list) =>
        list.Apps.TryGetValue(appId, out var depots) ? new Dictionary<uint, uint>(depots) : new Dictionary<uint, uint>();

    /// <summary>The manifest on disk for a depot of this game: its own record, or the owning app's when that app is installed in the same folder.</summary>
    static ulong? InstalledManifest(IReadOnlyDictionary<uint, InstalledApp> installed, InstalledApp app, uint depot, uint owner)
    {
        var source = owner == app.AppId ? app : installed.GetValueOrDefault(owner);
        if (source is null || (source != app && !PathRules.Same(source.InstallDir, app.InstallDir))) return null;
        return source.Depots.TryGetValue(depot, out var d) && d.ManifestId != 0 ? d.ManifestId : null;
    }

    /// <summary>Steam's installed manifest of every depot of every app installed in a folder.</summary>
    public Dictionary<uint, ulong> FolderManifests(string installDir)
    {
        var manifests = new Dictionary<uint, ulong>();
        foreach (var entry in Entries.Where(e => e.Installed is { } app && PathRules.Same(app.InstallDir, installDir)))
        {
            if (entry.InstalledManifests is null) continue;
            foreach (var (depot, manifest) in entry.InstalledManifests) manifests.TryAdd(depot, manifest);
        }
        return manifests;
    }

    public BuildHistoryResult History(GameEntry game)
    {
        var depots = new HashSet<uint>(game.Owners.Keys);
        return BuildHistory.Reconstruct(new BuildInput
        {
            DepotIds = game.DepotIds,
            Installed = game.InstalledManifests,
            Latest = game.LatestManifests,
            Cached = _cached.Values.Where(c => depots.Contains(c.DepotId)).ToList(),
            Seen = Log.Manifests.Where(m => depots.Contains(m.DepotId)).ToList(),
            Listed = List.Depots,
            ListedUpdatesFrom = List.HistoryFrom,
        });
    }

    public CachedManifest? Cached(uint depot, ulong manifest) => _cached.GetValueOrDefault((depot, manifest));

    /// <summary>The file list of a manifest Steam still has cached, or that DepotDownloader fetched for this tool before; null otherwise.</summary>
    public IReadOnlyList<ManifestFile>? Files(uint depot, ulong manifest) =>
        (_cached.TryGetValue((depot, manifest), out var cached) ? DepotManifest.TryLoadFiles(cached.Path) : null)
        ?? ManifestLists.Find(depot, manifest);

    public DepotInfo? DepotInfo(GameEntry game, uint depot)
    {
        var owner = game.OwnerOf(depot);
        var info = owner == game.AppId ? game.Info : _infos.GetValueOrDefault(owner);
        return info?.Depot(depot) ?? game.Info?.Depot(depot);
    }

    public ulong? DepotSize(GameEntry game, uint depot, ulong manifest)
    {
        if (_cached.TryGetValue((depot, manifest), out var cached) && cached.SizeOnDisk > 0) return cached.SizeOnDisk;

        var owner = _installed.GetValueOrDefault(game.OwnerOf(depot));
        if (owner is not null && owner.Depots.TryGetValue(depot, out var have) && have.ManifestId == manifest) return have.Size;

        var info = DepotInfo(game, depot);
        return info is not null && info.PublicManifest == manifest ? info.PublicSize : null;
    }

    public bool IsDlc(GameEntry game, uint depot)
    {
        if (DepotInfo(game, depot)?.DlcAppId is not null) return true;
        var owner = _installed.GetValueOrDefault(game.OwnerOf(depot));
        return owner is not null && owner.Depots.TryGetValue(depot, out var have) && have.DlcAppId is not null;
    }

    public string DepotName(GameEntry game, uint depot)
    {
        var info = DepotInfo(game, depot);
        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info.Name
            : IsDlc(game, depot) ? $"DLC {info?.DlcAppId ?? _installed.GetValueOrDefault(game.OwnerOf(depot))?.Depots.GetValueOrDefault(depot)?.DlcAppId}"
            : !string.IsNullOrEmpty(info?.Language) ? $"{info.Language} language"
            : "";

        var owner = game.OwnerOf(depot);
        if (owner == game.AppId) return name;
        var from = $"from {CatalogGames.Find(owner)?.Name ?? $"app {owner}"}";
        return name.Length == 0 ? from : $"{name}, {from}";
    }
}
