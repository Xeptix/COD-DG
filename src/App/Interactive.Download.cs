using System.Globalization;
using CODDowngrader.Download;
using CODDowngrader.Patching;
using CODDowngrader.Steam;
using Spectre.Console;

namespace CODDowngrader.App;

public sealed partial class Interactive
{
    sealed record RunOutcome(string? Account, bool Cancelled, bool Failed, IReadOnlySet<uint> Skipped, IReadOnlyList<string> Messages);

    async Task DownloadAsync(GameLibrary library, GameEntry game, IReadOnlyDictionary<uint, ulong> manifests,
        IReadOnlyDictionary<uint, uint> owners, string label, string folderTag)
    {
        if (manifests.Count == 0 || manifests.Values.Any(m => m == 0))
        {
            AnsiConsole.MarkupLine("[red]Every depot needs a manifest ID first.[/]");
            Pause();
            return;
        }

        var depots = manifests.OrderBy(m => m.Key).Select(m => (Depot: m.Key, Manifest: m.Value)).ToList();
        bool SameAsInstalled(uint depot, ulong manifest) =>
            game.InstalledManifests is { } installed && installed.TryGetValue(depot, out var have) && have == manifest;

        // --- what is about to be downloaded -----------------------------------------------

        var table = new Table().Border(TableBorder.Rounded).AddColumns("Depot", "Contents", "Manifest", "Size", "");
        var hidden = 0;
        foreach (var (depot, manifest) in depots)
        {
            var size = library.DepotSize(game, depot, manifest);
            var same = SameAsInstalled(depot, manifest);
            if (same && depots.Count > 12)
            {
                hidden++;
                continue;
            }
            var mark = game.InstalledManifests is null ? "" : same ? "" : game.InstalledManifests.ContainsKey(depot) ? "[yellow]differs[/]" : "[yellow]not installed[/]";
            table.AddRow(depot.ToString(), Markup.Escape(library.DepotName(game, depot)), manifest.ToString(),
                size is null ? "?" : Format.Size(size.Value), mark);
        }

        // Depots overlap (DLC packs repeat base files), so the size is counted by file when every list is known.
        var lists = depots.Select(d => library.Files(d.Depot, d.Manifest)).ToList();
        ulong total;
        var unknownSizes = 0;
        if (lists.All(l => l is not null))
        {
            total = (ulong)lists.SelectMany(l => l!).Where(f => !f.IsDirectory).DistinctBy(f => f.Name, PathRules.Comparer).Sum(f => (long)f.Size);
        }
        else
        {
            total = 0;
            foreach (var (depot, manifest) in depots)
            {
                if (library.DepotSize(game, depot, manifest) is { } size) total += size;
                else unknownSizes++;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(game.Name)}[/]: {Markup.Escape(label)}");
        AnsiConsole.Write(table);
        if (hidden > 0) AnsiConsole.MarkupLine($"[grey]{hidden} more depots are the same as installed.[/]");
        AnsiConsole.MarkupLine($"The whole game: {depots.Count} depots, {Format.Size(total)}{(unknownSizes > 0 ? $" plus {unknownSizes} of unknown size" : "")}.");

        // --- where --------------------------------------------------------------------------

        if (AskFolder(library, game, $"{game.Name} ({folderTag})") is not { } destination) return;

        // --- what the folder already holds ------------------------------------------------

        var record = AppState.LoadRecord(destination, out var unreadable);
        var part = record?.For(game.AppId);
        var resuming = part is not null && part.SameManifests(manifests);
        if (resuming)
        {
            AnsiConsole.MarkupLine(part!.Complete
                ? "[grey]That folder already holds this build. DepotDownloader checks it and fetches anything missing.[/]"
                : "[grey]That folder holds this download already. It carries on from where it stopped.[/]");
        }
        else if (part is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]That folder already holds {Markup.Escape(game.Name)}: {Markup.Escape(part.Build)}{(part.Complete ? "" : ", not finished")}.[/]");
            if (!AnsiConsole.Confirm("Download this build over it? Files that differ are replaced and files this build does not have are deleted.", false)) return;
        }
        else if (record is not null)
        {
            var held = string.Join("; ", record.Downloads.Select(d => $"{d.Game}: {d.Build}"));
            AnsiConsole.MarkupLine($"[yellow]That folder already holds {Markup.Escape(held)}.[/]");
            if (!AnsiConsole.Confirm($"Add {Markup.Escape(game.Name)} to it? Files both use are replaced by this build's.", false)) return;
        }
        else if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var question = unreadable
                ? $"That folder has files and a {AppState.RecordFileName} that cannot be read. Download into it anyway?"
                : "That folder already has files in it. Download into it anyway?";
            if (!AnsiConsole.Confirm(Markup.Escape(question), false)) return;
        }

        // --- starting from the install ----------------------------------------------------

        var plan = new List<SeedFile>();
        if (game.Installed is { } app && Directory.Exists(app.InstallDir))
        {
            var recorded = DepotConfig.Read(destination);
            foreach (var (depot, manifest) in depots)
            {
                ulong? have = game.InstalledManifests is { } installed && installed.TryGetValue(depot, out var h) ? h : null;
                var chosenFiles = library.Files(depot, manifest) ?? ManifestLists.Find(depot, manifest, destination);
                // DepotDownloader does not check the files of a depot it has recorded as finished, so nothing unchecked goes into one.
                if (chosenFiles is null && recorded?.GetValueOrDefault(depot) is { } done && done != 0 && done != DepotConfig.InProgress) continue;
                var installedFiles = have is null ? null : have == manifest ? chosenFiles : library.Files(depot, have.Value);
                plan.AddRange(Seeder.Plan(app.InstallDir, destination, depot, chosenFiles, installedFiles, have == manifest));
            }
        }

        var seedBytes = plan.Sum(p => p.Bytes);
        var seed = seedBytes > 0 && AnsiConsole.Confirm(
            $"Start from your installed copy? {Format.Size((ulong)seedBytes)} of files this build shares with it are copied first, and only what differs is downloaded.", true);

        var need = Math.Max((long)total, seed ? seedBytes : 0);
        if (!resuming && need > 0 && FreeSpace(destination) is { } free && free < (ulong)need
            && !AnsiConsole.Confirm($"That drive has {Format.Size(free)} free and this build needs {Format.Size((ulong)need)}. Continue anyway?", false)) return;

        var exe = await EnsureDepotDownloaderAsync();
        if (exe is null || !ChooseLogin(out var account)) return;

        // --- record the job before touching anything ------------------------------------

        Directory.CreateDirectory(destination);
        _settings.LastDestinationRoot = Path.GetDirectoryName(destination);
        AppState.SaveSettings(_settings);

        record ??= new DownloadRecord();
        if (part is null)
        {
            part = new DownloadPart { AppId = game.AppId, Game = game.Name };
            record.Downloads.Add(part);
        }
        if (!resuming)
        {
            part.Build = label;
            part.Complete = false;
            part.Started = DateTimeOffset.Now;
            part.Finished = null;
            part.Skipped.Clear();
            part.Personalized.Clear();
            part.SetManifests(manifests);
        }
        if (seed)
        {
            foreach (var group in plan.GroupBy(p => p.Depot))
            {
                var key = group.Key.ToString(CultureInfo.InvariantCulture);
                if (!part.CopiedFromInstall.TryGetValue(key, out var names)) part.CopiedFromInstall[key] = names = new List<string>();
                var known = new HashSet<string>(names, PathRules.Comparer);
                names.AddRange(group.Select(p => p.Name).Where(known.Add));
            }
        }
        AppState.SaveRecord(destination, record);

        if (seed)
        {
            await AnsiConsole.Progress().StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Copying from your install", maxValue: Math.Max(1, seedBytes));
                await Task.Run(() => Seeder.Copy(plan, (done, all) => task.Value = done, CancellationToken.None));
                task.Value = task.MaxValue;
            });
            DrainInput();
        }

        // --- download, one DepotDownloader run per owning app, until it finishes or the user stops ---

        var logPath = LogPath(game);
        var groups = Groups(depots, owners, game.AppId);
        Dictionary<uint, ulong> finished;
        List<uint> skippedDlc;

        while (true)
        {
            var run = await RunGroupsAsync(exe, groups, depots.Count, destination, logPath, account);
            account = run.Account;

            if (run.Cancelled)
            {
                AppState.SaveRecord(destination, record);
                AnsiConsole.MarkupLine("[yellow]Stopped. Choose the same version and folder again to carry on from where it stopped.[/]");
                Pause();
                return;
            }

            var config = DepotConfig.Read(destination);
            finished = config ?? (run.Failed
                ? new Dictionary<uint, ulong>()
                : depots.Where(d => !run.Skipped.Contains(d.Depot)).ToDictionary(d => d.Depot, d => d.Manifest));
            var missing = depots.Where(d => !finished.TryGetValue(d.Depot, out var m) || m != d.Manifest).Select(d => d.Depot).ToList();
            skippedDlc = missing.Where(d => run.Skipped.Contains(d) && library.IsDlc(game, d)).ToList();
            var notDownloaded = missing.Except(skippedDlc).ToList();

            if (!run.Failed && notDownloaded.Count == 0) break;

            AppState.SaveRecord(destination, record);
            AnsiConsole.MarkupLine(notDownloaded.Count > 0
                ? $"[red]DepotDownloader did not finish depots {string.Join(", ", notDownloaded)}.[/]"
                : "[red]DepotDownloader did not finish the download.[/]");
            ShowFailure(run, notDownloaded, logPath);

            if (!AnsiConsole.Confirm("Try again? The download carries on from where it stopped.", true)) return;
        }

        part.Complete = true;
        part.Finished = DateTimeOffset.Now;
        part.Skipped = skippedDlc.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();

        var cleanup = Seeder.RemoveExtras(destination, record, part, finished);
        AppState.SaveRecord(destination, record);

        if (cleanup.UnreadableDepot is { } unreadableDepot)
            AnsiConsole.MarkupLine($"[yellow]DepotDownloader's manifest for depot {unreadableDepot} could not be read, so the files copied from your install were left in place.[/]");
        else if (cleanup.Removed > 0)
            AnsiConsole.MarkupLine($"[grey]Removed {cleanup.Removed} copied files this build does not have.[/]");
        if (skippedDlc.Count > 0)
            AnsiConsole.MarkupLine($"[yellow]Skipped DLC depots this account does not own: {string.Join(", ", skippedDlc)}.[/]");

        if (game.Installed is { } installedGame && game.InstalledManifests is { } installedManifests && Directory.Exists(installedGame.InstallDir))
        {
            var built = depots.Where(d => finished.TryGetValue(d.Depot, out var m) && m == d.Manifest).ToDictionary(d => d.Depot, d => d.Manifest);
            var copies = PersonalizedInInstall(library, installedGame.InstallDir, built, installedManifests, destination);
            if (copies.Count > 0)
            {
                OfferPersonalized(destination, part, copies, installedGame.InstallDir);
                AppState.SaveRecord(destination, record);
            }
        }

        AnsiConsole.MarkupLine($"[green]Done.[/] {Markup.Escape(destination)} holds {Markup.Escape(game.Name)}: {Markup.Escape(label)}.");
        AnsiConsole.MarkupLine("[grey]Steam does not manage this folder, so it never updates it.[/]");
        if (game.Installed is not null)
            AnsiConsole.MarkupLine("[grey]To put this build into the installed game instead, choose Apply a patch or a downloaded build in the game's menu.[/]");
        Pause();
    }

    /// <summary>
    /// Offers a finished download the copies of its exes Steam personalized for the account in the installed game, and puts in the
    /// one chosen. The folder's record keeps which files are personalized copies.
    /// </summary>
    static void OfferPersonalized(string destination, DownloadPart part, IReadOnlyList<PersonalizedCopy> copies, string installDir)
    {
        var inFolder = new List<(PersonalizedCopy Copy, bool IsPersonalized)>();
        foreach (var copy in copies)
        {
            if (PatchApplier.PathIn(destination, copy.Name) is not { } path || !File.Exists(path)) continue;
            var sha = FileHash.Sha1(path);
            if (sha == copy.Sha || sha == copy.InstalledSha) inFolder.Add((copy, sha == copy.InstalledSha));
        }
        if (inFolder.Count == 0) return;

        var usePersonalized = ChoosePersonalized(inFolder.Select(f => f.Copy).ToList(), inGame: false);
        try
        {
            if (usePersonalized)
            {
                foreach (var (copy, isPersonalized) in inFolder)
                {
                    if (!isPersonalized) File.Copy(PatchApplier.PathIn(installDir, copy.Name)!, PatchApplier.PathIn(destination, copy.Name)!, overwrite: true);
                    part.Personalized[copy.Name] = copy.InstalledSha;
                }
                return;
            }

            // DepotDownloader never checks a file of a finished depot again, but it downloads one that is missing.
            var replaced = inFolder.Where(f => f.IsPersonalized).Select(f => f.Copy.Name).ToList();
            foreach (var name in replaced)
            {
                File.Delete(PatchApplier.PathIn(destination, name)!);
                part.Personalized.Remove(name);
            }
            if (replaced.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]Choose the same version and folder again, and DepotDownloader downloads Steam's original of {Markup.Escape(string.Join(", ", replaced))}.[/]");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
        }
    }

    static void ShowFailure(RunOutcome run, IReadOnlyCollection<uint> notDownloaded, string logPath)
    {
        var cannot = notDownloaded.Where(run.Skipped.Contains).ToList();
        if (cannot.Count > 0) AnsiConsole.MarkupLine($"[red]The signed-in account cannot download depots {string.Join(", ", cannot)}.[/]");
        foreach (var message in run.Messages.Distinct().TakeLast(8)) AnsiConsole.MarkupLine($"[red]  {Markup.Escape(message)}[/]");
        AnsiConsole.MarkupLine($"[grey]Full output: {Markup.Escape(logPath)}[/]");
    }

    static string LogPath(GameEntry game) =>
        Path.Combine(AppState.LogsFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{game.AppId}.log");

    /// <summary>Asks where a download goes, suggesting a folder called <paramref name="name"/>. Null when the answer cannot be used.</summary>
    string? AskFolder(GameLibrary library, GameEntry game, string name)
    {
        var libraryRoot = game.Installed?.Library ?? library.Steam.Libraries.FirstOrDefault() ?? library.Steam.Root;
        var root = _settings.LastDestinationRoot is { Length: > 0 } saved && Directory.Exists(saved) ? saved : Path.Combine(libraryRoot, "COD Downgrader");
        var suggested = Path.Combine(root, Safe(name));

        AnsiConsole.MarkupLine($"Download into [grey](Enter for {Markup.Escape(suggested)})[/]");
        var answer = AnsiConsole.Prompt(new TextPrompt<string>("Folder:").AllowEmpty());
        string destination;
        try
        {
            destination = PathRules.Normalize(string.IsNullOrWhiteSpace(answer) ? suggested : answer.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
            Pause();
            return null;
        }

        if (Path.GetPathRoot(destination) is { Length: > 0 } driveRoot && PathRules.Same(driveRoot, destination))
        {
            AnsiConsole.MarkupLine("[red]Pick a folder, not the root of a drive.[/]");
            Pause();
            return null;
        }
        if (library.Steam.Libraries.Any(l => PathRules.IsInside(destination, Path.Combine(l, "steamapps"))))
        {
            AnsiConsole.MarkupLine("[red]Pick a folder outside steamapps. Steam manages everything in there.[/]");
            Pause();
            return null;
        }
        return destination;
    }

    /// <summary>How DepotDownloader signs in: the account name, or null for a QR code. False when the user cancels.</summary>
    bool ChooseLogin(out string? account)
    {
        var logins = new List<Item>();
        if (_settings.SteamAccount is { } savedAccount) logins.Add(new Item($"Sign in as {Markup.Escape(savedAccount)} again", "saved", savedAccount));
        logins.Add(new Item("QR code: approve it in the Steam mobile app", "qr"));
        logins.Add(new Item("Account name and password, typed into DepotDownloader's own prompt", "account"));
        logins.Add(new Item("Cancel", "cancel"));
        var login = Prompt("How should DepotDownloader sign in to Steam?", logins);
        account = login.Kind switch
        {
            "saved" => (string)login.Value!,
            "account" => AnsiConsole.Prompt(new TextPrompt<string>("Steam account name:")).Trim(),
            _ => null,
        };
        return login.Kind != "cancel";
    }

    /// <summary>Depots by the app that owns them, the game's own app first: DepotDownloader runs once for each.</summary>
    static List<IGrouping<uint, (uint Depot, ulong Manifest)>> Groups(IEnumerable<(uint Depot, ulong Manifest)> depots, IReadOnlyDictionary<uint, uint> owners, uint appId) =>
        depots.GroupBy(d => owners.TryGetValue(d.Depot, out var owner) ? owner : appId)
            .OrderBy(g => g.Key == appId ? 0 : 1)
            .ThenBy(g => g.Key)
            .ToList();

    /// <summary>Runs DepotDownloader for each owning app in turn, stopping at the first run that fails or is stopped.</summary>
    async Task<RunOutcome> RunGroupsAsync(string exe, IReadOnlyList<IGrouping<uint, (uint Depot, ulong Manifest)>> groups,
        int depotCount, string destination, string logPath, string? account, string? fileList = null, bool manifestOnly = false)
    {
        var skipped = new HashSet<uint>();

        foreach (var group in groups)
        {
            for (var attempt = 1; ; attempt++)
            {
                var what = manifestOnly ? "file lists" : "depots";
                var title = groups.Count > 1
                    ? $"DepotDownloader · app {group.Key}, {group.Count()} of {depotCount} {what}"
                    : manifestOnly ? "DepotDownloader · file lists" : "DepotDownloader";
                AnsiConsole.Write(new Rule($"[grey]{Markup.Escape(title)}[/]").LeftJustified());
                if (account is null) AnsiConsole.MarkupLine("[grey]Scan the QR code with the Steam mobile app as soon as it appears.[/]");

                var arguments = DepotDownloaderTool.Arguments(group.Key, group.ToList(), destination, account, fileList, manifestOnly);
                var result = await DepotDownloaderTool.RunAsync(exe, arguments, logPath);
                DrainInput();
                AnsiConsole.Write(new Rule().RuleStyle("grey"));

                // DepotDownloader has saved the login by the time it names the account, so the name is kept at
                // once: the runs after this one, a retry and the next download all sign in with it.
                if (result.AccountName is not null)
                {
                    account = result.AccountName;
                    if (_settings.SteamAccount != account)
                    {
                        _settings.SteamAccount = account;
                        AppState.SaveSettings(_settings);
                    }
                }
                skipped.UnionWith(result.Unavailable);

                if (result.Cancelled) return new RunOutcome(account, true, false, skipped, result.Messages);
                if (result.ExitCode == 0) break;

                if (result.LostSessionAfterQrLogin && attempt == 1)
                {
                    AnsiConsole.MarkupLine("[yellow]The QR code was approved, but DepotDownloader lost that sign-in when Steam reconnected. Trying again with the saved login.[/]");
                    continue;
                }

                return new RunOutcome(account, false, true, skipped, result.Messages);
            }
        }

        return new RunOutcome(account, false, false, skipped, Array.Empty<string>());
    }

    async Task<string?> EnsureDepotDownloaderAsync()
    {
        if (_options.DepotDownloaderPath is { } own)
        {
            if (File.Exists(own)) return own;
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(own)} does not exist.[/]");
            Pause();
            return null;
        }

        if (DepotDownloaderTool.FindInstalled(AppState.Folder) is { } ready) return ready;

        var release = DepotDownloaderTool.ReleaseForThisMachine();
        if (release is null)
        {
            AnsiConsole.MarkupLine("[red]There is no pinned DepotDownloader download for this system.[/] Get it from https://github.com/SteamRE/DepotDownloader/releases and pass its path with --depotdownloader.");
            Pause();
            return null;
        }

        AnsiConsole.MarkupLine($"Downloads go through [bold]DepotDownloader {DepotDownloaderTool.Version}[/] by SteamRE (GPL-2.0), fetched from its GitHub release and checked against a pinned SHA-256.");
        if (!AnsiConsole.Confirm($"Download it now? ({Format.Size((ulong)release.Size)})", true)) return null;

        try
        {
            string? path = null;
            await AnsiConsole.Progress().StartAsync(async ctx =>
            {
                var task = ctx.AddTask("DepotDownloader", maxValue: release.Size);
                path = await DepotDownloaderTool.InstallAsync(AppState.Folder, release, (done, all) =>
                {
                    task.MaxValue = all;
                    task.Value = done;
                }, CancellationToken.None);
            });
            DrainInput();
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
            Pause();
            return null;
        }
    }

    internal static ulong? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (ulong)drive.AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
