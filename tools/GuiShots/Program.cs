using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using CODDowngrader.App;
using CODDowngrader.Catalog;
using CODDowngrader.Gui;
using CODDowngrader.Gui.ViewModels;
using CODDowngrader.Gui.Views;
using CODDowngrader.Jobs;

// dotnet run --project tools/GuiShots -- <folder> [appid]: every page of the window as a PNG, light and dark.
var folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "coddg-shots");
var appId = args.Length > 1 ? uint.Parse(args[1]) : 202990u;
Directory.CreateDirectory(folder);
Remembered.Writing = false;

AppBuilder.Configure<GuiApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var window = new MainWindow { Width = 1100, Height = 760 };
var model = new MainViewModel(new Options(), new NoPlatform());
window.DataContext = model;
window.Show();

var load = model.LoadAsync(appId);
Pump(() => load.IsCompleted);
var game = (GamePageViewModel)model.Page!;
foreach (var entry in model.Library!.Entries.Where(e => e.Installed is not null))
    Console.WriteLine($"  icon: {entry.Name}: {entry.LaunchExe() ?? model.Library!.SteamIcon(entry) ?? "none"}{(model.IconOf(entry.AppId) is { } icon ? $", {icon.PixelSize.Width}px" : ", no icon")}");
Pump(() => !game.Loading);
Shot("1-game");
// The whole page, below the fold too, and at the narrowest the window goes.
window.Height = 1500;
Shot("1b-game-whole");
window.Width = 820;
Shot("1c-game-narrow");
window.Width = 1100;
window.Height = 760;

// The sidebar's filter narrows the list, and clearing it leaves the open page as it was.
model.Filter = "black ops";
Shot("1d-filter");
model.Filter = "zzz";
Shot("1e-filter-nothing");
model.Filter = "";
Console.WriteLine(ReferenceEquals(model.Page, game) ? "  filter: the open page stayed" : "  filter: THE PAGE WAS OPENED AGAIN");

// Esc goes back the way the page's Back button does.
model.Show(new SettingsPageViewModel(model));
window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
Pump(() => ReferenceEquals(model.Page, game), 5_000);
Console.WriteLine(ReferenceEquals(model.Page, game) ? "  escape: back on the game page" : "  escape: DID NOT GO BACK");

// Put an older build into the game: the page works out what changes first.
var older = game.Builds.First(b => !b.IsInstalled && b.Build is not null && b.Kind != CODDowngrader.Builds.BuildKind.Latest);
game.SelectedBuild = older;
var action = new ActionPageViewModel(model, game, "ingame", older);
model.Show(action);
Pump(() => !action.IsPlanning, 120_000);
Shot("2-ingame-plan");
action.Part = 3;
foreach (var folderChoice in action.Folders) folderChoice.IsChecked = true;
Shot("3-ingame-choose-files");
model.Back();

var download = new ActionPageViewModel(model, game, "download", older);
model.Show(download);
Pump(() => !download.IsPlanning, 120_000);
Shot("3b-download");
model.Back();

var patchPage = new ActionPageViewModel(model, game, "patch", older);
model.Show(patchPage);
Pump(() => !patchPage.IsPlanning, 120_000);
Shot("3c-patch");
model.Back();

// Several files in one folder, as Black Ops II's exes are, given to the page as a plan: the file list is open at once.
var oneFolder = new ActionPageViewModel(model, game, "apply", null);
typeof(ActionPageViewModel).GetMethod("Take", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(oneFolder, new object[]
{
    JsonNode.Parse("""{ "ok": true, "plan": { "files": [ { "name": "t6mp.exe", "size": 13600000 }, { "name": "t6zm.exe", "size": 12900000 }, { "name": "t6sp.exe", "size": 11800000 } ], "remove": [] } }""")!.AsObject(),
});
oneFolder.Part = 3;
oneFolder.Folders[0].Files[1].IsChecked = false;
model.Show(oneFolder);
Shot("3d-one-folder");
model.Back();

// A date nothing on this PC reaches back to: the SteamDB helper.
game.AtDate = new DateTime(2010, 6, 1);
game.FindAtCommand.Execute(null);
Pump(() => model.Page is HelperPageViewModel);
var helper = (HelperPageViewModel)model.Page!;
helper.Rows[0].Text = "3 September 2026 – 16:03:08 UTC\t13 days ago\t1662081121680080981\n26 April 2008 – 17:04:52 UTC\t18 years ago\t5022752412624643985";
helper.Rows[1].Text = "not a manifest";
Shot("4-helper");
model.Back();

// A job that ends at once: undoing where nothing was written in.
var run = new RunPageViewModel(model, game.Game, "undo", new JobSettings(), "Undo the downgrade");
model.Show(run);
Pump(() => !run.Running);
run.ShowLog = true;
Shot("5-run-failed");
model.Back();

// With "run" as the third argument: a real patch job into a folder under the shots, to see progress come through.
if (args.Length > 2 && args[2] == "run")
{
    var into = Path.Combine(folder, "patch-run");
    var job = new RunPageViewModel(model, game.Game, "patch", new JobSettings { Build = older.Key, To = into, Yes = true }, "Saving a patch folder");
    model.Show(job);
    Pump(() => job.Stage.StartsWith("Downloading", StringComparison.Ordinal) && !job.Indeterminate && job.Fraction > 0 || !job.Running, 300_000);
    if (job.Running) Shot("10a-run-downloading");
    Pump(() => !job.Running, 600_000);
    job.ShowLog = true;
    Shot("10b-run-done");
    Console.WriteLine($"  run: {job.Stage}: {job.Message}");
    model.Back();
    try { Directory.Delete(into, recursive: true); } catch (IOException) { }
}

model.Show(new SettingsPageViewModel(model));
Shot("6-settings");
model.Back();

Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Shot("7-game-dark");

// A download part way, in the dark theme: the bar has to stand out from its track.
var progressRun = new RunPageViewModel(model, game.Game, "undo", new JobSettings(), "Saving a patch folder for Before the update of 11 Aug 2015");
model.Show(progressRun);
Pump(() => !progressRun.Running);
typeof(RunPageViewModel).GetProperty("Running")!.SetValue(progressRun, true);
typeof(RunPageViewModel).GetProperty("Failed")!.SetValue(progressRun, false);
progressRun.Progress(new JobProgress("Downloading", "9.7 GB of 16.2 GB · depot 42683, 2 of 7: iw_11.iwd", 0.6));
typeof(RunPageViewModel).GetProperty("Message")!.SetValue(progressRun, null);
Shot("7c-run-progress-dark");
model.Back();
window.Height = 1500;
Shot("7b-game-whole-dark");
window.Height = 760;
model.Show(action);
Shot("8-ingame-dark");
model.Back();

// Sharing: the chosen version as a shared build, then that text pasted back in, with a chat's code fences, as exes and DLLs only.
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
game.ShareCommand.Execute(null);
Pump(() => model.Page is SharePageViewModel);
Shot("11-share");
var sharedText = ((SharePageViewModel)model.Page!).Text;
model.Back();

var open = new OpenBuildPageViewModel(model);
model.Show(open);
open.Text = "```\n" + sharedText + "only binaries\n```\n";
Shot("12-open-build");
var patchFolder = Path.Combine(folder, "a patch folder");
Directory.CreateDirectory(patchFolder);
CODDowngrader.Patching.PatchStore.SavePatch(patchFolder, new CODDowngrader.Patching.PatchRecord
{
    AppIds = new List<uint> { appId },
    Game = game.Name,
    Build = older.Title,
    Part = "exes and DLLs only",
    From = "Latest on Steam",
    Complete = true,
    TargetManifests = CODDowngrader.Patching.IdMap.Write(older.Build!.Manifests),
    Files = new List<CODDowngrader.Patching.PatchWrite> { new("t6mp.exe", 0, 13_260_288, "", null) },
});
var reading = open.UseFolderAsync(patchFolder);
Pump(() => reading.IsCompleted);
open.Text = "";
Shot("12b-open-build-folder");

// Your builds, from a list written for the shot: the patch folder above, a download whose folder has gone, and a downgrade taken out since.
var listPath = Path.Combine(folder, "builds.json");
File.Delete(listPath);
var sampleShared = SharedBuildFor(older);
CODDowngrader.App.MadeBuilds.Add(new CODDowngrader.App.MadeBuild { Kind = "ingame", Made = DateTimeOffset.Now.AddDays(-2), AppId = appId, Game = game.Name, Build = older.Title, Part = "content only", Folder = game.Game.Installed!.InstallDir, Shared = sampleShared }, listPath, writing: true);
CODDowngrader.App.MadeBuilds.Add(new CODDowngrader.App.MadeBuild { Kind = "download", Made = DateTimeOffset.Now.AddDays(-1), AppId = appId, Game = game.Name, Build = older.Title, Folder = Path.Combine(folder, "a download that was deleted"), Shared = sampleShared }, listPath, writing: true);
CODDowngrader.App.MadeBuilds.Add(new CODDowngrader.App.MadeBuild { Kind = "patch", Made = DateTimeOffset.Now, AppId = appId, Game = game.Name, Build = older.Title, Part = "exes and DLLs only", From = "Latest on Steam", Folder = patchFolder, Shared = sampleShared }, listPath, writing: true);
var buildsPage = new BuildsPageViewModel(model, listPath);
model.Show(buildsPage);
Pump(() => !buildsPage.Loading);
Shot("15-your-builds");
model.Back();

// Undo's confirmation, from a plan written for the shot: nothing is written into a game on this PC to undo for real.
var undoPage = new ActionPageViewModel(model, game, "undo", null);
model.Show(undoPage);
Pump(() => !undoPage.IsPlanning);
typeof(ActionPageViewModel).GetProperty("Error")!.SetValue(undoPage, null);
typeof(ActionPageViewModel).GetMethod("Take", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(undoPage, new object[]
{
    JsonNode.Parse("""
    {
      "ok": true,
      "change": { "now": "Before the update of 10 Feb 2015, exes and DLLs only", "after": "Installed now (build 515837)",
        "depots": [ { "depot": 202991, "name": "Call of Duty: Black Ops II MP EXE", "now": "8255716060272897409", "after": "4238126704581628502" } ],
        "unchanged": 0 },
      "plan": { "fromBackup": [ { "name": "t6mp.dll", "size": 900000 } ],
        "fromSteam": [ { "name": "t6mp.exe", "depot": 202991, "size": 13260288, "personalized": true } ],
        "delete": [], "steamChanged": [], "lost": [], "download": 13260288 }
    }
    """)!.AsObject(),
});
typeof(ActionPageViewModel).GetProperty("Planned")!.SetValue(undoPage, true);
Shot("16-undo");
model.Back();
open.Text = "```\n" + sharedText + "only binaries\n```\n";
open.ContinueCommand.Execute(null);
Pump(() => model.Page is GamePageViewModel { Loading: false } page && page.Builds.FirstOrDefault()?.Chosen == true, 120_000);
var sharedGame = (GamePageViewModel)model.Page!;
Shot("13-shared-chosen");
var sharedAction = new ActionPageViewModel(model, sharedGame, "ingame", sharedGame.SelectedBuild);
model.Show(sharedAction);
Pump(() => !sharedAction.IsPlanning, 120_000);
Shot("14-shared-ingame");
model.Back();

var missing = new MainViewModel(new Options { SteamRoot = Path.Combine(folder, "no-steam-here") }, new NoPlatform());
window.DataContext = missing;
var none = missing.LoadAsync();
Pump(() => none.IsCompleted);
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
Shot("9-no-steam");

Console.WriteLine($"Pages in {folder}");
return 0;

string SharedBuildFor(BuildItem build) =>
    CODDowngrader.Builds.SharedBuild.Of(game.Game, build.Build!.Manifests, build.Title, "binaries", null, false).Text();

void Pump(Func<bool> until, int timeout = 60_000)
{
    var clock = Stopwatch.StartNew();
    while (!until() && clock.ElapsedMilliseconds < timeout)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }
    Dispatcher.UIThread.RunJobs();
    if (!until()) Console.WriteLine("  (timed out waiting)");
}

void Shot(string name)
{
    for (var i = 0; i < 3; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(folder, name + ".png");
    frame?.Save(path);
    Console.WriteLine(frame is null ? $"{name}: no frame" : path);
}

sealed class NoPlatform : IPlatform
{
    public Task<string?> PickFolderAsync(string title, string? start) => Task.FromResult<string?>(null);
    public Task<string?> PickFileAsync(string title) => Task.FromResult<string?>(null);
    public Task<string?> SaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
    public Task CopyAsync(string text) => Task.CompletedTask;
    public void Open(string target) { }
}
