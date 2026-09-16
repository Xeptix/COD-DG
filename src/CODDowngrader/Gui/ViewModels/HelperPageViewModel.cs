using System.Collections.ObjectModel;
using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Catalog;
using CODDowngrader.Cli;
using CODDowngrader.Jobs;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>One depot the helper needs a manifest for: its SteamDB page, and whatever was pasted for it.</summary>
public sealed class NeedRow : Observable
{
    readonly DateTimeOffset? _moment;
    readonly Action _changed;
    string _text = "";

    public NeedRow(uint depot, string name, DateTimeOffset? moment, bool optional, Action changed)
    {
        Depot = depot;
        Name = name;
        Url = BuildTimeline.SteamDbManifests(depot);
        _moment = moment;
        Optional = optional;
        _changed = changed;
        Resolve();
    }

    public uint Depot { get; }
    public string Name { get; }
    public string Url { get; }
    public bool Optional { get; }

    public IReadOnlyList<PastedManifest> Pasted { get; private set; } = Array.Empty<PastedManifest>();
    public PastedManifest? Chosen { get; private set; }
    public string Status { get; private set; } = "";
    public bool IsResolved => Chosen is not null;

    /// <summary>Text was pasted, and nothing usable came of it.</summary>
    public bool IsWrong => _text.Trim().Length > 0 && Chosen is null;

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            Resolve();
            _changed();
        }
    }

    void Resolve()
    {
        Pasted = SteamDbPaste.Read(_text);
        Chosen = SteamDbPaste.At(Pasted, _moment);
        var day = _moment?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        Status = Chosen is { } chosen
            ? $"Uses {chosen.Manifest}" + (chosen.FirstSeen is { } seen ? $", first seen on SteamDB {seen.ToLocalTime():d MMM yyyy}" : "")
            : _text.Trim().Length == 0
                ? Optional ? "Leave it empty and this depot keeps the build it is on." : "Open the page, copy the manifest rows, and paste them here."
                : Pasted.Count == 0
                    ? "No manifest ID in that."
                    : day is not null && Pasted.Any(p => p.FirstSeen is not null)
                        ? $"Nothing pasted was first seen on or before {day}."
                        : "Paste one manifest ID, or the rows with their dates.";
        Raise(nameof(Status));
        Raise(nameof(IsResolved));
        Raise(nameof(IsWrong));
    }
}

/// <summary>
/// Getting manifests from SteamDB into the tool: the pages to open, and a box for each depot to paste into. Nothing is fetched.
/// Everything pasted is remembered, with SteamDB's dates where the text had them.
/// </summary>
public sealed class HelperPageViewModel : Observable, IHasBack
{
    readonly MainViewModel _main;
    readonly GamePageViewModel _page;
    readonly GameLibrary _library;
    readonly GameEntry _game;
    readonly DateTime? _date;

    HelperPageViewModel(MainViewModel main, GamePageViewModel page, GameLibrary library, GameEntry game, DateTime? date, string title, string explanation)
    {
        _main = main;
        _page = page;
        _library = library;
        _game = game;
        _date = date;
        Title = title;
        Explanation = explanation;

        OpenCommand = new Command(row => { if (row is NeedRow r) _main.Platform.Open(r.Url); return Task.CompletedTask; });
        CopyCommand = new Command(row => row is NeedRow r ? _main.Platform.CopyAsync(r.Url) : Task.CompletedTask);
        CopyAllCommand = new Command(() => _main.Platform.CopyAsync(string.Join(Environment.NewLine, Rows.Select(r => $"{r.Depot}  {r.Name}  {r.Url}"))));
        ContinueCommand = new Command(Continue, () => CanContinue);
        BackCommand = new Command(() => _main.Back());
    }

    public static HelperPageViewModel ForDate(MainViewModel main, GamePageViewModel page, GameLibrary library, GameEntry game, DateTime date,
        IReadOnlyList<NeededManifest> needs, string explanation)
    {
        var helper = new HelperPageViewModel(main, page, library, game, date, $"{game.Name} on {date:d MMM yyyy}", explanation);
        var moment = Selectors.Moment(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var need in needs)
            helper.Rows.Add(new NeedRow(need.Depot, library.DepotName(game, need.Depot), moment, optional: false, helper.Changed));
        return helper;
    }

    public static HelperPageViewModel ForManifests(MainViewModel main, GamePageViewModel page, GameLibrary library, GameEntry game)
    {
        var helper = new HelperPageViewModel(main, page, library, game, null, $"{game.Name}: manifests from SteamDB",
            "For each depot you want on another build, open its SteamDB page, copy the manifest you want (or the rows around it) and paste it here. Depots left empty keep the build they are on.");
        foreach (var depot in game.Owners.Keys.OrderBy(d => d))
            helper.Rows.Add(new NeedRow(depot, library.DepotName(game, depot), null, optional: true, helper.Changed));
        return helper;
    }

    public string Title { get; }
    public string Explanation { get; }
    public ObservableCollection<NeedRow> Rows { get; } = new();

    public Command OpenCommand { get; }
    public Command CopyCommand { get; }
    public Command CopyAllCommand { get; }
    public Command ContinueCommand { get; }
    public Command BackCommand { get; }

    bool CanContinue => Rows.All(r => !r.IsWrong) && (_date is null ? Rows.Any(r => r.IsResolved) : Rows.All(r => r.IsResolved));

    void Changed() => ContinueCommand.Changed();

    void Continue()
    {
        // Everything pasted is remembered: the dated rows as SteamDB's, a bare ID as named by hand.
        _library.Remembered.Learn(Rows.SelectMany(r => r.Pasted
            .Where(p => p.FirstSeen is not null)
            .Select(p => new RememberedManifest(r.Depot, p.Manifest, RememberedSource.SteamDb, p.FirstSeen))
            .Concat(r.Chosen is { FirstSeen: null } bare ? new[] { new RememberedManifest(r.Depot, bare.Manifest, RememberedSource.Named) } : Array.Empty<RememberedManifest>())));

        var manifests = Rows.Where(r => r.Chosen is not null).Select(r => $"{r.Depot}={r.Chosen!.Manifest}").ToList();
        BuildItem item;
        if (_date is { } date)
        {
            var at = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            item = new BuildItem(at, $"The build of {date:d MMM yyyy}", $"{manifests.Count} manifests from SteamDB", null, BuildKind.Previous,
                new JobSettings { At = at, Manifests = manifests });
        }
        else
        {
            item = new BuildItem("manifests", "Manifests from SteamDB", string.Join(", ", manifests), null, BuildKind.Previous,
                new JobSettings { Manifests = manifests, Label = "Manifests from SteamDB" });
        }
        _main.Back();
        _page.Choose(item);
    }
}
