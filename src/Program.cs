using System.Text;
using CODDowngrader.App;
using CODDowngrader.Steam;
using Spectre.Console;

// UTF-8 before anything writes: Spectre keeps the writer it first sees, and DepotDownloader inherits the
// code page for its QR code. The window's own code page is put back on the way out.
var originalEncoding = SetUtf8();
try
{
    return await RunAsync(args);
}
finally
{
    Restore(originalEncoding);
}

static async Task<int> RunAsync(string[] args)
{
    var options = new Options();
    var list = false;
    string? export = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--list":
                list = true;
                break;
            case "--export":
                export = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
                break;
            case "--game" when i + 1 < args.Length && uint.TryParse(args[i + 1], out var appId):
                options.AppId = appId;
                i++;
                break;
            case "--steam" when i + 1 < args.Length:
                options.SteamRoot = args[++i];
                break;
            case "--depotdownloader" when i + 1 < args.Length:
                options.DepotDownloaderPath = args[++i];
                break;
            case "--no-color" or "--no-colour":
                AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { ColorSystem = ColorSystemSupport.NoColors });
                break;
            case "--version":
                Console.WriteLine(AppState.Version);
                return 0;
            case "--help" or "-h" or "/?":
                PrintHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown or incomplete option: {args[i]}");
                PrintHelp();
                return 2;
        }
    }

    if (list || export is not null)
    {
        try
        {
            var steam = SteamInstall.Find(options.SteamRoot);
            if (steam is null)
            {
                Console.Error.WriteLine("Steam was not found. Pass its folder with --steam.");
                return 1;
            }
            return export is not null ? ExportCommand.Run(steam, export) : ListCommand.Run(steam);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("COD Downgrader asks questions as it goes: run it in a terminal, or use --list.");
        return 2;
    }

    try
    {
        return await new Interactive(options).RunAsync();
    }
    catch (Exception e)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.GetType().Name)}: {Markup.Escape(e.Message)}[/]");
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
        return 1;
    }
}

static Encoding? SetUtf8()
{
    if (!OperatingSystem.IsWindows()) return null;
    try
    {
        var original = Console.OutputEncoding;
        if (original.CodePage == Encoding.UTF8.CodePage) return null;
        Console.OutputEncoding = Encoding.UTF8;
        return original;
    }
    catch (Exception e) when (e is IOException or PlatformNotSupportedException)
    {
        return null;
    }
}

static void Restore(Encoding? original)
{
    if (original is null) return;
    try
    {
        Console.OutputEncoding = original;
    }
    catch (Exception e) when (e is IOException or PlatformNotSupportedException)
    {
    }
}

static void PrintHelp()
{
    Console.WriteLine($"""
        COD Downgrader {AppState.Version} by Xep
        Download any build of a Call of Duty you own on Steam, into your installed game or into
        a folder of its own.
        https://github.com/Xeptix/COD-DG

        Usage: CODDowngrader [options]

          --list                    Show Steam, the installed Call of Duty games and every build
                                    known for them, then exit without changing anything
          --game <appid>            Open one game straight away, e.g. 311210 for Black Ops III
          --steam <folder>          Use this Steam folder instead of looking for one
          --depotdownloader <path>  Use this DepotDownloader instead of fetching one
          --no-color                Plain output
          --version                 Print the version
        """);
}
