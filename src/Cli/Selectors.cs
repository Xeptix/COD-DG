using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Builds;

namespace CODDowngrader.Cli;

/// <summary>A build a command works on: the manifest of every depot, and what to call it.</summary>
public sealed record BuildTarget(IReadOnlyDictionary<uint, ulong> Manifests, string Label, string Key, Build? Build);

/// <summary>Finding the game and the build a command line names.</summary>
public static class Selectors
{
    /// <summary>
    /// The game an argument names: its app ID, or part of its name. Null when nothing matches or more than one does, with
    /// <paramref name="error"/> saying which.
    /// </summary>
    public static GameEntry? Game(GameLibrary library, string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Name a game: its Steam app ID, or part of its name.";
            return null;
        }

        text = text.Trim();
        if (uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
        {
            var byId = library.Entries.FirstOrDefault(e => e.AppId == appId);
            if (byId is null) error = $"No Call of Duty on Steam has the app ID {appId}.";
            return byId;
        }

        var matches = library.Entries.Where(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        error = matches.Count == 0
            ? $"No Call of Duty here is called \"{text}\". CODDowngrader list names them all."
            : $"\"{text}\" matches {string.Join(", ", matches.Select(m => $"{m.Name} [{m.AppId}]"))}. Name one of them.";
        return null;
    }

    /// <summary>
    /// What a build is called on the command line: the date of the update that replaced it, or latest, installed or newest.
    /// Two builds replaced on one day take the time as well, as the menus do.
    /// </summary>
    public static IReadOnlyList<string> Keys(IReadOnlyList<Build> builds)
    {
        var keys = builds.Select(Key).ToList();
        var repeated = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (var i = 0; i < keys.Count; i++)
        {
            if (repeated.Contains(keys[i]) && builds[i].ReplacedBy?.Time is { } time)
                keys[i] += time.ToLocalTime().ToString("-HHmm", CultureInfo.InvariantCulture);
        }
        return keys;
    }

    static string Key(Build build) => build.Kind switch
    {
        BuildKind.Installed => "installed",
        BuildKind.Latest => "latest",
        _ => build.ReplacedBy?.Time is { } time
            ? time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "newest",
    };

    /// <summary>
    /// The build a command line names: <paramref name="build"/> is a key from <see cref="Keys"/>, and each
    /// <paramref name="manifests"/> entry is "depot=manifest", which changes that depot alone. Null with
    /// <paramref name="error"/> set when nothing matches.
    /// </summary>
    public static BuildTarget? Build(GameEntry game, BuildHistoryResult history, string? build, IReadOnlyList<string> manifests, out string? error)
    {
        error = null;
        if (build is null && manifests.Count == 0)
        {
            error = "Name a build with --build, or its depots with --manifest.";
            return null;
        }

        var keys = Keys(history.Builds);
        BuildTarget? target = null;
        if (build is not null)
        {
            var wanted = build.Trim();
            var found = keys.Select((key, i) => (Key: key, Index: i))
                .Where(k => string.Equals(k.Key, wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (found.Count == 0)
            {
                // A day with two builds: name either, or the day on its own when only one is left.
                var sameDay = keys.Select((key, i) => (Key: key, Index: i))
                    .Where(k => k.Key.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameDay.Count > 1)
                {
                    error = $"{wanted} has more than one build: {string.Join(", ", sameDay.Select(s => s.Key))}.";
                    return null;
                }
                found = sameDay;
            }
            if (found.Count == 0)
            {
                error = $"This game has no build called {wanted}. CODDowngrader builds {game.AppId} names them all.";
                return null;
            }

            var chosen = history.Builds[found[0].Index];
            if (chosen.Unknown.Count > 0 && manifests.Count == 0)
            {
                error = $"The manifest of depots {string.Join(", ", chosen.Unknown)} of that build is not known here. Add them with --manifest.";
                return null;
            }
            target = new BuildTarget(chosen.Manifests, Format.BuildTitles(game, history.Builds)[found[0].Index], found[0].Key, chosen);
        }

        var chosenManifests = new Dictionary<uint, ulong>(target?.Manifests ?? game.InstalledManifests ?? game.LatestManifests ?? new Dictionary<uint, ulong>());
        if (target is null && chosenManifests.Count == 0)
        {
            error = "This game has no installed or latest build to change with --manifest, so name every depot's manifest.";
            return null;
        }

        foreach (var pair in manifests)
        {
            var parts = pair.Split(new[] { '=', ':' }, 2);
            if (parts.Length != 2
                || !uint.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var depot)
                || !ulong.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var manifest)
                || depot == 0 || manifest == 0)
            {
                error = $"--manifest takes depot=manifest, as in 311211=9084453472036406216, not \"{pair}\".";
                return null;
            }
            chosenManifests[depot] = manifest;
        }

        if (manifests.Count == 0) return target;
        var label = target is null ? "Manifests named on the command line" : $"{target.Label}, with manifests named on the command line";
        return new BuildTarget(chosenManifests, label, target?.Key ?? "manifests", target?.Build);
    }
}
