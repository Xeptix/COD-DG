using System.Text.Json.Nodes;
using CODDowngrader.App;
using CODDowngrader.Builds;
using CODDowngrader.Patching;
using CODDowngrader.Steam;

namespace CODDowngrader.Cli;

/// <summary>Handing a build to someone else: "share" writes one, and --shared takes one in place of naming the build.</summary>
public static class ShareCommand
{
    /// <summary>
    /// share: the build --build, --at or --manifest names, with --only, --files and --siblings; a patch folder or a downloaded
    /// build with --from; and with none of those, the downgrade written into the game. Printed, or saved with --to.
    /// </summary>
    public static int Run(CliRun run, SteamInstall steam, ParsedCommand command)
    {
        var library = GameLibrary.Load(steam);
        var game = Selectors.Game(library, command.Arguments.FirstOrDefault() ?? command.Value("game"), out var gameError);
        if (game is null) return run.Fail(ExitCode.Usage, "no-game", gameError!);

        SharedBuild shared;
        if (run.Settings.From is { Length: > 0 } from)
        {
            string folder;
            try
            {
                folder = PathRules.Normalize(from.Trim().Trim('"'));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return run.Fail(ExitCode.Usage, "no-folder", e.Message);
            }

            if (PatchStore.LoadPatch(folder) is { } patch && patch.AppIds.Contains(game.AppId))
                shared = SharedBuild.Of(patch, game);
            else if (AppState.LoadRecord(folder, out _)?.For(game.AppId) is { Complete: true } downloaded)
                shared = SharedBuild.Of(downloaded, game);
            else
                return run.Fail(ExitCode.Usage, "no-patch", $"{folder} has no patch and no finished download of {game.Name} from COD Downgrader.");
        }
        else if (run.Settings.Build is null && run.Settings.At is null && run.Settings.Manifests.Count == 0)
        {
            if (game.Installed is not { } app || PatchStore.AppliedTo(app.InstallDir) is not { } record || !record.AppIds.Contains(game.AppId))
                return run.Fail(ExitCode.Usage, "no-build",
                    $"Name the build to share with --build, --at or --manifest, or a patch folder with --from. No downgrade is written into {game.Name} to share instead.");
            shared = SharedBuild.Of(record, game);
        }
        else
        {
            if (Actions.Target(run, library, game) is not { } target) return run.Reported ?? (int)ExitCode.Usage;
            if (Actions.Part(run) is not { } part) return run.Reported ?? (int)ExitCode.Usage;
            shared = SharedBuild.Of(game, target.Manifests, target.Label, SharedBuild.OnlyOf(part.Label), run.Settings.Files, run.Settings.Siblings);
        }

        var text = shared.Text();
        run.Set("game", new JsonObject { ["app"] = game.AppId, ["name"] = game.Name });
        run.Set("build", new JsonObject { ["title"] = shared.Title, ["part"] = shared.PartLabel });
        run.Set("shared", text);

        if (run.Settings.To is { Length: > 0 } to)
        {
            string path;
            try
            {
                path = PathRules.Normalize(to.Trim().Trim('"'));
                if (Directory.Exists(path)) path = Path.Combine(path, shared.FileName);
                if (Path.GetDirectoryName(path) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
                File.WriteAllText(path, text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return run.Fail(ExitCode.Failed, "not-saved", $"The shared build could not be saved: {e.Message}");
            }
            run.Set("file", path);
            run.Line($"Saved {game.Name}: {shared.Title} to {path}. Whoever owns the game opens it in COD Downgrader, or runs a command with --shared.");
        }
        else
        {
            run.Line(text.TrimEnd('\n'));
        }
        return run.Ok();
    }

    /// <summary>
    /// --shared: reads the shared build, finds its game here, and makes it the build the job names. --only and --files still choose
    /// the part. Null once the failure has been reported.
    /// </summary>
    public static GameEntry? Use(CliRun run, SteamInstall steam, ParsedCommand command, string path)
    {
        string text;
        try
        {
            text = path == "-" ? Console.In.ReadToEnd() : File.ReadAllText(PathRules.Normalize(path.Trim().Trim('"')));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            run.Fail(ExitCode.Usage, "no-shared", $"The shared build could not be read: {e.Message}");
            return null;
        }

        if (SharedBuild.Read(text, out var readError) is not { } shared)
        {
            run.Fail(ExitCode.Usage, "bad-shared", readError!);
            return null;
        }
        if (run.Settings.Build is not null || run.Settings.At is not null || run.Settings.Manifests.Count > 0)
        {
            run.Fail(ExitCode.Usage, "no-build", "--shared names the build already: leave out --build, --at and --manifest.");
            return null;
        }

        var library = GameLibrary.Load(steam);
        if (SharedBuilds.GameOf(library, shared, out var gameError) is not { } game)
        {
            run.Fail(ExitCode.Usage, "no-game", gameError!);
            return null;
        }
        if ((command.Arguments.FirstOrDefault() ?? command.Value("game")) is { } named
            && Selectors.Game(library, named, out _) is { } other && other.AppId != game.AppId)
        {
            run.Fail(ExitCode.Usage, "no-game", $"That shared build is for {game.Name}, not {other.Name}.");
            return null;
        }

        if (SharedBuilds.Resolve(game, library.History(game), shared, out var resolveError) is not { } target)
        {
            run.Fail(ExitCode.Usage, "no-build", resolveError!);
            return null;
        }
        foreach (var note in target.Notes) run.Warn(note);
        if (target.Notes.Count > 0) run.Set("sharedNotes", new JsonArray(target.Notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()));

        // --only and --files on the command line choose the part over what the shared build chose.
        var choosesPart = run.Settings.Only is not null || run.Settings.Files is not null;
        run.Replace(run.Settings with
        {
            Build = target.Settings.Build,
            Manifests = target.Settings.Manifests,
            Label = target.Settings.Label,
            Only = choosesPart ? run.Settings.Only : target.Settings.Only,
            Files = choosesPart ? run.Settings.Files : target.Settings.Files,
            Siblings = run.Settings.Siblings || target.Settings.Siblings,
            Language = target.Settings.Language,
        });
        return game;
    }
}
