using System.Globalization;
using CODDowngrader.Builds;
using CODDowngrader.Patching;
using CODDowngrader.Steam;
using Spectre.Console;

namespace CODDowngrader.App;

public sealed partial class Interactive
{
    const string ApplyItem = "Apply a patch or a downloaded build to this game";

    /// <summary>After a version is chosen: into the installed game, into a folder of its own, or as a patch folder.</summary>
    async Task ChooseHowAsync(GameLibrary library, GameEntry game, BuildHistoryResult history, Build? build,
        Dictionary<uint, ulong> manifests, Dictionary<uint, uint> owners, string label, string tag)
    {
        var items = new List<Item>();
        var isInstalled = game.InstalledManifests is { Count: > 0 } have && SameManifests(manifests, have);
        if (game.Installed is { } app && Directory.Exists(app.InstallDir) && !isInstalled)
            items.Add(new Item("Put it into the installed game: download only the files that differ and swap them in", "inplace"));
        items.Add(new Item("Download the whole build into a folder of its own", "folder"));
        var patchBase = PatchBase(game, history);
        if (patchBase.Count > 0 && !manifests.All(m => patchBase.TryGetValue(m.Key, out var v) && v == m.Value))
            items.Add(new Item($"Save a patch folder: only the files that differ from {PatchBaseName(game)}", "patch"));
        items.Add(new Item("Back", "back"));

        switch (Prompt("How?", items).Kind)
        {
            case "inplace":
                await DowngradeInPlaceAsync(library, game, build, manifests, owners, label, tag);
                break;
            case "folder":
                await DownloadAsync(library, game, manifests, owners, label, tag);
                break;
            case "patch":
                await SavePatchAsync(library, game, manifests, owners, label, tag, patchBase);
                break;
        }
    }

    /// <summary>What a patch folder is made from: Steam's latest build, or the newest build known when Steam on this PC has no product info for the game.</summary>
    static IReadOnlyDictionary<uint, ulong> PatchBase(GameEntry game, BuildHistoryResult history) =>
        game.LatestManifests
        ?? history.Builds.FirstOrDefault(b => b.ReplacedBy is null && b.Unknown.Count == 0)?.Manifests
        ?? new Dictionary<uint, ulong>();

    static string PatchBaseName(GameEntry game) => game.LatestManifests is not null ? "the latest build on Steam" : "the newest known build";

    static bool SameManifests(IReadOnlyDictionary<uint, ulong> a, IReadOnlyDictionary<uint, ulong> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>The build a game had just before an update at <paramref name="time"/>, allowing for the apps of one update arriving minutes apart.</summary>
    static Build? LiveBefore(IReadOnlyList<Build> builds, DateTimeOffset time)
    {
        Build? live = null;
        foreach (var build in builds)
        {
            if (build.ReplacedBy?.Time is { } replaced && replaced < time - TimeSpan.FromMinutes(10)) break;
            live = build;
        }
        return live;
    }

    static AppliedRecord? DowngradeOf(GameEntry game) => game.Installed is { } app ? PatchStore.AppliedTo(app.InstallDir) : null;

    /// <summary>The game menu's entries for a build written into its folder.</summary>
    static IEnumerable<Item> DowngradeItems(GameLibrary library, GameEntry game, AppliedRecord? applied)
    {
        if (game.Installed is not null) yield return new Item(ApplyItem, "apply");
        if (applied is null) yield break;
        var state = PatchApplier.StateOf(applied, FolderManifests(library, applied.InstallDir));
        if (state is DowngradeState.FilesChanged or DowngradeState.SteamUpdated)
            yield return new Item($"Downgrade again: {Markup.Escape(applied.Build)}", "again");
        yield return new Item("Undo the downgrade", "undo");
    }

    bool RefuseSecondDowngrade(string installDir)
    {
        if (PatchStore.AppliedTo(installDir) is not { } earlier) return false;
        AnsiConsole.MarkupLine($"[yellow]This folder already has a build written into it: {Markup.Escape(earlier.Game)}, {Markup.Escape(earlier.Build)}. Undo that in the game's menu first.[/]");
        Pause();
        return true;
    }

    // --- into the installed game ------------------------------------------------------------

    async Task DowngradeInPlaceAsync(GameLibrary library, GameEntry game, Build? build, IReadOnlyDictionary<uint, ulong> manifests,
        IReadOnlyDictionary<uint, uint> owners, string label, string tag)
    {
        var app = game.Installed!;
        if (RefuseSecondDowngrade(app.InstallDir)) return;
        if (app.UpdatePending)
            AnsiConsole.MarkupLine($"[yellow]Steam has build {app.TargetBuildId} queued for this game, and installing it replaces these files again.[/]");

        // The apps installed in one folder share content, so each is offered the build it had at the same time.
        var apps = new List<GameEntry> { game };
        var target = new Dictionary<uint, ulong>(manifests);
        var owner = new Dictionary<uint, uint>(owners);
        foreach (var sibling in FolderApps(library, app.InstallDir).Where(e => e.AppId != game.AppId))
        {
            if (sibling.Owners.Count > 0 && sibling.Owners.Keys.All(manifests.ContainsKey))
            {
                apps.Add(sibling);
                continue;
            }

            var shared = sibling.Owners.Keys
                .Where(d => manifests.TryGetValue(d, out var m) && sibling.InstalledManifests?.GetValueOrDefault(d) is { } have && have != 0 && have != m)
                .OrderBy(d => d).ToList();
            var match = sibling.Downgradable && build?.ReplacedBy?.Time is { } time ? LiveBefore(library.History(sibling).Builds, time) : null;
            var offer = match is { Unknown.Count: 0 } && sibling.InstalledManifests is { } siblingInstalled
                        && match.Manifests.Any(m => siblingInstalled.GetValueOrDefault(m.Key) != m.Value);
            var uses = shared.Count > 0 ? $" and uses depots {string.Join(", ", shared)} of this build" : "";

            if (offer && AnsiConsole.Confirm($"{Markup.Escape(sibling.Name)} is installed in the same folder{uses}. Take it back to its build from the same time too?", true))
            {
                apps.Add(sibling);
                foreach (var (depot, manifest) in match!.Manifests)
                {
                    target.TryAdd(depot, manifest);
                    owner.TryAdd(depot, sibling.OwnerOf(depot));
                }
            }
            else if (shared.Count > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(sibling.Name)} stays on its installed build, running on this build's depots {string.Join(", ", shared)}.[/]");
            }
        }

        var folder = FolderManifests(library, app.InstallDir);
        var baseManifests = target.Keys.Where(folder.ContainsKey).ToDictionary(d => d, d => folder[d]);
        var kept = folder.Where(f => !target.TryGetValue(f.Key, out var t) || t == f.Value).ToDictionary(f => f.Key, f => f.Value);
        var staging = Path.Combine(app.Library, "COD Downgrader", "Staging", Safe($"{game.AppId} {tag}"));
        var job = new PatchJob(game, staging, baseManifests, target, owner, kept, LogPath(game));
        var names = string.Join(" + ", apps.Select(a => a.Name));

        var signIn = new SignIn();
        var plan = await PlanAsync(library, job, signIn);
        if (plan is null)
        {
            Pause();
            return;
        }
        if (plan.Writes.Count == 0 && plan.Removes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]The installed files are already this build.[/]");
            Pause();
            return;
        }

        var check = await CheckAsync(app.InstallDir, plan);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(names)}[/]: {Markup.Escape(label)}, into {Markup.Escape(app.InstallDir)}");
        ShowPlan(library, game, plan, check);
        if (check.Locked.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(check.Locked[0])} is in use, so close the game before the files are swapped in.[/]");

        var writes = plan.Writes.Where(w => !check.AlreadyThere.Contains(w.Name)).ToList();
        var size = writes.Aggregate(0UL, (sum, w) => sum + w.Size);
        if (writes.Count == 0 && plan.Removes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]The installed files are already this build.[/]");
            Pause();
            return;
        }
        var question = writes.Count > 0 ? $"Download these {writes.Count} files ({Format.Size(size)}) and swap them into the game?" : "Remove those files from the game?";
        if (!AnsiConsole.Confirm(question, true)
            || (size > 0 && FreeSpace(staging) is { } free && free < size
                && !AnsiConsole.Confirm($"That drive has {Format.Size(free)} free and the download needs {Format.Size(size)}. Continue anyway?", false)))
        {
            DeleteIfOnlyFileLists(staging);
            return;
        }

        var skipped = await DownloadPlanAsync(job, writes, signIn);
        if (skipped is null)
        {
            Pause();
            return;
        }
        if (skipped.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]The signed-in account cannot download depots {string.Join(", ", skipped)}, so the game is left as it is.[/]");
            Pause();
            return;
        }

        if (!await ApplyPlanAsync(library, apps, label, plan, check.AlreadyThere, staging, move: true, target, owner))
        {
            Pause();
            return;
        }
        TryDelete(staging);

        AnsiConsole.MarkupLine($"[green]Done.[/] {Markup.Escape(names)} in {Markup.Escape(app.InstallDir)} is now {Markup.Escape(label)}.");
        AnsiConsole.MarkupLine("[grey]Steam still lists its latest build for the game. Verify integrity of game files, or Steam's next update of the game, brings latest files back; the game's menu here then offers to downgrade it again. Undo takes the downgrade out.[/]");
        Pause();
    }

    /// <summary>Takes out a downgrade Steam has partly undone, and writes the same build in again.</summary>
    async Task DowngradeAgainAsync(GameLibrary library, GameEntry game, AppliedRecord record)
    {
        if (!Undo(library, record, ask: false)) return;
        await DowngradeInPlaceAsync(library, game, null, IdMap.Manifests(record.TargetManifests), IdMap.Owners(record.Owners), record.Build, "again");
    }

    // --- a patch folder ---------------------------------------------------------------------

    async Task SavePatchAsync(GameLibrary library, GameEntry game, IReadOnlyDictionary<uint, ulong> manifests,
        IReadOnlyDictionary<uint, uint> owners, string label, string tag, IReadOnlyDictionary<uint, ulong> patchBase)
    {
        if (AskFolder(library, game, $"{game.Name} (patch to {tag})") is not { } folder) return;

        var baseManifests = manifests.Keys.Where(patchBase.ContainsKey).ToDictionary(d => d, d => patchBase[d]);
        var existing = PatchStore.LoadPatch(folder);
        var sameJob = existing is not null && existing.AppIds.Contains(game.AppId)
                      && SameManifests(IdMap.Manifests(existing.TargetManifests), manifests)
                      && SameManifests(IdMap.Manifests(existing.BaseManifests), baseManifests);
        if (sameJob && existing!.Complete)
        {
            AnsiConsole.MarkupLine("[green]That folder already holds this patch.[/]");
            Pause();
            return;
        }
        if (!sameJob && Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()
            && !AnsiConsole.Confirm("That folder already has files in it. Put the patch in it anyway?", false)) return;

        var kept = patchBase.Where(b => !manifests.TryGetValue(b.Key, out var t) || t == b.Value).ToDictionary(b => b.Key, b => b.Value);
        var job = new PatchJob(game, folder, baseManifests, manifests, owners, kept, LogPath(game));
        var signIn = new SignIn();
        var plan = await PlanAsync(library, job, signIn);
        if (plan is null)
        {
            Pause();
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(game.Name)}[/]: a patch from {PatchBaseName(game)} to {Markup.Escape(label)}");
        ShowPlan(library, game, plan, null);
        if (plan.Writes.Count == 0 && plan.Removes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Those two builds have the same files.[/]");
            Pause();
            return;
        }
        if (!AnsiConsole.Confirm($"Download the {plan.Writes.Count} files ({Format.Size(plan.WriteBytes)}) into {Markup.Escape(folder)}?", true)) return;

        Directory.CreateDirectory(folder);
        _settings.LastDestinationRoot = Path.GetDirectoryName(folder);
        AppState.SaveSettings(_settings);

        var record = new PatchRecord
        {
            AppIds = new List<uint> { game.AppId },
            Game = game.Name,
            Build = label,
            From = game.LatestManifests is not null
                ? $"Latest on Steam{(game.Info?.PublicBuildId is { } id ? $" (build {id})" : "")}"
                : "Newest build on the built-in list",
            Created = DateTimeOffset.Now,
            BaseManifests = IdMap.Write(baseManifests),
            TargetManifests = IdMap.Write(manifests),
            Owners = IdMap.Write(owners),
            Files = plan.Writes.ToList(),
            Remove = plan.Removes.ToList(),
            RemovesKnown = plan.RemovesKnown,
        };
        PatchStore.SavePatch(folder, record);

        var skipped = await DownloadPlanAsync(job, plan.Writes, signIn);
        if (skipped is null)
        {
            Pause();
            return;
        }
        if (skipped.Count > 0)
        {
            record.Files = record.Files.Where(f => !skipped.Contains(f.Depot)).ToList();
            AnsiConsole.MarkupLine($"[yellow]Skipped DLC depots this account does not own: {string.Join(", ", skipped)}. The patch leaves their files as they are.[/]");
        }
        record.Complete = true;
        PatchStore.SavePatch(folder, record);
        TryDelete(Path.Combine(folder, ".DepotDownloader"));

        var bytes = record.Files.Aggregate(0UL, (sum, f) => sum + f.Size);
        AnsiConsole.MarkupLine($"[green]Done.[/] {Markup.Escape(folder)} holds a patch from {PatchBaseName(game)} to {Markup.Escape(label)}: {record.Files.Count} files, {Format.Size(bytes)}.");
        AnsiConsole.MarkupLine($"[grey]To use it, choose {ApplyItem} in the game's menu.[/]");
        Pause();
    }

    // --- a folder into the installed game -----------------------------------------------------

    async Task ApplyFolderAsync(GameLibrary library, GameEntry game)
    {
        var app = game.Installed!;
        if (RefuseSecondDowngrade(app.InstallDir)) return;

        AnsiConsole.MarkupLine("[grey]A patch folder, or a folder COD Downgrader downloaded a whole build of this game into.[/]");
        var answer = AnsiConsole.Prompt(new TextPrompt<string>("Folder (empty to go back):").AllowEmpty());
        if (string.IsNullOrWhiteSpace(answer)) return;
        string folder;
        try
        {
            folder = PathRules.Normalize(answer.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
            Pause();
            return;
        }

        var installedNow = FolderManifests(library, app.InstallDir);
        PatchPlan plan;
        IReadOnlyDictionary<uint, ulong> target;
        IReadOnlyDictionary<uint, uint> owners;
        string build;
        var mixes = false;

        if (PatchStore.LoadPatch(folder) is { } patch)
        {
            if (!patch.AppIds.Contains(game.AppId) || !patch.Complete)
            {
                AnsiConsole.MarkupLine(patch.Complete
                    ? $"[red]That patch is for {Markup.Escape(patch.Game)}.[/]"
                    : "[red]That patch did not finish downloading. Save it into the same folder again to carry on.[/]");
                Pause();
                return;
            }
            var differ = IdMap.Manifests(patch.BaseManifests).Count(b => installedNow.TryGetValue(b.Key, out var have) && have != b.Value);
            if (differ > 0)
            {
                mixes = true;
                AnsiConsole.MarkupLine($"[yellow]The patch was made from {Markup.Escape(patch.From)}, and {differ} of its depots are on a different build in this game. Applying it anyway mixes the two builds.[/]");
            }
            plan = patch.Plan();
            target = IdMap.Manifests(patch.TargetManifests);
            owners = IdMap.Owners(patch.Owners);
            build = patch.Build;
        }
        else if (AppState.LoadRecord(folder, out _)?.For(game.AppId) is { Complete: true } part)
        {
            target = part.ManifestMap();
            owners = game.Owners;
            build = part.Build;

            var targetLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
            var baseLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
            foreach (var (depot, manifest) in target)
            {
                var have = installedNow.GetValueOrDefault(depot);
                if (have == manifest) continue;
                if ((ManifestLists.Find(depot, manifest, folder) ?? library.Files(depot, manifest)) is not { } list)
                {
                    AnsiConsole.MarkupLine($"[red]The file list of depot {depot} is not in that folder, so its files cannot be matched to the game.[/]");
                    Pause();
                    return;
                }
                targetLists[depot] = list;
                if (have != 0 && library.Files(depot, have) is { } before) baseLists[depot] = before;
            }
            var kept = installedNow.Where(f => !targetLists.ContainsKey(f.Key)).Select(f => library.Files(f.Key, f.Value)).ToList();
            plan = PatchPlan.Compute(baseLists, targetLists, kept);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]That folder has no patch and no finished download of {Markup.Escape(game.Name)} from COD Downgrader.[/]");
            Pause();
            return;
        }

        var check = await CheckAsync(app.InstallDir, plan);
        var writes = plan.Writes.Where(w => !check.AlreadyThere.Contains(w.Name)).ToList();
        if (writes.Count == 0 && plan.Removes.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]The installed files are already this build.[/]");
            Pause();
            return;
        }

        // Every file has to be there and whole before anything in the game changes.
        var bad = await VerifyAsync(folder, writes, "Checking the files in that folder");
        if (bad.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{bad.Count} files are missing from that folder or damaged, {Markup.Escape(bad[0].Name)} among them. Nothing in the game was changed.[/]");
            Pause();
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(game.Name)}[/]: {Markup.Escape(build)}, into {Markup.Escape(app.InstallDir)}");
        ShowPlan(library, game, plan, check);
        var changedDepots = writes.Select(w => w.Depot).ToHashSet();
        foreach (var sibling in FolderApps(library, app.InstallDir).Where(e => e.AppId != game.AppId))
        {
            var shared = sibling.Owners.Keys.Where(changedDepots.Contains).OrderBy(d => d).ToList();
            if (shared.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(sibling.Name)} is installed in the same folder and uses depots {string.Join(", ", shared)}, which this changes.[/]");
        }

        var size = writes.Aggregate(0UL, (sum, w) => sum + w.Size);
        var removing = plan.Removes.Count > 0 ? $" and remove {plan.Removes.Count}" : "";
        if (!AnsiConsole.Confirm($"Copy {writes.Count} files ({Format.Size(size)}) into the game{removing}?", !mixes)) return;
        if (!await ApplyPlanAsync(library, new[] { game }, build, plan, check.AlreadyThere, folder, move: false, target, owners))
        {
            Pause();
            return;
        }

        AnsiConsole.MarkupLine($"[green]Done.[/] {Markup.Escape(game.Name)} in {Markup.Escape(app.InstallDir)} is now {Markup.Escape(build)}.");
        var folderSize = FolderSize(folder);
        if (AnsiConsole.Confirm($"Delete {Markup.Escape(folder)}{(folderSize is { } s ? $" ({Format.Size(s)})" : "")}? The game has what it needs from it.", false))
            TryDelete(folder);
        AnsiConsole.MarkupLine("[grey]Steam still lists its latest build for the game. Verify integrity of game files, or Steam's next update of the game, brings latest files back; the game's menu here then offers to downgrade it again. Undo takes the downgrade out.[/]");
        Pause();
    }

    // --- undo ---------------------------------------------------------------------------------

    /// <summary>A line for the game's menu about the build written into its folder.</summary>
    static string DowngradeStatus(GameLibrary library, AppliedRecord record)
    {
        var what = $"{Markup.Escape(record.Build)}, written into this folder on {Format.Date(record.Applied)}";
        return PatchApplier.StateOf(record, FolderManifests(library, record.InstallDir)) switch
        {
            DowngradeState.Intact => $"[green]Downgraded in place: {what}.[/] [grey]Steam still lists its latest build.[/]",
            DowngradeState.Unfinished => $"[red]Writing {what} stopped part way. Undo puts back what was changed.[/]",
            DowngradeState.FilesChanged => $"[yellow]{what}. Steam has put back some of its files since.[/]",
            _ => $"[yellow]{what}. Steam has updated the game since, so its files are a mix of builds.[/]",
        };
    }

    /// <summary>Takes a downgrade out of the game. False when nothing was undone, or not all of it.</summary>
    bool Undo(GameLibrary library, AppliedRecord record, bool ask = true)
    {
        var state = PatchApplier.StateOf(record, FolderManifests(library, record.InstallDir));
        var backup = record.Backup is not null && Directory.Exists(record.Backup);
        if (ask)
        {
            AnsiConsole.MarkupLine(backup
                ? "[grey]Undo moves the files the downgrade replaced back from the backup, and removes the files it added.[/]"
                : "[grey]No backup was kept, so Undo removes the files the downgrade added. Verify integrity of game files in Steam then brings back the rest of the latest build.[/]");
            if (state is DowngradeState.FilesChanged or DowngradeState.SteamUpdated)
                AnsiConsole.MarkupLine("[grey]Files Steam has replaced since are left as Steam wrote them.[/]");
        }

        var names = record.Written.Select(f => f.Name).Concat(record.Removed);
        if (names.FirstOrDefault(n => PatchApplier.PathIn(record.InstallDir, n) is { } p && PatchApplier.IsLocked(p)) is { } locked)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(locked)} is in use. Close the game first.[/]");
            Pause();
            return false;
        }
        if (ask && !AnsiConsole.Confirm("Undo the downgrade?", true)) return false;

        var result = PatchApplier.Undo(record);
        if (result.Failed.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{result.Failed.Count} files could not be put back, {Markup.Escape(result.Failed[0])} among them. The backup and the record are kept, so Undo can try again.[/]");
            Pause();
            return false;
        }

        PatchStore.DeleteApplied(record);
        AnsiConsole.MarkupLine($"[green]Undone.[/] {result.Restored} files put back, {result.Deleted} removed.");
        if (!backup || state == DowngradeState.SteamUpdated)
            AnsiConsole.MarkupLine("[grey]Use Verify integrity of game files in Steam to be sure the game is its latest build again.[/]");
        if (ask) Pause();
        return true;
    }

    static ulong? FolderSize(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Aggregate(0UL, (sum, f) => sum + (ulong)new FileInfo(f).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes a staging folder DepotDownloader only fetched file lists into, keeping one that holds downloaded files.</summary>
    static void DeleteIfOnlyFileLists(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).All(e => Path.GetFileName(e) == ".DepotDownloader"))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(folder)} could not be deleted: {Markup.Escape(e.Message)}[/]");
        }
    }
}
