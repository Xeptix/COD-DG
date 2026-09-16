using System.Collections.ObjectModel;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Patching;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>A game a folder holds a build of, and what can be done with it from here.</summary>
public sealed class FolderBuild
{
    public FolderBuild(string title, string details, Command? apply, Command? share)
    {
        Title = title;
        Details = details;
        ApplyCommand = apply;
        ShareCommand = share;
    }

    public string Title { get; }
    public string Details { get; }
    public Command? ApplyCommand { get; }
    public Command? ShareCommand { get; }
    public bool CanApply => ApplyCommand is not null;
    public bool CanShare => ShareCommand is not null;
}

/// <summary>
/// Builds that are not in a game's list: one someone shared, pasted or opened from a file and chosen on its game's page with the
/// files and options it came with; or a patch folder or downloaded build already on this PC, put into its game or shared.
/// </summary>
public sealed class OpenBuildPageViewModel : Observable, IHasBack
{
    /// <summary>A shared build is a few kilobytes; anything far bigger is not one.</summary>
    const long LargestFile = 1_048_576;

    readonly MainViewModel _main;
    string _text = "";
    SharedBuild? _shared;
    string? _summary;
    string? _details;
    string? _error;
    string? _folder;
    string? _folderError;
    bool _reading;

    public OpenBuildPageViewModel(MainViewModel main)
    {
        _main = main;
        OpenFileCommand = new Command(OpenFileAsync);
        ContinueCommand = new Command(ContinueAsync, () => _shared is not null && _error is null);
        ChooseFolderCommand = new Command(ChooseFolderAsync);
        BackCommand = new Command(() => _main.Back());
    }

    // --- a shared build -------------------------------------------------------------------------

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value)) Read();
        }
    }

    public string? Summary { get => _summary; private set { Set(ref _summary, value); Raise(nameof(HasSummary)); } }
    public bool HasSummary => _summary is not null;
    public string? Details { get => _details; private set => Set(ref _details, value); }
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;

    public Command OpenFileCommand { get; }
    public Command ContinueCommand { get; }
    public Command BackCommand { get; }

    void Read()
    {
        _shared = null;
        Summary = null;
        Details = null;
        Error = null;
        if (_text.Trim().Length > 0)
        {
            if (SharedBuild.Read(_text, out var readError) is not { } shared)
            {
                Error = readError;
            }
            else
            {
                _shared = shared;
                var game = _main.Library?.Entries.FirstOrDefault(e => e.AppId == shared.AppId);
                Summary = $"{game?.Name ?? (shared.Game.Length > 0 ? shared.Game : $"Steam app {shared.AppId}")}: {shared.Title}";
                var depots = shared.Manifests.Count == 1 ? "1 depot" : $"{shared.Manifests.Count} depots";
                Details = $"{depots}, {shared.PartLabel ?? "every file that differs"}{(shared.Siblings ? ", with the games sharing its folder" : "")}."
                          + (game is { Installed: null } ? " The game is not installed here, so the build can be downloaded into a folder of its own." : "");
                if (_main.Library is { } library && SharedBuilds.GameOf(library, shared, out var gameError) is null) Error = gameError;
            }
        }
        ContinueCommand.Changed();
    }

    async Task OpenFileAsync()
    {
        if (await _main.Platform.PickFileAsync("Open a shared build") is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > LargestFile)
            {
                Error = "That file is far too big to be a shared build.";
                return;
            }
            Text = await File.ReadAllTextAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"That file could not be read: {e.Message}";
        }
    }

    async Task ContinueAsync()
    {
        if (_shared is not { } shared || _main.Library is not { } library) return;
        if (SharedBuilds.GameOf(library, shared, out var gameError) is not { } game)
        {
            Error = gameError;
            return;
        }

        var history = await Task.Run(() => library.History(game));
        if (SharedBuilds.Resolve(game, history, shared, out var resolveError) is not { } target)
        {
            Error = resolveError;
            return;
        }
        if (await _main.OpenGameAsync(game.AppId) is not { } page) return;

        var part = shared.PartLabel is { } label ? $", {label}" : "";
        var detail = target.Build is null ? "shared with you" : "shared with you · the same manifests as a build in this list";
        page.Choose(new BuildItem("shared", target.Title + part, detail, target.Build, target.Build?.Kind ?? BuildKind.Previous, target.Settings),
            string.Join(" ", new[] { $"{target.Title}{part}: shared with you, and chosen. Pick what to do with it below." }.Concat(target.Notes)));
    }

    // --- a patch folder or downloaded build -----------------------------------------------------

    public string? Folder { get => _folder; private set { Set(ref _folder, value); Raise(nameof(HasFolder)); } }
    public bool HasFolder => _folder is not null;
    public string? FolderError { get => _folderError; private set { Set(ref _folderError, value); Raise(nameof(HasFolderError)); } }

    /// <summary>The chosen folder's records are being read. A folder on a network share can take a moment to answer.</summary>
    public bool Reading { get => _reading; private set => Set(ref _reading, value); }
    public bool HasFolderError => _folderError is not null;

    /// <summary>The builds the chosen folder holds: one for a patch, one per game for a download.</summary>
    public ObservableCollection<FolderBuild> FolderBuilds { get; } = new();

    public Command ChooseFolderCommand { get; }

    async Task ChooseFolderAsync()
    {
        var start = await Task.Run(StartFolder);
        if (await _main.Platform.PickFolderAsync("A patch folder, or a folder a build was downloaded into", start) is not { } folder) return;
        await UseFolderAsync(folder);
    }

    /// <summary>
    /// Where the picker opens: beside the folder last chosen here, or else where downloads and patch folders go unless told
    /// otherwise, in a Steam library on this PC. Left to itself, Windows opens wherever it was last, a network share included,
    /// and the picker waits for that share before it shows anything.
    /// </summary>
    string? StartFolder() => _folder is { } last
        ? Path.GetDirectoryName(last)
        : _main.Library?.Steam.Libraries.Select(library => Path.Combine(library, "COD Downgrader")).FirstOrDefault(Directory.Exists);

    /// <summary>Reads what a folder holds, off the window's thread: the patch record, or the download record and each game in it.</summary>
    public async Task UseFolderAsync(string folder)
    {
        Folder = folder;
        FolderError = null;
        FolderBuilds.Clear();
        if (_main.Library is not { } library) return;

        Reading = true;
        var (patch, record) = await Task.Run(() =>
        {
            var found = PatchStore.LoadPatch(folder);
            return (found, found is null ? AppState.LoadRecord(folder, out _) : null);
        });
        Reading = false;
        if (!string.Equals(_folder, folder, StringComparison.Ordinal)) return;

        var unknown = new List<string>();
        if (patch is not null)
        {
            var bytes = patch.Files.Aggregate(0UL, (sum, f) => sum + f.Size);
            var files = patch.Files.Count == 1 ? "1 file" : $"{patch.Files.Count} files";
            foreach (var appId in patch.AppIds)
            {
                if (library.Entries.FirstOrDefault(e => e.AppId == appId) is not { } game)
                {
                    unknown.Add(patch.Game);
                    continue;
                }
                var details = patch.Complete
                    ? $"A patch from {patch.From}: {files}, {Format.Size(bytes)}."
                    : "A patch that did not finish downloading. Save it into the same folder again to carry on.";
                Add(game, patch.Title, details, patch.Complete, patch.Complete ? SharedBuild.Of(patch, game) : null);
            }
        }
        else if (record is not null)
        {
            foreach (var download in record.Downloads)
            {
                if (library.Entries.FirstOrDefault(e => e.AppId == download.AppId) is not { } game)
                {
                    unknown.Add(download.Game);
                    continue;
                }
                var details = download.Complete
                    ? $"The whole build, downloaded{(download.Finished is { } finished ? $" {Format.Date(finished)}" : "")}."
                    : "A download that did not finish. Choose the same version and folder again to carry on.";
                Add(game, download.Build, details, download.Complete, download.Complete ? SharedBuild.Of(download, game) : null);
            }
        }

        if (FolderBuilds.Count == 0)
            FolderError = unknown.Count > 0
                ? $"That folder holds {string.Join(" and ", unknown.Distinct())}, which is not a Call of Duty COD Downgrader knows here."
                : "That folder holds no patch and no download from COD Downgrader.";
    }

    void Add(GameEntry game, string build, string details, bool complete, SharedBuild? shared)
    {
        Command? apply = null;
        if (complete && game.Installed is not null && game.Downgradable)
        {
            var folder = _folder!;
            apply = new Command(async () =>
            {
                if (await _main.OpenGameAsync(game.AppId) is { } page) _main.Show(new ActionPageViewModel(_main, page, "apply", null, folder));
            });
        }
        else if (complete)
        {
            details += game.Installed is null ? " The game is not installed here, so it cannot be put into it." : "";
        }
        var share = shared is null ? null : new Command(() => _main.Show(new SharePageViewModel(_main, shared)));
        FolderBuilds.Add(new FolderBuild($"{game.Name}: {build}", details, apply, share));
    }
}
