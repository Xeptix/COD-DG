using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CODDowngrader.App;
using CODDowngrader.Builds;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>One build on the list: what it is, where it is now, and what can be done with it.</summary>
public sealed class MadeBuildItem
{
    public MadeBuildItem(MadeBuild build, string state, Bitmap? icon, Command? share, Command? openFolder, Command remove,
        Command? apply = null, Command? undo = null)
    {
        Build = build;
        ApplyCommand = apply;
        UndoCommand = undo;
        State = state;
        Icon = icon;
        ShareCommand = share;
        OpenFolderCommand = openFolder;
        RemoveCommand = remove;
    }

    public MadeBuild Build { get; }
    public string Game => Build.Game;
    public string Title => Build.Title;
    public string What => $"{Build.What} · {Format.DateTime(Build.Made)}";
    public string Folder => Build.Kind == "apply" && Build.From is { Length: > 0 } from ? $"{Build.Folder}, from {from}" : Build.Folder;
    public string State { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public Command? ShareCommand { get; }
    public Command? OpenFolderCommand { get; }
    public Command RemoveCommand { get; }

    /// <summary>A download or a patch still whole in its folder, for a game installed here: put it into the game.</summary>
    public Command? ApplyCommand { get; }

    /// <summary>The downgrade written into its game now: take it out.</summary>
    public Command? UndoCommand { get; }
    public bool CanApply => ApplyCommand is not null;
    public bool CanUndo => UndoCommand is not null;
    public bool CanShare => ShareCommand is not null;
    public bool CanOpenFolder => OpenFolderCommand is not null;
    public string OpenFolderLabel => Build.Kind is "ingame" or "apply" ? "Open the game folder" : "Open the folder";
}

/// <summary>Every download, downgrade and patch folder made on this PC, newest first, to find again or share.</summary>
public sealed class BuildsPageViewModel : Observable, IHasBack
{
    readonly MainViewModel _main;
    readonly string? _path;
    bool _loading = true;

    /// <param name="path">The list to show; the tool's own when null.</param>
    public BuildsPageViewModel(MainViewModel main, string? path = null)
    {
        _main = main;
        _path = path;
        BackCommand = new Command(() => _main.Back());
        Loaded = LoadAsync();
    }

    public ObservableCollection<MadeBuildItem> Builds { get; } = new();
    public Command BackCommand { get; }

    /// <summary>The list's reading, started when the page opened.</summary>
    public Task Loaded { get; }

    public bool Loading
    {
        get => _loading;
        private set
        {
            Set(ref _loading, value);
            Raise(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !_loading && Builds.Count == 0;

    async Task LoadAsync()
    {
        var library = _main.Library;
        var found = await Task.Run(() => MadeBuilds.Load(library, _path)
            .OrderByDescending(b => b.Made)
            .Select(b => (Build: b, State: MadeBuilds.StateOf(b, library), InGame: MadeBuilds.InGame(b), Holds: MadeBuilds.FolderHolds(b),
                Exists: Directory.Exists(b.Folder)))
            .ToList());

        Builds.Clear();
        foreach (var (build, state, inGame, holds, exists) in found)
        {
            var shared = build.Shared.Length > 0 ? SharedBuild.Read(build.Shared, out _) : null;
            var folder = build.Folder;
            var game = library?.Entries.FirstOrDefault(e => e.AppId == build.AppId);
            var canWrite = library is not null && game is { Installed: not null, Downgradable: true };
            Builds.Add(new MadeBuildItem(build, state, _main.IconOf(build.AppId),
                shared is null ? null : new Command(() => _main.Show(new SharePageViewModel(_main, shared))),
                exists ? new Command(() => _main.Platform.Open(folder)) : null,
                new Command(() => Remove(build)),
                canWrite && holds ? new Command(() => _main.Show(new ActionPageViewModel(_main, new GamePageViewModel(_main, library!, game!), "apply", null, folder))) : null,
                canWrite && inGame ? new Command(() => _main.Show(new ActionPageViewModel(_main, new GamePageViewModel(_main, library!, game!), "undo", null))) : null));
        }
        Loading = false;
    }

    /// <summary>Takes a build off the list. What it made stays where it is.</summary>
    void Remove(MadeBuild build)
    {
        MadeBuilds.Remove(build, _path);
        if (Builds.FirstOrDefault(b => ReferenceEquals(b.Build, build)) is { } item) Builds.Remove(item);
        Raise(nameof(IsEmpty));
    }
}
