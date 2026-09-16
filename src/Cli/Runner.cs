using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Download;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>How the runs of one command sign in: the account, and the depot a sign-in window would ask Steam about.</summary>
public sealed class LoginState
{
    public required LoginProbe Probe { get; init; }
    public string? Account { get; set; }
    public bool SignedIn { get; set; }
}

/// <param name="Skipped">Depots the account cannot download.</param>
public sealed record RunOutcome(bool Ok, bool Cancelled, IReadOnlySet<uint> Skipped, IReadOnlyList<string> Messages);

/// <summary>DepotDownloader runs for a command line: no questions, and one sign-in window if Steam wants one.</summary>
public static class Runner
{
    public static string LogPath(uint appId) =>
        Path.Combine(AppState.LogsFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{appId}.log");

    /// <summary>Makes sure there is a sign-in to run with. False once the failure has been reported.</summary>
    public static async Task<bool> SignInAsync(CliRun run, LoginState login)
    {
        if (login.SignedIn) return true;
        var (ok, account) = await Login.EnsureAsync(run, login.Probe);
        if (!ok) return false;
        login.Account = account;
        login.SignedIn = true;
        return true;
    }

    /// <summary>
    /// Downloads the depots, one DepotDownloader run per owning app. A run that ends because Steam would not take the sign-in
    /// opens one sign-in window and is tried again.
    /// </summary>
    public static async Task<RunOutcome> DepotsAsync(CliRun run, string exe, uint appId, IReadOnlyList<(uint Depot, ulong Manifest)> depots,
        IReadOnlyDictionary<uint, uint> owners, string directory, string logPath, LoginState login, string? fileList = null, bool manifestOnly = false)
    {
        var skipped = new HashSet<uint>();
        var messages = new List<string>();

        foreach (var group in depots.GroupBy(d => owners.TryGetValue(d.Depot, out var owner) ? owner : appId)
                     .OrderBy(g => g.Key == appId ? 0 : 1)
                     .ThenBy(g => g.Key))
        {
            for (var attempt = 1; ; attempt++)
            {
                if (!await SignInAsync(run, login)) return new RunOutcome(false, false, skipped, messages);

                var arguments = DepotDownloaderTool.Arguments(group.Key, group.ToList(), directory, login.Account, fileList, manifestOnly);
                var result = await DepotDownloaderTool.RunAsync(exe, arguments, logPath, mirror: run.Json ? Console.Error : null);
                skipped.UnionWith(result.Unavailable);
                messages.AddRange(result.Messages);
                Remember(result.AccountName, login);

                if (result.Cancelled) return new RunOutcome(false, true, skipped, messages);
                if (result.ExitCode == 0) break;

                if (result.LostSessionAfterQrLogin && attempt == 1)
                {
                    run.Line("The sign-in was approved, and DepotDownloader lost it when Steam reconnected. Trying again with the saved login.");
                    continue;
                }
                if (Login.NeedsSignIn(result) && attempt == 1 && run.Login is LoginMode.Auto or LoginMode.Window)
                {
                    run.Line("Steam would not take that sign-in.");
                    if (await Login.WindowAsync(run, login.Probe) is not { } account) return new RunOutcome(false, false, skipped, messages);
                    login.Account = account;
                    continue;
                }
                return new RunOutcome(false, false, skipped, messages);
            }
        }

        return new RunOutcome(true, false, skipped, messages);
    }

    /// <summary>
    /// The file lists of every manifest named, fetching the ones this PC does not have. Null once the failure has been
    /// reported. A run takes each depot once, so the same depot at two manifests is fetched in two rounds.
    /// </summary>
    public static async Task<Dictionary<(uint Depot, ulong Manifest), IReadOnlyList<ManifestFile>>?> ListsAsync(CliRun run, string exe,
        GameLibrary library, GameEntry game, IReadOnlyCollection<(uint Depot, ulong Manifest)> wanted, string folder, string logPath, LoginState login)
    {
        IReadOnlyList<ManifestFile>? Known(uint depot, ulong manifest) => library.Files(depot, manifest) ?? ManifestLists.Find(depot, manifest, folder);

        var missing = wanted.Where(w => Known(w.Depot, w.Manifest) is null).Distinct().ToList();
        while (missing.Count > 0)
        {
            var round = missing.GroupBy(m => m.Depot).Select(g => g.First()).ToList();
            run.Line($"Fetching the file lists of {round.Count} manifests.");
            Directory.CreateDirectory(folder);
            var outcome = await DepotsAsync(run, exe, game.AppId, round, game.Owners, folder, logPath, login, manifestOnly: true);
            if (!outcome.Ok)
            {
                run.Fail(outcome.Cancelled ? ExitCode.Failed : ExitCode.Failed, "no-file-lists",
                    $"The file lists could not be fetched. {string.Join(" ", outcome.Messages.Distinct().TakeLast(3))} Full output: {logPath}");
                return null;
            }
            missing = missing.Except(round).Where(m => Known(m.Depot, m.Manifest) is null).ToList();
            if (missing.Count > 0 && missing.Count == round.Count) break;
        }

        var lists = new Dictionary<(uint, ulong), IReadOnlyList<ManifestFile>>();
        foreach (var (depot, manifest) in wanted.Distinct())
        {
            if (Known(depot, manifest) is not { } files)
            {
                run.Fail(ExitCode.Failed, "no-file-list", $"The file list of depot {depot} manifest {manifest} could not be read. Full output: {logPath}");
                return null;
            }
            lists[(depot, manifest)] = files;
        }
        return lists;
    }

    static void Remember(string? account, LoginState login)
    {
        if (account is null) return;
        login.Account = account;
        var settings = AppState.LoadSettings();
        if (settings.SteamAccount == account) return;
        settings.SteamAccount = account;
        AppState.SaveSettings(settings);
    }
}
