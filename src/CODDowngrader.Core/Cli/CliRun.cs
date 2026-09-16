using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Jobs;

namespace CODDowngrader.Cli;

/// <summary>What a command may do about signing in to Steam.</summary>
public enum LoginMode
{
    /// <summary>Use the signed-in account, and open a login window when there is none.</summary>
    Auto,

    /// <summary>Use the signed-in account, and fail when there is none.</summary>
    Saved,

    /// <summary>Open a login window before anything else.</summary>
    Window,

    /// <summary>Do nothing that needs an account.</summary>
    Never,
}

/// <summary>
/// A job run from the command line. Text goes to the console line by line unless --json was asked for, and then one object
/// is printed at the end instead, whether the command worked or not.
/// </summary>
public sealed class CliRun : Job
{
    public CliRun(ParsedCommand command, Options options)
        : base(command.Name, SettingsOf(command), options, CancellationToken.None)
    {
        Command = command;
        Json = command.Flag("json");
    }

    public ParsedCommand Command { get; }
    public bool Json { get; }

    public override bool CanPromptHere => !Json && !Console.IsInputRedirected && Environment.UserInteractive;

    public override TextWriter? ToolOutput => Json ? Console.Error : null;

    /// <summary>A line of the human report. Nothing is printed when the caller asked for JSON.</summary>
    public override void Line(string text = "")
    {
        if (!Json) Console.WriteLine(text);
    }

    /// <summary>A line of the human report that says something did not go to plan. Nothing is printed when the caller asked for JSON.</summary>
    public override void Warn(string text)
    {
        if (!Json) Console.Error.WriteLine(text);
    }

    protected override void Failed(string message)
    {
        if (!Json) Console.Error.WriteLine(message);
    }

    protected override void Finished(JsonObject result)
    {
        if (Json) Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>What the command line asks the job to do.</summary>
    static JobSettings SettingsOf(ParsedCommand command) => new()
    {
        Build = command.Value("build"),
        At = command.Value("at"),
        Manifests = command.Values("manifest"),
        To = command.Value("to"),
        // apply takes its folder as the argument after the game as well.
        From = command.Value("from") ?? (command.Name.Equals("apply", StringComparison.OrdinalIgnoreCase) ? command.Arguments.Skip(1).FirstOrDefault() : null),
        Only = command.Value("only") ?? command.Value("part"),
        Files = command.Value("files"),
        Exe = command.Value("exe"),
        Backup = command.Value("backup"),
        Plan = command.Flag("plan"),
        Yes = command.Flag("yes"),
        Again = command.Flag("again"),
        Siblings = command.Flag("siblings"),
        NoSeed = command.Flag("no-seed"),
        Delete = command.Flag("delete"),
        Username = command.Value("username"),
        Qr = command.Flag("qr"),
        Login = (command.Value("login") ?? "auto").ToLowerInvariant() switch
        {
            "saved" => LoginMode.Saved,
            "window" => LoginMode.Window,
            "never" or "no" => LoginMode.Never,
            _ => LoginMode.Auto,
        },
        ProbeApp = command.AppId("app"),
        ProbeDepot = command.AppId("depot"),
        ProbeManifest = ulong.TryParse(command.Values("manifest").LastOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var manifest)
            ? manifest
            : null,
    };
}
