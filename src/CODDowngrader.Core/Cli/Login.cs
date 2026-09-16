using System.Diagnostics;
using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Download;
using CODDowngrader.Jobs;
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
    public static async Task<string?> ToolAsync(Job run)
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
    public static async Task<(bool Ok, string? Account)> EnsureAsync(Job run, LoginProbe probe)
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
                if (run.CanPromptHere)
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
    public static async Task<string?> WindowAsync(Job run, LoginProbe probe)
    {
        var exe = Environment.ProcessPath;
        // CODDowngrader.exe is a window program on Windows and gets no console; its console face beside it does.
        if (OperatingSystem.IsWindows() && exe is not null && Path.ChangeExtension(exe, ".com") is var console && File.Exists(console)) exe = console;
        if (exe is null) return CouldNotOpen(run, null);

        var result = Path.Combine(Path.GetTempPath(), $"coddowngrader-login-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var arguments = new List<string>
        {
            "login", "--app", probe.App.ToString(CultureInfo.InvariantCulture), "--depot", probe.Depot.ToString(CultureInfo.InvariantCulture),
            "--manifest", probe.Manifest.ToString(CultureInfo.InvariantCulture), "--result", result,
        };
        if (run.Username is { } username) arguments.AddRange(new[] { "--username", username });
        if (run.Settings.Qr) arguments.Add("--qr");
        if (run.Options.SteamRoot is { } steamRoot) arguments.AddRange(new[] { "--steam", steamRoot });
        if (run.Options.DepotDownloaderPath is { } toolPath) arguments.AddRange(new[] { "--depotdownloader", toolPath });

        ProcessStartInfo psi;
        var waits = true;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        }
        else if (Terminal(exe, arguments) is { } terminal)
        {
            psi = terminal.Start;
            waits = terminal.Waits;
        }
        else
        {
            return CouldNotOpen(run, "No terminal was found to sign in in. Run \"CODDowngrader login\" in a terminal, then run this again.");
        }

        run.Line("Signing in: a window has opened for it. Scan the QR code with the Steam mobile app, or type the account name and password there.");
        run.Progress(new JobProgress("Signing in to Steam", "in the window that opened for it"));
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return CouldNotOpen(run, null);

            if (waits)
            {
                await process.WaitForExitAsync(run.Cancel);
            }
            else
            {
                // A terminal that hands its command to a process of its own returns at once: the result file says when it is done.
                var clock = Stopwatch.StartNew();
                while (!File.Exists(result) && clock.Elapsed < TimeSpan.FromMinutes(15)) await Task.Delay(500, run.Cancel);
            }
        }
        catch (OperationCanceledException)
        {
            run.Fail(ExitCode.SignIn, "cancelled", "The sign-in was stopped.");
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CouldNotOpen(run, $"A sign-in window could not be started: {e.Message}");
        }

        var code = ReadResult(result);
        if (code != 0)
        {
            run.Fail(ExitCode.SignIn, "signin", code is null ? "The sign-in window closed without signing in." : "DepotDownloader did not sign in.");
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

    static string? CouldNotOpen(Job run, string? message)
    {
        run.Fail(ExitCode.SignIn, "signin", message ?? "A sign-in window could not be started. Run \"CODDowngrader login\" yourself, then run this again.");
        return null;
    }

    /// <summary>The exit code "login --result" wrote, read once and deleted; null when it wrote none.</summary>
    static int? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A terminal command on Linux, and whether it waits for what it runs.</summary>
    sealed record TerminalStart(ProcessStartInfo Start, bool Waits);

    /// <summary>
    /// The first terminal found: the one $TERMINAL names, then the ones desktops ship, Konsole first since a Steam Deck has it.
    /// Each is asked to wait for its command where it has a way to; the ones that might not are read through the result file.
    /// </summary>
    static TerminalStart? Terminal(string exe, IReadOnlyList<string> arguments)
    {
        var known = new (string Name, string[] Before, bool Waits)[]
        {
            ("konsole", new[] { "--nofork", "-e" }, true),
            ("gnome-terminal", new[] { "--wait", "--" }, true),
            ("xfce4-terminal", new[] { "--disable-server", "-x" }, true),
            ("kitty", Array.Empty<string>(), true),
            ("alacritty", new[] { "-e" }, true),
            ("foot", Array.Empty<string>(), true),
            ("xterm", new[] { "-e" }, true),
            ("x-terminal-emulator", new[] { "-e" }, false),
        };

        var candidates = new List<(string Path, string[] Before, bool Waits)>();
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } chosen && Which(chosen) is { } chosenPath)
        {
            var name = Path.GetFileName(chosenPath);
            var match = known.FirstOrDefault(k => k.Name == name);
            candidates.Add(match.Name is null ? (chosenPath, new[] { "-e" }, false) : (chosenPath, match.Before, match.Waits));
        }
        foreach (var (name, before, waits) in known)
            if (Which(name) is { } path) candidates.Add((path, before, waits));
        if (candidates.Count == 0) return null;

        var (terminal, flags, waitsToo) = candidates[0];
        var psi = new ProcessStartInfo(terminal) { UseShellExecute = false };
        foreach (var flag in flags) psi.ArgumentList.Add(flag);
        psi.ArgumentList.Add(exe);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return new TerminalStart(psi, waitsToo);
    }

    static string? Which(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>The login command: signs DepotDownloader in, in this window, and fetches one file list to prove it worked.</summary>
    public static async Task<int> RunAsync(Job run, SteamInstall steam)
    {
        if (await ToolAsync(run) is not { } exe) return (int)ExitCode.Failed;

        var probe = Probe(run, steam);
        if (probe is null)
            return run.Fail(ExitCode.Usage, "no-probe",
                "Signing in needs a game to ask Steam about. Install a Call of Duty, or name one with --app, --depot and --manifest.");

        var account = run.Username ?? (run.Settings.Qr ? null : SavedAccount());
        var folder = Path.Combine(AppState.Folder, "login");
        var log = Path.Combine(AppState.LogsFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-login.log");
        Directory.CreateDirectory(folder);

        run.Line(account is null
            ? "Signing in to Steam. Scan the QR code with the Steam mobile app."
            : $"Signing in to Steam as {account}.");

        var arguments = DepotDownloaderTool.Arguments(probe.App, new[] { (probe.Depot, probe.Manifest) }, folder, account, manifestOnly: true);
        var result = await DepotDownloaderTool.RunAsync(exe, arguments, log, run.Cancel, mirror: run.ToolOutput);

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
    public static LoginProbe? Probe(Job run, SteamInstall steam)
    {
        if (run.Settings.ProbeApp is { } app && run.Settings.ProbeDepot is { } depot && run.Settings.ProbeManifest is { } manifest)
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
        result.AskedToSignIn
        // DepotDownloader names the account ("Logging 'X' into Steam3...") before Steam has taken the sign-in, so only an
        // approved QR code says the sign-in itself went through.
        || (!result.QrApproved
        && result.Messages.Any(m =>
            m.Contains("Unable to get password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Invalid password", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Unable to login", StringComparison.OrdinalIgnoreCase)
            || m.Contains("login failure", StringComparison.OrdinalIgnoreCase)
            || m.Contains("two-factor", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Access token was rejected", StringComparison.OrdinalIgnoreCase)));

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
