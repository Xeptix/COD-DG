using System.Collections.ObjectModel;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Cli;
using CODDowngrader.Jobs;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>A job running, and how it ended.</summary>
public sealed class RunPageViewModel : Observable, IHasBack, IJobView
{
    /// <summary>Failures a person may choose to go past, as --yes does on the command line.</summary>
    static readonly HashSet<string> Overridable = new() { "folder-not-empty", "different-build", "no-space" };

    readonly MainViewModel _main;
    readonly GameEntry _game;
    readonly string _kind;
    JobSettings _settings;
    CancellationTokenSource? _cancel;

    string _stage = "Starting";
    string? _detail;
    double _fraction;
    bool _indeterminate = true;
    bool _running;
    bool _succeeded;
    bool _failed;
    string? _message;
    string? _code;
    string? _folder;
    bool _folderExists;
    SharedBuild? _shared;
    bool _showLog;

    public RunPageViewModel(MainViewModel main, GameEntry game, string kind, JobSettings settings, string title)
    {
        _main = main;
        _game = game;
        _kind = kind;
        _settings = settings;
        Title = title;

        CancelCommand = new Command(() => _cancel?.Cancel(), () => _running);
        DoneCommand = new Command(() => _main.ReloadAsync(_game.AppId), () => !_running);
        RetryCommand = new Command(() => StartAsync(), () => !_running && _failed);
        OverrideCommand = new Command(() =>
        {
            _settings = _settings with { Yes = true };
            return StartAsync();
        }, () => !_running && CanOverride);
        BackCommand = new Command(() => _main.Back(), () => !_running && _failed);
        LogsCommand = new Command(() => _main.Platform.Open(AppState.LogsFolder));
        OpenFolderCommand = new Command(() => { if (_folder is { } folder) _main.Platform.Open(folder); }, () => HasFolder);
        ShareCommand = new Command(() => { if (_shared is { } shared) _main.Show(new SharePageViewModel(_main, shared)); }, () => CanShare);
        ToggleLogCommand = new Command(() => ShowLog = !ShowLog);

        _ = StartAsync();
    }

    public string Title { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    public Command CancelCommand { get; }
    public Command DoneCommand { get; }
    public Command RetryCommand { get; }
    public Command OverrideCommand { get; }
    public Command BackCommand { get; }
    public Command LogsCommand { get; }
    public Command OpenFolderCommand { get; }
    public Command ShareCommand { get; }
    public Command ToggleLogCommand { get; }

    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    public string? Detail { get => _detail; private set { Set(ref _detail, value); Raise(nameof(HasDetail)); } }
    public bool HasDetail => !string.IsNullOrEmpty(_detail);
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public bool ShowLog
    {
        get => _showLog;
        set
        {
            Set(ref _showLog, value);
            Raise(nameof(LogToggleLabel));
        }
    }

    public string LogToggleLabel => _showLog ? "Hide the details" : "Show the details";

    public bool Running
    {
        get => _running;
        private set
        {
            Set(ref _running, value);
            foreach (var command in new[] { CancelCommand, DoneCommand, RetryCommand, OverrideCommand, BackCommand }) command.Changed();
        }
    }

    public bool Succeeded { get => _succeeded; private set => Set(ref _succeeded, value); }
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public bool CanOverride => _code is not null && Overridable.Contains(_code);

    /// <summary>Where the job's files went, once it has ended: the download or patch folder, or the game it changed.</summary>
    public bool HasFolder => !_running && _folderExists;
    public string OpenFolderLabel => _kind is "download" or "patch" ? "Open the folder" : "Open the game folder";

    /// <summary>What the job made, as a shared build: for a download, a downgrade, a patch or a folder applied, once it has worked.</summary>
    public bool CanShare => !_running && _succeeded && _shared is not null;

    async Task StartAsync()
    {
        if (_main.Steam is not { } steam) return;
        _cancel = new CancellationTokenSource();
        Running = true;
        Succeeded = false;
        Failed = false;
        Message = null;
        _code = null;
        _folder = null;
        _folderExists = false;
        _shared = null;
        Raise(nameof(CanOverride));
        Raise(nameof(HasFolder));
        Raise(nameof(CanShare));
        Stage = "Starting";
        Detail = null;
        Indeterminate = true;

        var job = new GuiJob(_kind, _settings, _main.Options, this, _cancel.Token);
        try
        {
            await Task.Run(() => _kind == "login"
                ? SignInAsync(job, steam)
                : Actions.RunAsync(job, steam, _game, _kind));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            job.Fail(ExitCode.Failed, e is OperationCanceledException ? "cancelled" : "failed", e.Message);
        }

        var result = job.Result;
        _main.RefreshAccount();
        Running = false;
        if (result["ok"]?.GetValue<bool>() == true)
        {
            Succeeded = true;
            Stage = "Done";
            Indeterminate = false;
            Fraction = 1;
            Detail = null;
            lock (job.Lines) Message = job.Lines.LastOrDefault(l => l.StartsWith("Done.", StringComparison.Ordinal))?["Done.".Length..].Trim()
                                       ?? job.Lines.LastOrDefault(l => l.Length > 0) ?? "Done.";
        }
        else
        {
            Failed = true;
            _code = result["error"]?["code"]?.GetValue<string>();
            Stage = _code == "cancelled" ? "Stopped" : "Did not finish";
            Indeterminate = false;
            Message = result["error"]?["message"]?.GetValue<string>();
        }
        _folder = _kind switch
        {
            "login" => null,
            "download" or "patch" => result["folder"]?.GetValue<string>() ?? _settings.To,
            _ => _game.Installed?.InstallDir,
        };
        var folderToCheck = _folder;
        _folderExists = folderToCheck is not null && await Task.Run(() => Directory.Exists(folderToCheck));
        _shared = result["shared"]?.GetValue<string>() is { } text ? SharedBuild.Read(text, out _) : null;
        Raise(nameof(CanOverride));
        Raise(nameof(HasFolder));
        Raise(nameof(CanShare));
        RetryCommand.Changed();
        OverrideCommand.Changed();
        BackCommand.Changed();
        OpenFolderCommand.Changed();
        ShareCommand.Changed();
    }

    /// <summary>
    /// Signing in from the window: DepotDownloader asks its questions in a console window of its own ("CODDowngrader login"),
    /// since this process has no console to ask them in.
    /// </summary>
    static async Task<int> SignInAsync(GuiJob job, Steam.SteamInstall steam)
    {
        if (Login.Probe(job, steam) is not { } probe)
            return job.Fail(ExitCode.Usage, "no-probe", "Signing in needs a game to ask Steam about, and no Call of Duty is installed here.");
        if (await Login.WindowAsync(job, probe) is not { } account) return job.Reported ?? (int)ExitCode.SignIn;
        job.Line($"Done. Signed in to Steam as {account}.");
        job.Set("account", account);
        return job.Ok();
    }

    public void Progress(JobProgress progress)
    {
        // Progress posted while the job ran can arrive after it ended; the outcome is what the page shows by then.
        if (!_running) return;
        Stage = progress.Stage;
        Detail = progress.Detail;
        Indeterminate = progress.Fraction is null;
        if (progress.Fraction is { } fraction) Fraction = fraction;
    }

    public void Log(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }
}
