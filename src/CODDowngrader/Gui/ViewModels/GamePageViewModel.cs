using System.Collections.ObjectModel;
using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Cli;
using CODDowngrader.Jobs;
using CODDowngrader.Patching;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>A build to choose: one the history knows, or one put together for a date from manifests found on SteamDB.</summary>
public sealed class BuildItem
{
    public BuildItem(string key, string title, string detail, Build? build, BuildKind kind, JobSettings? settings = null)
    {
        Key = key;
        Title = title;
        Detail = detail.TrimStart(' ', '·').Trim();
        Build = build;
        Kind = kind;
        _settings = settings;
    }

    readonly JobSettings? _settings;

    public string Key { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public Build? Build { get; }
    public BuildKind Kind { get; }
    public bool IsInstalled => Kind == BuildKind.Installed;

    /// <summary>What a job needs to name this build.</summary>
    public JobSettings Settings => _settings ?? new JobSettings { Build = Key };

    /// <summary>Put into the list from elsewhere, the SteamDB helper or a shared build, rather than read from the history.</summary>
    public bool Chosen { get; set; }
}

public sealed class GamePageViewModel : Observable
{
    readonly MainViewModel _main;
    readonly GameLibrary _library;
    BuildHistoryResult? _history;
    BuildItem? _selected;
    bool _loading = true;
    DateTime? _atDate;
    string? _atMessage;
    string? _notice;
    string? _shareMessage;

    public GamePageViewModel(MainViewModel main, GameLibrary library, GameEntry game)
    {
        _main = main;
        _library = library;
        Game = game;
        Applied = game.Installed is { } app ? PatchStore.AppliedTo(app.InstallDir) : null;

        InGameCommand = new Command(() => Act("ingame"), () => CanInGame);
        DownloadCommand = new Command(() => Act("download"), () => _selected is not null);
        PatchCommand = new Command(() => Act("patch"), () => _selected is not null && Builds.Count > 1);
        OpenFolderCommand = new Command(() => { if (Game.Installed?.InstallDir is { } folder) _main.Platform.Open(folder); }, () => IsInstalled);
        UndoCommand = new Command(() => _main.Show(new ActionPageViewModel(_main, this, "undo", null)), () => Applied is not null);
        AgainCommand = new Command(Again, () => CanAgain);
        FindAtCommand = new Command(FindAtAsync, () => _atDate is not null && _history is not null);
        ManualCommand = new Command(() => _main.Show(HelperPageViewModel.ForManifests(_main, this, _library, Game)), () => _history is not null);
        ShareCommand = new Command(ShareSelected, () => _selected is not null && _history is not null);
        ShowJobCommand = new Command(() => { if (Job is { } run) _main.Show(run); }, () => HasJob);
        ShareAppliedCommand = new Command(() => { if (Applied is not null) _main.Show(new SharePageViewModel(_main, SharedBuild.Of(Applied, Game))); }, () => Applied is not null);
    }

    /// <summary>The builds' reading, started when the page opened.</summary>
    public Task Loaded { get; set; } = Task.CompletedTask;

    public GameEntry Game { get; }
    public string Name => Game.Name;

    /// <summary>The job this game has, running or ended: shown above everything, with a way back to its page.</summary>
    public RunPageViewModel? Job => _main.JobOf(Game.AppId);
    public bool HasJob => Job is not null;

    public void JobChanged()
    {
        Raise(nameof(Job));
        Raise(nameof(HasJob));
        ShowJobCommand.Changed();
    }
    public Avalonia.Media.Imaging.Bitmap? Icon => _main.IconOf(Game.AppId);
    public bool HasIcon => Icon is not null;
    public bool IsInstalled => Game.Installed is not null;
    public string Folder => Game.Installed?.InstallDir ?? "Not installed on this PC. Downloading a build into a folder of its own still works.";

    public string Status
    {
        get
        {
            if (Game.Installed is not { } app) return "";
            var text = $"Build {app.BuildId} installed";
            return app.UpdatePending ? $"{text}. Steam has build {app.TargetBuildId} queued, and installing it replaces these files." : $"{text}.";
        }
    }

    public bool Downgradable => Game.Downgradable;
    public string? NotDowngradable => Game.Title?.NotDowngradable;

    public AppliedRecord? Applied { get; }
    public bool HasApplied => Applied is not null;

    public string? AppliedText => Applied is null
        ? null
        : PatchApplier.StateOf(Applied, _library.FolderManifests(Applied.InstallDir)) switch
        {
            DowngradeState.Intact => $"{Applied.Title} is written into this game, since {Format.Date(Applied.Applied)}.",
            DowngradeState.Unfinished => $"Writing {Applied.Title} stopped part way. Undo puts back what was changed.",
            DowngradeState.FilesChanged => $"{Applied.Title} was written into this game, and Steam has put back some of its own files since.",
            _ => $"{Applied.Title} was written into this game, and Steam has updated it since.",
        };

    public bool CanAgain => Applied is not null
                            && PatchApplier.StateOf(Applied, _library.FolderManifests(Applied.InstallDir)) is DowngradeState.FilesChanged or DowngradeState.SteamUpdated;

    public ObservableCollection<BuildItem> Builds { get; } = new();

    public BuildItem? SelectedBuild
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ShareMessage = null;
            Raise(nameof(HasSelection));
            Raise(nameof(InGameHint));
            Refresh();
        }
    }

    public bool HasSelection => _selected is not null;

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public DateTime? AtDate
    {
        get => _atDate;
        set
        {
            if (Set(ref _atDate, value)) FindAtCommand.Changed();
        }
    }

    public string? AtMessage
    {
        get => _atMessage;
        private set
        {
            Set(ref _atMessage, value);
            Raise(nameof(HasAtMessage));
        }
    }

    public bool HasAtMessage => _atMessage is not null;

    /// <summary>Said above the list when a build was put into it from elsewhere.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            Set(ref _notice, value);
            Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice is not null;

    public string? ShareMessage
    {
        get => _shareMessage;
        private set
        {
            Set(ref _shareMessage, value);
            Raise(nameof(HasShareMessage));
        }
    }

    public bool HasShareMessage => _shareMessage is not null;

    bool CanInGame => IsInstalled && _selected is { IsInstalled: false };

    public string? InGameHint => _selected is { IsInstalled: true } ? "That build is the one installed." : null;

    public Command InGameCommand { get; }
    public Command DownloadCommand { get; }
    public Command PatchCommand { get; }
    public Command OpenFolderCommand { get; }
    public Command UndoCommand { get; }
    public Command AgainCommand { get; }
    public Command FindAtCommand { get; }
    public Command ManualCommand { get; }
    public Command ShareCommand { get; }
    public Command ShowJobCommand { get; }
    public Command ShareAppliedCommand { get; }

    /// <summary>The sidebar's Open a build, offered where the list has no build that fits.</summary>
    public Command OpenBuildCommand => _main.OpenBuildCommand;

    public IReadOnlyList<BuildItem> PatchStarts => Builds.Where(b => b != _selected && b.Build is not null && !b.Chosen).ToList();

    public async Task LoadAsync()
    {
        if (!Game.Downgradable)
        {
            Loading = false;
            return;
        }
        var history = await Task.Run(() => _library.History(Game));
        _history = history;
        var keys = Selectors.Keys(history.Builds);
        var titles = Format.BuildTitles(Game, history.Builds);
        Builds.Clear();
        for (var i = 0; i < history.Builds.Count; i++)
            Builds.Add(new BuildItem(keys[i], titles[i], Format.BuildDetail(_library, Game, history.Builds[i]), history.Builds[i], history.Builds[i].Kind));
        SelectedBuild = Builds.FirstOrDefault(b => !b.IsInstalled && b.Kind != BuildKind.Latest) ?? Builds.FirstOrDefault();
        Loading = false;
        Refresh();
    }

    /// <summary>A build put together elsewhere (the SteamDB helper, a shared build): it joins the list and is chosen.</summary>
    public void Choose(BuildItem item, string? notice = null)
    {
        var existing = Builds.FirstOrDefault(b => b.Key == item.Key && b.Chosen);
        if (existing is not null) Builds.Remove(existing);
        item.Chosen = true;
        Builds.Insert(0, item);
        SelectedBuild = item;
        Notice = notice ?? $"{item.Title}: chosen. Pick what to do with it below.";
    }

    /// <summary>The chosen version, as a shared build: every depot's manifest, and the part and options it came with.</summary>
    void ShareSelected()
    {
        if (_selected is not { } item || _history is not { } history) return;
        var settings = item.Settings;
        if (Selectors.Of(Game, history, settings, out var error) is not { } target)
        {
            ShareMessage = error;
            return;
        }
        ShareMessage = null;
        var title = item.Chosen ? settings.Label ?? target.Label : item.Title;
        _main.Show(new SharePageViewModel(_main, SharedBuild.Of(Game, target.Manifests, title, settings.Only, settings.Files, settings.Siblings)));
    }

    async Task FindAtAsync()
    {
        if (_history is not { } history || _atDate is not { } date) return;
        var at = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = Selectors.At(Game, history, at, Array.Empty<string>(), out var error, out var needs);
        if (target is not null)
        {
            var match = Builds.FirstOrDefault(b => string.Equals(b.Key, target.Key, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedBuild = match;
                AtMessage = $"On {date:d MMM yyyy} the game was: {match.Title}.";
            }
            return;
        }
        if (needs.Count == 0)
        {
            AtMessage = error;
            return;
        }
        AtMessage = null;
        _main.Show(HelperPageViewModel.ForDate(_main, this, _library, Game, date, needs, error!));
        await Task.CompletedTask;
    }

    void Act(string kind)
    {
        if (_selected is null) return;
        _main.Show(new ActionPageViewModel(_main, this, kind, _selected));
    }

    void Again()
    {
        if (Applied is null) return;
        var settings = new JobSettings
        {
            Manifests = IdMap.Manifests(Applied.TargetManifests).Select(m => $"{m.Key}={m.Value}").ToList(),
            Label = Applied.Build,
            Again = true,
        };
        var item = new BuildItem("again", Applied.Build, "the build written in before", null, BuildKind.Previous, settings);
        _main.Show(new ActionPageViewModel(_main, this, "ingame", item));
    }

    void Refresh()
    {
        foreach (var command in new[] { InGameCommand, DownloadCommand, PatchCommand, UndoCommand, AgainCommand, FindAtCommand, ManualCommand, ShareCommand, ShareAppliedCommand })
            command.Changed();
    }
}
