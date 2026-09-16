using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CODDowngrader.Builds;
using CODDowngrader.Steam;
using Spectre.Console;

namespace CODDowngrader.App;

public sealed class Options
{
    public string? SteamRoot { get; set; }
    public uint? AppId { get; set; }
    public string? DepotDownloaderPath { get; set; }
}

public sealed partial class Interactive
{
    sealed record Item(string Label, string Kind, object? Value = null);

    readonly Options _options;
    readonly Settings _settings = AppState.LoadSettings();

    public Interactive(Options options) => _options = options;

    public async Task<int> RunAsync()
    {
        AnsiConsole.Write(new Rule($"[bold]COD Downgrader[/] [grey]{Markup.Escape(AppState.Version)}[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Download any build of a Call of Duty you own on Steam, into your installed game or into a folder of its own.[/]");

        var steam = SteamInstall.Find(_options.SteamRoot);
        while (steam is null)
        {
            AnsiConsole.MarkupLine("[yellow]Steam was not found.[/]");
            var path = AnsiConsole.Prompt(new TextPrompt<string>("Steam folder (empty to quit):").AllowEmpty());
            if (string.IsNullOrWhiteSpace(path)) return 1;
            steam = SteamInstall.Find(path.Trim().Trim('"'));
        }

        var library = GameLibrary.Load(steam);
        var preselected = _options.AppId is { } id ? library.Entries.FirstOrDefault(e => e.AppId == id) : null;

        while (true)
        {
            var game = preselected;
            preselected = null;
            if (game is null)
            {
                var choice = PickGame(library);
                if (choice.Kind == "quit") return 0;
                game = choice.Kind == "others" ? PickOther(library) : (GameEntry)choice.Value!;
                if (game is null) continue;
            }
            await GameMenuAsync(library, game);
        }
    }

    Item PickGame(GameLibrary library)
    {
        var items = new List<Item>();
        foreach (var game in library.Entries.Where(e => e.Installed is not null))
        {
            var app = game.Installed!;
            var older = game.Downgradable ? library.History(game).Builds.Count(b => b.Kind == BuildKind.Previous) : 0;
            var notes = new List<string> { $"build {app.BuildId}" };
            if (older > 0) notes.Add(older == 1 ? "1 older build" : $"{older} older builds");
            if (app.UpdatePending) notes.Add("update queued");
            if (DowngradeOf(game) is { } applied) notes.Add($"downgraded in place: {applied.Title}");
            if (!game.Downgradable) notes.Add("online-only");
            items.Add(new Item($"{Markup.Escape(game.Name)} [grey]{Markup.Escape(string.Join(" · ", notes))}[/]", "game", game));
        }

        AnsiConsole.WriteLine();
        if (items.Count == 0) AnsiConsole.MarkupLine("[yellow]No Call of Duty is installed in these Steam libraries.[/]");
        items.Add(new Item("Other Call of Duty games (not installed)", "others"));
        items.Add(new Item("Quit", "quit"));
        return Prompt("Which game?", items);
    }

    GameEntry? PickOther(GameLibrary library)
    {
        var items = library.Entries
            .Where(e => e.Installed is null)
            .Select(e => new Item(Markup.Escape(e.Name) + (e.Downgradable ? "" : " [grey]online-only[/]"), "game", e))
            .Append(new Item("Back", "back"))
            .ToList();
        AnsiConsole.MarkupLine("[grey]Steam only sends a game's files to an account that owns it.[/]");
        var choice = Prompt("Which game?", items);
        return choice.Kind == "game" ? (GameEntry)choice.Value! : null;
    }

    async Task GameMenuAsync(GameLibrary library, GameEntry game)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(game.Name)}[/]").LeftJustified());

        if (!game.Downgradable)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(game.Title!.NotDowngradable!)}[/]");
            Pause();
            return;
        }

        while (true)
        {
            var history = library.History(game);
            var applied = DowngradeOf(game);
            if (game.Installed is { } app)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(app.InstallDir)}[/]");
                if (app.UpdatePending)
                    AnsiConsole.MarkupLine($"[yellow]Steam has build {app.TargetBuildId} queued for this game. The files on disk are still build {app.BuildId}.[/]");
                if (applied is not null) AnsiConsole.MarkupLine(DowngradeStatus(library, applied));
            }
            if (game.Owners.Count == 0)
                AnsiConsole.MarkupLine("[yellow]Neither Steam on this PC nor the built-in list has a depot list for this game, so every depot and its manifest ID has to come from SteamDB.[/]");
            else if (!history.Builds.Any(b => b.Kind == BuildKind.Previous))
                AnsiConsole.MarkupLine("[grey]No older build of this game is known. Manifest IDs from SteamDB will get any other.[/]");

            var titles = Format.BuildTitles(game, history.Builds);
            var items = history.Builds
                .Select((b, i) => new Item(Markup.Escape(titles[i]) + $"[grey]{Markup.Escape(Format.BuildDetail(library, game, b))}[/]", "build", (b, titles[i])))
                .Append(new Item("Enter manifest IDs from SteamDB", "custom"))
                .Concat(DowngradeItems(library, game, applied))
                .Append(new Item("Back", "back"))
                .ToList();

            var choice = Prompt("Which version?", items);
            if (choice.Kind == "back") return;

            try
            {
                var owners = new Dictionary<uint, uint>(game.Owners);
                switch (choice.Kind)
                {
                    case "build":
                    {
                        var (build, title) = ((Build, string))choice.Value!;
                        var tag = FolderTag(game, build, withTime: title != Format.BuildTitle(game, build));
                        var manifests = new Dictionary<uint, ulong>(build.Manifests);
                        if (build.Unknown.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[yellow]Neither this PC nor the built-in list names this build's manifest for depots {string.Join(", ", build.Unknown)}. Add them from SteamDB.[/]");
                            foreach (var depot in build.Unknown) manifests[depot] = 0;
                            var filled = CustomManifests(library, game, history, manifests, owners, build.Kind == BuildKind.Installed ? "as installed" : "this build");
                            if (filled is null) break;
                            manifests = filled;
                        }
                        await ChooseHowAsync(library, game, history, build, manifests, owners, title, tag);
                        break;
                    }
                    case "apply":
                        await ApplyFolderAsync(library, game);
                        break;
                    case "again":
                        await DowngradeAgainAsync(library, game, applied!);
                        break;
                    case "undo":
                        Undo(library, applied!);
                        break;
                    default:
                    {
                        var start = history.Builds.FirstOrDefault(b => b.Kind == BuildKind.Installed) ?? history.Builds.FirstOrDefault();
                        var manifests = new Dictionary<uint, ulong>(start?.Manifests ?? new Dictionary<uint, ulong>());
                        foreach (var depot in game.Owners.Keys) manifests.TryAdd(depot, 0);
                        var origin = game.Installed is not null ? "as installed" : "latest";
                        var custom = CustomManifests(library, game, history, manifests, owners, origin);
                        if (custom is not null)
                            await ChooseHowAsync(library, game, history, null, custom, owners, "Manifests entered by hand", $"custom {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}");
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                          or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
                Pause();
            }
        }
    }

    // --- choosing manifests by hand -------------------------------------------------------

    /// <summary>
    /// Lets the user fill in or change manifests, starting from <paramref name="start"/>, where 0 marks a
    /// depot that still needs one. Depots added along the way get their owning app in <paramref name="owners"/>.
    /// </summary>
    Dictionary<uint, ulong>? CustomManifests(GameLibrary library, GameEntry game, BuildHistoryResult history,
        Dictionary<uint, ulong> start, Dictionary<uint, uint> owners, string baseOrigin)
    {
        var chosen = start;
        var origin = chosen.ToDictionary(kv => kv.Key, kv => kv.Value == 0 ? "needed" : baseOrigin);

        AnsiConsole.MarkupLine("[grey]On SteamDB, open a depot's Manifests list, find the date you want and copy its manifest ID. Only depots that changed need one; the rest stay as they are.[/]");

        while (true)
        {
            ShowChosen(library, game, chosen, origin, baseOrigin);

            var items = new List<Item>
            {
                new("Paste manifest IDs", "paste"),
                new("Open a depot's manifest list on SteamDB", "steamdb"),
            };
            if (history.Depots.Any(d => d.Value.Count > 1)) items.Add(new Item("Pick a known manifest", "local"));
            items.Add(new Item("Download this set", "done"));
            items.Add(new Item("Cancel", "cancel"));

            switch (Prompt("Manifests", items).Kind)
            {
                case "paste":
                    Paste(library, game, chosen, origin, owners);
                    break;
                case "steamdb":
                    OpenSteamDb(library, game, chosen.Count > 0 ? chosen.Keys : game.Owners.Keys);
                    break;
                case "local":
                    PickLocal(library, game, history, chosen, origin);
                    break;
                case "done":
                    if (chosen.Count > 0 && chosen.Values.All(v => v != 0)) return chosen.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
                    AnsiConsole.MarkupLine("[red]Every depot needs a manifest ID first.[/]");
                    break;
                default:
                    return null;
            }
        }
    }

    static void ShowChosen(GameLibrary library, GameEntry game, Dictionary<uint, ulong> chosen, Dictionary<uint, string> origin, string baseOrigin)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Depot", "Contents", "Manifest", "From");
        var hidden = 0;
        foreach (var (depot, manifest) in chosen.OrderBy(kv => kv.Key))
        {
            if (chosen.Count > 12 && manifest != 0 && origin[depot] == baseOrigin)
            {
                hidden++;
                continue;
            }
            var cached = manifest != 0 && library.Cached(depot, manifest) is { } c ? $" (built {Format.Date(c.Created)})" : "";
            table.AddRow(depot.ToString(), Markup.Escape(library.DepotName(game, depot)),
                manifest == 0 ? "[red]needed[/]" : manifest.ToString(), Markup.Escape(origin[depot] + cached));
        }
        AnsiConsole.Write(table);
        if (hidden > 0) AnsiConsole.MarkupLine($"[grey]{hidden} more depots stay {Markup.Escape(baseOrigin)}.[/]");
    }

    [GeneratedRegex(@"download_depot\s+([0-9]{1,10})\s+([0-9]{1,10})\s+([0-9]{1,20})")]
    private static partial Regex DownloadDepotRegex();

    [GeneratedRegex(@"^\s*([0-9]{1,10})[^0-9]+([0-9]{1,20})\s*$")]
    private static partial Regex PairRegex();

    static void Paste(GameLibrary library, GameEntry game, Dictionary<uint, ulong> chosen, Dictionary<uint, string> origin, Dictionary<uint, uint> owners)
    {
        AnsiConsole.MarkupLine("[grey]One per line: a download_depot command, or a depot ID and a manifest ID. An empty line finishes.[/]");
        while (Console.ReadLine() is { } line && line.Trim().Length > 0)
        {
            uint? app = null;
            string depotText, manifestText;
            var command = DownloadDepotRegex().Match(line);
            if (command.Success)
            {
                app = uint.TryParse(command.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var a) ? a : null;
                depotText = command.Groups[2].Value;
                manifestText = command.Groups[3].Value;
            }
            else if (PairRegex().Match(line) is { Success: true } pair)
            {
                depotText = pair.Groups[1].Value;
                manifestText = pair.Groups[2].Value;
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Not a depot and manifest. Skipped.[/]");
                continue;
            }

            if (!uint.TryParse(depotText, NumberStyles.None, CultureInfo.InvariantCulture, out var depot) || depot == 0
                || !ulong.TryParse(manifestText, NumberStyles.None, CultureInfo.InvariantCulture, out var manifest) || manifest == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line.Trim())} is not a depot ID and a manifest ID. Skipped.[/]");
                continue;
            }

            if (OwnerForPastedDepot(library, game, owners, depot, app) is not { } owner)
            {
                AnsiConsole.MarkupLine(app is { } other && other != game.AppId && !owners.ContainsKey(depot)
                    ? $"[yellow]That command is for app {other}, and depot {depot} is not part of {Markup.Escape(game.Name)}. Skipped.[/]"
                    : $"[yellow]Depot {depot} is not part of {Markup.Escape(game.Name)} in Steam's product info. Skipped.[/]");
                continue;
            }

            if (!chosen.ContainsKey(depot))
            {
                AnsiConsole.MarkupLine(game.Info is null && library.DepotInfo(game, depot) is null
                    ? $"[grey]Depot {depot} added. Steam has no depot list for this game here, so DepotDownloader checks it.[/]"
                    : $"[grey]Depot {depot} added.[/]");
            }
            owners[depot] = owner;
            chosen[depot] = manifest;
            origin[depot] = "pasted";
        }
    }

    /// <summary>The app that owns a pasted depot, or null when the depot does not belong to this game.</summary>
    static uint? OwnerForPastedDepot(GameLibrary library, GameEntry game, IReadOnlyDictionary<uint, uint> owners, uint depot, uint? commandApp)
    {
        if (owners.TryGetValue(depot, out var known))
            return commandApp is null || commandApp == game.AppId || commandApp == known ? known : null;

        if (game.Info?.Depot(depot) is { } info)
        {
            if (info.IsRedistributable) return null;
            var owner = info.IsBorrowed ? info.DepotFromApp!.Value : game.AppId;
            return commandApp is null || commandApp == game.AppId || commandApp == owner ? owner : null;
        }

        // No product info to check against: accept it, and DepotDownloader refuses a depot the app does not have.
        return game.Info is null ? commandApp ?? game.AppId : null;
    }

    static void OpenSteamDb(GameLibrary library, GameEntry game, IEnumerable<uint> depots)
    {
        var items = depots.OrderBy(d => d)
            .Select(d => new Item($"{d} [grey]{Markup.Escape(library.DepotName(game, d))}[/]", "depot", d))
            .Append(new Item("Back", "back"))
            .ToList();
        if (items.Count == 1)
        {
            var app = $"https://steamdb.info/app/{game.AppId}/depots/";
            AnsiConsole.MarkupLine($"[grey]The depots are listed on SteamDB: [link]{app}[/][/]");
            OpenUrl(app);
            return;
        }

        var choice = Prompt("Which depot?", items);
        if (choice.Kind != "depot") return;
        OpenUrl($"https://steamdb.info/depot/{choice.Value}/manifests/");
    }

    static void OpenUrl(string url)
    {
        AnsiConsole.MarkupLine($"[link]{url}[/]");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AnsiConsole.MarkupLine("[grey]Open that link in a browser.[/]");
        }
    }

    static void PickLocal(GameLibrary library, GameEntry game, BuildHistoryResult history, Dictionary<uint, ulong> chosen, Dictionary<uint, string> origin)
    {
        var depots = history.Depots.Where(d => d.Value.Count > 1).Select(d => d.Key).OrderBy(d => d).ToList();
        var depotChoice = Prompt("Which depot?", depots
            .Select(d => new Item($"{d} [grey]{Markup.Escape(library.DepotName(game, d))} · {history.Depots[d].Count} manifests[/]", "depot", d))
            .Append(new Item("Back", "back"))
            .ToList());
        if (depotChoice.Kind != "depot") return;

        var depot = (uint)depotChoice.Value!;
        var pick = Prompt($"Depot {depot}", history.Depots[depot].Reverse()
            .Select(k => new Item(Markup.Escape(ManifestLabel(k)), "manifest", k))
            .Append(new Item("Back", "back"))
            .ToList());
        if (pick.Kind != "manifest") return;

        var known = (KnownManifest)pick.Value!;
        chosen[depot] = known.ManifestId;
        origin[depot] = known is { FirstSeen: null, Created: null, Installed: false, Latest: false } ? "built-in list" : "this PC";
    }

    static string ManifestLabel(KnownManifest k)
    {
        var parts = new List<string> { k.ManifestId.ToString() };
        if (k.Listed is { } listed) parts.Add($"first seen on SteamDB {Format.Date(listed)}");
        if (k.Created is { } built) parts.Add($"built {Format.Date(built)}");
        if (k.FirstSeen is { } seen) parts.Add($"fetched {Format.DateTime(seen)}");
        if (k.Installed) parts.Add("installed");
        if (k.Latest) parts.Add("latest");
        return string.Join(" · ", parts);
    }

    // --- helpers --------------------------------------------------------------------------

    static Item Prompt(string title, List<Item> items) =>
        AnsiConsole.Prompt(new SelectionPrompt<Item>().Title(title).PageSize(15).UseConverter(i => i.Label).AddChoices(items));

    static void Pause()
    {
        AnsiConsole.MarkupLine("[grey]Press Enter to continue.[/]");
        Console.ReadLine();
    }

    /// <summary>Throws away keys pressed while something long ran, so they cannot answer the prompts that follow.</summary>
    static void DrainInput()
    {
        try
        {
            if (Console.IsInputRedirected) return;
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    static string FolderTag(GameEntry game, Build build, bool withTime) => build.Kind switch
    {
        BuildKind.Installed => game.Installed is { BuildId: > 0 } app ? $"build {app.BuildId}" : "installed",
        BuildKind.Latest => game.Info?.PublicBuildId is { } id ? $"build {id}" : "latest",
        _ => build.ReplacedBy?.Time is { } time
            ? $"before {time.ToLocalTime().ToString(withTime ? "yyyy-MM-dd HHmm" : "yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : "earlier build",
    };

    internal static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        return new string(name.Replace(": ", " - ").Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
    }
}
