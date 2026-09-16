using System.Globalization;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Catalog;
using CODDowngrader.Download;
using CODDowngrader.Jobs;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>Which files of a build a command takes, from --only or --files.</summary>
public sealed record PartChoice(string? Label, Func<string, bool> Includes);

/// <summary>The commands that download, write into a game, or take a build back out.</summary>
public static partial class Actions
{
    public static async Task<int> RunAsync(Job run, SteamInstall steam, GameEntry chosen, string command)
    {
        var library = GameLibrary.Load(steam);
        var game = library.Entries.First(e => e.AppId == chosen.AppId);

        if (!game.Downgradable && command != "undo")
            return run.Fail(ExitCode.Usage, "not-downgradable", $"{game.Name}: {game.Title!.NotDowngradable}");

        var code = command switch
        {
            "download" => await DownloadAsync(run, library, game),
            "ingame" => await InGameAsync(run, library, game),
            "patch" => await PatchAsync(run, library, game),
            "apply" => await ApplyAsync(run, library, game),
            "undo" => await UndoAsync(run, library, game),
            _ => run.Fail(ExitCode.Usage, "unknown", $"\"{command}\" is not a command."),
        };

        // What was made goes on the list of builds made on this PC, whichever face ran it.
        if (code == (int)ExitCode.Ok && !run.Settings.Plan && command is "download" or "ingame" or "patch" or "apply"
            && MadeBuilds.FromJob(command, run.Result, game) is { } made)
            MadeBuilds.Add(made);
        return code;
    }

    // --- the whole build, into a folder of its own ------------------------------------------

    static async Task<int> DownloadAsync(Job run, GameLibrary library, GameEntry game)
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
        if (game.Installed is { } app && Directory.Exists(app.InstallDir) && !run.Settings.NoSeed)
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
        if (run.Settings.Plan)
        {
            run.Line($"  {depots.Count} depots{(sizes.All(s => s is not null) ? $", {Format.Size((ulong)sizes.Sum(s => (long)s!.Value))}" : "")}, {seed.Count} files from your installed copy. Nothing was changed.");
            run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
            run.Set("build", new JsonObject { ["key"] = target.Key, ["title"] = target.Label });
            run.Set("folder", destination);
            run.Set("plan", new JsonObject
            {
                ["depots"] = new JsonArray(depots.Select((d, i) => (JsonNode)new JsonObject
                {
                    ["depot"] = d.Depot,
                    ["manifest"] = d.Manifest.ToString(CultureInfo.InvariantCulture),
                    ["name"] = library.DepotName(game, d.Depot),
                    ["size"] = sizes[i],
                }).ToArray()),
                ["size"] = sizes.All(s => s is not null) ? (ulong)sizes.Sum(s => (long)s!.Value) : null,
                ["copiedFromInstall"] = seed.Count,
                ["copiedBytes"] = seed.Sum(s => s.Bytes),
            });
            return run.Ok();
        }
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
            run.Progress(new JobProgress("Copying from your installed copy", $"{seed.Count} files", 0));
            try
            {
                Seeder.Copy(seed, (done, all) => run.Progress(new JobProgress("Copying from your installed copy", $"{seed.Count} files", all > 0 ? (double)done / all : null)), run.Cancel);
            }
            catch (OperationCanceledException)
            {
                AppState.SaveRecord(destination, record);
                return run.Fail(ExitCode.Failed, "cancelled", "The download was stopped. Run the same command again to carry on.");
            }
        }

        // Each depot weighs what it holds; one whose size is not known here weighs what the others do on average.
        var sized = sizes.Where(s => s is > 0).Select(s => s!.Value).ToList();
        var usual = sized.Count > 0 ? (ulong)sized.Average(s => (double)s) : 1UL;
        run.DownloadSizes = depots.Select((d, i) => (d.Depot, Size: sizes[i] is > 0 ? sizes[i]!.Value : usual))
            .GroupBy(d => d.Depot).ToDictionary(g => g.Key, g => g.First().Size);
        RunOutcome outcome;
        try
        {
            outcome = await Runner.DepotsAsync(run, exe, game.AppId, depots, game.Owners, destination, log, login);
        }
        finally
        {
            run.DownloadSizes = null;
        }
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
        run.Set("shared", SharedBuild.Of(game, target.Manifests, target.Label, only: null, files: null, siblings: false).Text());
        return run.Ok();
    }

    /// <summary>
    /// Puts the account's personalized copies of the build's exes into a finished download when --exe installed was asked
    /// for, and says which files those are.
    /// </summary>
    static List<string> TakePersonalized(Job run, GameLibrary library, GameEntry game, IReadOnlyDictionary<uint, ulong> manifests,
        string destination, DownloadPart part)
    {
        var taken = new List<string>();
        if (game.Installed is not { } app || !Directory.Exists(app.InstallDir)) return taken;
        if (!string.Equals(run.Settings.Exe, "installed", StringComparison.OrdinalIgnoreCase)) return taken;

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

    /// <summary>
    /// undo: takes the downgrade written into the game out. Each file's original comes back from the backup; where the backup
    /// does not hold it, from Steam at the build Steam has installed, downloaded into staging first. With --plan it says which,
    /// and changes nothing.
    /// </summary>
    static async Task<int> UndoAsync(Job run, GameLibrary library, GameEntry game)
    {
        if (game.Installed is not { } app) return run.Fail(ExitCode.Usage, "not-installed", $"{game.Name} is not installed here.");
        if (PatchStore.AppliedTo(app.InstallDir) is not { } record)
            return run.Fail(ExitCode.Usage, "nothing-applied", $"No build from COD Downgrader is written into {app.InstallDir}.");

        var steam = library.FolderManifests(record.InstallDir);
        var depots = UndoPlan.Depots(record).Where(steam.ContainsKey).OrderBy(d => d).ToList();
        var lists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
        foreach (var depot in depots)
            if (library.Files(depot, steam[depot]) is { } files) lists[depot] = files;
        var plan = UndoPlan.Compute(record, lists);

        var owners = IdMap.Owners(record.Owners);
        if (owners.Count == 0) owners = new Dictionary<uint, uint>(game.Owners);
        var staging = Path.Combine(app.Library, "COD Downgrader", "Staging", Interactive.Safe($"{game.AppId} undo"));
        var log = Runner.LogPath(game.AppId);
        string? exe = null;
        LoginState? login = null;

        // A file with no backup copy comes from Steam's file list of the build it has installed; a list this PC lacks is fetched.
        var unlisted = depots.Where(d => !lists.ContainsKey(d)).ToList();
        if (plan.Lost.Count > 0 && unlisted.Count > 0)
        {
            if (await Login.ToolAsync(run) is not { } tool) return run.Reported ?? (int)ExitCode.Failed;
            exe = tool;
            login = new LoginState { Probe = new LoginProbe(owners.GetValueOrDefault(unlisted[0], game.OwnerOf(unlisted[0])), unlisted[0], steam[unlisted[0]]) };
            var wanted = unlisted.Select(d => (Depot: d, Manifest: steam[d])).ToList();
            if (await Runner.ListsAsync(run, exe, library, game, wanted, staging, log, login) is not { } fetched) return run.Reported ?? (int)ExitCode.Failed;
            foreach (var ((depot, _), files) in fetched) lists[depot] = files;
            plan = UndoPlan.Compute(record, lists);
        }

        var steamTitle = $"Installed now (build {app.BuildId})";
        ReportChange(run, library, game, app, depots.ToDictionary(d => d, d => steam[d]), steamTitle);
        run.Line($"{game.Name}: {record.Title} comes out of {app.InstallDir}");
        run.Line($"  {Count(plan.FromBackup.Count, "file")} back from the backup, {Count(plan.FromSteam.Count, "file")} from Steam"
                 + $"{(plan.FromSteam.Count > 0 ? $" ({Format.Size(plan.DownloadBytes)})" : "")}, {Count(plan.Delete.Count, "file")} deleted");
        if (plan.SteamChanged.Count > 0)
            run.Line($"  {Count(plan.SteamChanged.Count, "file")} Steam has put back since {(plan.SteamChanged.Count == 1 ? "stays" : "stay")} as {(plan.SteamChanged.Count == 1 ? "it is" : "they are")}.");
        var personalized = plan.FromSteam.Where(w => w.Personalized).Select(w => w.Name).ToList();
        if (personalized.Count > 0)
            run.Line($"  {string.Join(", ", personalized)} comes back as Steam's original: Verify integrity of game files in Steam gives back the copy it personalizes for your account.");
        if (plan.Lost.Count > 0)
            run.Warn($"{Count(plan.Lost.Count, "file")} cannot come back from a backup or from Steam, {plan.Lost[0]} among them, and {(plan.Lost.Count == 1 ? "stays" : "stay")} as the downgrade left {(plan.Lost.Count == 1 ? "it" : "them")}. Verify integrity of game files in Steam puts {(plan.Lost.Count == 1 ? "it" : "them")} right.");

        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["title"] = record.Title });
        run.Set("folder", app.InstallDir);
        if (run.Settings.Plan)
        {
            run.Set("plan", new JsonObject
            {
                ["fromBackup"] = new JsonArray(plan.FromBackup.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["size"] = f.Size }).ToArray()),
                ["fromSteam"] = new JsonArray(plan.FromSteam.Select(w => (JsonNode)new JsonObject
                {
                    ["name"] = w.Name,
                    ["depot"] = w.Depot,
                    ["size"] = w.Size,
                    ["personalized"] = w.Personalized,
                }).ToArray()),
                ["delete"] = new JsonArray(plan.Delete.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                ["steamChanged"] = new JsonArray(plan.SteamChanged.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                ["lost"] = new JsonArray(plan.Lost.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                ["download"] = plan.DownloadBytes,
            });
            run.Line("  Nothing was changed.");
            return run.Ok();
        }

        var names = record.Written.Select(f => f.Name).Concat(record.Removed);
        if (names.FirstOrDefault(n => PatchApplier.PathIn(record.InstallDir, n) is { } p && PatchApplier.IsLocked(p)) is { } locked)
            return run.Fail(ExitCode.Locked, "locked", $"{locked} is in use. Close the game and run this again.");

        if (plan.FromSteam.Count > 0)
        {
            if (exe is null)
            {
                if (await Login.ToolAsync(run) is not { } tool) return run.Reported ?? (int)ExitCode.Failed;
                exe = tool;
            }
            var first = plan.FromSteam[0].Depot;
            login ??= new LoginState { Probe = new LoginProbe(owners.GetValueOrDefault(first, game.OwnerOf(first)), first, steam[first]) };
            if (!EnoughSpace(run, staging, plan.DownloadBytes)) return (int)ExitCode.Failed;
            if (!await FetchAsync(run, exe, game, owners, plan.FromSteam, steam, staging, log, login)) return run.Reported ?? (int)ExitCode.Failed;

            run.Progress(new JobProgress("Putting the game's own files back"));
            foreach (var write in plan.FromSteam)
            {
                try
                {
                    if (PatchApplier.PathIn(staging, write.Name) is not { } from || PatchApplier.PathIn(record.InstallDir, write.Name) is not { } to) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return run.Fail(ExitCode.Failed, "undo", $"{write.Name} could not be put back: {e.Message} Run this again to carry on.");
                }
            }
        }

        run.Progress(new JobProgress("Putting the game's own files back"));
        var result = PatchApplier.Undo(record);
        if (result.Failed.Count > 0)
            return run.Fail(ExitCode.Failed, "undo", $"{result.Failed.Count} files could not be put back, {result.Failed[0]} among them. The backup and the record are kept, so this can be run again.");

        PatchStore.DeleteApplied(record);
        TryDelete(staging);
        var restored = result.Restored + plan.FromSteam.Count;
        run.Line($"Done. {Count(restored, "file")} put back{(plan.FromSteam.Count > 0 ? $", {plan.FromSteam.Count} of them from Steam" : "")}, {result.Deleted} removed.");
        run.Set("restored", restored);
        run.Set("downloaded", plan.FromSteam.Count);
        run.Set("deleted", result.Deleted);
        run.Set("lost", new JsonArray(plan.Lost.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()));
        return run.Ok();
    }

    // --- what the command line asks for ------------------------------------------------------

    /// <summary>The build a command works on. Null once the failure has been reported.</summary>
    internal static BuildTarget? Target(Job run, GameLibrary library, GameEntry game)
    {
        var target = TargetAsNamed(run, library, game);
        return target is not null && run.Settings.Label is { } label ? target with { Label = label } : target;
    }

    static BuildTarget? TargetAsNamed(Job run, GameLibrary library, GameEntry game)
    {
        var history = library.History(game);
        if (run.Settings.At is { } at)
        {
            var dated = Selectors.At(game, history, at, run.Settings.Manifests, out var atError, out var needs);
            if (dated is not null)
            {
                RememberNamed(library, run.Settings.Manifests);
                return dated;
            }
            if (needs.Count == 0)
            {
                run.Fail(ExitCode.Usage, "no-build", atError!);
                return null;
            }
            run.Set("needs", Needs(library, game, needs));
            foreach (var need in needs) run.Line($"  {need.Depot,-8} {library.DepotName(game, need.Depot)}  {need.Url}");
            run.Fail(ExitCode.Failed, "needs-manifests", atError + " Add each one with --manifest depot=manifest.");
            return null;
        }
        var target = Selectors.Build(game, history, run.Settings.Build, run.Settings.Manifests, out var error);
        if (target is null) run.Fail(ExitCode.Usage, "no-build", error!);
        else RememberNamed(library, run.Settings.Manifests);
        return target;
    }

    /// <summary>Remembers the manifests named with --manifest.</summary>
    static void RememberNamed(GameLibrary library, IReadOnlyList<string> named)
    {
        library.Remembered.Learn(named
            .Select(pair => pair.Split(new[] { '=', ':' }, 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (Depot: uint.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0,
                Manifest: ulong.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0))
            .Select(p => new RememberedManifest(p.Depot, p.Manifest, RememberedSource.Named)));
    }

    /// <summary>The depots a date needs from SteamDB, as JSON: each with its name, its owning app and its manifests page.</summary>
    internal static JsonArray Needs(GameLibrary library, GameEntry game, IReadOnlyList<NeededManifest> needs) =>
        new(needs.Select(n => (JsonNode)new JsonObject
        {
            ["depot"] = n.Depot,
            ["name"] = library.DepotName(game, n.Depot),
            ["app"] = n.Owner,
            ["url"] = n.Url,
        }).ToArray());

    /// <summary>Where a download or a patch goes: --to, or a folder named after the game beside the Steam library.</summary>
    static string? Destination(Job run, GameLibrary library, GameEntry game, string name, out string? error)
    {
        error = null;
        var answer = run.Settings.To;
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
    static bool EnoughSpace(Job run, string folder, ulong needed)
    {
        if (run.Yes || needed == 0 || Interactive.FreeSpace(folder) is not { } free || free >= needed) return true;
        run.Fail(ExitCode.Failed, "no-space",
            $"{folder} is on a drive with {Format.Size(free)} free, and this needs {Format.Size(needed)}. Pass --yes to go ahead anyway.");
        return false;
    }

    /// <summary>Which files of the build go in: --only all, content or binaries, or the names --files gives. Null once the failure has been reported.</summary>
    internal static PartChoice? Part(Job run)
    {
        if (run.Settings.Files is { Length: > 0 } files)
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

        return (run.Settings.Only ?? "all").ToLowerInvariant() switch
        {
            "all" or "everything" => new PartChoice(null, _ => true),
            "content" or "maps" => new PartChoice("content only", name => !PatchPlan.IsBinary(name)),
            "binaries" or "exes" or "exe" => new PartChoice("exes and DLLs only", PatchPlan.IsBinary),
            var other => Bad(run, other),
        };

        static PartChoice? Bad(Job run, string other)
        {
            run.Fail(ExitCode.Usage, "bad-only", $"--only takes all, content or binaries, not \"{other}\".");
            return null;
        }
    }
}
