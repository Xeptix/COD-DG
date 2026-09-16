using System.Diagnostics;
using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Download;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>One depot of a build, small enough to be worth fetching the file list of just to sign in.</summary>
public sealed record LoginProbe(uint App, uint Depot, ulong Manifest);

/// <summary>
/// Signing in to Steam. DepotDownloader does it and keeps the sign-in, so a command only has to make sure there is one. A
/// password or a Steam Guard code is only ever typed into DepotDownloader's own prompt: this tool never takes one, and neither
/// does a program that runs this tool. When there is no sign-in and the caller cannot answer questions, "CODDowngrader login"
/// is started in a console window of its own, and the command carries on once it has signed in.
/// </summary>
public static class Login
{
    public static string? SavedAccount() => AppState.LoadSettings().SteamAccount;

    /// <summary>DepotDownloader, fetched if this PC does not have it yet. Null once the failure has been reported.</summary>
    public static async Task<string?> ToolAsync(CliRun run)
    {
        if (run.Options.DepotDownloaderPath is { } own)
        {
            if (File.Exists(own)) return own;
            run.Fail(ExitCode.Usage, "no-depotdownloader", $"{own} does not exist.");
            return null;
        }
        if (DepotDownloaderTool.FindInstalled(AppState.Folder) is { } ready) return ready;

        var release = DepotDownloaderTool.ReleaseForThisMachine();
        if (release is null)
        {
            run.Fail(ExitCode.Failed, "no-depotdownloader",
                "There is no pinned DepotDownloader download for this system. Get it from https://github.com/SteamRE/DepotDownloader/releases and pass its path with --depotdownloader.");
            return null;
        }

        run.Line($"Fetching DepotDownloader {DepotDownloaderTool.Version} by SteamRE (GPL-2.0), {Format.Size((ulong)release.Size)}.");
        try
        {
            return await DepotDownloaderTool.InstallAsync(AppState.Folder, release, (_, _) => { }, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            run.Fail(ExitCode.Failed, "no-depotdownloader", $"DepotDownloader could not be fetched: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The account a download runs as, after signing in if that is allowed and needed. Null once the failure has been reported.
    /// <paramref name="probe"/> is what a sign-in window fetches to prove the sign-in works.
    /// </summary>
    public static async Task<(bool Ok, string? Account)> EnsureAsync(CliRun run, LoginProbe probe)
    {
        var saved = run.Username ?? SavedAccount();

        switch (run.Login)
        {
            case LoginMode.Never:
                run.Fail(ExitCode.SignIn, "signin", "This command has to download, which needs a signed-in Steam account, and --login never was asked for.");
                return (false, null);

            case LoginMode.Saved when saved is null:
                run.Fail(ExitCode.SignIn, "signin", "No Steam account is signed in. Run \"CODDowngrader login\" once, then run this again.");
                return (false, null);

            case LoginMode.Saved:
                return (true, saved);

            case LoginMode.Window:
                var opened = await WindowAsync(run, probe);
                return (opened is not null, opened);

            default:
                if (saved is not null) return (true, saved);

                // A person at a terminal can answer DepotDownloader's own prompts in place; anything else gets a window.
                if (!run.Json && !Console.IsInputRedirected && Environment.UserInteractive)
                {
                    run.Line("No Steam account is signed in yet: DepotDownloader shows a QR code to scan with the Steam mobile app.");
                    return (true, null);
                }
                var account = await WindowAsync(run, probe);
                return (account is not null, account);
        }
    }

    /// <summary>
    /// Starts "CODDowngrader login" in a console window of its own and waits for it. The sign-in itself happens in
    /// DepotDownloader, in that window: nothing types a password into this process or into whatever started it.
    /// </summary>
    public static async Task<string?> WindowAsync(CliRun run, LoginProbe probe)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            run.Fail(ExitCode.SignIn, "signin", "A sign-in window could not be started. Run \"CODDowngrader login\" yourself, then run this again.");
            return null;
        }

        var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add("--app");
        psi.ArgumentList.Add(probe.App.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--depot");
        psi.ArgumentList.Add(probe.Depot.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--manifest");
        psi.ArgumentList.Add(probe.Manifest.ToString(CultureInfo.InvariantCulture));
        if (run.Username is { } username)
        {
            psi.ArgumentList.Add("--username");
            psi.ArgumentList.Add(username);
        }
        if (run.Options.SteamRoot is { } steamRoot)
        {
            psi.ArgumentList.Add("--steam");
            psi.ArgumentList.Add(steamRoot);
        }
        if (run.Options.DepotDownloaderPath is { } toolPath)
        {
            psi.ArgumentList.Add("--depotdownloader");
            psi.ArgumentList.Add(toolPath);
        }

        run.Line("Signing in: a window has opened for it. Scan the QR code with the Steam mobile app, or type the account name and password there.");
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                run.Fail(ExitCode.SignIn, "signin", "A sign-in window could not be started. Run \"CODDowngrader login\" yourself, then run this again.");
                return null;
            }
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                run.Fail(ExitCode.SignIn, "signin", "The sign-in window closed without signing in.");
                return null;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            run.Fail(ExitCode.SignIn, "signin", $"A sign-in window could not be started: {e.Message}");
            return null;
        }

        // The window's DepotDownloader kept the sign-in, and the account name came back through the settings file.
        var account = SavedAccount();
        if (account is null)
        {
            run.Fail(ExitCode.SignIn, "signin", "The sign-in window finished without an account name.");
            return null;
        }
        run.Line($"Signed in as {account}.");
        return account;
    }

    /// <summary>The login command: signs DepotDownloader in, in this window, and fetches one file list to prove it worked.</summary>
    public static async Task<int> RunAsync(CliRun run, SteamInstall steam)
    {
        if (await ToolAsync(run) is not { } exe) return (int)ExitCode.Failed;

        var probe = Probe(run, steam);
        if (probe is null)
            return run.Fail(ExitCode.Usage, "no-probe",
                "Signing in needs a game to ask Steam about. Install a Call of Duty, or name one with --app, --depot and --manifest.");

        var account = run.Username ?? (run.Command.Flag("qr") ? null : SavedAccount());
        var folder = Path.Combine(AppState.Folder, "login");
        var log = Path.Combine(AppState.LogsFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-login.log");
        Directory.CreateDirectory(folder);

        run.Line(account is null
            ? "Signing in to Steam. Scan the QR code with the Steam mobile app."
            : $"Signing in to Steam as {account}.");

        var arguments = DepotDownloaderTool.Arguments(probe.App, new[] { (probe.Depot, probe.Manifest) }, folder, account, manifestOnly: true);
        var result = await DepotDownloaderTool.RunAsync(exe, arguments, log, mirror: run.Json ? Console.Error : null);

        if (result.AccountName is { } name)
        {
            var settings = AppState.LoadSettings();
            if (settings.SteamAccount != name)
            {
                settings.SteamAccount = name;
                AppState.SaveSettings(settings);
            }
            run.Set("account", name);
        }

        // Whatever it fetched belongs in the tool's cache, and the folder is only ever a scratch space.
        ManifestLists.Find(probe.Depot, probe.Manifest, folder);
        TryDelete(folder);

        if (result.Cancelled) return run.Fail(ExitCode.SignIn, "cancelled", "The sign-in was stopped.");
        if (result.ExitCode != 0 && result.AccountName is null)
            return run.Fail(ExitCode.SignIn, "signin", "DepotDownloader did not sign in. " + string.Join(" ", result.Messages.TakeLast(3)));

        run.Set("signedIn", System.Text.Json.Nodes.JsonValue.Create(true));
        run.Line(result.AccountName is { } signedIn ? $"Signed in as {signedIn}." : "Signed in.");
        return run.Ok();
    }

    /// <summary>What a sign-in asks Steam for: a depot of a game the account owns, named on the command line or taken from a game here.</summary>
    public static LoginProbe? Probe(CliRun run, SteamInstall steam)
    {
        if (run.Command.AppId("app") is { } app && run.Command.AppId("depot") is { } depot
            && ulong.TryParse(run.Command.Value("manifest"), NumberStyles.None, CultureInfo.InvariantCulture, out var manifest))
            return new LoginProbe(app, depot, manifest);

        var library = GameLibrary.Load(steam);
        foreach (var game in library.Entries.Where(e => e.Installed is not null && e.InstalledManifests is { Count: > 0 }))
        {
            var smallest = game.InstalledManifests!
                .OrderBy(m => library.DepotSize(game, m.Key, m.Value) ?? ulong.MaxValue)
                .ThenBy(m => m.Key)
                .First();
            return new LoginProbe(game.OwnerOf(smallest.Key), smallest.Key, smallest.Value);
        }
        return null;
    }

    /// <summary>A run that ended because Steam would not take the sign-in, rather than because of the download itself.</summary>
    public static bool NeedsSignIn(DepotDownloaderResult result) =>
        result.AccountName is null
        && result.Messages.Any(m =>
            m.Contains("Unable to get password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Invalid password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Unable to login", StringComparison.OrdinalIgnoreCase)
            || m.Contains("login failure", StringComparison.OrdinalIgnoreCase)
            || m.Contains("two-factor", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase));

    static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
