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
    var asked = await CODDowngrader.Cli.Dispatch.RunAsync(args);
    if (asked.Code is { } code) return code;

    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("COD Downgrader asks questions as it goes: run it in a terminal, or use a command. \"CODDowngrader help\" lists them.");
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

static Encoding? SetUtf8()
{
    if (!OperatingSystem.IsWindows()) return null;

    // Only a console window needs the code page changed, for DepotDownloader's QR code. Setting it while output is
    // redirected hands a program that runs this tool an empty pipe instead of what was written.
    if (Console.IsOutputRedirected) return null;
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
