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

    /// <summary>The game as Steam has it, when this entry is that game in another language (<see cref="GameLibrary.ForLanguage"/>).</summary>
    public GameEntry? InLanguageOf { get; init; }

    /// <summary>The language chosen for this entry, when it is <see cref="InLanguageOf"/> another.</summary>
    public string? Language { get; init; }

    /// <summary>Each language depot this entry takes instead of the game's own: the depot taken, to the depot it stands in for.</summary>
    public IReadOnlyDictionary<uint, uint> LanguageSwaps { get; init; } = new Dictionary<uint, uint>();

    public bool Downgradable => Title?.Downgradable ?? true;

    public uint OwnerOf(uint depot) => Owners.TryGetValue(depot, out var owner) ? owner : AppId;

    /// <summary>The exe Steam starts for this game, in its install folder: the first Windows launch option that is there.</summary>
    public string? LaunchExe()
    {
        if (Installed is not { } app || Info is not { } info) return null;
        foreach (var option in info.Launch.Where(l => l.ForWindows).OrderBy(l => l.Type is null or "default" ? 0 : 1))
        {
            var relative = option.Executable.Replace('\\', '/').TrimStart('/');
            if (!relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(app.InstallDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
        }
        return null;
    }
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
        IReadOnlyDictionary<uint, AppInfo> infos, IReadOnlyDictionary<uint, InstalledApp> installed, string? appInfoProblem, Remembered remembered)
    {
        Remembered = remembered;
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

    /// <summary>Every manifest the tool has learned on this PC, this run's included.</summary>
    public Remembered Remembered { get; }
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
            var owners = app is not null ? InstalledDepots(app, info) : DefaultDepots(id, info, list.NotDefault);
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
        var depots = new HashSet<uint>(ordered.SelectMany(e => e.Owners.Keys));
        var cached = steam.CachedManifests(depots);
        var log = ContentLog.Load(steam.Root);

        // What Steam shows today, kept for the day it has deleted the cached manifest or rotated the log line.
        var remembered = Remembered.Load();
        remembered.Learn(ordered.SelectMany(e => (e.InstalledManifests ?? new Dictionary<uint, ulong>()).Select(m => new RememberedManifest(m.Key, m.Value, RememberedSource.Installed)))
            .Concat(ordered.SelectMany(e => (e.LatestManifests ?? new Dictionary<uint, ulong>()).Select(m => new RememberedManifest(m.Key, m.Value, RememberedSource.Latest))))
            .Concat(cached.Select(c => new RememberedManifest(c.DepotId, c.ManifestId, RememberedSource.Built, c.Created)))
            .Concat(log.Manifests.Where(m => depots.Contains(m.DepotId)).Select(m => new RememberedManifest(m.DepotId, m.ManifestId, RememberedSource.Fetched, m.Time))));

        return new GameLibrary(steam, log, list, ordered, cached, infos, installed, problem, remembered);
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

    /// <summary>
    /// For a game that is not installed: its Windows English depots, its own and borrowed alike, less those the built-in list
    /// says are never part of one.
    /// </summary>
    internal static Dictionary<uint, uint> DefaultDepots(uint appId, AppInfo? info, IReadOnlySet<uint> notDefault)
    {
        var owners = new Dictionary<uint, uint>();
        if (info is null) return owners;

        foreach (var d in info.Depots)
        {
            if (d.IsRedistributable || !d.ForWindows || d.LowViolence || notDefault.Contains(d.DepotId)) continue;
            if (!string.IsNullOrEmpty(d.Language) && !d.Language.Equals("english", StringComparison.OrdinalIgnoreCase)) continue;
            if (d.IsBorrowed) owners[d.DepotId] = d.DepotFromApp!.Value;
            else if (d.PublicManifest is not null) owners[d.DepotId] = appId;
        }
        return owners;
    }

    /// <summary>For a game Steam has no depot list for on this PC: the depots the built-in list names for it.</summary>
    static Dictionary<uint, uint> ListedDepots(uint appId, ManifestCatalog list) =>
        list.Apps.TryGetValue(appId, out var depots)
            ? depots.Where(d => !list.NotDefault.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value)
            : new Dictionary<uint, uint>();

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

    /// <summary>
    /// The icon Steam shows for a game, from its library cache: appcache/librarycache/&lt;app&gt;/&lt;icon&gt;.jpg, or
    /// &lt;app&gt;_icon.jpg as older clients kept it. Null when Steam has not cached one.
    /// </summary>
    public string? SteamIcon(GameEntry game)
    {
        var cache = Path.Combine(Steam.Root, "appcache", "librarycache");
        var id = game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (game.Info?.Icon is { Length: > 0 } icon && Path.Combine(cache, id, icon + ".jpg") is var hashed && File.Exists(hashed)) return hashed;
        var old = Path.Combine(cache, id + "_icon.jpg");
        return File.Exists(old) ? old : null;
    }

    public BuildHistoryResult History(GameEntry game)
    {
        if (game.InLanguageOf is { } steamHas) return Remembered.Known(InLanguage(game, History(steamHas)));

        var depots = new HashSet<uint>(game.Owners.Keys);
        return Remembered.Known(BuildHistory.Reconstruct(Remembered.Into(new BuildInput
        {
            DepotIds = game.DepotIds,
            Installed = game.InstalledManifests,
            Latest = game.LatestManifests,
            Cached = _cached.Values.Where(c => depots.Contains(c.DepotId)).ToList(),
            Seen = Log.Manifests.Where(m => depots.Contains(m.DepotId)).ToList(),
            Listed = List.Depots,
            ListedUpdatesFrom = List.HistoryFrom,
        })));
    }

    /// <summary>
    /// The builds of a game in another language: the builds of the game as Steam has it, each with the chosen language's depots
    /// instead of its own, so they keep their names and dates. Product info names those depots' manifests for the build Steam
    /// publishes today and no other. A language depot keeps today's manifest in a build where the depot it stands in for has
    /// today's manifest as well, since Steam updates a game's languages together; in any other build it is unknown, and comes
    /// from SteamDB.
    /// </summary>
    internal static BuildHistoryResult InLanguage(GameEntry game, BuildHistoryResult steamHas)
    {
        var replaced = game.LanguageSwaps.Values.ToHashSet();
        var published = game.InLanguageOf!.LatestManifests;
        var builds = steamHas.Builds.Select(build =>
        {
            var manifests = build.Manifests.Where(m => !replaced.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);
            var unknown = build.Unknown.Where(d => !replaced.Contains(d)).ToList();
            foreach (var (depot, standsFor) in game.LanguageSwaps)
            {
                var unchanged = build.Manifests.TryGetValue(standsFor, out var had) && published?.GetValueOrDefault(standsFor) == had;
                if (unchanged && game.LatestManifests?.GetValueOrDefault(depot) is { } latest and not 0) manifests[depot] = latest;
                else unknown.Add(depot);
            }
            return build with
            {
                Manifests = manifests,
                Unknown = unknown.OrderBy(d => d).ToList(),
                DifferentFromInstalled = build.DifferentFromInstalled.Where(d => !replaced.Contains(d)).ToList(),
            };
        }).ToList();

        var depots = steamHas.Depots.Where(d => !replaced.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
        foreach (var depot in game.LanguageSwaps.Keys)
            depots[depot] = game.LatestManifests?.GetValueOrDefault(depot) is { } latest and not 0
                ? new[] { new KnownManifest(depot, latest, null, null, false, true) }
                : Array.Empty<KnownManifest>();
        return new BuildHistoryResult(builds, depots);
    }

    /// <summary>
    /// A game's language depots in groups of the same content: in product info each group is an English depot followed by the
    /// other languages of it, and a depot with no language ends one. Low-violence and not-default depots are left out, and
    /// only the first depot of a language in a group counts (Black Ops lists German twice).
    /// </summary>
    List<List<(uint Depot, string Language, uint Owner)>> LanguageGroups(GameEntry game) => LanguageGroups(game.AppId, game.Info, List.NotDefault);

    internal static List<List<(uint Depot, string Language, uint Owner)>> LanguageGroups(uint appId, AppInfo? info, IReadOnlySet<uint> notDefault)
    {
        var groups = new List<List<(uint Depot, string Language, uint Owner)>>();
        if (info is null) return groups;

        List<(uint Depot, string Language, uint Owner)>? group = null;
        foreach (var d in info.Depots)
        {
            if (!d.ForWindows || d.IsRedistributable) continue;
            if (string.IsNullOrEmpty(d.Language))
            {
                group = null;
                continue;
            }
            var language = d.Language.ToLowerInvariant();
            if (group is null || language == SteamLanguages.English) groups.Add(group = new());
            if (d.LowViolence || notDefault.Contains(d.DepotId) || (!d.IsBorrowed && d.PublicManifest is null)) continue;
            if (group.Any(m => m.Language == language)) continue;
            group.Add((d.DepotId, language, d.IsBorrowed ? d.DepotFromApp!.Value : appId));
        }
        groups.RemoveAll(g => g.Count == 0);
        return groups;
    }

    /// <summary>The languages a download of this game can be in, English first. Empty for a game with no language depots.</summary>
    public IReadOnlyList<string> Languages(GameEntry game)
    {
        var source = game.InLanguageOf ?? game;
        return LanguageGroups(source)
            .Where(g => g.Any(m => source.Owners.ContainsKey(m.Depot)))
            .SelectMany(g => g.Select(m => m.Language))
            .Distinct()
            .OrderBy(SteamLanguages.Order)
            .ThenBy(l => l, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The language a game's depots are in as it is: the one chosen for it, or the language most of its language depots have.</summary>
    public string? LanguageOf(GameEntry game)
    {
        if (game.Language is { } chosen) return chosen;
        var byDepot = LanguageGroups(game).SelectMany(g => g).Where(m => game.Owners.ContainsKey(m.Depot)).ToList();
        return byDepot.GroupBy(m => m.Language)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => SteamLanguages.Order(g.Key))
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// The language Steam would download this game in: the language it is installed in, or else the Steam client's, when the
    /// game has it; English otherwise. Null for a game with no language depots.
    /// </summary>
    public string? DefaultLanguage(GameEntry game)
    {
        var offered = Languages(game);
        if (offered.Count == 0) return null;
        var source = game.InLanguageOf ?? game;
        foreach (var candidate in new[] { source.Installed?.Language, SteamLanguages.ClientLanguage(), LanguageOf(source), SteamLanguages.English })
            if (candidate is not null && offered.Contains(candidate.ToLowerInvariant())) return candidate.ToLowerInvariant();
        return offered[0];
    }

    /// <summary>
    /// The game with its language depots in <paramref name="language"/>: in each group of the same content it has a depot of,
    /// that language's depot instead, where the group has one. The game itself when that changes nothing.
    /// </summary>
    public GameEntry ForLanguage(GameEntry game, string language)
    {
        var source = game.InLanguageOf ?? game;
        language = language.Trim().ToLowerInvariant();

        var owners = new Dictionary<uint, uint>(source.Owners);
        var swaps = new Dictionary<uint, uint>();
        foreach (var group in LanguageGroups(source))
        {
            var have = group.Where(m => source.Owners.ContainsKey(m.Depot)).ToList();
            var wanted = group.FirstOrDefault(m => m.Language == language);
            if (have.Count == 0 || wanted.Depot == 0 || have.Any(m => m.Depot == wanted.Depot)) continue;
            foreach (var m in have) owners.Remove(m.Depot);
            owners[wanted.Depot] = wanted.Owner;
            swaps[wanted.Depot] = have[0].Depot;
        }
        if (swaps.Count == 0) return source;

        var latest = new Dictionary<uint, ulong>();
        foreach (var (depot, owner) in owners)
        {
            var ownerInfo = owner == source.AppId ? source.Info : _infos.GetValueOrDefault(owner);
            if (ownerInfo?.Depot(depot)?.PublicManifest is { } manifest) latest[depot] = manifest;
        }
        return new GameEntry
        {
            AppId = source.AppId,
            Name = source.Name,
            Title = source.Title,
            Installed = source.Installed,
            Info = source.Info,
            Owners = owners,
            InstalledManifests = source.InstalledManifests?.Where(m => owners.ContainsKey(m.Key)).ToDictionary(m => m.Key, m => m.Value),
            LatestManifests = latest.Count > 0 ? latest : null,
            InLanguageOf = source,
            Language = language,
            LanguageSwaps = swaps,
        };
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
        if (info is not null && info.PublicManifest == manifest && info.PublicSize is { } size) return size;

        return List.Depots.TryGetValue(depot, out var rows) ? rows.FirstOrDefault(r => r.ManifestId == manifest)?.Size : null;
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
            : List.Names.TryGetValue(depot, out var listed) ? listed
            : IsDlc(game, depot) ? $"DLC {info?.DlcAppId ?? _installed.GetValueOrDefault(game.OwnerOf(depot))?.Depots.GetValueOrDefault(depot)?.DlcAppId}"
                                   + (!string.IsNullOrEmpty(info?.Language) ? $", {SteamLanguages.Name(info.Language)}" : "")
            : !string.IsNullOrEmpty(info?.Language) ? $"{SteamLanguages.Name(info.Language)} language"
            : "";

        var owner = game.OwnerOf(depot);
        if (owner == game.AppId) return name;
        var from = $"from {CatalogGames.Find(owner)?.Name ?? $"app {owner}"}";
        return name.Length == 0 ? from : $"{name}, {from}";
    }
}
