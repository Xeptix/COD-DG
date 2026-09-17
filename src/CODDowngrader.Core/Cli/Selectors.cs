using System.Globalization;
using CODDowngrader.App;
using CODDowngrader.Builds;

namespace CODDowngrader.Cli;

/// <summary>A depot a date needs a manifest for, and the SteamDB page that lists that depot's manifests.</summary>
public sealed record NeededManifest(uint Depot, uint Owner, string Url);

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
    public static BuildTarget? Build(GameEntry game, BuildHistoryResult history, string? build, IReadOnlyList<string> manifests, out string? error) =>
        Build(game, history, build, manifests, out error, out _, out _);

    /// <summary>
    /// As <see cref="Build(GameEntry, BuildHistoryResult, string?, IReadOnlyList{string}, out string?)"/>, and when the build has
    /// depots whose manifest is not known here, <paramref name="needs"/> lists them and <paramref name="before"/> is the moment
    /// to look them up at: each one's manifest is the newest SteamDB first saw before it.
    /// </summary>
    public static BuildTarget? Build(GameEntry game, BuildHistoryResult history, string? build, IReadOnlyList<string> manifests, out string? error,
        out IReadOnlyList<NeededManifest> needs, out DateTimeOffset? before)
    {
        error = null;
        needs = Array.Empty<NeededManifest>();
        before = null;
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
                needs = Needs(game, chosen.Unknown);
                before = NeededBefore(chosen);
                error = UnknownText(chosen.Unknown, before);
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

    /// <summary>Each depot's SteamDB page, for depots whose manifest has to come from there.</summary>
    public static IReadOnlyList<NeededManifest> Needs(GameEntry game, IEnumerable<uint> depots) =>
        depots.OrderBy(d => d).Select(d => new NeededManifest(d, game.OwnerOf(d), BuildTimeline.SteamDbManifests(d))).ToList();

    /// <summary>
    /// The moment a build's missing manifests are looked up at: shortly before the update that replaced it began, since every
    /// manifest of one update is first seen within minutes. Null for a build nothing replaced.
    /// </summary>
    public static DateTimeOffset? NeededBefore(Build build) => build.ReplacedBy?.Time?.AddMinutes(-10);

    /// <summary>What to tell someone about depots of a build whose manifest is not known here.</summary>
    public static string UnknownText(IReadOnlyCollection<uint> depots, DateTimeOffset? before) =>
        $"The manifest of {(depots.Count == 1 ? "depot" : "depots")} {string.Join(", ", depots.OrderBy(d => d))} of that build is not known here."
        + (before is { } moment
            ? $" On each depot's SteamDB page, take the newest manifest first seen before {moment.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}."
            : "");

    /// <summary>The build job settings name, without a job to report on: --at, or --build with --manifest. Null with <paramref name="error"/> set.</summary>
    public static BuildTarget? Of(GameEntry game, BuildHistoryResult history, Jobs.JobSettings settings, out string? error) =>
        settings.At is { } at
            ? At(game, history, at, settings.Manifests, out error, out _)
            : Build(game, history, settings.Build, settings.Manifests, out error);

    /// <summary>
    /// A moment the command line names: a date, which means the end of that day so an update released that day counts, or a
    /// date and a time. Local time, as the menus show dates. Null when the text is neither.
    /// </summary>
    public static DateTimeOffset? Moment(string text)
    {
        text = text.Trim();
        var local = TimeZoneInfo.Local;
        if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HHmm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return new DateTimeOffset(at, local.GetUtcOffset(at));
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var end = day.Date.AddDays(1).AddTicks(-1);
            return new DateTimeOffset(end, local.GetUtcOffset(end));
        }
        return null;
    }

    /// <summary>
    /// The build a game had at a date. Where the history cannot say (the date is before every update it knows, or the build
    /// has depots whose manifest is not known here), <paramref name="needs"/> lists the depots whose manifest has to come
    /// from SteamDB, and <paramref name="manifests"/> fills them in: "depot=manifest", as with <see cref="Build"/>.
    /// </summary>
    public static BuildTarget? At(GameEntry game, BuildHistoryResult history, string at, IReadOnlyList<string> manifests,
        out string? error, out IReadOnlyList<NeededManifest> needs)
    {
        needs = Array.Empty<NeededManifest>();
        if (Moment(at) is not { } moment)
        {
            error = $"--at takes a date such as 2015-03-12, or a date and time such as 2015-03-12T18:30, not \"{at}\".";
            return null;
        }
        if (Pairs(manifests, out error) is not { } given) return null;

        var found = BuildTimeline.At(history.Builds, moment);
        var day = moment.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        var missing = found is { Certain: true, Build: { } certain }
            ? certain.Unknown
            : game.Owners.Keys.OrderBy(d => d).ToList();
        var stillMissing = missing.Where(d => !given.ContainsKey(d)).OrderBy(d => d).ToList();
        if (stillMissing.Count > 0)
        {
            needs = stillMissing.Select(d => new NeededManifest(d, game.OwnerOf(d), BuildTimeline.SteamDbManifests(d))).ToList();
            var why = found.Certain
                ? $"The build {game.Name} had on {day} is known here except for depots {string.Join(", ", stillMissing)}."
                : found.KnownFrom is { } from
                    ? $"The builds of {game.Name} known here go back to {from.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, so {day} is not."
                    : $"No update of {game.Name} is known here, so its build on {day} is not.";
            error = $"{why} On each depot's SteamDB page, take the newest manifest first seen on or before {day}.";
            return null;
        }

        var chosen = new Dictionary<uint, ulong>(found.Build?.Manifests ?? new Dictionary<uint, ulong>());
        foreach (var (depot, manifest) in given) chosen[depot] = manifest;
        if (given.Count == 0 && found.Build is not null)
        {
            var keys = Keys(history.Builds);
            return new BuildTarget(chosen, Format.BuildTitles(game, history.Builds)[found.Index], keys[found.Index], found.Build);
        }
        return new BuildTarget(chosen, $"The build of {day}", moment.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), found.Certain ? found.Build : null);
    }

    /// <summary>"depot=manifest" entries as a map. Null with <paramref name="error"/> set when one is not that.</summary>
    static Dictionary<uint, ulong>? Pairs(IReadOnlyList<string> manifests, out string? error)
    {
        error = null;
        var pairs = new Dictionary<uint, ulong>();
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
            pairs[depot] = manifest;
        }
        return pairs;
    }
}
