using System.Text.Json;
using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Steam;

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
/// One run of one command: what it was asked for, what it prints, and what it hands back. Text goes to the console line by
/// line unless --json was asked for, and then one object is printed at the end instead, whether the command worked or not.
/// </summary>
public sealed class CliRun
{
    readonly JsonObject _data = new();

    public CliRun(ParsedCommand command, Options options)
    {
        Command = command;
        Options = options;
        Json = command.Flag("json");
        Yes = command.Flag("yes");
        Username = command.Value("username");
        Login = (command.Value("login") ?? "auto").ToLowerInvariant() switch
        {
            "saved" => LoginMode.Saved,
            "window" => LoginMode.Window,
            "never" or "no" => LoginMode.Never,
            _ => LoginMode.Auto,
        };
    }

    public ParsedCommand Command { get; }
    public Options Options { get; }
    public bool Json { get; }
    public bool Yes { get; }
    public string? Username { get; }
    public LoginMode Login { get; }

    /// <summary>A line of the human report. Nothing is printed when the caller asked for JSON.</summary>
    public void Line(string text = "")
    {
        if (!Json) Console.WriteLine(text);
    }

    /// <summary>A line of the human report that says something did not go to plan. Nothing is printed when the caller asked for JSON.</summary>
    public void Warn(string text)
    {
        if (!Json) Console.Error.WriteLine(text);
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

    /// <summary>What this run has already reported, since a command says how it went exactly once.</summary>
    public int? Reported { get; private set; }

    public int Ok() => Finish(ExitCode.Ok, null, null);

    /// <param name="code">A short word a program can switch on, such as "signin" or "locked".</param>
    public int Fail(ExitCode exit, string code, string message)
    {
        // The first thing that went wrong is the one that gets reported: what follows it is its consequence.
        if (Reported is { } already) return already;
        if (!Json) Console.Error.WriteLine(message);
        return Finish(exit, code, message);
    }

    int Finish(ExitCode exit, string? code, string? message)
    {
        if (Reported is { } already) return already;
        Reported = (int)exit;
        if (!Json) return (int)exit;

        var result = new JsonObject
        {
            ["tool"] = $"COD Downgrader {AppState.Version}",
            ["command"] = Command.Name,
            ["ok"] = exit == ExitCode.Ok,
            ["exitCode"] = (int)exit,
        };
        if (code is not null) result["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        foreach (var (name, value) in _data) result[name] = value?.DeepClone();
        Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (int)exit;
    }

    /// <summary>Steam as the command line names it, or null once the failure has been reported.</summary>
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
