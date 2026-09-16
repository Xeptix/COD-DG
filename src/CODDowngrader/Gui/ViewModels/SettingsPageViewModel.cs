using CODDowngrader.App;
using CODDowngrader.Catalog;
using CODDowngrader.Download;
using CODDowngrader.Jobs;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>Steam, the sign-in, what is remembered, and the logs.</summary>
public sealed class SettingsPageViewModel : Observable, IHasBack
{
    readonly MainViewModel _main;
    bool _confirmForget;
    int _remembered;

    public SettingsPageViewModel(MainViewModel main)
    {
        _main = main;
        _remembered = main.Library?.Remembered.Rows.Count ?? Remembered.Load().Rows.Count;

        BackCommand = new Command(() => _main.Back());
        ChangeSteamCommand = new Command(ChangeSteamAsync);
        SignInCommand = new Command(() => SignIn(qr: false), () => AnyGame is not null);
        SignInOtherCommand = new Command(() => SignIn(qr: true), () => AnyGame is not null);
        OpenFolderCommand = new Command(() => _main.Platform.Open(AppState.Folder));
        LogsCommand = new Command(() => _main.Platform.Open(AppState.LogsFolder));
        ForgetCommand = new Command(ForgetAsync, () => _remembered > 0);
    }

    public string SteamFolder => _main.Steam?.Root ?? "Not found";
    public string Account => _main.Account;

    public string DepotDownloader => _main.Options.DepotDownloaderPath
                                     ?? DepotDownloaderTool.FindInstalled(AppState.Folder)
                                     ?? $"Fetched from SteamRE's GitHub release the first time a download needs it (version {DepotDownloaderTool.Version}).";

    public string RememberedText => $"{_remembered} manifest{(_remembered == 1 ? "" : "s")} remembered on this PC, in {Remembered.DefaultPath}";

    public string? RememberNote => Remembered.Writing
        ? null
        : "This run remembers nothing new: COD Downgrader was started with --no-remember. What is remembered already is still used.";

    public bool HasRememberNote => RememberNote is not null;

    public bool ConfirmForget
    {
        get => _confirmForget;
        private set
        {
            Set(ref _confirmForget, value);
            Raise(nameof(ForgetLabel));
        }
    }

    public string ForgetLabel => _confirmForget ? "Forget them all" : "Forget remembered manifests";

    GameEntry? AnyGame => _main.Library?.Entries.FirstOrDefault(e => e.Installed is not null) ?? _main.Library?.Entries.FirstOrDefault();

    public Command BackCommand { get; }
    public Command ChangeSteamCommand { get; }
    public Command SignInCommand { get; }
    public Command SignInOtherCommand { get; }
    public Command OpenFolderCommand { get; }
    public Command LogsCommand { get; }
    public Command ForgetCommand { get; }

    void SignIn(bool qr)
    {
        if (AnyGame is not { } game) return;
        _main.Show(new RunPageViewModel(_main, game, "login", new JobSettings { Qr = qr }, qr ? "Signing in to Steam with another account" : "Signing in to Steam"));
    }

    async Task ChangeSteamAsync()
    {
        if (await _main.Platform.PickFolderAsync("The folder Steam is installed in", _main.Steam?.Root) is not { } folder) return;
        _main.Options.SteamRoot = folder;
        await _main.LoadAsync();
    }

    async Task ForgetAsync()
    {
        // Two presses: the first only asks.
        if (!ConfirmForget)
        {
            ConfirmForget = true;
            return;
        }
        try
        {
            File.Delete(Remembered.DefaultPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        ConfirmForget = false;
        _remembered = 0;
        Raise(nameof(RememberedText));
        ForgetCommand.Changed();
        await _main.LoadAsync(_main.Library?.Entries.FirstOrDefault()?.AppId);
    }
}
