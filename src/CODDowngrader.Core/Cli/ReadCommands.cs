using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Patching;
using CODDowngrader.Catalog;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>The commands that only read: list, builds, status, history and export.</summary>
public static class ReadCommands
{
    /// <summary>history: every build made on this PC, newest first, where each is now, and each as a shared build.</summary>
    public static int History(CliRun run, SteamInstall steam, GameEntry? only)
    {
        var library = GameLibrary.Load(steam);
        if (run.Command.Value("find") is { } search)
        {
            var root = PathRules.Normalize(search.Trim().Trim('"'));
            if (!Directory.Exists(root)) return run.Fail(ExitCode.Usage, "no-folder", $"{root} is not a folder.");
            var found = MadeBuilds.Find(root, library).Where(b => only is null || b.AppId == only.AppId).ToList();
            var added = MadeBuilds.AddFound(found);
            run.Line($"{(found.Count == 1 ? "1 build" : $"{found.Count} builds")} in {root}, {added} of them new to the list{(Remembered.Writing ? "" : ", and not kept: this run remembers nothing new")}.");
            run.Line();
            run.Set("found", new JsonObject { ["folder"] = root, ["builds"] = found.Count, ["added"] = Remembered.Writing ? added : 0 });
        }
        var builds = MadeBuilds.Load(library).Where(b => only is null || b.AppId == only.AppId).OrderByDescending(b => b.Made).ToList();
        var list = run.Array("builds");
        if (builds.Count == 0)
            run.Line(only is null ? "No builds made on this PC yet." : $"No builds of {only.Name} made on this PC yet.");

        foreach (var build in builds)
        {
            var state = MadeBuilds.StateOf(build, library);
            run.Line($"{Format.DateTime(build.Made)}  {build.Game}: {build.Title}");
            run.Line($"  {build.What}, {build.Folder}");
            run.Line($"  {state}");
            list.Add(new JsonObject
            {
                ["kind"] = build.Kind,
                ["made"] = build.Made.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["app"] = build.AppId,
                ["game"] = build.Game,
                ["build"] = build.Build,
                ["part"] = build.Part,
                ["folder"] = build.Folder,
                ["from"] = build.From,
                ["state"] = state,
                ["shared"] = build.Shared,
            });
        }
        return run.Ok();
    }

    public static int List(CliRun run, SteamInstall steam)
    {
        if (!run.Json) return ListCommand.Run(steam);

        var library = GameLibrary.Load(steam);
        run.Set("steam", new JsonObject
        {
            ["root"] = steam.Root,
            ["libraries"] = new JsonArray(steam.Libraries.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            ["unreachable"] = new JsonArray(steam.MissingLibraries.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            ["downloadLogFrom"] = library.Log.Start is { } start ? start.ToString("o") : null,
            ["appInfoProblem"] = library.AppInfoProblem,
        });
        run.Set("builtInList", new JsonObject
        {
            ["depots"] = library.List.Depots.Count,
            ["updated"] = library.List.Updated?.ToString("yyyy-MM-dd"),
        });

        var games = run.Array("games");
        foreach (var game in library.Entries) games.Add(Describe(library, game, withBuilds: true));
        return run.Ok();
    }

    public static int Builds(CliRun run, SteamInstall steam, GameEntry game)
    {
        var library = GameLibrary.Load(steam);
        game = library.Entries.First(e => e.AppId == game.AppId);
        if (!game.Downgradable)
        {
            run.Line($"{game.Name} [{game.AppId}]: {game.Title!.NotDowngradable}");
            run.Set("game", Describe(library, game, withBuilds: false));
            return run.Ok();
        }

        if (run.Settings.Language is { Length: > 0 } language)
        {
            if (Actions.InLanguage(run, library, game, language) is not { } inLanguage) return run.Reported ?? (int)ExitCode.Usage;
            game = inLanguage;
        }
        var history = library.History(game);
        if (run.Command.Value("at") is { } at)
        {
            var dated = Selectors.At(game, history, at, run.Command.Values("manifest"), out var error, out var needs);
            if (dated is null && needs.Count == 0) return run.Fail(ExitCode.Usage, "no-build", error!);
            run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
            if (dated is not null)
            {
                run.Line($"{game.Name} [{game.AppId}] at {at}: {dated.Label} (--build {dated.Key})");
                run.Set("at", new JsonObject { ["key"] = dated.Key, ["title"] = dated.Label });
                return run.Ok();
            }
            run.Line($"{game.Name} [{game.AppId}] at {at}:");
            run.Line(error + " Add each one with --manifest depot=manifest.");
            foreach (var need in needs) run.Line($"  {need.Depot,-8} {library.DepotName(game, need.Depot)}  {need.Url}");
            run.Set("needs", Actions.Needs(library, game, needs));
            return run.Fail(ExitCode.Failed, "needs-manifests", error + " Add each one with --manifest depot=manifest.");
        }

        var keys = Selectors.Keys(history.Builds);
        var titles = Format.BuildTitles(game, history.Builds);
        run.Line($"{game.Name} [{game.AppId}]");
        for (var i = 0; i < history.Builds.Count; i++)
            run.Line($"  {keys[i],-16} {titles[i]}{Format.BuildDetail(library, game, history.Builds[i])}");
        run.Line();
        run.Line("Use one of the names on the left with --build.");

        run.Set("game", Describe(library, game, withBuilds: true));
        return run.Ok();
    }

    public static int Status(CliRun run, SteamInstall steam, GameEntry? only)
    {
        var library = GameLibrary.Load(steam);
        var games = library.Entries.Where(e => e.Installed is not null && (only is null || e.AppId == only.AppId)).ToList();
        if (games.Count == 0)
        {
            run.Line(only is null ? "No Call of Duty is installed in these Steam libraries." : $"{only.Name} is not installed.");
            run.Array("games");
            return run.Ok();
        }

        var array = run.Array("games");
        foreach (var game in games)
        {
            var app = game.Installed!;
            run.Line($"{game.Name} [{game.AppId}]");
            run.Line($"  {app.InstallDir}");
            run.Line($"  build {app.BuildId}{(app.UpdatePending ? $", Steam has build {app.TargetBuildId} queued" : "")}");
            if (PatchStore.AppliedTo(app.InstallDir) is { } applied)
            {
                run.Line($"  {applied.Title}, written on {Format.DateTime(applied.Applied)}: {StateText(PatchApplier.StateOf(applied, library.FolderManifests(app.InstallDir)))}" +
                         (applied.Backup is null ? ", no backup" : ", backup kept"));
            }
            array.Add(Describe(library, game, withBuilds: false));
        }
        return run.Ok();
    }

    public static int Export(CliRun run, SteamInstall steam, string target)
    {
        var code = ExportCommand.Run(steam, target, run.Json, out var written);
        run.Set("file", written);
        return code == 0 ? run.Ok() : run.Fail(code == 2 ? ExitCode.Usage : ExitCode.Failed, "export", $"The export was not written to {target}.");
    }

    static string StateText(DowngradeState state) => state switch
    {
        DowngradeState.Intact => "files in place",
        DowngradeState.Unfinished => "stopped part way",
        DowngradeState.FilesChanged => "Steam has put back some of its files since",
        _ => "Steam has updated the game since",
    };

    /// <summary>One game as JSON: what Steam has, what is installed, any build written into it, and the builds it can be taken to.</summary>
    static JsonObject Describe(GameLibrary library, GameEntry game, bool withBuilds)
    {
        var node = new JsonObject
        {
            ["app"] = game.AppId,
            ["name"] = game.Name,
            ["downgradable"] = game.Downgradable,
        };
        if (!game.Downgradable) node["notDowngradable"] = game.Title!.NotDowngradable;

        if (game.Installed is { } app)
        {
            node["installed"] = new JsonObject
            {
                ["folder"] = app.InstallDir,
                ["buildId"] = app.BuildId,
                ["updatePending"] = app.UpdatePending,
                ["queuedBuildId"] = app.UpdatePending ? app.TargetBuildId : null,
                ["depots"] = app.Depots.Count,
            };

            if (PatchStore.AppliedTo(app.InstallDir) is { } applied)
            {
                node["applied"] = new JsonObject
                {
                    ["build"] = applied.Build,
                    ["part"] = applied.Part,
                    ["title"] = applied.Title,
                    ["written"] = applied.Applied.ToString("o"),
                    ["state"] = PatchApplier.StateOf(applied, library.FolderManifests(app.InstallDir)).ToString().ToLowerInvariant(),
                    ["files"] = applied.Written.Count,
                    ["removed"] = applied.Removed.Count,
                    ["backup"] = applied.Backup,
                };
            }
        }

        if (library.Languages(game) is { Count: > 0 } languages)
        {
            node["language"] = library.LanguageOf(game);
            node["defaultLanguage"] = library.DefaultLanguage(game);
            node["languages"] = new JsonArray(languages.Select(l => (JsonNode)new JsonObject { ["code"] = l, ["name"] = SteamLanguages.Name(l) }).ToArray());
        }

        if (!withBuilds || !game.Downgradable) return node;

        var history = library.History(game);
        var keys = Selectors.Keys(history.Builds);
        var titles = Format.BuildTitles(game, history.Builds);
        var builds = new JsonArray();
        for (var i = 0; i < history.Builds.Count; i++)
        {
            var build = history.Builds[i];
            var sizes = build.DifferentFromInstalled.Select(d => library.DepotSize(game, d, build.Manifests[d])).ToList();
            builds.Add(new JsonObject
            {
                ["key"] = keys[i],
                ["title"] = titles[i],
                ["kind"] = build.Kind.ToString().ToLowerInvariant(),
                ["built"] = build.Built?.ToString("o"),
                ["replaced"] = build.ReplacedBy?.Time?.ToString("o"),
                ["fromBuiltInList"] = build.ListOnly,
                ["depotsDifferent"] = new JsonArray(build.DifferentFromInstalled.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["downloadSize"] = sizes.Count > 0 && sizes.All(s => s is not null) ? (ulong)sizes.Sum(s => (long)s!.Value) : null,
                ["depotsUnknown"] = new JsonArray(build.Unknown.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["manifests"] = new JsonObject(build.Manifests.OrderBy(m => m.Key)
                    .Select(m => new KeyValuePair<string, JsonNode?>(m.Key.ToString(), JsonValue.Create(m.Value.ToString())))),
            });
        }
        node["builds"] = builds;

        var depots = new JsonArray();
        foreach (var (depot, known) in history.Depots.OrderBy(d => d.Key))
        {
            depots.Add(new JsonObject
            {
                ["depot"] = depot,
                ["name"] = library.DepotName(game, depot),
                ["owner"] = game.OwnerOf(depot),
                ["manifests"] = new JsonArray(known.Select(k => (JsonNode)new JsonObject
                {
                    ["manifest"] = k.ManifestId.ToString(),
                    ["fetched"] = k.FirstSeen?.ToString("o"),
                    ["built"] = k.Created?.ToString("o"),
                    ["firstSeenOnSteamDb"] = k.Listed?.ToString("o"),
                    ["installed"] = k.Installed,
                    ["latest"] = k.Latest,
                    ["remembered"] = k.Remembered,
                }).ToArray()),
            });
        }
        node["depots"] = depots;
        return node;
    }
}
