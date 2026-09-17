using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Cli;
using CODDowngrader.Jobs;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>One file the plan writes, as the checklist offers it.</summary>
public sealed class FileChoice : Observable
{
    readonly Action _changed;
    bool _checked = true;

    public FileChoice(string name, ulong size, bool alreadyThere, Action changed)
    {
        Name = name;
        Size = size;
        AlreadyThere = alreadyThere;
        _changed = changed;
    }

    public string Name { get; }
    public ulong Size { get; }
    public bool AlreadyThere { get; }
    public string SizeText => AlreadyThere ? "already this build's" : Format.Size(Size);

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (Set(ref _checked, value)) _changed();
        }
    }
}

/// <summary>A depot whose manifest the job changes: what the game has now, and what it will have.</summary>
public sealed record DepotChange(string Name, string Depot, string Now, string After);

/// <summary>The files of one top folder, which can be picked all at once.</summary>
public sealed class FolderChoice : Observable
{
    bool _isExpanded;

    public FolderChoice(string name, IReadOnlyList<FileChoice> files)
    {
        Name = name.Length == 0 ? "The game folder" : name + "/";
        Files = files;
        Summary = $"{files.Count} file{(files.Count == 1 ? "" : "s")}, {Format.Size(files.Aggregate(0UL, (sum, f) => sum + f.Size))}";
    }

    public string Name { get; }
    public string Summary { get; }
    public IReadOnlyList<FileChoice> Files { get; }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool? IsChecked
    {
        get => Files.All(f => f.IsChecked) ? true : Files.Any(f => f.IsChecked) ? null : false;
        set
        {
            var to = value != false;
            foreach (var file in Files) file.IsChecked = to;
            Raise();
        }
    }

    public void Changed() => Raise(nameof(IsChecked));
}

/// <summary>
/// What to do with a build before it runs: the job works out what would change (its --plan), and the page offers the choices
/// that exist for it. Nothing changes until Start.
/// </summary>
public sealed class ActionPageViewModel : Observable, IHasBack, IJobView
{
    readonly MainViewModel _main;
    readonly GamePageViewModel _game;
    readonly BuildItem? _build;
    CancellationTokenSource? _planning;

    bool _isPlanning;
    string? _stage;
    string? _error;
    string? _errorCode;
    bool _planned;
    string _summary = "";
    string? _warning;
    int _part;
    bool _keepPersonalized = true;
    bool _copyPersonalized;
    bool _keepBackup = true;
    bool _takeSiblings;
    bool _startFromInstall = true;
    bool _deleteAfter;
    string _folder = "";
    BuildItem? _patchFrom;
    JsonObject? _plan;
    LanguageChoice? _language;

    /// <summary>The folder the job suggested, which follows the language while it is left as it is.</summary>
    string? _suggestedFolder;

    /// <summary>Manifests the SteamDB helper filled in for depots the build needed, as "depot=manifest".</summary>
    readonly List<string> _foundManifests = new();

    /// <summary>What the last plan said it needed from SteamDB, and the moment to take each manifest at.</summary>
    IReadOnlyList<NeededManifest> _needs = Array.Empty<NeededManifest>();
    DateTimeOffset? _neededBefore;

    /// <summary>The files a shared build chose, ticked when the plan arrives.</summary>
    readonly HashSet<string>? _chosenFiles;

    /// <param name="folder">apply: the folder to take, when it was chosen before this page opened.</param>
    public ActionPageViewModel(MainViewModel main, GamePageViewModel game, string kind, BuildItem? build, string? folder = null)
    {
        _main = main;
        _game = game;
        _build = build;
        Kind = kind;
        _startFromInstall = game.IsInstalled;

        Title = kind switch
        {
            "ingame" => "Put this build into the game",
            "download" => "Download this build into a folder of its own",
            "patch" => "Save a patch folder",
            "undo" => "Undo the downgrade",
            _ => "Apply a patch or a downloaded build",
        };
        Subtitle = kind == "undo" && game.Applied is { } applied
            ? $"{applied.Title} · {game.Name}"
            : build is null ? game.Name : $"{build.Title} · {game.Name}";
        Again = kind is "ingame" or "apply" && (build?.Settings.Again == true || game.HasApplied);

        // A shared build comes with the part of it and the games sharing the folder as its sender chose them.
        if (build?.Settings is { } given && kind != "download")
        {
            _takeSiblings = given.Siblings;
            if (given.Files is { Length: > 0 } files)
            {
                _part = 3;
                _chosenFiles = new HashSet<string>(files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ManifestFile.NormalizeName), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _part = given.Only switch { "content" => 1, "binaries" => 2, _ => 0 };
            }
        }

        // A download can be in any language Steam has for the game, and starts in the one Steam would download.
        if (kind == "download" && main.Library is { } library && library.Languages(game.Game) is { Count: > 1 } languages)
        {
            var steamDefault = library.DefaultLanguage(game.Game);
            foreach (var code in languages) Languages.Add(Flags.Choice(code, steamDefault));
            var wanted = build?.Settings.Language ?? steamDefault;
            _language = Languages.FirstOrDefault(l => l.Code == wanted) ?? Languages.FirstOrDefault(l => l.IsDefault) ?? Languages[0];
        }

        if (kind == "patch")
        {
            PatchStarts = game.PatchStarts;
            _patchFrom = PatchStarts.FirstOrDefault(b => b.Kind == Builds.BuildKind.Latest) ?? PatchStarts.FirstOrDefault(b => b.IsInstalled) ?? PatchStarts.FirstOrDefault();
        }

        BackCommand = new Command(() =>
        {
            _planning?.Cancel();
            _main.Back();
        });
        BrowseCommand = new Command(BrowseAsync);
        StartCommand = new Command(Start, () => CanStart);
        RetryCommand = new Command(PlanAsync, () => !_isPlanning);
        FindManifestsCommand = new Command(FindManifests, () => _errorCode == "needs-manifests");

        if (kind == "apply" && folder is { Length: > 0 }) _folder = folder;
        if (kind != "apply" || _folder.Length > 0) _ = PlanAsync();
    }

    public string Kind { get; }

    public string StartLabel => Kind switch
    {
        "download" => "Download",
        "patch" => "Save the patch folder",
        "undo" => "Undo it",
        _ => "Put it into the game",
    };
    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>The biggest files the job writes, as a short list under the summary.</summary>
    public ObservableCollection<string> TopFiles { get; } = new();

    public bool HasTopFiles => TopFiles.Count > 0;

    /// <summary>Said when a file the job writes is an exe Steam personalizes: what is downloaded is Steam's original.</summary>
    public string? PersonalizedWrites { get; private set; }

    public bool HasPersonalizedWrites => PersonalizedWrites is not null;
    public bool IsInGame => Kind == "ingame";
    public bool IsDownload => Kind == "download";
    public bool IsPatch => Kind == "patch";
    public bool IsApply => Kind == "apply";
    public bool IsUndo => Kind == "undo";

    /// <summary>What the game holds now and what it will hold after, with each depot whose manifest changes.</summary>
    public string? ChangeNow { get; private set; }
    public string? ChangeAfter { get; private set; }
    public string? ChangeUnchanged { get; private set; }
    public ObservableCollection<DepotChange> ChangeDepots { get; } = new();
    public bool HasChange => ChangeNow is not null;
    public bool HasChangeUnchanged => ChangeUnchanged is not null;
    public bool HasFolder => Kind is "download" or "patch" or "apply";
    public bool ChoosesDestination => Kind is "download" or "patch";
    public bool WritesIntoGame => Kind is "ingame" or "apply";

    /// <summary>Applying works nothing out until there is a folder to look in.</summary>
    public bool ApplyWaitingForFolder => IsApply && _folder.Length == 0;
    public bool Again { get; }
    public string? AgainText => Again && _game.Applied is { } applied ? $"{applied.Title} comes out of the game first." : null;

    public ObservableCollection<FolderChoice> Folders { get; } = new();

    public ObservableCollection<LanguageChoice> Languages { get; } = new();
    public bool HasLanguages => Languages.Count > 1;

    /// <summary>The language the download is in. Choosing another works out the download again, and forgets manifests found for the last one.</summary>
    public LanguageChoice? Language
    {
        get => _language;
        set
        {
            if (value is null || !Set(ref _language, value)) return;
            _foundManifests.Clear();
            _ = PlanAsync();
        }
    }
    public IReadOnlyList<BuildItem> PatchStarts { get; } = Array.Empty<BuildItem>();
    public List<string> Siblings { get; } = new();

    public Command BackCommand { get; }
    public Command BrowseCommand { get; }
    public Command StartCommand { get; }
    public Command RetryCommand { get; }
    public Command FindManifestsCommand { get; }

    public bool IsPlanning
    {
        get => _isPlanning;
        private set
        {
            Set(ref _isPlanning, value);
            StartCommand.Changed();
            RetryCommand.Changed();
        }
    }

    public string? Stage
    {
        get => _stage;
        private set => Set(ref _stage, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            Set(ref _error, value);
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;
    public bool NeedsManifests => _errorCode == "needs-manifests";

    public bool Planned
    {
        get => _planned;
        private set
        {
            Set(ref _planned, value);
            StartCommand.Changed();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? Warning
    {
        get => _warning;
        private set
        {
            Set(ref _warning, value);
            Raise(nameof(HasWarning));
        }
    }

    public bool HasWarning => _warning is not null;

    /// <summary>0 everything, 1 content only, 2 exes and DLLs only, 3 the files ticked below.</summary>
    public int Part
    {
        get => _part;
        set
        {
            if (!Set(ref _part, value)) return;
            Raise(nameof(ChoosingFiles));
            Recount();
        }
    }

    public bool PartEverything { get => _part == 0; set { if (value) Part = 0; } }
    public bool PartContent { get => _part == 1; set { if (value) Part = 1; } }
    public bool PartBinaries { get => _part == 2; set { if (value) Part = 2; } }
    public bool PartFiles { get => _part == 3; set { if (value) Part = 3; } }
    public bool ChoosingFiles => _part == 3;

    /// <summary>Whether the plan can be cut down: more than one file, or a part already chosen, and the choice is not a whole build.</summary>
    public bool CanChoosePart => !IsDownload && !IsUndo && (Folders.Sum(f => f.Files.Count) > 1 || _part != 0);

    /// <summary>Content only and exes and DLLs only are choices only when the plan has some of each.</summary>
    public bool CanChooseByKind
    {
        get
        {
            var files = Folders.SelectMany(f => f.Files).ToList();
            return files.Any(f => PatchPlan.IsBinary(f.Name)) && files.Any(f => !PatchPlan.IsBinary(f.Name));
        }
    }

    public List<string> PersonalizedCopies { get; } = new();
    public bool HasPersonalizedCopies => PersonalizedCopies.Count > 0 && !IsPatch;
    public string PersonalizedText => $"Steam personalized {string.Join(", ", PersonalizedCopies)} for your account when it installed the game. This build has the same exe, so it can keep that copy or take Steam's original.";

    public bool KeepPersonalized { get => _keepPersonalized; set { if (Set(ref _keepPersonalized, value)) Raise(nameof(UseSteamOriginal)); } }
    public bool UseSteamOriginal { get => !_keepPersonalized; set => KeepPersonalized = !value; }

    public bool CopyPersonalized { get => _copyPersonalized; set => Set(ref _copyPersonalized, value); }

    public bool KeepBackup { get => _keepBackup; set => Set(ref _keepBackup, value); }

    public bool HasSiblings => Siblings.Count > 0;
    public string SiblingsText => $"Also take {string.Join(" and ", Siblings)} back to its build from the same time. They share this folder.";
    public bool TakeSiblings { get => _takeSiblings; set => Set(ref _takeSiblings, value); }

    public bool CanStartFromInstall => IsDownload && _game.IsInstalled;
    public bool StartFromInstall { get => _startFromInstall; set => Set(ref _startFromInstall, value); }

    public bool DeleteAfter { get => _deleteAfter; set => Set(ref _deleteAfter, value); }

    public string Folder
    {
        get => _folder;
        set
        {
            if (!Set(ref _folder, value)) return;
            Raise(nameof(ApplyWaitingForFolder));
            StartCommand.Changed();
        }
    }

    public BuildItem? PatchFrom
    {
        get => _patchFrom;
        set
        {
            if (Set(ref _patchFrom, value) && value is not null) _ = PlanAsync();
        }
    }

    bool CanStart => Planned && !IsPlanning && (!HasFolder || Folder.Length > 0) && (Part != 3 || Folders.Any(f => f.IsChecked != false));

    /// <summary>The job's settings, as chosen so far.</summary>
    JobSettings Settings(bool plan)
    {
        var baseline = _build?.Settings ?? new JobSettings();
        var manifests = baseline.Manifests.Concat(_foundManifests).ToList();
        var files = Part == 3
            ? string.Join(",", Folders.SelectMany(f => f.Files).Where(f => f.IsChecked).Select(f => f.Name))
            : null;
        return baseline with
        {
            Plan = plan,
            Yes = plan,
            Only = Part switch { 1 => "content", 2 => "binaries", _ => null },
            Files = files is { Length: > 0 } ? files : null,
            Exe = IsDownload ? (CopyPersonalized ? "installed" : null) : KeepPersonalized ? null : "steam",
            Backup = KeepBackup ? null : "no",
            Siblings = TakeSiblings,
            NoSeed = IsDownload && !StartFromInstall,
            Delete = DeleteAfter,
            Again = Again,
            To = IsDownload || IsPatch ? (plan && Folder == _suggestedFolder ? null : NullIfEmpty(Folder)) : null,
            Language = IsDownload ? _language?.Code ?? baseline.Language : baseline.Language,
            Manifests = manifests,
            Label = _foundManifests.Count > 0 ? baseline.Label ?? _build?.Title : baseline.Label,
            From = IsPatch ? _patchFrom?.Key : IsApply ? NullIfEmpty(Folder) : null,
        };
    }

    static string? NullIfEmpty(string text) => text.Trim().Length == 0 ? null : text.Trim();

    async Task PlanAsync()
    {
        if (IsApply && Folder.Length == 0) return;
        _planning?.Cancel();
        var cancel = new CancellationTokenSource();
        _planning = cancel;

        IsPlanning = true;
        Planned = false;
        Error = null;
        _errorCode = null;
        Raise(nameof(NeedsManifests));
        Stage = "Working out what changes";
        try
        {
            if (_main.Steam is not { } steam) return;
            var job = new GuiJob(Kind, Settings(plan: true), _main.Options, this, cancel.Token);
            await Task.Run(() => Actions.RunAsync(job, steam, _game.Game, Kind), cancel.Token);
            if (cancel.IsCancellationRequested) return;
            _main.RefreshAccount();

            var result = job.Result;
            if (result["ok"]?.GetValue<bool>() != true)
            {
                _errorCode = result["error"]?["code"]?.GetValue<string>();
                Error = (result["error"]?["message"]?.GetValue<string>() ?? "It could not be worked out what would change.")
                    .Replace(" Add each one with --manifest depot=manifest.", "", StringComparison.Ordinal);
                _needs = (result["needs"]?.AsArray() ?? new JsonArray())
                    .Select(n => new NeededManifest(n!["depot"]!.GetValue<uint>(), n["app"]!.GetValue<uint>(), n["url"]!.GetValue<string>()))
                    .ToList();
                _neededBefore = result["neededBefore"]?.GetValue<string>() is { } before
                    && DateTimeOffset.TryParse(before, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var moment)
                    ? moment
                    : null;
                Raise(nameof(NeedsManifests));
                FindManifestsCommand.Changed();
                return;
            }
            Take(result);
            Planned = true;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_planning == cancel)
            {
                IsPlanning = false;
                Stage = null;
            }
        }
    }

    /// <summary>What the plan says, into what the page shows.</summary>
    void Take(JsonObject result)
    {
        _plan = result["plan"]?.AsObject();
        if (result["folder"]?.GetValue<string>() is { } folder && (Folder.Length == 0 || IsApply || Folder == _suggestedFolder))
        {
            Folder = folder;
            _suggestedFolder = folder;
        }

        Folders.Clear();
        PersonalizedCopies.Clear();
        Siblings.Clear();
        TakeChange(result["change"]?.AsObject());

        if (IsUndo)
        {
            TakeUndo();
        }
        else if (IsDownload)
        {
            var size = _plan?["size"] is JsonValue known ? known.GetValue<ulong>() : (ulong?)null;
            var depots = _plan?["depots"]?.AsArray().Count ?? 0;
            var copied = _plan?["copiedFromInstall"]?.GetValue<int>() ?? 0;
            Summary = $"The whole game at this build: {depots} depot{(depots == 1 ? "" : "s")}{(size is { } s ? $", {Format.Size(s)}" : "")}."
                      + (copied > 0 ? $" {copied} files can come from your installed copy." : "");
        }
        else
        {
            var files = _plan?["files"]?.AsArray().Select(f => f!.AsObject()).ToList() ?? new List<JsonObject>();
            foreach (var group in files.GroupBy(f => FolderOf(f["name"]!.GetValue<string>())).OrderBy(g => g.Key.Length == 0 ? "" : g.Key, StringComparer.OrdinalIgnoreCase))
            {
                FolderChoice? owner = null;
                var choices = group.Select(f => new FileChoice(f["name"]!.GetValue<string>(), f["size"]!.GetValue<ulong>(),
                    f["alreadyThere"]?.GetValue<bool>() == true, () => { owner?.Changed(); Recount(); })).ToList();
                owner = new FolderChoice(group.Key, choices);
                Folders.Add(owner);
            }
            // With everything in one folder there is nothing to pick between but the files, so they are shown straight away.
            if (Folders.Count == 1) Folders[0].IsExpanded = true;
            if (_chosenFiles is not null)
            {
                foreach (var file in Folders.SelectMany(f => f.Files)) file.IsChecked = _chosenFiles.Contains(file.Name);
            }
            else if (!CanChooseByKind && Part is 1 or 2)
            {
                // Content only or exes and DLLs only, in a plan of one kind: that is all of its files, or none of them.
                Func<string, bool> includes = Part == 1 ? name => !PatchPlan.IsBinary(name) : PatchPlan.IsBinary;
                var all = Folders.SelectMany(f => f.Files).ToList();
                if (all.All(f => includes(f.Name)))
                {
                    Part = 0;
                }
                else
                {
                    foreach (var file in all) file.IsChecked = includes(file.Name);
                    Part = 3;
                }
            }
            PersonalizedCopies.AddRange(_plan?["personalizedCopies"]?.AsArray().Select(c => c!["name"]!.GetValue<string>()) ?? Array.Empty<string>());
            foreach (var sibling in result["siblings"]?.AsArray() ?? new JsonArray())
                if (sibling?["canFollow"]?.GetValue<bool>() == true) Siblings.Add(sibling["name"]!.GetValue<string>());

            var personalizedWrites = files.Where(f => f["personalized"]?.GetValue<bool>() == true && f["alreadyThere"]?.GetValue<bool>() != true)
                .Select(f => f["name"]!.GetValue<string>()).ToList();
            PersonalizedWrites = personalizedWrites.Count == 0 || IsPatch
                ? null
                : $"Steam personalizes {string.Join(", ", personalizedWrites)} for each account when it installs the game, and only for the build it installs, so what is downloaded is Steam's original.";

            var modified = _plan?["modified"]?.AsArray().Count ?? 0;
            var locked = _plan?["locked"]?.AsArray().Select(n => n!.GetValue<string>()).FirstOrDefault();
            Warning = locked is not null
                ? $"{locked} is in use. Close the game before starting."
                : modified > 0
                    ? $"{modified} of these files are not the installed build's version, as when a mod or a client has replaced them. They are replaced as well."
                    : null;
            Recount();
        }

        foreach (var name in new[] { nameof(CanChoosePart), nameof(CanChooseByKind), nameof(HasPersonalizedCopies), nameof(PersonalizedText), nameof(HasSiblings), nameof(SiblingsText), nameof(PersonalizedWrites), nameof(HasPersonalizedWrites) })
            Raise(name);
    }

    void TakeChange(JsonObject? change)
    {
        ChangeDepots.Clear();
        ChangeNow = change?["now"]?.GetValue<string>();
        ChangeAfter = change?["after"]?.GetValue<string>();
        foreach (var depot in change?["depots"]?.AsArray() ?? new JsonArray())
        {
            var id = depot!["depot"]!.GetValue<uint>();
            ChangeDepots.Add(new DepotChange(depot["name"]?.GetValue<string>() ?? $"Depot {id}", $"Depot {id}",
                depot["now"]?.GetValue<string>() ?? "not installed", depot["after"]!.GetValue<string>()));
        }
        var unchanged = change?["unchanged"]?.GetValue<int>() ?? 0;
        ChangeUnchanged = change is null
            ? null
            : ChangeDepots.Count == 0
                ? "Every depot is already on these manifests."
                : unchanged > 0 ? $"{unchanged} other depot{(unchanged == 1 ? " stays" : "s stay")} as {(unchanged == 1 ? "it is" : "they are")}." : null;
        foreach (var name in new[] { nameof(ChangeNow), nameof(ChangeAfter), nameof(ChangeUnchanged), nameof(HasChange), nameof(HasChangeUnchanged) })
            Raise(name);
    }

    /// <summary>What undo says it will do: which files come from the backup, which from Steam, and what stays.</summary>
    void TakeUndo()
    {
        var fromBackup = _plan?["fromBackup"]?.AsArray() ?? new JsonArray();
        var fromSteam = _plan?["fromSteam"]?.AsArray() ?? new JsonArray();
        var delete = _plan?["delete"]?.AsArray() ?? new JsonArray();
        var steamChanged = _plan?["steamChanged"]?.AsArray().Count ?? 0;
        var lost = _plan?["lost"]?.AsArray() ?? new JsonArray();
        var download = _plan?["download"]?.GetValue<ulong>() ?? 0;

        string Files(int count) => count == 1 ? "1 file" : $"{count} files";
        var parts = new List<string>();
        if (fromBackup.Count > 0) parts.Add($"{Files(fromBackup.Count)} back from the backup");
        if (fromSteam.Count > 0) parts.Add($"{Files(fromSteam.Count)} downloaded from Steam, {Format.Size(download)}");
        if (delete.Count > 0) parts.Add($"{Files(delete.Count)} the downgrade added, deleted");
        Summary = parts.Count == 0 ? "Nothing to put back: the game's own files are all there." : string.Join("; ", parts) + ".";

        TopFiles.Clear();
        var listed = fromSteam.Select(f => $"{f!["name"]!.GetValue<string>()}  ·  from Steam, {Format.Size(f["size"]!.GetValue<ulong>())}")
            .Concat(fromBackup.Select(f => $"{f!["name"]!.GetValue<string>()}  ·  from the backup"))
            .Concat(delete.Select(n => $"{n!.GetValue<string>()}  ·  deleted"))
            .ToList();
        foreach (var line in listed.Take(6)) TopFiles.Add(line);
        if (listed.Count > 6) TopFiles.Add($"and {listed.Count - 6} more");
        Raise(nameof(HasTopFiles));

        var personalized = fromSteam.Where(f => f!["personalized"]?.GetValue<bool>() == true).Select(f => f!["name"]!.GetValue<string>()).ToList();
        PersonalizedWrites = personalized.Count == 0
            ? null
            : $"{string.Join(", ", personalized)} comes back as Steam's original: Verify integrity of game files in Steam gives back the copy it personalizes for your account.";
        Warning = lost.Count > 0
            ? $"{Files(lost.Count)} cannot come back from the backup or from Steam, {lost[0]!.GetValue<string>()} among them, and stay as the downgrade left them. Verify integrity of game files in Steam puts them right."
            : steamChanged > 0
                ? $"{Files(steamChanged)} Steam has put back since {(steamChanged == 1 ? "stays" : "stay")} as {(steamChanged == 1 ? "it is" : "they are")}."
                : null;
        StartCommand.Changed();
    }

    static string FolderOf(string name) => name.IndexOf('/') is var cut and > 0 ? name[..cut] : "";

    /// <summary>The summary for the part chosen, counted from the plan's files.</summary>
    void Recount()
    {
        if (IsDownload || _plan is null) return;
        var chosen = Folders.SelectMany(f => f.Files).Where(f => Part switch
        {
            1 => !PatchPlan.IsBinary(f.Name),
            2 => PatchPlan.IsBinary(f.Name),
            3 => f.IsChecked,
            _ => true,
        }).ToList();
        var writes = chosen.Where(f => !f.AlreadyThere).ToList();
        var bytes = writes.Aggregate(0UL, (sum, f) => sum + f.Size);
        var removes = _plan["remove"]?.AsArray().Count(r => Part switch
        {
            1 => !PatchPlan.IsBinary(r!["name"]!.GetValue<string>()),
            2 => PatchPlan.IsBinary(r!["name"]!.GetValue<string>()),
            3 => false,
            _ => true,
        }) ?? 0;

        TopFiles.Clear();
        foreach (var file in writes.OrderByDescending(f => f.Size).Take(6)) TopFiles.Add($"{file.Name}  ·  {Format.Size(file.Size)}");
        if (writes.Count > 6) TopFiles.Add($"and {writes.Count - 6} more");
        Raise(nameof(HasTopFiles));

        Summary = Part == 3 && chosen.Count == 0
            ? "No files are chosen."
            : writes.Count == 0 && removes == 0
            ? "Those files are already this build: nothing to change."
            : $"{writes.Count} file{(writes.Count == 1 ? "" : "s")} to download, {Format.Size(bytes)}" + (removes > 0 ? $", and {removes} to remove" : "") + ".";
        StartCommand.Changed();
    }

    /// <summary>Opens the SteamDB helper for the depots the plan needed; what is pasted there joins the build, and it is worked out again.</summary>
    void FindManifests()
    {
        if (_main.Library is not { } library || _needs.Count == 0)
        {
            _main.Back();
            return;
        }
        var language = IsDownload && _language is { } chosen ? $" in {chosen.Name}" : "";
        var explanation = $"For “{_build?.Title ?? "this build"}”{language}, Steam's product info on this PC does not name these depots' manifests."
                          + (_neededBefore is { } before
                              ? $" On each depot's SteamDB page, the one to take is the newest first seen before {before.ToLocalTime():d MMM yyyy HH:mm}: paste the rows and it is picked for you."
                              : " Paste the manifest each one had in that build.");
        _main.Show(HelperPageViewModel.ForBuild(_main, library, _game.Game, $"{_game.Name}{language}: manifests from SteamDB", explanation, _needs, _neededBefore,
            found =>
            {
                var depots = found.Select(f => f.Split('=')[0]).ToHashSet();
                _foundManifests.RemoveAll(m => depots.Contains(m.Split('=')[0]));
                _foundManifests.AddRange(found);
                _ = PlanAsync();
            }));
    }

    async Task BrowseAsync()
    {
        var title = IsApply ? "The patch folder, or the folder a build was downloaded into" : "Where the files go";
        var start = Folder.Length > 0 ? Path.GetDirectoryName(Folder) : null;
        if (await _main.Platform.PickFolderAsync(title, start) is not { } picked) return;
        if (IsApply)
        {
            Folder = picked;
            await PlanAsync();
            return;
        }
        // A folder named after the build goes inside the one picked, unless the one picked is already empty.
        var holdsFiles = await Task.Run(() => Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any());
        Folder = holdsFiles && Path.GetFileName(_folder) is { Length: > 0 } name
            ? Path.Combine(picked, name)
            : picked;
    }

    void Start()
    {
        var settings = Settings(plan: false);
        var title = Kind switch
        {
            "ingame" => $"Putting {_build?.Title} into {_game.Name}",
            "download" => $"Downloading {_build?.Title}{(HasLanguages && _language is { } language ? $" in {language.Name}" : "")}",
            "patch" => $"Saving a patch folder for {_build?.Title}",
            "undo" => $"Undoing {_game.Applied?.Title}",
            _ => $"Applying {Folder}",
        };
        if (_main.StartJob(_game.Game, Kind, settings, title) is { } refused) Error = refused;
    }

    public void Progress(JobProgress progress) => Stage = progress.Detail is { } detail ? $"{progress.Stage}: {detail}" : progress.Stage;

    public void Log(string line)
    {
    }
}
