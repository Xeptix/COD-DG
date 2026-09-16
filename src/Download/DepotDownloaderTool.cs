using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CODDowngrader.Steam;

namespace CODDowngrader.Download;

public sealed record DepotDownloaderRelease(string Asset, long Size, string Sha256)
{
    public string Url => $"https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_{DepotDownloaderTool.Version}/{Asset}";
}

/// <summary>What one DepotDownloader run said about itself. Whether depots finished is read from depot.config, not from here.</summary>
public sealed class DepotDownloaderResult
{
    public int ExitCode { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>The Steam account DepotDownloader signed in as, when it said so.</summary>
    public string? AccountName { get; set; }

    /// <summary>A QR code was approved in this run. DepotDownloader saves that login before it does anything else.</summary>
    public bool QrApproved { get; set; }

    /// <summary>Depots DepotDownloader skipped: not owned by the account, or no decryption key.</summary>
    public HashSet<uint> Unavailable { get; } = new();

    /// <summary>Lines that explain a failure, oldest first.</summary>
    public List<string> Messages { get; } = new();

    /// <summary>
    /// The QR code was approved and DepotDownloader then threw the sign-in away. When Steam drops the
    /// connection while the code is on screen, DepotDownloader 3.4.0 starts one more wait on the same
    /// sign-in for every reconnect: the first to see the approval signs in and saves the login, and a later
    /// one finds the sign-in used up ("FileNotFound") and aborts the run. The saved login still works.
    /// </summary>
    public bool LostSessionAfterQrLogin =>
        QrApproved && !Cancelled && ExitCode != 0
        && Messages.Any(m => m.StartsWith("Failed to authenticate with Steam", StringComparison.Ordinal));
}

/// <summary>
/// SteamRE's DepotDownloader does the downloading. It is fetched from its own GitHub release on first
/// use rather than shipped inside this tool (it is GPL-2.0), and the download has to match a pinned
/// SHA-256 before anything is extracted. It runs in this console, so its sign-in prompts and QR code
/// appear in place and a password only ever goes to DepotDownloader.
/// </summary>
public static partial class DepotDownloaderTool
{
    public const string Version = "3.4.0";

    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // Hashes as published in winget-pkgs for SteamRE.DepotDownloader 3.4.0, not derived from the download.
    static readonly DepotDownloaderRelease WindowsX64 =
        new("DepotDownloader-windows-x64.zip", 33_474_005, "41C9E9F0DF54B3AD02E67A11726756E5C73283BD7C2E1B04ACFA5AE4C2ED3767");

    static readonly DepotDownloaderRelease WindowsArm64 =
        new("DepotDownloader-windows-arm64.zip", 32_428_618, "1449BA47775E9974036E615BEDF00D72BEF747CC5B93A78048ED4E0B2C63B2B3");

    static readonly DepotDownloaderRelease LinuxX64 =
        new("DepotDownloader-linux-x64.zip", 33_442_357, "");

    public static DepotDownloaderRelease? ReleaseForThisMachine()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        var release = OperatingSystem.IsWindows() ? (arm ? WindowsArm64 : WindowsX64)
            : OperatingSystem.IsLinux() && !arm ? LinuxX64
            : null;
        return release is { Sha256.Length: 64 } ? release : null;
    }

    public static string Folder(string stateFolder) => Path.Combine(stateFolder, "tools", $"DepotDownloader-{Version}");

    static string ExecutableName => OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader";

    /// <summary>The verified copy in the tool's folder, if it has been fetched.</summary>
    public static string? FindInstalled(string stateFolder)
    {
        var folder = Folder(stateFolder);
        if (!File.Exists(Path.Combine(folder, ".verified"))) return null;
        return Directory.EnumerateFiles(folder, ExecutableName, SearchOption.AllDirectories).FirstOrDefault();
    }

    public static async Task<string> InstallAsync(string stateFolder, DepotDownloaderRelease release, Action<long, long> progress, CancellationToken ct)
    {
        var folder = Folder(stateFolder);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, release.Asset + ".part");

        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"COD-Downgrader/{App.AppState.Version} (+https://github.com/Xeptix/COD-DG)");
            using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Size;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;

            // HttpClient's timeout stops at the headers; a connection that goes quiet mid-body would otherwise wait forever.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress(done, total);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"The download of {release.Asset} stopped receiving data for {StallTimeout.TotalSeconds:0} seconds.");
            }
        }

        string hash;
        await using (var stream = File.OpenRead(zip))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        }
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zip);
            throw new InvalidDataException($"{release.Asset} did not match its published SHA-256 (got {hash}). Nothing was extracted.");
        }

        ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: true);
        File.Delete(zip);

        var exe = Directory.EnumerateFiles(folder, ExecutableName, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new FileNotFoundException($"{release.Asset} has no {ExecutableName} in it.");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                      UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.WriteAllText(Path.Combine(folder, ".verified"), hash);
        return exe;
    }

    /// <summary>
    /// One run for one app. DepotDownloader reads an empty -depot list as "the current build", so there must be depots.
    /// <paramref name="fileList"/> limits the run to the files it names; <paramref name="manifestOnly"/> fetches only the
    /// file lists, as manifest_&lt;depot&gt;_&lt;manifest&gt;.txt in the folder.
    /// </summary>
    public static IReadOnlyList<string> Arguments(uint appId, IReadOnlyList<(uint Depot, ulong Manifest)> depots, string directory, string? accountName,
        string? fileList = null, bool manifestOnly = false)
    {
        if (depots.Count == 0) throw new ArgumentException("A download needs at least one depot.", nameof(depots));
        if (depots.Select(d => d.Depot).Distinct().Count() != depots.Count) throw new ArgumentException("A run takes each depot once.", nameof(depots));

        var args = new List<string> { "-app", appId.ToString() };
        args.Add("-depot");
        args.AddRange(depots.Select(d => d.Depot.ToString()));
        args.Add("-manifest");
        args.AddRange(depots.Select(d => d.Manifest.ToString()));
        args.AddRange(new[] { "-dir", directory, "-max-downloads", "8" });
        if (fileList is not null) args.AddRange(new[] { "-filelist", fileList });
        if (manifestOnly) args.Add("-manifest-only");
        if (accountName is null) args.Add("-qr");
        else args.AddRange(new[] { "-username", accountName });
        args.Add("-remember-password");
        return args;
    }

    /// <summary>
    /// The lines of a -filelist for these files. DepotDownloader matches a file by its name as the manifest writes it, with / for
    /// \, and manifests from 2011 write ".\main\file", so every file is listed both ways.
    /// </summary>
    public static IEnumerable<string> FileListLines(IEnumerable<string> names) =>
        names.Select(ManifestFile.NormalizeName).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(name => new[] { name, "./" + name });

    [GeneratedRegex(@"Next time you can login with -username (\S+) -remember-password")]
    private static partial Regex QrApprovedRegex();

    [GeneratedRegex(@"^Logging '(.+)' into Steam3\.\.\.")]
    private static partial Regex LoggingInRegex();

    [GeneratedRegex(@"^Depot (\d+) is not available from this account|^No valid depot key for (\d+)")]
    private static partial Regex SkippedDepotRegex();

    static readonly string[] FailureMarkers =
    {
        "unable to", "not listed for app", "is not available from this account", "couldn't", "could not",
        "aborting", "was rejected", "failed", "was not completely downloaded", "no manifest request code",
        "encountered", "insufficient privileges",
    };

    /// <param name="mirror">Where DepotDownloader's own output goes; null for this console. A caller printing JSON on
    /// stdout sends it to stderr, so what it prints stays the only thing on stdout.</param>
    public static async Task<DepotDownloaderResult> RunAsync(string exe, IReadOnlyList<string> arguments, string logPath,
        CancellationToken ct = default, TextWriter? mirror = null)
    {
        var result = new DepotDownloaderResult();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        // Ctrl+C reaches DepotDownloader too, which stops it; this process stays up to say so.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            result.Cancelled = true;
        };
        Console.CancelKeyPress += onCancel;

        var gate = new object();
        StreamWriter? log = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            log = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("DepotDownloader did not start.");
            using var stop = ct.Register(() =>
            {
                result.Cancelled = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            });
            var stdout = Pump(process.StandardOutput, mirror ?? Console.Out, () => log, v => log = v, gate, result);
            var stderr = Pump(process.StandardError, mirror ?? Console.Error, () => log, v => log = v, gate, result);
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync();
            result.ExitCode = process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            log?.Dispose();
        }
        return result;
    }

    static async Task Pump(StreamReader reader, TextWriter console, Func<StreamWriter?> getLog, Action<StreamWriter?> setLog, object gate, DepotDownloaderResult result)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;

        // Relay characters as they arrive: a password prompt has no newline to wait for. Nothing in this
        // loop may throw, or the pipe stops being drained and DepotDownloader blocks forever.
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            lock (gate)
            {
                try
                {
                    console.Write(buffer, 0, read);
                    console.Flush();
                }
                catch (IOException)
                {
                }
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    line.Append(buffer[i]);
                    continue;
                }
                Record(line.ToString().TrimEnd('\r'), getLog, setLog, gate, result);
                line.Clear();
            }
        }

        if (line.Length > 0) Record(line.ToString(), getLog, setLog, gate, result);
    }

    static void Record(string text, Func<StreamWriter?> getLog, Action<StreamWriter?> setLog, object gate, DepotDownloaderResult result)
    {
        lock (gate)
        {
            if (getLog() is { } log)
            {
                try
                {
                    log.WriteLine(text);
                }
                catch (IOException)
                {
                    // A full disk loses the log, not the download.
                    setLog(null);
                }
            }

            Interpret(text, result);
        }
    }

    /// <summary>What one line of DepotDownloader's output says about the run.</summary>
    internal static void Interpret(string text, DepotDownloaderResult result)
    {
        var approved = QrApprovedRegex().Match(text);
        if (approved.Success)
        {
            result.AccountName = approved.Groups[1].Value;
            result.QrApproved = true;
        }
        else if (LoggingInRegex().Match(text) is { Success: true } loggingIn)
        {
            result.AccountName ??= loggingIn.Groups[1].Value;
        }

        var skipped = SkippedDepotRegex().Match(text);
        if (skipped.Success && uint.TryParse(skipped.Groups[1].Success ? skipped.Groups[1].Value : skipped.Groups[2].Value, out var depot))
            result.Unavailable.Add(depot);

        var trimmed = text.Trim();
        if (trimmed.Length > 0 && (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                                   || FailureMarkers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            result.Messages.Add(trimmed);
        }
    }
}
