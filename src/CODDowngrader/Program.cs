using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using CODDowngrader.App;
using CODDowngrader.Cli;
using CODDowngrader.Gui;
using Spectre.Console;

namespace CODDowngrader;

/// <summary>
/// One program, three faces: the window with nothing on the command line, the menus with "cli", and a command with anything
/// else. On Windows this is a window program, so the menus and the commands first take the console of whatever started them
/// (CODDowngrader.com, a terminal, or another program), and the menus make one of their own when there is none.
/// </summary>
static partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Catalog.Remembered.Writing = CommandLine.Parse(args, out _)?.Flag("no-remember") != true;
        var face = Dispatch.FaceOf(args);
        if (face == Dispatch.Face.Window) return Window(args);

        UseConsole(ownIfNone: face == Dispatch.Face.Menus || args.FirstOrDefault()?.Equals("login", StringComparison.OrdinalIgnoreCase) == true);
        var originalEncoding = SetUtf8();
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        finally
        {
            Restore(originalEncoding);
        }
    }

    static int Window(string[] args)
    {
        var parsed = CommandLine.Parse(args, out _);
        GuiApp.StartOptions = new Options
        {
            SteamRoot = parsed?.Value("steam"),
            DepotDownloaderPath = parsed?.Value("depotdownloader"),
            AppId = parsed?.AppId("game"),
        };
        try
        {
            return AppBuilder.Configure<GuiApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e) when (!OperatingSystem.IsWindows())
        {
            // A Linux session with no display: say where the tool's other faces are.
            Console.Error.WriteLine($"The window could not open ({e.Message}). \"CODDowngrader cli\" opens the menus in this terminal.");
            return 1;
        }
    }

    static async Task<int> RunAsync(string[] args)
    {
        var asked = await Dispatch.RunAsync(args);
        if (asked.Code is { } code) return code;

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("The menus ask questions as they go: run \"CODDowngrader cli\" in a terminal, or use a command. \"CODDowngrader help\" lists them.");
            return 2;
        }

        try
        {
            return await new Interactive(asked.Options).RunAsync();
        }
        catch (Exception e)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.GetType().Name)}: {Markup.Escape(e.Message)}[/]");
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
            return 1;
        }
    }

    const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    /// <summary>
    /// Takes the console of the process that started this one. Output handles handed over by that process (a pipe, a file,
    /// CODDowngrader.com's own) stay what they were; the console is what menus and DepotDownloader's prompts need besides.
    /// </summary>
    static void UseConsole(bool ownIfNone)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!AttachConsole(AttachParentProcess) && ownIfNone) AllocConsole();
    }

    // UTF-8 before anything writes: Spectre keeps the writer it first sees, and DepotDownloader inherits the code page for
    // its QR code. Only a console needs it, and setting it while output is redirected hands a program that runs this tool an
    // empty pipe. The console's own code page is put back on the way out.
    static Encoding? SetUtf8()
    {
        if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return null;
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
}
