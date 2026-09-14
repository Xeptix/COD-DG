using System.Globalization;
using CODDowngrader.Download;
using CODDowngrader.Patching;
using CODDowngrader.Steam;
using Spectre.Console;

namespace CODDowngrader.App;

public sealed partial class Interactive
{
    /// <summary>DepotDownloader and a sign-in, asked for once however many runs a patch takes.</summary>
    sealed class SignIn
    {
        public string? Exe { get; set; }
        public string? Account { get; set; }
    }

    /// <summary>
    /// A patch being made. <paramref name="Base"/> is the manifest each depot has before, for the depots that have one;
    /// <paramref name="Target"/> is every depot of the build; <paramref name="Kept"/> is the folder's other depots, which stay
    /// as they are. DepotDownloader fetches into <paramref name="Folder"/>.
    /// </summary>
    sealed record PatchJob(
        GameEntry Game,
        string Folder,
        IReadOnlyDictionary<uint, ulong> Base,
        IReadOnlyDictionary<uint, ulong> Target,
        IReadOnlyDictionary<uint, uint> Owners,
        IReadOnlyDictionary<uint, ulong> Kept,
        string LogPath)
    {
        public IReadOnlyList<uint> Changed =>
            Target.Where(t => !Base.TryGetValue(t.Key, out var b) || b != t.Value).Select(t => t.Key).OrderBy(d => d).ToList();
    }

    async Task<bool> SignInAsync(SignIn signIn)
    {
        if (signIn.Exe is not null) return true;
        var exe = await EnsureDepotDownloaderAsync();
        if (exe is null || !ChooseLogin(out var account)) return false;
        signIn.Exe = exe;
        signIn.Account = account;
        return true;
    }

    /// <summary>
    /// The files that turn the job's base build into its target. File lists come from Steam's manifest cache or the tool's
    /// own, and DepotDownloader fetches any others. Null when that was stopped or a list could not be had.
    /// </summary>
    async Task<PatchPlan?> PlanAsync(GameLibrary library, PatchJob job, SignIn signIn)
    {
        IReadOnlyList<ManifestFile>? List(uint depot, ulong manifest) =>
            library.Files(depot, manifest) ?? ManifestLists.Find(depot, manifest, job.Folder);

        var changed = job.Changed;

        // A run takes each depot once, so the target's lists and the base's are fetched in separate rounds.
        var rounds = new[]
        {
            changed.Select(d => (Depot: d, Manifest: job.Target[d])).Where(x => List(x.Depot, x.Manifest) is null).ToList(),
            changed.Where(job.Base.ContainsKey).Select(d => (Depot: d, Manifest: job.Base[d])).Where(x => List(x.Depot, x.Manifest) is null).ToList(),
        };
        if (rounds.Any(r => r.Count > 0))
        {
            AnsiConsole.MarkupLine($"[grey]Steam on this PC does not have the file lists of {rounds.Sum(r => r.Count)} of these manifests. DepotDownloader fetches them first, to work out which files differ.[/]");
            if (!await SignInAsync(signIn)) return null;
            Directory.CreateDirectory(job.Folder);
            foreach (var round in rounds.Where(r => r.Count > 0))
            {
                var run = await RunGroupsAsync(signIn.Exe!, Groups(round, job.Owners, job.Game.AppId), round.Count, job.Folder, job.LogPath, signIn.Account, manifestOnly: true);
                signIn.Account = run.Account;
                if (run.Cancelled) return null;
                if (run.Failed)
                {
                    AnsiConsole.MarkupLine("[red]DepotDownloader could not fetch the file lists.[/]");
                    ShowFailure(run, round.Select(r => r.Depot).ToList(), job.LogPath);
                    return null;
                }
            }
        }

        var baseLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
        var targetLists = new Dictionary<uint, IReadOnlyList<ManifestFile>>();
        foreach (var depot in changed)
        {
            var target = List(depot, job.Target[depot]);
            var before = job.Base.TryGetValue(depot, out var b) ? List(depot, b) : null;
            if (target is null || (job.Base.ContainsKey(depot) && before is null))
            {
                AnsiConsole.MarkupLine($"[red]The file list of depot {depot} could not be read, so which of its files differ is not known.[/]");
                AnsiConsole.MarkupLine($"[grey]Full output: {Markup.Escape(job.LogPath)}[/]");
                return null;
            }
            targetLists[depot] = target;
            if (before is not null) baseLists[depot] = before;
        }

        var kept = job.Kept.Where(k => !targetLists.ContainsKey(k.Key)).Select(k => List(k.Key, k.Value)).ToList();
        return PatchPlan.Compute(baseLists, targetLists, kept);
    }

    /// <summary>What a patch writes, depot by depot, and what the check of the installed files found.</summary>
    static void ShowPlan(GameLibrary library, GameEntry game, PatchPlan plan, PatchCheck? check)
    {
        var writes = plan.Writes.Where(w => check is null || !check.AlreadyThere.Contains(w.Name)).ToList();
        if (writes.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumns("Depot", "Contents", "Files", "Size");
            foreach (var group in writes.GroupBy(w => w.Depot).OrderBy(g => g.Key))
            {
                table.AddRow(group.Key.ToString(CultureInfo.InvariantCulture), Markup.Escape(library.DepotName(game, group.Key)),
                    group.Count().ToString(CultureInfo.InvariantCulture), Format.Size(group.Aggregate(0UL, (sum, w) => sum + w.Size)));
            }
            AnsiConsole.Write(table);
            foreach (var write in writes.OrderByDescending(w => w.Size).Take(5))
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(write.Name)}  {Format.Size(write.Size)}{(write.BaseSha is null ? ", new" : "")}[/]");
            if (writes.Count > 5) AnsiConsole.MarkupLine($"[grey]  and {writes.Count - 5} more[/]");
        }

        if (plan.Removes.Count > 0)
        {
            var names = string.Join(", ", plan.Removes.Take(4).Select(r => r.Name)) + (plan.Removes.Count > 4 ? ", ..." : "");
            AnsiConsole.MarkupLine($"[grey]{plan.Removes.Count} files this build does not have are removed: {Markup.Escape(names)}[/]");
        }
        if (!plan.RemovesKnown)
            AnsiConsole.MarkupLine("[grey]Files this build does not have are left in place, because a depot that stays as it is has no file list on this PC.[/]");
        if (check is { AlreadyThere.Count: > 0 })
            AnsiConsole.MarkupLine($"[grey]{check.AlreadyThere.Count} files are already this build's version.[/]");
        var personalized = writes.Where(w => w.Personalized).Select(w => w.Name).ToList();
        if (personalized.Count > 0)
            AnsiConsole.MarkupLine($"[grey]Steam personalizes {Markup.Escape(string.Join(", ", personalized))} for each account when it installs the game. What is downloaded is Steam's original.[/]");
        if (check is { Modified.Count: > 0 })
        {
            AnsiConsole.MarkupLine($"[yellow]{check.Modified.Count} files are not the installed build's version, as when a mod or a client has replaced them. They are replaced as well:[/]");
            foreach (var name in check.Modified.Take(8)) AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(name)}[/]");
            if (check.Modified.Count > 8) AnsiConsole.MarkupLine($"[yellow]  and {check.Modified.Count - 8} more[/]");
        }
    }

    /// <summary>Hashes the installed files the plan touches, with a progress bar.</summary>
    static async Task<PatchCheck> CheckAsync(string installDir, PatchPlan plan)
    {
        ulong total = 0;
        foreach (var name in plan.Writes.Select(w => w.Name).Concat(plan.Removes.Select(r => r.Name)))
            if (PatchApplier.PathIn(installDir, name) is { } path && File.Exists(path)) total += (ulong)new FileInfo(path).Length;

        PatchCheck? check = null;
        await AnsiConsole.Progress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask("Checking your installed files", maxValue: Math.Max(1, (double)total));
            check = await Task.Run(() => PatchApplier.Check(installDir, plan, n => task.Increment(n)));
            task.Value = task.MaxValue;
        });
        return check!;
    }

    /// <summary>Hashes the files of a folder against the build, with a progress bar. Returns the ones missing or wrong.</summary>
    static async Task<List<PatchWrite>> VerifyAsync(string folder, IReadOnlyList<PatchWrite> writes, string title)
    {
        var bad = new List<PatchWrite>();
        var total = writes.Aggregate(0UL, (sum, w) => sum + w.Size);
        await AnsiConsole.Progress().StartAsync(async ctx =>
        {
            var task = ctx.AddTask(title, maxValue: Math.Max(1, (double)total));
            bad = await Task.Run(() => PatchApplier.Missing(folder, writes, n => task.Increment(n)));
            task.Value = task.MaxValue;
        });
        return bad;
    }

    /// <summary>
    /// Downloads only the files in <paramref name="writes"/> into the job's folder and checks every one against the build.
    /// Returns the depots the account could not download, or null when the download was stopped or given up on.
    /// </summary>
    async Task<IReadOnlySet<uint>?> DownloadPlanAsync(PatchJob job, IReadOnlyList<PatchWrite> writes, SignIn signIn)
    {
        var skipped = new HashSet<uint>();
        if (writes.Count == 0) return skipped;
        if (!await SignInAsync(signIn)) return null;

        Directory.CreateDirectory(job.Folder);
        var listPath = Path.ChangeExtension(job.LogPath, ".files.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
        await File.WriteAllLinesAsync(listPath, DepotDownloaderTool.FileListLines(writes.Select(w => w.Name)));
        var depots = writes.Select(w => w.Depot).Distinct().OrderBy(d => d).Select(d => (Depot: d, Manifest: job.Target[d])).ToList();

        while (true)
        {
            var run = await RunGroupsAsync(signIn.Exe!, Groups(depots, job.Owners, job.Game.AppId), depots.Count, job.Folder, job.LogPath, signIn.Account, fileList: listPath);
            signIn.Account = run.Account;
            if (run.Cancelled)
            {
                AnsiConsole.MarkupLine("[yellow]Stopped. Choose the same version again to carry on from where it stopped.[/]");
                return null;
            }

            skipped.UnionWith(run.Skipped);
            if (!run.Failed)
            {
                var bad = await VerifyAsync(job.Folder, writes.Where(w => !skipped.Contains(w.Depot)).ToList(), "Checking the downloaded files");
                if (bad.Count == 0) return skipped;

                var names = string.Join(", ", bad.Take(4).Select(b => b.Name)) + (bad.Count > 4 ? ", ..." : "");
                AnsiConsole.MarkupLine($"[red]{bad.Count} files did not download correctly: {Markup.Escape(names)}[/]");
                // Deleted, so the next run downloads them again instead of trusting them.
                foreach (var file in bad)
                    if (PatchApplier.PathIn(job.Folder, file.Name) is { } path && File.Exists(path)) File.Delete(path);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]DepotDownloader did not finish.[/]");
                ShowFailure(run, depots.Select(d => d.Depot).ToList(), job.LogPath);
            }

            if (!AnsiConsole.Confirm("Try again? The download carries on from where it stopped.", true)) return null;
        }
    }

    /// <summary>
    /// Writes a downloaded plan into the installed game, keeping what it replaces when the user wants to, and records it for
    /// Undo. False when the user backed out, or writing stopped part way.
    /// </summary>
    async Task<bool> ApplyPlanAsync(GameLibrary library, IReadOnlyList<GameEntry> apps, string build, PatchPlan plan,
        IReadOnlySet<string> skip, string source, bool move, IReadOnlyDictionary<uint, ulong> target, IReadOnlyDictionary<uint, uint> owners)
    {
        var app = apps[0].Installed!;
        var names = plan.Writes.Where(w => !skip.Contains(w.Name)).Select(w => w.Name).Concat(plan.Removes.Select(r => r.Name)).ToList();
        while (names.FirstOrDefault(n => PatchApplier.PathIn(app.InstallDir, n) is { } p && PatchApplier.IsLocked(p)) is { } locked)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(locked)} is in use. Close the game first.[/]");
            if (!AnsiConsole.Confirm("Try again?", true)) return false;
        }

        var replacing = names.Select(n => PatchApplier.PathIn(app.InstallDir, n)).Where(File.Exists).Select(p => new FileInfo(p!).Length).ToList();
        string? backup = null;
        if (replacing.Count > 0 && AnsiConsole.Confirm(
                $"Keep the {replacing.Count} files this replaces or removes ({Format.Size((ulong)replacing.Sum())}), so Undo can put them back without downloading?", true))
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
            backup = Path.Combine(app.Library, "COD Downgrader", "Backups", Safe($"{apps[0].Name} {stamp}"));
        }

        var record = new AppliedRecord
        {
            InstallDir = app.InstallDir,
            AppIds = apps.Select(a => a.AppId).ToList(),
            Game = string.Join(" + ", apps.Select(a => a.Name)),
            Build = build,
            Applied = DateTimeOffset.Now,
            SteamManifests = IdMap.Write(FolderManifests(library, app.InstallDir)),
            TargetManifests = IdMap.Write(target),
            Owners = IdMap.Write(owners),
            Backup = backup,
        };

        var total = plan.Writes.Where(w => !skip.Contains(w.Name)).Aggregate(0UL, (sum, w) => sum + w.Size);
        try
        {
            await AnsiConsole.Progress().StartAsync(async ctx =>
            {
                var task = ctx.AddTask(move ? "Moving the build into the game" : "Copying the build into the game", maxValue: Math.Max(1, (double)total));
                await Task.Run(() => PatchApplier.Apply(record, plan, source, move, skip, r => PatchStore.SaveApplied(r), n => task.Increment(n)));
                task.Value = task.MaxValue;
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Writing the build into the game stopped: {Markup.Escape(e.Message)}[/]");
            AnsiConsole.MarkupLine("[yellow]Undo in the game's menu puts back what was changed.[/]");
            return false;
        }
        finally
        {
            DrainInput();
        }
        return true;
    }

    static IEnumerable<GameEntry> FolderApps(GameLibrary library, string installDir) =>
        library.Entries.Where(e => e.Installed is { } app && PathRules.Same(app.InstallDir, installDir));

    static Dictionary<uint, ulong> FolderManifests(GameLibrary library, string installDir) => library.FolderManifests(installDir);
}
