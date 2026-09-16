namespace CODDowngrader.Gui.ViewModels;

/// <summary>What the window shows when there is nothing to work with yet: Steam not found, or no Call of Duty in it.</summary>
public sealed class SetupPageViewModel : Observable
{
    readonly MainViewModel _main;

    public SetupPageViewModel(MainViewModel main, bool noGames = false)
    {
        _main = main;
        Title = noGames ? "No Call of Duty here yet" : "Steam was not found";
        Text = noGames
            ? "Steam is here, and no Call of Duty from it is. Every Call of Duty you own can still be downloaded: pick one under Other Call of Duty games."
            : "COD Downgrader looks for Steam where Steam installs itself. Choose the folder Steam is installed in, the one with steam.exe or steamapps in it.";
        ShowChoose = !noGames;
        ChooseCommand = new Command(ChooseAsync);
    }

    public string Title { get; }
    public string Text { get; }
    public bool ShowChoose { get; }
    public Command ChooseCommand { get; }

    async Task ChooseAsync()
    {
        if (await _main.Platform.PickFolderAsync("The folder Steam is installed in", null) is not { } folder) return;
        _main.Options.SteamRoot = folder;
        await _main.LoadAsync();
    }
}
