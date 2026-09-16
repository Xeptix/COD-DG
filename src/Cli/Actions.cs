using System.Globalization;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Download;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>Which files of a build a command takes, from --only or --files.</summary>
public sealed record PartChoice(string? Label, Func<string, bool> Includes);

/// <summary>The commands that download, write into a game, or take a build back out.</summary>
public static partial class Actions
{
    public static async Task<int> RunAsync(CliRun run, SteamInstall steam, GameEntry chosen, string command)
    {
        var library = GameLibrary.Load(steam);
        var game = library.Entries.First(e => e.AppId == chosen.AppId);

        if (!game.Downgradable && command != "undo")
            return run.Fail(ExitCode.Usage, "not-downgradable", $"{game.Name}: {game.Title!.NotDowngradable}");

        return command switch
        {
            "download" => await DownloadAsync(run, library, game),
            "ingame" => await InGameAsync(run, library, game),
            "patch" => await PatchAsync(run, library, game),
            "apply" => await ApplyAsync(run, library, game),
            "undo" => Undo(run, library, game),
            _ => run.Fail(ExitCode.Usage, "unknown", $"\"{command}\" is not a command."),
        };
    }

    // --- the whole build, into a folder of its own ------------------------------------------

    static async Task<int> DownloadAsync(CliRun run, GameLibrary library, GameEntry game)
    {
        if (Target(run, library, game) is not { } target) return (int)ExitCode.Usage;
        if (target.Manifests.Count == 0 || target.Manifests.Values.Any(m => m == 0))
            return run.Fail(ExitCode.Usage, "no-manifests", "Every depot of this build needs a manifest ID. Add them with --manifest.");

        var destination = Destination(run, library, game, $"{game.Name} ({target.Key})", out var error);
        if (destination is null) return run.Fail(ExitCode.Usage, "no-folder", error!);

        var record = AppState.LoadRecord(destination, out var unreadable);
        var part = record?.For(game.AppId);
        var resuming = part is not null && part.SameManifests(target.Manifests);
        if (!resuming && !run.Yes && (part is not null || record is not null || unreadable
                                      || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())))
        {
            return run.Fail(ExitCode.Usage, "folder-not-empty",
                $"{destination} already holds files{(part is not null ? $", including {game.Name}: {part.Build}" : "")}. Pass --yes to download into it anyway.");
        }

        if (await Login.ToolAsync(run) is not { } exe) return (int)ExitCode.Failed;
        var depots = target.Manifests.OrderBy(m => m.Key).Select(m => (Depot: m.Key, Manifest: m.Value)).ToList();
        var login = new LoginState { Probe = new LoginProbe(game.OwnerOf(depots[0].Depot), depots[0].Depot, depots[0].Manifest) };
        var log = Runner.LogPath(game.AppId);

        run.Line($"{game.Name}: {target.Label}");
        run.Line($"  into {destination}");

        // Files the build shares with the installed game are copied first, and DepotDownloader checks them.
        var seed = new List<SeedFile>();
        if (game.Installed is { } app && Directory.Exists(app.InstallDir) && !run.Command.Flag("no-seed"))
        {
            var recorded = DepotConfig.Read(destination);
            foreach (var (depot, manifest) in depots)
            {
                ulong? have = game.InstalledManifests is { } installed && installed.TryGetValue(depot, out var h) ? h : null;
                var chosenFiles = library.Files(depot, manifest) ?? ManifestLists.Find(depot, manifest, destination);
                if (chosenFiles is null && recorded?.GetValueOrDefault(depot) is { } done && done != 0 && done != DepotConfig.InProgress) continue;
                var installedFiles = have is null ? null : have == manifest ? chosenFiles : library.Files(depot, have.Value);
                seed.AddRange(Seeder.Plan(app.InstallDir, destination, depot, chosenFiles, installedFiles, have == manifest));
            }
        }

        var sizes = depots.Select(d => library.DepotSize(game, d.Depot, d.Manifest)).ToList();
        if (sizes.All(s => s is not null) && !EnoughSpace(run, destination, (ulong)sizes.Sum(s => (long)s!.Value))) return (int)ExitCode.Failed;

        Directory.CreateDirectory(destination);
        record ??= new DownloadRecord();
        if (part is null)
        {
            part = new DownloadPart { AppId = game.AppId, Game = game.Name };
            record.Downloads.Add(part);
        }
        if (!resuming)
        {
            part.Build = target.Label;
            part.Complete = false;
            part.Started = DateTimeOffset.Now;
            part.Finished = null;
            part.Skipped.Clear();
            part.Personalized.Clear();
            part.SetManifests(target.Manifests);
        }
        foreach (var group in seed.GroupBy(s => s.Depot))
        {
            var key = group.Key.ToString(CultureInfo.InvariantCulture);
            if (!part.CopiedFromInstall.TryGetValue(key, out var names)) part.CopiedFromInstall[key] = names = new List<string>();
            var known = new HashSet<string>(names, PathRules.Comparer);
            names.AddRange(group.Select(s => s.Name).Where(known.Add));
        }
        AppState.SaveRecord(destination, record);

        if (seed.Count > 0)
        {
            run.Line($"  copying {seed.Count} files from your installed copy first");
            Seeder.Copy(seed, (_, _) => { }, CancellationToken.None);
        }

        var outcome = await Runner.DepotsAsync(run, exe, game.AppId, depots, game.Owners, destination, log, login);
        if (!outcome.Ok && !outcome.Cancelled && outcome.Skipped.Count == 0)
        {
            AppState.SaveRecord(destination, record);
            return run.Fail(ExitCode.Failed, "download", $"DepotDownloader did not finish. {string.Join(" ", outcome.Messages.Distinct().TakeLast(3))} Full output: {log}");
        }
        if (outcome.Cancelled) return run.Fail(ExitCode.Failed, "cancelled", "The download was stopped. Run the same command again to carry on.");

        var finished = DepotConfig.Read(destination) ?? new Dictionary<uint, ulong>();
        var missing = depots.Where(d => !finished.TryGetValue(d.Depot, out var m) || m != d.Manifest).Select(d => d.Depot).ToList();
        var skippedDlc = missing.Where(d => outcome.Skipped.Contains(d) && library.IsDlc(game, d)).ToList();
        var notDownloaded = missing.Except(skippedDlc).ToList();
        if (notDownloaded.Count > 0)
        {
            AppState.SaveRecord(destination, record);
            var owned = notDownloaded.Where(outcome.Skipped.Contains).ToList();
            return run.Fail(owned.Count > 0 ? ExitCode.NotOwned : ExitCode.Failed, owned.Count > 0 ? "not-owned" : "download",
                owned.Count > 0
                    ? $"The signed-in account cannot download depots {string.Join(", ", owned)}."
                    : $"Depots {string.Join(", ", notDownloaded)} did not finish. Full output: {log}");
        }

        part.Complete = true;
        part.Finished = DateTimeOffset.Now;
        part.Skipped = skippedDlc.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
        var cleanup = Seeder.RemoveExtras(destination, record, part, finished);
        AppState.SaveRecord(destination, record);

        if (cleanup.UnreadableDepot is { } unreadableDepot)
            run.Warn($"DepotDownloader's manifest for depot {unreadableDepot} could not be read, so the files copied from your install were left in place.");
        if (skippedDlc.Count > 0) run.Warn($"Skipped DLC depots this account does not own: {string.Join(", ", skippedDlc)}.");

        var personalized = TakePersonalized(run, library, game, target.Manifests, destination, part);
        AppState.SaveRecord(destination, record);

        run.Line($"Done. {destination} holds {game.Name}: {target.Label}.");
        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["key"] = target.Key, ["title"] = target.Label });
        run.Set("folder", destination);
        run.Set("copiedFromInstall", seed.Count);
        run.Set("skippedDepots", new JsonArray(skippedDlc.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()));
        run.Set("personalizedFiles", new JsonArray(personalized.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()));
        return run.Ok();
    }

    /// <summary>
    /// Puts the account's personalized copies of the build's exes into a finished download when --exe installed was asked
    /// for, and says which files those are.
    /// </summary>
    static List<string> TakePersonalized(CliRun run, GameLibrary library, GameEntry game, IReadOnlyDictionary<uint, ulong> manifests,
        string destination, DownloadPart part)
    {
        var taken = new List<string>();
        if (game.Installed is not { } app || !Directory.Exists(app.InstallDir)) return taken;
        if (!string.Equals(run.Command.Value("exe"), "installed", StringComparison.OrdinalIgnoreCase)) return taken;

        var copies = Interactive.PersonalizedInInstall(library, app.InstallDir, manifests, game.InstalledManifests ?? new Dictionary<uint, ulong>(), destination);
        foreach (var copy in copies)
        {
            var from = PatchApplier.PathIn(app.InstallDir, copy.Name);
            var to = PatchApplier.PathIn(destination, copy.Name);
            if (from is null || to is null || !File.Exists(from) || !File.Exists(to)) continue;
            try
            {
                File.Copy(from, to, overwrite: true);
                part.Personalized[copy.Name] = copy.InstalledSha;
                taken.Add(copy.Name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                run.Warn($"{copy.Name} could not be copied from the installed game: {e.Message}");
            }
        }
        if (taken.Count > 0) run.Line($"  {string.Join(", ", taken)} came from your installed game, not from Steam.");
        return taken;
    }

    // --- taking a build back out ------------------------------------------------------------

    static int Undo(CliRun run, GameLibrary library, GameEntry game)
    {
        if (game.Installed is not { } app) return run.Fail(ExitCode.Usage, "not-installed", $"{game.Name} is not installed here.");
        if (PatchStore.AppliedTo(app.InstallDir) is not { } record)
            return run.Fail(ExitCode.Usage, "nothing-applied", $"No build from COD Downgrader is written into {app.InstallDir}.");

        var names = record.Written.Select(f => f.Name).Concat(record.Removed);
        if (names.FirstOrDefault(n => PatchApplier.PathIn(record.InstallDir, n) is { } p && PatchApplier.IsLocked(p)) is { } locked)
            return run.Fail(ExitCode.Locked, "locked", $"{locked} is in use. Close the game and run this again.");

        var state = PatchApplier.StateOf(record, library.FolderManifests(record.InstallDir));
        var result = PatchApplier.Undo(record);
        if (result.Failed.Count > 0)
            return run.Fail(ExitCode.Failed, "undo", $"{result.Failed.Count} files could not be put back, {result.Failed[0]} among them. The backup and the record are kept, so this can be run again.");

        PatchStore.DeleteApplied(record);
        run.Line($"Undone. {result.Restored} files put back, {result.Deleted} removed.");
        if (record.Backup is null || state == DowngradeState.SteamUpdated)
            run.Line("Use Verify integrity of game files in Steam to be sure the game is its latest build again.");

        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["title"] = record.Title });
        run.Set("restored", result.Restored);
        run.Set("deleted", result.Deleted);
        return run.Ok();
    }

    // --- what the command line asks for ------------------------------------------------------

    /// <summary>The build a command works on. Null once the failure has been reported.</summary>
    static BuildTarget? Target(CliRun run, GameLibrary library, GameEntry game)
    {
        var history = library.History(game);
        var target = Selectors.Build(game, history, run.Command.Value("build"), run.Command.Values("manifest"), out var error);
        if (target is null) run.Fail(ExitCode.Usage, "no-build", error!);
        return target;
    }

    /// <summary>Where a download or a patch goes: --to, or a folder named after the game beside the Steam library.</summary>
    static string? Destination(CliRun run, GameLibrary library, GameEntry game, string name, out string? error)
    {
        error = null;
        var answer = run.Command.Value("to");
        var root = game.Installed?.Library ?? library.Steam.Libraries.FirstOrDefault() ?? library.Steam.Root;
        try
        {
            var path = PathRules.Normalize(answer is { Length: > 0 }
                ? answer.Trim().Trim('"')
                : Path.Combine(root, "COD Downgrader", Interactive.Safe(name)));

            if (Path.GetPathRoot(path) is { Length: > 0 } driveRoot && PathRules.Same(driveRoot, path))
            {
                error = "Pick a folder, not the root of a drive.";
                return null;
            }
            if (library.Steam.Libraries.Any(l => PathRules.IsInside(path, Path.Combine(l, "steamapps"))))
            {
                error = "Pick a folder outside steamapps. Steam manages everything in there.";
                return null;
            }
            return path;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>False once the failure has been reported: the drive holding the folder has less room than the download needs.</summary>
    static bool EnoughSpace(CliRun run, string folder, ulong needed)
    {
        if (run.Yes || needed == 0 || Interactive.FreeSpace(folder) is not { } free || free >= needed) return true;
        run.Fail(ExitCode.Failed, "no-space",
            $"{folder} is on a drive with {Format.Size(free)} free, and this needs {Format.Size(needed)}. Pass --yes to go ahead anyway.");
        return false;
    }

    /// <summary>Which files of the build go in: --only all, content or binaries, or the names --files gives. Null once the failure has been reported.</summary>
    static PartChoice? Part(CliRun run)
    {
        if (run.Command.Value("files") is { Length: > 0 } files)
        {
            var names = new HashSet<string>(files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ManifestFile.NormalizeName), PathRules.Comparer);
            if (names.Count == 0)
            {
                run.Fail(ExitCode.Usage, "no-files", "--files takes file names, separated by commas.");
                return null;
            }
            return new PartChoice("chosen files", names.Contains);
        }

        return (run.Command.Value("only") ?? run.Command.Value("part") ?? "all").ToLowerInvariant() switch
        {
            "all" or "everything" => new PartChoice(null, _ => true),
            "content" or "maps" => new PartChoice("content only", name => !PatchPlan.IsBinary(name)),
            "binaries" or "exes" or "exe" => new PartChoice("exes and DLLs only", PatchPlan.IsBinary),
            var other => Bad(run, other),
        };

        static PartChoice? Bad(CliRun run, string other)
        {
            run.Fail(ExitCode.Usage, "bad-only", $"--only takes all, content or binaries, not \"{other}\".");
            return null;
        }
    }
}
