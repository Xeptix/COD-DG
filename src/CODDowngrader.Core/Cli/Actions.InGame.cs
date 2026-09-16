using System.Globalization;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Download;
using CODDowngrader.Jobs;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

public static partial class Actions
{
    // --- into the installed game --------------------------------------------------------------

    static async Task<int> InGameAsync(Job run, GameLibrary library, GameEntry game)
    {
        if (game.Installed is not { } app || !Directory.Exists(app.InstallDir))
            return run.Fail(ExitCode.Usage, "not-installed", $"{game.Name} is not installed here.");
        if (Target(run, library, game) is not { } target) return (int)ExitCode.Usage;
        if (Part(run) is not { } part) return (int)ExitCode.Usage;

        // Steam puts its own files back when it verifies or updates a game, and --again writes the build in over that.
        if (TakeOutFirst(run, game, app) is { } stopped) return stopped;

        // Apps that share a folder share content. --siblings takes each of them to its own build from the same time.
        var manifests = new Dictionary<uint, ulong>(target.Manifests);
        var owners = new Dictionary<uint, uint>(game.Owners);
        var apps = new List<GameEntry> { game };
        var withSiblings = run.Settings.Siblings;
        var siblingNotes = new JsonArray();
        foreach (var sibling in library.Entries.Where(e => e.AppId != game.AppId && e.Installed is { } s && PathRules.Same(s.InstallDir, app.InstallDir)))
        {
            if (sibling.Owners.Count > 0 && sibling.Owners.Keys.All(manifests.ContainsKey))
            {
                apps.Add(sibling);
                continue;
            }

            var shared = sibling.Owners.Keys
                .Where(d => manifests.TryGetValue(d, out var m) && sibling.InstalledManifests?.GetValueOrDefault(d) is { } have && have != 0 && have != m)
                .OrderBy(d => d).ToList();
            var match = withSiblings && sibling.Downgradable && target.Build?.ReplacedBy?.Time is { } time
                ? Interactive.LiveBefore(library.History(sibling).Builds, time)
                : null;

            var canFollow = sibling.Downgradable && target.Build?.ReplacedBy?.Time is { } when
                            && Interactive.LiveBefore(library.History(sibling).Builds, when) is { Unknown.Count: 0 } same
                            && sibling.InstalledManifests is { } siblingHas && same.Manifests.Any(m => siblingHas.GetValueOrDefault(m.Key) != m.Value);
            siblingNotes.Add(new JsonObject
            {
                ["app"] = sibling.AppId,
                ["name"] = sibling.Name,
                ["sharedDepots"] = new JsonArray(shared.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["canFollow"] = canFollow,
            });

            if (match is { Unknown.Count: 0 } && sibling.InstalledManifests is { } installed
                && match.Manifests.Any(m => installed.GetValueOrDefault(m.Key) != m.Value))
            {
                apps.Add(sibling);
                foreach (var (depot, manifest) in match.Manifests)
                {
                    manifests.TryAdd(depot, manifest);
                    owners.TryAdd(depot, sibling.OwnerOf(depot));
                }
                run.Line($"  {sibling.Name} goes back to its build from the same time as well.");
            }
            else if (shared.Count > 0)
            {
                run.Warn(withSiblings
                    ? $"{sibling.Name} is installed in the same folder and uses depots {string.Join(", ", shared)}, which this changes. No build of it from the same time is known here, so it stays as it is."
                    : $"{sibling.Name} is installed in the same folder and uses depots {string.Join(", ", shared)}, which this changes. Pass --siblings to take it back to its build from the same time as well.");
            }
        }

        var folder = library.FolderManifests(app.InstallDir);
        var baseManifests = manifests.Keys.Where(folder.ContainsKey).ToDictionary(d => d, d => folder[d]);
        var kept = folder.Where(f => !manifests.TryGetValue(f.Key, out var t) || t == f.Value).ToDictionary(f => f.Key, f => f.Value);
        var staging = Path.Combine(app.Library, "COD Downgrader", "Staging", Interactive.Safe($"{game.AppId} {target.Key}"));

        if (await Login.ToolAsync(run) is not { } exe) return (int)ExitCode.Failed;
        var first = manifests.OrderBy(m => m.Key).First();
        var login = new LoginState { Probe = new LoginProbe(owners.GetValueOrDefault(first.Key, game.AppId), first.Key, first.Value) };
        var log = Runner.LogPath(game.AppId);
        var name = string.Join(" + ", apps.Select(a => a.Name));

        run.Line($"{name}: {target.Label}");
        run.Line($"  into {app.InstallDir}");
        if (app.UpdatePending) run.Warn($"Steam has build {app.TargetBuildId} queued for this game, and installing it replaces these files again.");

        if (await PlanAsync(run, library, game, exe, baseManifests, manifests, kept, staging, log, login) is not { } plan) return (int)ExitCode.Failed;

        var check = PatchApplier.Check(app.InstallDir, plan, Reading(run, "Checking your installed files", plan.Writes.Aggregate(0UL, (sum, w) => sum + w.Size)));
        var full = plan;
        plan = plan.Only(part.Includes);

        // An exe Steam personalized for this account stays as it is unless --exe steam asks for Steam's original.
        var changing = new HashSet<string>(plan.Writes.Select(w => w.Name), PathRules.Comparer);
        var copies = Interactive.PersonalizedInInstall(library, app.InstallDir, manifests, folder, staging)
            .Where(c => !changing.Contains(c.Name) && part.Includes(c.Name)).ToList();
        if (copies.Count > 0 && string.Equals(run.Settings.Exe, "steam", StringComparison.OrdinalIgnoreCase))
        {
            plan = Interactive.WithOriginals(plan, copies);
            run.Line($"  {string.Join(", ", copies.Select(c => c.Name))}: Steam's original goes in, over the copy Steam personalized for your account.");
        }

        var writes = plan.Writes.Where(w => !check.AlreadyThere.Contains(w.Name)).ToList();
        if (run.Settings.Plan)
        {
            run.Set("siblings", siblingNotes);
            ReportChange(run, library, game, app, manifests, part.Label is null ? target.Label : $"{target.Label}, {part.Label}");
            return ReportPlan(run, game, target, full, check, copies, writes, plan.Removes, app.InstallDir);
        }
        if (writes.Count == 0 && plan.Removes.Count == 0)
        {
            run.Line(part.Label is null ? "The installed files are already this build." : "Those files are already this build.");
            Report(run, game, target, part, 0, 0, app.InstallDir, null, apps.Count > 1);
            return run.Ok();
        }
        if (check.Locked.Count > 0)
            return run.Fail(ExitCode.Locked, "locked", $"{check.Locked[0]} is in use. Close the game and run this again.");
        if (check.Modified.Count > 0)
            run.Warn($"{check.Modified.Count} files are not the installed build's version, as when a mod or a client has replaced them, {check.Modified[0]} among them. They are replaced as well.");

        var bytes = writes.Aggregate(0UL, (sum, w) => sum + w.Size);
        run.Line($"  {Count(writes.Count, "file")} to download ({Format.Size(bytes)}){(plan.Removes.Count > 0 ? $", {plan.Removes.Count} to remove" : "")}");
        if (!EnoughSpace(run, staging, bytes)) return (int)ExitCode.Failed;

        if (!await FetchAsync(run, exe, game, owners, writes, manifests, staging, log, login)) return (int)ExitCode.Failed;

        foreach (var written in writes.Select(w => w.Name).Concat(plan.Removes.Select(r => r.Name)))
            if (PatchApplier.PathIn(app.InstallDir, written) is { } path && PatchApplier.IsLocked(path))
                return run.Fail(ExitCode.Locked, "locked", $"{written} is in use. Close the game and run this again.");

        string? backup = null;
        if (!string.Equals(run.Settings.Backup, "no", StringComparison.OrdinalIgnoreCase))
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            backup = Path.Combine(app.Library, "COD Downgrader", "Backups", Interactive.Safe($"{game.Name} {stamp}"));
        }

        var record = new AppliedRecord
        {
            InstallDir = app.InstallDir,
            AppIds = apps.Select(a => a.AppId).ToList(),
            Game = name,
            Build = target.Label,
            Part = part.Label,
            Applied = DateTimeOffset.Now,
            SteamManifests = IdMap.Write(folder),
            TargetManifests = IdMap.Write(manifests),
            Owners = IdMap.Write(owners),
            Backup = backup,
        };

        try
        {
            PatchApplier.Apply(record, plan, staging, move: true, check.AlreadyThere, r => PatchStore.SaveApplied(r), Reading(run, "Writing into the game", writes.Aggregate(0UL, (sum, w) => sum + w.Size)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return run.Fail(ExitCode.Failed, "apply", $"Writing the build into the game stopped: {e.Message} \"CODDowngrader undo {game.AppId}\" puts back what was changed.");
        }
        TryDelete(staging);

        run.Line($"Done. {name} in {app.InstallDir} is now {(part.Label is null ? target.Label : $"{target.Label}, {part.Label}")}.");
        Report(run, game, target, part, record.Written.Count, record.Removed.Count, app.InstallDir, backup, apps.Count > 1);
        run.Set("apps", new JsonArray(apps.Select(a => (JsonNode)JsonValue.Create(a.AppId)!).ToArray()));
        return run.Ok();
    }

    // --- a patch folder -----------------------------------------------------------------------

    static async Task<int> PatchAsync(Job run, GameLibrary library, GameEntry game)
    {
        if (Target(run, library, game) is not { } target) return (int)ExitCode.Usage;
        if (Part(run) is not { } part) return (int)ExitCode.Usage;

        var history = library.History(game);
        var fromKey = run.Settings.From ?? (Selectors.Build(game, history, "latest", Array.Empty<string>(), out _) is not null ? "latest" : "newest");
        var from = Selectors.Build(game, history, fromKey, Array.Empty<string>(), out var fromError);
        if (from is null) return run.Fail(ExitCode.Usage, "no-from", fromError!);
        if (from.Manifests.Count == 0) return run.Fail(ExitCode.Usage, "no-from", $"The build {fromKey} has no manifests here.");

        var destination = Destination(run, library, game, $"{game.Name} (patch {from.Key} to {target.Key})", out var error);
        if (destination is null) return run.Fail(ExitCode.Usage, "no-folder", error!);
        if (!run.Yes && Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()
            && PatchStore.LoadPatch(destination) is null)
            return run.Fail(ExitCode.Usage, "folder-not-empty", $"{destination} already holds files. Pass --yes to write the patch into it anyway.");

        if (await Login.ToolAsync(run) is not { } exe) return (int)ExitCode.Failed;
        var first = target.Manifests.OrderBy(m => m.Key).First();
        var login = new LoginState { Probe = new LoginProbe(game.OwnerOf(first.Key), first.Key, first.Value) };
        var log = Runner.LogPath(game.AppId);

        var baseManifests = target.Manifests.Keys.Where(from.Manifests.ContainsKey).ToDictionary(d => d, d => from.Manifests[d]);
        var kept = from.Manifests.Where(b => !target.Manifests.TryGetValue(b.Key, out var t) || t == b.Value).ToDictionary(b => b.Key, b => b.Value);

        run.Line($"{game.Name}: a patch from {from.Label} to {target.Label}");
        run.Line($"  into {destination}");
        if (await PlanAsync(run, library, game, exe, baseManifests, target.Manifests, kept, destination, log, login) is not { } plan) return (int)ExitCode.Failed;

        var full = plan;
        plan = plan.Only(part.Includes);
        if (run.Settings.Plan) return ReportPlan(run, game, target, full, null, Array.Empty<PersonalizedCopy>(), plan.Writes, plan.Removes, destination);
        if (plan.Writes.Count == 0 && plan.Removes.Count == 0)
        {
            run.Line("Those two builds have the same files.");
            Report(run, game, target, part, 0, 0, destination, null, siblings: false);
            return run.Ok();
        }

        Directory.CreateDirectory(destination);
        RememberDestination(destination);
        var record = new PatchRecord
        {
            AppIds = new List<uint> { game.AppId },
            Game = game.Name,
            Build = target.Label,
            Part = part.Label,
            From = from.Label,
            Created = DateTimeOffset.Now,
            BaseManifests = IdMap.Write(baseManifests),
            TargetManifests = IdMap.Write(target.Manifests),
            Owners = IdMap.Write(game.Owners),
            Files = plan.Writes.ToList(),
            Remove = plan.Removes.ToList(),
            RemovesKnown = plan.RemovesKnown,
        };
        PatchStore.SavePatch(destination, record);

        run.Line($"  {Count(plan.Writes.Count, "file")} to download ({Format.Size(plan.WriteBytes)})");
        if (!EnoughSpace(run, destination, plan.WriteBytes)) return (int)ExitCode.Failed;
        if (!await FetchAsync(run, exe, game, game.Owners, plan.Writes, target.Manifests, destination, log, login)) return (int)ExitCode.Failed;

        record.Complete = true;
        PatchStore.SavePatch(destination, record);
        TryDelete(Path.Combine(destination, ".DepotDownloader"));

        run.Line($"Done. {destination} holds a patch from {from.Label} to {target.Label}: {Count(record.Files.Count, "file")}.");
        Report(run, game, target, part, record.Files.Count, record.Remove.Count, destination, null, siblings: false);
        run.Set("from", new JsonObject { ["key"] = from.Key, ["title"] = from.Label });
        return run.Ok();
    }

    // --- a folder into the installed game ------------------------------------------------------

    static async Task<int> ApplyAsync(Job run, GameLibrary library, GameEntry game)
    {
        if (game.Installed is not { } app || !Directory.Exists(app.InstallDir))
            return run.Fail(ExitCode.Usage, "not-installed", $"{game.Name} is not installed here.");
        if (Part(run) is not { } part) return (int)ExitCode.Usage;
        if (PatchStore.AppliedTo(app.InstallDir) is { } earlier && !run.Settings.Again)
            return run.Fail(ExitCode.Usage, "already-applied",
                $"{app.InstallDir} already holds {earlier.Title}. Run \"CODDowngrader undo {game.AppId}\" first, or pass --again to take it out and put this in.");

        var answer = run.Settings.From;
        if (string.IsNullOrWhiteSpace(answer))
            return run.Fail(ExitCode.Usage, "no-folder", "Name the patch folder or downloaded build with --from.");
        string folder;
        try
        {
            folder = PathRules.Normalize(answer.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return run.Fail(ExitCode.Usage, "no-folder", e.Message);
        }

        var installedNow = library.FolderManifests(app.InstallDir);
        PatchPlan plan;
        IReadOnlyDictionary<uint, ulong> target;
        IReadOnlyDictionary<uint, uint> owners;
        IReadOnlyDictionary<string, string>? personalized = null;
        string build;
        string? builtPart;
        string? recordFiles = null;

        if (PatchStore.LoadPatch(folder) is { } patch)
        {
            if (!patch.AppIds.Contains(game.AppId) || !patch.Complete)
                return run.Fail(ExitCode.Usage, "wrong-patch", patch.Complete
                    ? $"That patch is for {patch.Game}."
                    : "That patch did not finish downloading. Save it into the same folder again to carry on.");
            var differ = IdMap.Manifests(patch.BaseManifests).Count(b => installedNow.TryGetValue(b.Key, out var have) && have != b.Value);
            if (differ > 0 && !run.Yes)
                return run.Fail(ExitCode.Usage, "different-build",
                    $"The patch was made from {patch.From}, and {differ} of its depots are on a different build in this game. Pass --yes to mix the two builds.");
            plan = patch.Plan();
            target = IdMap.Manifests(patch.TargetManifests);
            owners = IdMap.Owners(patch.Owners);
            build = patch.Build;
            builtPart = patch.Part;
            if (patch.Part == "chosen files") recordFiles = string.Join(",", patch.Files.Select(f => f.Name));
        }
        else if (AppState.LoadRecord(folder, out _)?.For(game.AppId) is { Complete: true } downloaded)
        {
            target = downloaded.ManifestMap();
            owners = game.Owners;
            build = downloaded.Build;
            builtPart = null;
            personalized = new Dictionary<string, string>(downloaded.Personalized, PathRules.Comparer);

            var targetLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
            var baseLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
            foreach (var (depot, manifest) in target)
            {
                var have = installedNow.GetValueOrDefault(depot);
                if (have == manifest) continue;
                if ((ManifestLists.Find(depot, manifest, folder) ?? library.Files(depot, manifest)) is not { } list)
                    return run.Fail(ExitCode.Failed, "no-file-list", $"The file list of depot {depot} is not in that folder, so its files cannot be matched to the game.");
                targetLists[depot] = list;
                if (have != 0 && library.Files(depot, have) is { } before) baseLists[depot] = before;
            }
            var kept = installedNow.Where(f => !targetLists.ContainsKey(f.Key)).Select(f => library.Files(f.Key, f.Value)).ToList();
            plan = PatchPlan.Compute(baseLists, targetLists, kept);
        }
        else
        {
            return run.Fail(ExitCode.Usage, "no-patch", $"{folder} has no patch and no finished download of {game.Name} from COD Downgrader.");
        }

        // The folder is read and good: a downgrade already in the game comes out now, before the game's files are checked.
        if (TakeOutFirst(run, game, app) is { } stopped) return stopped;

        var check = PatchApplier.Check(app.InstallDir, plan, Reading(run, "Checking your installed files", plan.Writes.Aggregate(0UL, (sum, w) => sum + w.Size)));
        var full = plan;
        plan = plan.Only(part.Includes);
        builtPart = part.Label ?? builtPart;

        var changing = new HashSet<string>(plan.Writes.Select(w => w.Name), PathRules.Comparer);
        var copies = await Task.Run(() => Interactive.PersonalizedInInstall(library, app.InstallDir, target, installedNow, folder)
            .Where(c => !changing.Contains(c.Name) && part.Includes(c.Name)
                        && PatchApplier.PathIn(folder, c.Name) is { } p && File.Exists(p) && FileHash.Sha1(p) == c.Sha)
            .ToList());
        if (copies.Count > 0 && string.Equals(run.Settings.Exe, "steam", StringComparison.OrdinalIgnoreCase))
        {
            plan = Interactive.WithOriginals(plan, copies);
            run.Line($"  {string.Join(", ", copies.Select(c => c.Name))}: Steam's original goes in, over the copy Steam personalized for your account.");
        }

        var writes = plan.Writes.Where(w => !check.AlreadyThere.Contains(w.Name)).ToList();
        if (run.Settings.Plan)
        {
            ReportChange(run, library, game, app, target, builtPart is null ? build : $"{build}, {builtPart}");
            return ReportPlan(run, game, new BuildTarget(target, build, "folder", null), full, check, copies, writes, plan.Removes, folder);
        }
        if (writes.Count == 0 && plan.Removes.Count == 0)
        {
            run.Line("The installed files are already this build.");
            run.Set("folder", folder);
            return run.Ok();
        }
        if (check.Locked.Count > 0)
            return run.Fail(ExitCode.Locked, "locked", $"{check.Locked[0]} is in use. Close the game and run this again.");

        var bad = PatchApplier.Missing(folder, writes, Reading(run, "Checking the files in that folder", writes.Aggregate(0UL, (sum, w) => sum + w.Size)), personalized);
        if (bad.Count > 0)
            return run.Fail(ExitCode.Failed, "incomplete", $"{bad.Count} files are missing from that folder or damaged, {bad[0].Name} among them. Nothing in the game was changed.");

        string? backup = null;
        if (!string.Equals(run.Settings.Backup, "no", StringComparison.OrdinalIgnoreCase))
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            backup = Path.Combine(app.Library, "COD Downgrader", "Backups", Interactive.Safe($"{game.Name} {stamp}"));
        }

        var record = new AppliedRecord
        {
            InstallDir = app.InstallDir,
            AppIds = new List<uint> { game.AppId },
            Game = game.Name,
            Build = build,
            Part = builtPart,
            Applied = DateTimeOffset.Now,
            SteamManifests = IdMap.Write(installedNow),
            TargetManifests = IdMap.Write(target),
            Owners = IdMap.Write(owners),
            Backup = backup,
        };

        try
        {
            PatchApplier.Apply(record, plan, folder, move: false, check.AlreadyThere, r => PatchStore.SaveApplied(r), Reading(run, "Writing into the game", writes.Aggregate(0UL, (sum, w) => sum + w.Size)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return run.Fail(ExitCode.Failed, "apply", $"Writing the build into the game stopped: {e.Message} \"CODDowngrader undo {game.AppId}\" puts back what was changed.");
        }

        if (run.Settings.Delete)
        {
            TryDelete(folder);
            run.Line($"Deleted {folder}.");
        }

        run.Line($"Done. {game.Name} in {app.InstallDir} is now {record.Title}.");
        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["title"] = record.Title, ["part"] = builtPart });
        run.Set("folder", folder);
        run.Set("written", record.Written.Count);
        run.Set("removed", record.Removed.Count);
        run.Set("backup", backup);
        var sharedFiles = part.Label == "chosen files" ? run.Settings.Files : part.Label is null ? recordFiles : null;
        run.Set("shared", SharedBuild.Of(game, target, build, SharedBuild.OnlyOf(builtPart), sharedFiles, siblings: false).Text());
        return run.Ok();
    }

    // --- shared ---------------------------------------------------------------------------------

    /// <summary>
    /// --again: the downgrade already written into the folder comes out before this build goes in. Without --again that is a
    /// failure, and under --plan nothing comes out: the plan says it would. Null to carry on; otherwise the exit code.
    /// </summary>
    static int? TakeOutFirst(Job run, GameEntry game, InstalledApp app)
    {
        if (PatchStore.AppliedTo(app.InstallDir) is not { } earlier) return null;
        if (!run.Settings.Again)
            return run.Fail(ExitCode.Usage, "already-applied",
                $"{app.InstallDir} already holds {earlier.Title}. Run \"CODDowngrader undo {game.AppId}\" first, or pass --again to take it out and write this build in.");
        if (run.Settings.Plan)
        {
            run.Line($"  {earlier.Title} comes out of the game first.");
            run.Set("takesOut", earlier.Title);
            return null;
        }

        var busy = earlier.Written.Select(f => f.Name).Concat(earlier.Removed)
            .FirstOrDefault(n => PatchApplier.PathIn(earlier.InstallDir, n) is { } p && PatchApplier.IsLocked(p));
        if (busy is not null) return run.Fail(ExitCode.Locked, "locked", $"{busy} is in use. Close the game and run this again.");

        var undone = PatchApplier.Undo(earlier);
        if (undone.Failed.Count > 0)
            return run.Fail(ExitCode.Failed, "undo", $"{undone.Failed.Count} files of {earlier.Title} could not be put back, {undone.Failed[0]} among them.");
        PatchStore.DeleteApplied(earlier);
        run.Line($"Took {earlier.Title} out first: {undone.Restored} files put back, {undone.Deleted} removed.");
        return null;
    }

    /// <summary>
    /// What the game holds now and what it will hold after, for a page to show before anything changes: the two builds' names,
    /// and each depot of <paramref name="after"/> whose manifest changes. A downgrade written in counts as what the game holds.
    /// </summary>
    static void ReportChange(Job run, GameLibrary library, GameEntry game, InstalledApp app, IReadOnlyDictionary<uint, ulong> after, string afterTitle)
    {
        var now = library.FolderManifests(app.InstallDir);
        var nowTitle = $"Installed now (build {app.BuildId})";
        if (PatchStore.AppliedTo(app.InstallDir) is { } applied)
        {
            foreach (var (depot, manifest) in IdMap.Manifests(applied.TargetManifests))
                if (now.ContainsKey(depot)) now[depot] = manifest;
            nowTitle = applied.Title;
        }

        var depots = new JsonArray();
        var unchanged = 0;
        foreach (var (depot, manifest) in after.OrderBy(a => a.Key))
        {
            ulong? had = now.TryGetValue(depot, out var n) ? n : null;
            if (had == manifest)
            {
                unchanged++;
                continue;
            }
            depots.Add(new JsonObject
            {
                ["depot"] = depot,
                ["name"] = library.DepotName(game, depot),
                ["now"] = had?.ToString(CultureInfo.InvariantCulture),
                ["after"] = manifest.ToString(CultureInfo.InvariantCulture),
            });
        }
        run.Set("change", new JsonObject { ["now"] = nowTitle, ["after"] = afterTitle, ["depots"] = depots, ["unchanged"] = unchanged });
    }

    /// <summary>The files that turn one build into another, fetching any file list this PC does not have. Null once the failure has been reported.</summary>
    static async Task<PatchPlan?> PlanAsync(Job run, GameLibrary library, GameEntry game, string exe,
        IReadOnlyDictionary<uint, ulong> baseManifests, IReadOnlyDictionary<uint, ulong> target, IReadOnlyDictionary<uint, ulong> kept,
        string folder, string log, LoginState login)
    {
        var changed = target.Where(t => !baseManifests.TryGetValue(t.Key, out var b) || b != t.Value).Select(t => t.Key).OrderBy(d => d).ToList();
        if (changed.Count == 0) return new PatchPlan(Array.Empty<PatchWrite>(), Array.Empty<PatchRemove>(), true);

        var wanted = changed.Select(d => (Depot: d, Manifest: target[d]))
            .Concat(changed.Where(baseManifests.ContainsKey).Select(d => (Depot: d, Manifest: baseManifests[d])))
            .ToList();
        if (await Runner.ListsAsync(run, exe, library, game, wanted, folder, log, login) is not { } lists) return null;

        var targetLists = changed.ToDictionary(d => d, d => lists[(d, target[d])]);
        var baseLists = changed.Where(baseManifests.ContainsKey).ToDictionary(d => d, d => lists[(d, baseManifests[d])]);
        var keptLists = kept.Where(k => !targetLists.ContainsKey(k.Key))
            .Select(k => library.Files(k.Key, k.Value) ?? ManifestLists.Find(k.Key, k.Value, folder))
            .ToList();
        return PatchPlan.Compute(baseLists, targetLists, keptLists);
    }

    /// <summary>Downloads only the files of the plan into the folder, and checks every one against the build.</summary>
    static async Task<bool> FetchAsync(Job run, string exe, GameEntry game, IReadOnlyDictionary<uint, uint> owners,
        IReadOnlyList<PatchWrite> writes, IReadOnlyDictionary<uint, ulong> target, string folder, string log, LoginState login)
    {
        if (writes.Count == 0) return true;

        Directory.CreateDirectory(folder);
        var listPath = Path.ChangeExtension(log, ".files.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
        await File.WriteAllLinesAsync(listPath, DepotDownloaderTool.FileListLines(writes.Select(w => w.Name)));

        var depots = writes.Select(w => w.Depot).Distinct().OrderBy(d => d).Select(d => (Depot: d, Manifest: target[d])).ToList();
        run.DownloadSizes = writes.GroupBy(w => w.Depot).ToDictionary(g => g.Key, g => g.Aggregate(0UL, (sum, w) => sum + w.Size));
        RunOutcome outcome;
        try
        {
            outcome = await Runner.DepotsAsync(run, exe, game.AppId, depots, owners, folder, log, login, listPath);
        }
        finally
        {
            run.DownloadSizes = null;
        }
        if (outcome.Cancelled)
        {
            run.Fail(ExitCode.Failed, "cancelled", "The download was stopped. Run the same command again to carry on.");
            return false;
        }
        if (outcome.Skipped.Count > 0)
        {
            run.Fail(ExitCode.NotOwned, "not-owned", $"The signed-in account cannot download depots {string.Join(", ", outcome.Skipped)}, so nothing was changed.");
            return false;
        }
        if (!outcome.Ok)
        {
            run.Fail(ExitCode.Failed, "download", $"DepotDownloader did not finish. {string.Join(" ", outcome.Messages.Distinct().TakeLast(3))} Full output: {log}");
            return false;
        }

        var bad = PatchApplier.Missing(folder, writes, Reading(run, "Checking the downloaded files", writes.Aggregate(0UL, (sum, w) => sum + w.Size)));
        if (bad.Count == 0) return true;
        run.Fail(ExitCode.Failed, "incomplete", $"{bad.Count} files did not download correctly, {bad[0].Name} among them. Run the same command again to fetch them.");
        return false;
    }

    /// <summary>
    /// --plan: what the job would change, and nothing changed. The files are the whole plan, before --only or --files cut it, so a
    /// caller can offer that choice; the counts and the size are what the job as asked would download and remove.
    /// </summary>
    static int ReportPlan(Job run, GameEntry game, BuildTarget target, PatchPlan full, PatchCheck? check,
        IReadOnlyList<PersonalizedCopy> copies, IReadOnlyList<PatchWrite> writes, IReadOnlyList<PatchRemove> removes, string folder)
    {
        var bytes = writes.Aggregate(0UL, (sum, w) => sum + w.Size);
        run.Line($"  {Count(writes.Count, "file")} to download ({Format.Size(bytes)}){(removes.Count > 0 ? $", {removes.Count} to remove" : "")}. Nothing was changed.");
        if (check is { Modified.Count: > 0 })
            run.Line($"  {check.Modified.Count} files are not the installed build's version, {check.Modified[0]} among them.");

        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["key"] = target.Key, ["title"] = target.Label });
        run.Set("folder", folder);
        run.Set("plan", new JsonObject
        {
            ["files"] = new JsonArray(full.Writes.Select(w => (JsonNode)new JsonObject
            {
                ["name"] = w.Name,
                ["depot"] = w.Depot,
                ["size"] = w.Size,
                ["binary"] = PatchPlan.IsBinary(w.Name),
                ["alreadyThere"] = check?.AlreadyThere.Contains(w.Name) ?? false,
                ["new"] = w.BaseSha is null,
                ["personalized"] = w.Personalized,
            }).ToArray()),
            ["remove"] = new JsonArray(full.Removes.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["size"] = r.Size }).ToArray()),
            ["removesKnown"] = full.RemovesKnown,
            ["modified"] = new JsonArray((check?.Modified ?? Array.Empty<string>()).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["locked"] = new JsonArray((check?.Locked ?? Array.Empty<string>()).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["personalizedCopies"] = new JsonArray(copies.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["depot"] = c.Depot }).ToArray()),
            ["writeCount"] = writes.Count,
            ["removeCount"] = removes.Count,
            ["download"] = bytes,
        });
        return run.Ok();
    }

    /// <summary>A byte counter that reports <paramref name="stage"/> as a share of <paramref name="total"/> bytes.</summary>
    static Action<long> Reading(Job run, string stage, ulong total)
    {
        long done = 0;
        run.Progress(new JobProgress(stage, null, total > 0 ? 0 : null));
        return bytes =>
        {
            done += bytes;
            run.Progress(new JobProgress(stage, null, total > 0 ? Math.Min(1, (double)done / total) : null));
        };
    }

    /// <param name="siblings">The games sharing the install folder went along.</param>
    static void Report(Job run, GameEntry game, BuildTarget target, PartChoice part, int written, int removed, string folder, string? backup, bool siblings)
    {
        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["key"] = target.Key, ["title"] = target.Label, ["part"] = part.Label });
        run.Set("folder", folder);
        run.Set("written", written);
        run.Set("removed", removed);
        run.Set("backup", backup);
        run.Set("shared", SharedBuild.Of(game, target.Manifests, target.Label, SharedBuild.OnlyOf(part.Label), part.Label == "chosen files" ? run.Settings.Files : null, siblings).Text());
    }

    static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
