using System.Globalization;

namespace CODDowngrader.Cli;

/// <summary>What a command ended as. The exit code a script reads.</summary>
public enum ExitCode
{
    Ok = 0,

    /// <summary>Something went wrong: Steam not found, a download that did not finish, a file that could not be written.</summary>
    Failed = 1,

    /// <summary>The command line itself: an unknown command, a missing option, a build that names nothing.</summary>
    Usage = 2,

    /// <summary>DepotDownloader has no signed-in account. Run "CODDowngrader login", or allow a login window.</summary>
    SignIn = 3,

    /// <summary>A file the command has to write is in use, usually because the game is running.</summary>
    Locked = 4,

    /// <summary>The signed-in account does not own something the command needs.</summary>
    NotOwned = 5,
}

/// <summary>One command line: the command's name, what came after it, and the options.</summary>
public sealed class ParsedCommand
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required IReadOnlyDictionary<string, List<string>> Options { get; init; }

    public bool Has(string option) => Options.ContainsKey(option);

    /// <summary>The option's last value, or null when it was not given.</summary>
    public string? Value(string option) => Options.TryGetValue(option, out var values) && values.Count > 0 ? values[^1] : null;

    public IReadOnlyList<string> Values(string option) => Options.TryGetValue(option, out var values) ? values : Array.Empty<string>();

    public bool Flag(string option)
    {
        if (!Options.TryGetValue(option, out var values)) return false;
        var value = values.Count > 0 ? values[^1] : null;
        return value is null || !(value.Equals("no", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    public uint? AppId(string option) =>
        Value(option) is { } text && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>
/// The command line: "CODDowngrader &lt;command&gt; [arguments] [--option value]". Options may be written as "--option value" or
/// "--option=value", and an option with no value is a flag. Everything after "--" is an argument, however it starts.
/// </summary>
public static class CommandLine
{
    /// <summary>Options that take a value, so "--to X" reads X as the value rather than as an argument.</summary>
    static readonly HashSet<string> TakesValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "depotdownloader", "game", "build", "at", "manifest", "from", "to", "only", "files", "exe", "backup",
        "login", "username", "app", "depot", "out", "part", "result", "shared", "language", "find",
    };

    /// <summary>Null when <paramref name="args"/> names no command; <paramref name="error"/> says what was wrong when it is set.</summary>
    public static ParsedCommand? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var name = "";
        var arguments = new List<string>();
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var onlyArguments = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (onlyArguments || !arg.StartsWith("-", StringComparison.Ordinal) || arg.Length == 1)
            {
                if (name.Length == 0 && arguments.Count == 0 && !onlyArguments) name = arg;
                else arguments.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyArguments = true;
                continue;
            }

            var option = arg.TrimStart('-');
            string? value = null;
            if (option.IndexOf('=') is var cut and >= 0)
            {
                value = option[(cut + 1)..];
                option = option[..cut];
            }
            option = Alias(option);
            if (option.Length == 0)
            {
                error = $"{arg} is not an option.";
                return null;
            }

            if (value is null && TakesValue.Contains(option))
            {
                if (i + 1 >= args.Count)
                {
                    error = $"--{option} needs a value.";
                    return null;
                }
                value = args[++i];
            }

            if (!options.TryGetValue(option, out var values)) options[option] = values = new List<string>();
            if (value is not null) values.Add(value);
        }

        if (name.Length == 0 && options.Count == 0 && arguments.Count == 0) return null;
        return new ParsedCommand { Name = name, Arguments = arguments, Options = options };
    }

    static string Alias(string option) => option.ToLowerInvariant() switch
    {
        "h" or "?" => "help",
        "v" => "version",
        "y" => "yes",
        "no-colour" => "no-color",
        "dir" or "folder" => "to",
        "account" => "username",
        "lang" => "language",
        _ => option.ToLowerInvariant(),
    };

    public const string Commands = """
        Commands:
          list                      Steam, the games, and every build known for them
          builds <game>             The builds of one game, with the name each one takes on this command line
          status [<game>]           What is installed, and any build written into a game folder
          download <game>           Download a whole build into a folder of its own
          ingame <game>             Put a build into the installed game, downloading only what differs
          patch <game>              Save a patch folder: the files that turn one build into another
          apply <game>              Put a patch folder or a downloaded build into the installed game
          undo <game>               Take a build back out of the installed game, from the backup, and
                                    from Steam for any file the backup does not hold
          history [<game>]          Every download, downgrade and patch folder made on this PC, and
                                    where each is now. --find <folder> first adds the downloads and
                                    patch folders in that folder and the folders under it
          share <game>              A build as text to hand to someone who owns the game: the one
                                    --build, --at or --manifest names, a patch folder or downloaded
                                    build with --from, or else the downgrade written into the game
          login                     Sign DepotDownloader in to Steam, in this window
          export [<path>]           A zip of what this PC knows about the builds, for the built-in list
        """;

    public static string Help(string version) => $"""
        COD Downgrader {version} by Xep
        Download any build of a Call of Duty you own on Steam, into your installed game or into
        a folder of its own.
        https://github.com/Xeptix/COD-DG

        Usage: CODDowngrader                      the window
               CODDowngrader cli                  the menus, in this console
               CODDowngrader <command> [options]  one command, no questions asked

        {Commands}

        Naming a game: its Steam app ID (311210), or part of its name ("black ops iii").

        Naming a build:
          --build 2026-09-10        the build before the update of that day, as the menus name it
          --build latest            what Steam has now
          --build installed         what is in the game folder now
          --at 2015-03-12           the build the game had at the end of that day, or at a time
                                    with 2015-03-12T18:30. When that is not known here, it lists
                                    the SteamDB pages to take each depot's manifest from
          --manifest 311211=9084453472036406216
                                    one depot's manifest, repeatable, for a build the list
                                    does not know. Every depot not named keeps its build.
          --shared <file>           download, ingame, patch: the build someone shared, with the
                                    files they chose. - reads it from standard input

        Options:
          --to <folder>             Where a download, a patch, an export or a shared build goes
          --from <build|folder>     patch: the build it starts from. apply: the folder to take
          --language <language>     download, builds: the build in another language Steam has for
                                    the game, by Steam's name for it: english, french, german,
                                    spanish, italian, russian, polish, japanese, brazilian, schinese...
                                    builds --json lists each game's
          --only <what>             all (default), content (keep your exes and DLLs), or binaries
          --files <name,name>       Only these files of the build, instead of --only
          --exe <steam|installed>   Which copy of an exe Steam personalizes for your account
          --backup <yes|no>         Keep the files a downgrade replaces, so undo can put them back
          --plan                    download, ingame, patch, apply, undo: work out and print what would
                                    change, file by file with --json, and change nothing
          --again                   ingame, apply: take the build already written in out first
          --siblings                ingame: take a game sharing the folder back to its build from
                                    the same time as well
          --no-seed                 download: fetch everything instead of copying what the
                                    installed game already has
          --delete                  apply: delete the folder afterwards
          --yes                     Answer every question with its usual answer, and do not ask
          --json                    One JSON object of what happened, for another program
          --login <auto|saved|window|never>
                                    auto (default): use the signed-in account, and open a login
                                    window when there is none. saved: never open one. window:
                                    always open one. never: fail instead of signing in
          --username <account>      Sign in as this account instead of the last one used
          --game <appid>            The game, when it is not the first argument
          --no-remember             Remember nothing new this run: no manifests, and no builds added
                                    to history. Every face takes it: the window, "cli" and every
                                    command. What is remembered already is still used
          --steam <folder>          Use this Steam folder instead of looking for one
          --depotdownloader <path>  Use this DepotDownloader instead of fetching one
          --no-color                Plain output
          --version                 Print the version
          --help                    This text, or "CODDowngrader help <command>"

        Exit codes: 0 done, 1 failed, 2 wrong command line, 3 sign-in needed, 4 file in use,
        5 the account does not own it.
        """;
}
