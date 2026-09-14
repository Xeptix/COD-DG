using System.Globalization;
using CODDowngrader.Builds;
using CODDowngrader.Steam;

namespace CODDowngrader.App;

/// <summary>--list: everything the tool can see, changing nothing. The thing to ask for when a download did not go as expected.</summary>
public static class ListCommand
{
    public static int Run(SteamInstall steam)
    {
        var library = GameLibrary.Load(steam);

        Console.WriteLine($"COD Downgrader {AppState.Version}");
        Console.WriteLine($"Steam        {steam.Root}");
        foreach (var path in steam.Libraries) Console.WriteLine($"library      {path}");
        foreach (var path in steam.MissingLibraries) Console.WriteLine($"unreachable  {path}");
        Console.WriteLine(library.Log.Start is { } start
            ? $"download log {Format.DateTime(start)} onwards"
            : "download log none");
        if (library.AppInfoProblem is { } problem) Console.WriteLine($"appinfo.vdf  {problem}");
        Console.WriteLine($"built-in list {library.List.Depots.Count} depots, updated {(library.List.Updated is { } updated ? updated.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "never")}");

        foreach (var game in library.Entries.Where(e => e.Installed is not null))
        {
            var app = game.Installed!;
            var borrowed = game.Owners.Where(o => o.Value != game.AppId).ToList();
            Console.WriteLine();
            Console.WriteLine($"{game.Name}  [{game.AppId}]");
            Console.WriteLine($"  {app.InstallDir}");
            Console.WriteLine($"  build {app.BuildId}, {Plural(app.Depots.Count, "depot")}" +
                              (borrowed.Count > 0 ? $" + {Plural(borrowed.Count, "depot")} from {string.Join(", ", borrowed.Select(b => b.Value).Distinct())}" : "") +
                              (app.UpdatePending ? $", Steam has build {app.TargetBuildId} queued" : ""));
            if (Patching.PatchStore.AppliedTo(app.InstallDir) is { } applied)
            {
                var state = Patching.PatchApplier.StateOf(applied, library.FolderManifests(app.InstallDir)) switch
                {
                    Patching.DowngradeState.Intact => "files in place",
                    Patching.DowngradeState.Unfinished => "stopped part way",
                    Patching.DowngradeState.FilesChanged => "Steam has put back some of its files since",
                    _ => "Steam has updated the game since",
                };
                Console.WriteLine($"  written into this folder on {Format.DateTime(applied.Applied)}: {applied.Build} ({state}{(applied.Backup is null ? "" : ", backup kept")})");
            }

            if (!game.Downgradable)
            {
                Console.WriteLine($"  {game.Title!.NotDowngradable}");
                continue;
            }

            var history = library.History(game);
            var titles = Format.BuildTitles(game, history.Builds);
            for (var i = 0; i < history.Builds.Count; i++)
            {
                var build = history.Builds[i];
                Console.WriteLine($"  - {titles[i]}{Format.BuildDetail(library, game, build)}");
                if (build.Kind == BuildKind.Installed) continue;
                foreach (var depot in build.DifferentFromInstalled)
                {
                    var to = build.Manifests[depot];
                    var known = history.Depots[depot].FirstOrDefault(k => k.ManifestId == to);
                    var source = library.Cached(depot, to) is not null ? "cached"
                        : known?.FirstSeen is not null ? "in the log"
                        : known?.Listed is not null ? "in the built-in list"
                        : "Steam's latest";
                    Console.WriteLine($"      depot {depot}: {game.InstalledManifests![depot]} -> {to} ({source})");
                }
            }
        }

        var others = library.Entries.Where(e => e.Installed is null).Select(e => e.Name).ToList();
        if (others.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Not installed: " + string.Join("; ", others));
        }
        return 0;
    }

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
