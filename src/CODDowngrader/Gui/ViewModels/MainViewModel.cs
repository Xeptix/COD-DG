using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CODDowngrader.App;
using CODDowngrader.Catalog;
using CODDowngrader.Steam;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>A game in the sidebar.</summary>
public sealed class GameItem : Observable
{
    RunPageViewModel? _job;

    public GameItem(GameEntry entry, GameLibrary library, Bitmap? icon = null)
    {
        Entry = entry;
        Icon = icon;
        var notes = new List<string>();
        if (entry.Installed is { } app)
        {
            notes.Add($"build {app.BuildId}");
            if (app.UpdatePending) notes.Add("update queued");
        }
        if (!entry.Downgradable) notes.Add("online only");
        Subtitle = string.Join(" · ", notes);
        if (entry.Installed is { } installed && Patching.PatchStore.AppliedTo(installed.InstallDir) is { } applied)
            Badge = Patching.PatchApplier.StateOf(applied, library.FolderManifests(installed.InstallDir)) == Patching.DowngradeState.Intact
                ? "Downgraded"
                : "Downgraded, changed since";
    }

    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string Subtitle { get; }
    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>The icon of the exe Steam starts for an installed game.</summary>
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;

    /// <summary>Whether the sidebar's filter lets this game through: part of its name, or its app ID.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Entry.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture) == filter;
    public string? Badge { get; }
    public bool HasBadge => Badge is not null;

    /// <summary>The job this game has, running or ended and not yet looked at.</summary>
    public RunPageViewModel? Job
    {
        get => _job;
        set
        {
            _job = value;
            Refresh();
        }
    }

    public bool HasJob => _job is not null;
    public bool JobRunning => _job?.Running == true;
    public bool JobIndeterminate => _job?.Indeterminate != false;
    public double JobFraction => _job?.Fraction ?? 0;
    public bool JobDone => _job?.Succeeded == true;
    public bool JobFailed => _job?.Failed == true;

    public string? JobText => _job switch
    {
        null => null,
        { Running: true, Indeterminate: true } running => running.Stage,
        { Running: true } running => $"{running.Stage} · {Math.Round(running.Fraction * 100):0}%",
        { Succeeded: true } => "Done",
        var ended => ended.Stage,
    };

    /// <summary>The job has moved on: what the list shows follows it.</summary>
    public void Refresh()
    {
        foreach (var name in new[] { nameof(Job), nameof(HasJob), nameof(JobRunning), nameof(JobIndeterminate), nameof(JobFraction), nameof(JobDone), nameof(JobFailed), nameof(JobText) })
            Raise(name);
    }
}

/// <summary>The window: the games down the side, and the page for whatever is chosen.</summary>
public sealed class MainViewModel : Observable
{
    readonly Stack<object> _back = new();
    readonly List<GameItem> _allInstalled = new();
    readonly List<GameItem> _allOthers = new();
    readonly Dictionary<uint, Bitmap> _icons = new();
    readonly Dictionary<uint, RunPageViewModel> _jobs = new();
    GamePageViewModel? _gamePage;
    bool _askToClose;
    object? _page;
    GameItem? _open;
    string _filter = "";
    GameItem? _selectedInstalled;
    GameItem? _selectedOther;
    bool _loading;
    string _account = "";

    public MainViewModel(Options options, IPlatform platform)
    {
        Options = options;
        Platform = platform;
        SettingsCommand = new Command(() => Show(new SettingsPageViewModel(this)));
        OpenBuildCommand = new Command(() => Show(new OpenBuildPageViewModel(this)), () => Library is not null);
        KeepGoingCommand = new Command(() => AskToClose = false);
        StopAndCloseCommand = new Command(StopAndCloseAsync);
        BuildsCommand = new Command(() => Show(new BuildsPageViewModel(this)), () => Library is not null);
    }

    public Options Options { get; }
    public IPlatform Platform { get; }
    public SteamInstall? Steam { get; private set; }
    public GameLibrary? Library { get; private set; }

    public string Title => $"COD Downgrader {AppState.Version}";
    public string Version => $"Version {AppState.Version}";

    public ObservableCollection<GameItem> Installed { get; } = new();
    public ObservableCollection<GameItem> Others { get; } = new();
    public bool HasInstalled => Installed.Count > 0;
    public bool HasOthers => Others.Count > 0;
    public bool NothingMatches => _filter.Length > 0 && Installed.Count == 0 && Others.Count == 0;

    /// <summary>Narrows the sidebar to the games whose name holds it, or whose app ID it is.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? "")) ApplyFilter();
        }
    }

    void ApplyFilter()
    {
        var filter = _filter.Trim();
        Installed.Clear();
        Others.Clear();
        foreach (var item in _allInstalled.Where(i => i.Matches(filter))) Installed.Add(item);
        foreach (var item in _allOthers.Where(i => i.Matches(filter))) Others.Add(item);
        // The game that is open stays highlighted whenever it is in the list.
        if (_open is not null && Installed.Contains(_open)) SelectedInstalled = _open;
        if (_open is not null && Others.Contains(_open)) SelectedOther = _open;
        Raise(nameof(HasInstalled));
        Raise(nameof(HasOthers));
        Raise(nameof(NothingMatches));
    }

    public Command SettingsCommand { get; }
    public Command OpenBuildCommand { get; }
    public Command KeepGoingCommand { get; }
    public Command StopAndCloseCommand { get; }

    /// <summary>The window is asked to close while jobs run: it asks first.</summary>
    public bool AskToClose { get => _askToClose; private set { Set(ref _askToClose, value); Raise(nameof(RunningText)); } }

    public int RunningJobs => _jobs.Values.Count(j => j.Running);

    public string RunningText
    {
        get
        {
            var names = _jobs.Values.Where(j => j.Running).Select(j => j.Game.Name).ToList();
            return names.Count == 1
                ? $"{names[0]} is still going. Closing stops it; starting the same download again later carries on from where it stopped."
                : $"{names.Count} jobs are still going: {string.Join(", ", names)}. Closing stops them; starting the same download again later carries on from where it stopped.";
        }
    }

    /// <summary>Raised once every job has stopped after Stop them and close.</summary>
    public event EventHandler? CloseReady;

    /// <summary>
    /// Starts a job for a game, one per game: a game with one running shows it instead, and one that has ended makes way for the
    /// new one. A job that writes into a game's folder waits for any other job writing into that folder. Null once the job's
    /// page shows; otherwise why it did not start.
    /// </summary>
    public string? StartJob(GameEntry game, string kind, Jobs.JobSettings settings, string title)
    {
        if (_jobs.TryGetValue(game.AppId, out var existing))
        {
            if (existing.Running)
            {
                ShowJob(existing);
                return null;
            }
            EndJob(existing);
        }
        if (kind is "ingame" or "apply" or "undo" && game.Installed is { } app
            && _jobs.Values.FirstOrDefault(j => j.Running && j.WritesIntoGame && j.Game.Installed is { } other && PathRules.Same(other.InstallDir, app.InstallDir)) is { } busy)
            return $"{busy.Game.Name} is being changed in the same folder right now. Start this once that has finished.";

        var run = new RunPageViewModel(this, game, kind, settings, title);
        _jobs[game.AppId] = run;
        run.PropertyChanged += (_, _) => JobMoved(game.AppId);
        JobMoved(game.AppId);
        ShowJob(run);
        return null;
    }

    /// <summary>The job a game has, running or ended and not yet looked at.</summary>
    public RunPageViewModel? JobOf(uint appId) => _jobs.GetValueOrDefault(appId);

    /// <summary>An ended job's page has been left: the game goes back to showing nothing.</summary>
    public void EndJob(RunPageViewModel run)
    {
        if (!_jobs.TryGetValue(run.Game.AppId, out var kept) || !ReferenceEquals(kept, run) || run.Running) return;
        _jobs.Remove(run.Game.AppId);
        JobMoved(run.Game.AppId);
    }

    /// <summary>A job's page over its game's page, in place of the choice page it was started from.</summary>
    void ShowJob(RunPageViewModel run)
    {
        if (_page is ActionPageViewModel) Page = run;
        else Show(run);
    }

    void JobMoved(uint appId)
    {
        var run = _jobs.GetValueOrDefault(appId);
        foreach (var item in _allInstalled.Concat(_allOthers).Where(i => i.Entry.AppId == appId))
        {
            if (ReferenceEquals(item.Job, run)) item.Refresh();
            else item.Job = run;
        }
        if (_gamePage?.Game.AppId == appId) _gamePage.JobChanged();
        Raise(nameof(RunningJobs));
    }

    /// <summary>The window's close button: straight away with nothing running, and otherwise after asking.</summary>
    public bool CanCloseNow()
    {
        if (RunningJobs == 0) return true;
        AskToClose = true;
        return false;
    }

    async Task StopAndCloseAsync()
    {
        var running = _jobs.Values.Where(j => j.Running).ToList();
        foreach (var run in running) run.Stop();
        await Task.WhenAny(Task.WhenAll(running.Select(r => r.Completion)), Task.Delay(TimeSpan.FromSeconds(20)));
        CloseReady?.Invoke(this, EventArgs.Empty);
    }
    public Command BuildsCommand { get; }

    public object? Page
    {
        get => _page;
        private set => Set(ref _page, value);
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string Account
    {
        get => _account;
        private set => Set(ref _account, value);
    }

    /// <summary>What the status line says about remembering: only worth saying when this run does not.</summary>
    public string? RememberNote => Remembered.Writing ? null : "Remembering nothing new this run";

    public bool HasRememberNote => RememberNote is not null;

    public GameItem? SelectedInstalled
    {
        get => _selectedInstalled;
        set
        {
            if (!Set(ref _selectedInstalled, value) || value is null) return;
            SelectedOther = null;
            if (value != _open) OpenGame(value);
        }
    }

    public GameItem? SelectedOther
    {
        get => _selectedOther;
        set
        {
            if (!Set(ref _selectedOther, value) || value is null) return;
            SelectedInstalled = null;
            if (value != _open) OpenGame(value);
        }
    }

    /// <summary>Finds Steam and reads every game, off the window's thread, and opens the first installed game.</summary>
    public async Task LoadAsync(uint? preselect = null)
    {
        Loading = true;
        try
        {
            RefreshAccount();
            var steam = await Task.Run(() => SteamInstall.Find(Options.SteamRoot));
            Steam = steam;
            if (steam is null)
            {
                Library = null;
                OpenBuildCommand.Changed();
                BuildsCommand.Changed();
                _open = null;
                _allInstalled.Clear();
                _allOthers.Clear();
                ApplyFilter();
                _back.Clear();
                Page = new SetupPageViewModel(this);
                return;
            }

            var (library, icons) = await Task.Run(() =>
            {
                var loaded = GameLibrary.Load(steam);
                return (loaded, GameIcons.Read(loaded));
            });
            _icons.Clear();
            foreach (var (appId, ico) in icons)
                if (GameIcons.Bitmap(ico) is { } bitmap) _icons[appId] = bitmap;
            Library = library;
            OpenBuildCommand.Changed();
            BuildsCommand.Changed();
            var keep = _selectedInstalled?.Entry.AppId ?? _selectedOther?.Entry.AppId ?? _open?.Entry.AppId ?? preselect;
            _allInstalled.Clear();
            _allOthers.Clear();
            foreach (var entry in library.Entries)
                (entry.Installed is not null ? _allInstalled : _allOthers).Add(new GameItem(entry, library, _icons.GetValueOrDefault(entry.AppId)));

            _selectedInstalled = null;
            _selectedOther = null;
            _open = null;
            _back.Clear();
            ApplyFilter();
            // The games read again: each keeps the job it had.
            foreach (var appId in _jobs.Keys.ToList()) JobMoved(appId);
            var chosen = _allInstalled.Concat(_allOthers).FirstOrDefault(i => i.Entry.AppId == keep)
                         ?? Installed.FirstOrDefault() ?? _allInstalled.FirstOrDefault();
            if (chosen is null)
            {
                Page = new SetupPageViewModel(this, noGames: true);
                return;
            }
            if (chosen.Entry.Installed is not null) SelectedInstalled = chosen;
            else SelectedOther = chosen;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>An installed game's icon, when its exe has one.</summary>
    public Bitmap? IconOf(uint appId) => _icons.GetValueOrDefault(appId);

    public void RefreshAccount()
    {
        var account = AppState.LoadSettings().SteamAccount;
        Account = account is null ? "Not signed in to Steam yet" : $"Signed in as {account}";
    }

    void OpenGame(GameItem item)
    {
        if (Library is null) return;
        _back.Clear();
        _open = item;
        var page = new GamePageViewModel(this, Library, item.Entry);
        _gamePage = page;
        Page = page;
        page.Loaded = page.LoadAsync();
        // Back on a game with a job: its page again, over the game's.
        if (_showJobs && _jobs.TryGetValue(item.Entry.AppId, out var run)) Show(run);
    }

    /// <summary>Whether opening a game shows its job; off while a page opens a game to work on it, as Open a build does.</summary>
    bool _showJobs = true;

    /// <summary>Opens a game's page as picking it in the sidebar does, and waits until its builds are read. Null when it is not listed.</summary>
    public async Task<GamePageViewModel?> OpenGameAsync(uint appId)
    {
        var item = _allInstalled.Concat(_allOthers).FirstOrDefault(i => i.Entry.AppId == appId);
        if (item is null) return null;
        // A fresh page, and the game in view in the sidebar.
        Filter = "";
        _selectedInstalled = null;
        _selectedOther = null;
        _open = null;
        _showJobs = false;
        try
        {
            if (item.Entry.Installed is not null) SelectedInstalled = item;
            else SelectedOther = item;
        }
        finally
        {
            _showJobs = true;
        }
        Raise(nameof(SelectedInstalled));
        Raise(nameof(SelectedOther));
        if (Page is not GamePageViewModel page) return null;
        await page.Loaded;
        return page;
    }

    /// <summary>Opens a page over the current one; <see cref="Back"/> returns to it.</summary>
    public void Show(object page)
    {
        if (_page is not null) _back.Push(_page);
        Page = page;
    }

    public void Back()
    {
        if (_back.Count > 0) Page = _back.Pop();
    }

    /// <summary>After a job changed a game: read everything again and open that game's page, fresh.</summary>
    public async Task ReloadAsync(uint appId)
    {
        _selectedInstalled = null;
        _selectedOther = null;
        await LoadAsync(appId);
    }
}
