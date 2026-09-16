using CODDowngrader.App;
using CODDowngrader.Steam;
using Spectre.Console;

namespace CODDowngrader.Cli;

/// <summary>The command line: what each command is, and what it needs before it runs.</summary>
public static class Dispatch
{
    static readonly string[] KnownCommands =
    {
        "list", "builds", "status", "download", "ingame", "patch", "apply", "undo", "share", "history", "login", "export", "help", "cli",
    };

    /// <summary>Which of the tool's faces a command line asks for.</summary>
    public enum Face
    {
        /// <summary>A command ran, and <see cref="Result.Code"/> is its exit code.</summary>
        Command,

        /// <summary>The window: nothing but options on the command line.</summary>
        Window,

        /// <summary>The menus in the console: "CODDowngrader cli".</summary>
        Menus,
    }

    /// <summary>What a command line asked for: an exit code when it was a command, or the face to open and its options.</summary>
    public sealed record Result(int? Code, Options Options, Face Face = Face.Command);

    /// <summary>
    /// Which face a command line asks for, decided before anything runs: the window needs no console, and the menus and the
    /// commands need one first. A command line that does not parse is a command, so its error is printed.
    /// </summary>
    public static Face FaceOf(string[] args)
    {
        var parsed = CommandLine.Parse(args, out var error);
        if (error is not null) return Face.Command;
        if (parsed is null) return Face.Window;
        if (parsed.Name.Equals("cli", StringComparison.OrdinalIgnoreCase)) return Face.Menus;
        if (parsed.Name.Length > 0) return Face.Command;
        return parsed.Has("list") || parsed.Has("export") || parsed.Has("help") || parsed.Has("version") || parsed.Has("password")
            ? Face.Command
            : Face.Window;
    }

    public static async Task<Result> RunAsync(string[] args)
    {
        var menus = new Options();
        var parsed = CommandLine.Parse(args, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            return new Result((int)ExitCode.Usage, menus);
        }
        if (parsed is null) return new Result(null, menus, Face.Window);

        if (parsed.Flag("no-color")) AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = ColorSystemSupport.NoColors });
        if (parsed.Has("password"))
        {
            Console.Error.WriteLine("COD Downgrader never takes a password. Run \"CODDowngrader login\" and sign in to DepotDownloader's own prompt, then run this again.");
            return new Result((int)ExitCode.Usage, menus);
        }
        if (parsed.Has("version") && parsed.Name.Length == 0)
        {
            Console.WriteLine(AppState.Version);
            return new Result((int)ExitCode.Ok, menus);
        }
        if (parsed.Has("help") || parsed.Name == "help")
        {
            Console.WriteLine(CommandLine.Help(AppState.Version));
            return new Result((int)ExitCode.Ok, menus);
        }

        var options = new Options
        {
            SteamRoot = parsed.Value("steam"),
            DepotDownloaderPath = parsed.Value("depotdownloader"),
            AppId = parsed.AppId("game"),
        };

        // Before commands, --list and --export were the whole non-interactive side. They still are what they were.
        if (parsed.Name.Length == 0)
        {
            if (parsed.Has("list")) return new Result(await OneAsync(new ParsedCommand { Name = "list", Arguments = parsed.Arguments, Options = parsed.Options }, options), options);
            if (parsed.Has("export")) return new Result(await OneAsync(new ParsedCommand { Name = "export", Arguments = parsed.Values("export"), Options = parsed.Options }, options), options);
            return new Result(null, options, Face.Window);
        }

        if (parsed.Name.Equals("cli", StringComparison.OrdinalIgnoreCase)) return new Result(null, options, Face.Menus);

        if (!KnownCommands.Contains(parsed.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"\"{parsed.Name}\" is not a command.");
            Console.Error.WriteLine(CommandLine.Commands);
            return new Result((int)ExitCode.Usage, options);
        }

        return new Result(await OneAsync(parsed, options), options);
    }

    static async Task<int> OneAsync(ParsedCommand command, Options options)
    {
        var run = new CliRun(command, options);
        try
        {
            var code = await CommandAsync(run, command);
            code = run.Reported ?? code;
            if (command.Name.Equals("login", StringComparison.OrdinalIgnoreCase) && command.Value("result") is { } result)
            {
                // Opened as a sign-in window: say how it went, and keep a failure on screen until it has been read.
                try
                {
                    File.WriteAllText(result, code.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
                if (code != 0 && !Console.IsInputRedirected)
                {
                    Console.WriteLine("Press Enter to close this window.");
                    Console.ReadLine();
                }
            }
            return code;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                      or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            return run.Fail(ExitCode.Failed, "failed", $"{e.GetType().Name}: {e.Message}");
        }
    }

    static async Task<int> CommandAsync(CliRun run, ParsedCommand command)
    {
        if (run.Steam(out var exit) is not { } steam) return exit;

        switch (command.Name.ToLowerInvariant())
        {
            case "list":
                return ReadCommands.List(run, steam);

            case "export":
                return ReadCommands.Export(run, steam, command.Arguments.FirstOrDefault() ?? command.Value("to") ?? "");

            case "login":
                return await Login.RunAsync(run, steam);

            case "status":
            {
                if (command.Arguments.Count == 0 && command.Value("game") is null) return ReadCommands.Status(run, steam, null);
                return Game(run, steam, command) is { } game ? ReadCommands.Status(run, steam, game) : (int)ExitCode.Usage;
            }

            case "share":
                return ShareCommand.Run(run, steam, command);

            case "history":
            {
                if (command.Arguments.Count == 0 && command.Value("game") is null) return ReadCommands.History(run, steam, null);
                return Game(run, steam, command) is { } game ? ReadCommands.History(run, steam, game) : (int)ExitCode.Usage;
            }

            case "builds":
                return Game(run, steam, command) is { } forBuilds ? ReadCommands.Builds(run, steam, forBuilds) : (int)ExitCode.Usage;

            default:
                if (command.Value("shared") is { } sharedPath)
                {
                    var name = command.Name.ToLowerInvariant();
                    if (name is not ("download" or "ingame" or "patch"))
                        return run.Fail(ExitCode.Usage, "no-build", "--shared goes with download, ingame and patch.");
                    return ShareCommand.Use(run, steam, command, sharedPath) is { } shared
                        ? await Actions.RunAsync(run, steam, shared, name)
                        : run.Reported ?? (int)ExitCode.Usage;
                }
                return Game(run, steam, command) is { } chosen
                    ? await Actions.RunAsync(run, steam, chosen, command.Name.ToLowerInvariant())
                    : (int)ExitCode.Usage;
        }
    }

    /// <summary>The game the command line names, as its first argument or with --game. Null once the failure has been reported.</summary>
    static GameEntry? Game(CliRun run, SteamInstall steam, ParsedCommand command)
    {
        var library = GameLibrary.Load(steam);
        var text = command.Arguments.FirstOrDefault() ?? command.Value("game");
        var game = Selectors.Game(library, text, out var error);
        if (game is null) run.Fail(ExitCode.Usage, "no-game", error!);
        return game;
    }
}
