using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Cli;
using CODDowngrader.Steam;

namespace CODDowngrader.Jobs;

/// <summary>What a download, a downgrade, a patch, applying, undoing or signing in was asked to do, whichever face asked.</summary>
public sealed record JobSettings
{
    /// <summary>A build's key, as <see cref="Selectors.Keys"/> names it.</summary>
    public string? Build { get; init; }

    /// <summary>A date or a moment: the build the game had then. See <see cref="Selectors.Moment"/>.</summary>
    public string? At { get; init; }

    /// <summary>What to call the build instead of the name its manifests give it, as when a downgrade is written in again.</summary>
    public string? Label { get; init; }

    /// <summary>"depot=manifest" entries, each changing one depot of the build.</summary>
    public IReadOnlyList<string> Manifests { get; init; } = Array.Empty<string>();

    /// <summary>Where a download, a patch or an export goes.</summary>
    public string? To { get; init; }

    /// <summary>A patch's starting build, or the folder applying takes.</summary>
    public string? From { get; init; }

    /// <summary>all, content or binaries.</summary>
    public string? Only { get; init; }

    /// <summary>
    /// The language a download is in, as Steam's code names it (english, french...): each language depot of the build is that
    /// language's instead. Null for the languages the game has here. download, ingame and patch take it.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>File names, separated by commas, instead of <see cref="Only"/>.</summary>
    public string? Files { get; init; }

    /// <summary>steam or installed: which copy of an exe Steam personalizes goes in.</summary>
    public string? Exe { get; init; }

    /// <summary>Keep what a downgrade replaces. Only "no" turns it off.</summary>
    public string? Backup { get; init; }

    /// <summary>Work out what would change, report it, and change nothing.</summary>
    public bool Plan { get; init; }

    public bool Yes { get; init; }
    public bool Again { get; init; }
    public bool Siblings { get; init; }
    public bool NoSeed { get; init; }
    public bool Delete { get; init; }

    public LoginMode Login { get; init; } = LoginMode.Auto;
    public string? Username { get; init; }

    /// <summary>Sign in with a QR code even when an account name is saved.</summary>
    public bool Qr { get; init; }

    /// <summary>What a sign-in asks Steam about, when the caller names it.</summary>
    public uint? ProbeApp { get; init; }
    public uint? ProbeDepot { get; init; }
    public ulong? ProbeManifest { get; init; }
}

/// <summary>How far a job has got: a stage in words, and how much of it is done when that is known.</summary>
public sealed record JobProgress(string Stage, string? Detail = null, double? Fraction = null);

/// <summary>
/// One run of one job, and everything it reports: lines of a human report, warnings, progress, and the result a program or
/// the window reads. A job says how it went exactly once, and the first failure is the one reported. The command line
/// prints it (<see cref="CliRun"/>); the window shows it.
/// </summary>
public abstract class Job
{
    readonly JsonObject _data = new();

    protected Job(string name, JobSettings settings, Options options, CancellationToken cancel)
    {
        Name = name;
        Settings = settings;
        Options = options;
        Cancel = cancel;
    }

    /// <summary>The command this job is: download, ingame, patch, apply, undo, login.</summary>
    public string Name { get; }

    public JobSettings Settings { get; private set; }

    /// <summary>Settings worked out after the job was made, as a shared build names the build it is for.</summary>
    internal void Replace(JobSettings settings) => Settings = settings;
    public Options Options { get; }
    public CancellationToken Cancel { get; }

    public bool Yes => Settings.Yes;
    public string? Username => Settings.Username;
    public LoginMode Login => Settings.Login;

    /// <summary>Whether DepotDownloader may ask for a sign-in in this process's own console.</summary>
    public abstract bool CanPromptHere { get; }

    /// <summary>Where DepotDownloader's own output goes; null for this process's console.</summary>
    public abstract TextWriter? ToolOutput { get; }

    /// <summary>
    /// The bytes each depot of the download under way fetches. DepotDownloader's percentages are each depot's own, and with this
    /// they add up to one bar across the whole download. Null when it is not known.
    /// </summary>
    public IReadOnlyDictionary<uint, ulong>? DownloadSizes { get; set; }

    public abstract void Line(string text = "");

    public abstract void Warn(string text);

    public virtual void Progress(JobProgress progress)
    {
    }

    public void Set(string name, JsonNode? value) => _data[name] = value;

    public void Set(string name, string? value) => _data[name] = value is null ? null : JsonValue.Create(value);

    public JsonArray Array(string name)
    {
        if (_data[name] is JsonArray array) return array;
        var made = new JsonArray();
        _data[name] = made;
        return made;
    }

    /// <summary>What this job has already reported, since it says how it went exactly once.</summary>
    public int? Reported { get; private set; }

    /// <summary>The result, as the command line's --json prints it: the tool, the command, ok, the exit code, any error, and the data.</summary>
    public JsonObject Result { get; private set; } = new();

    public int Ok() => Finish(ExitCode.Ok, null, null);

    /// <param name="code">A short word a program can switch on, such as "signin" or "locked".</param>
    public int Fail(ExitCode exit, string code, string message)
    {
        // The first thing that went wrong is the one that gets reported: what follows it is its consequence.
        if (Reported is { } already) return already;
        Failed(message);
        return Finish(exit, code, message);
    }

    /// <summary>How a failure is told as it happens, before the result.</summary>
    protected abstract void Failed(string message);

    /// <summary>The job is over: the command line prints the result, the window shows it.</summary>
    protected abstract void Finished(JsonObject result);

    int Finish(ExitCode exit, string? code, string? message)
    {
        if (Reported is { } already) return already;
        Reported = (int)exit;

        var result = new JsonObject
        {
            ["tool"] = $"COD Downgrader {AppState.Version}",
            ["command"] = Name,
            ["ok"] = exit == ExitCode.Ok,
            ["exitCode"] = (int)exit,
        };
        if (code is not null) result["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        foreach (var (key, value) in _data) result[key] = value?.DeepClone();
        Result = result;
        Finished(result);
        return (int)exit;
    }

    /// <summary>Steam as the options name it, or null once the failure has been reported.</summary>
    public SteamInstall? Steam(out int exit)
    {
        var steam = SteamInstall.Find(Options.SteamRoot);
        if (steam is not null)
        {
            exit = 0;
            return steam;
        }
        exit = Fail(ExitCode.Failed, "no-steam", "Steam was not found. Pass its folder with --steam.");
        return null;
    }
}
