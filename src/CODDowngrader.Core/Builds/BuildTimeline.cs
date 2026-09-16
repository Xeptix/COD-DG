namespace CODDowngrader.Builds;

/// <summary>The build a game had at a moment, as far as its history can tell.</summary>
/// <param name="Build">That build; null when the history has none.</param>
/// <param name="Index">Its place in the history's builds.</param>
/// <param name="Certain">
/// False when the moment is before every update the history knows: the oldest build known here was live then only if no
/// earlier update came between, and nothing here can tell.
/// </param>
/// <param name="KnownFrom">The oldest update the history knows, from which on every moment is certain.</param>
public sealed record BuildAt(Build? Build, int Index, bool Certain, DateTimeOffset? KnownFrom);

public static class BuildTimeline
{
    /// <summary>
    /// The build live at <paramref name="moment"/>. A history is newest first, and each build was live from the moment the
    /// build before it was replaced until its own <see cref="Build.ReplacedBy"/>.
    /// </summary>
    public static BuildAt At(IReadOnlyList<Build> builds, DateTimeOffset moment)
    {
        var knownFrom = builds.Select(b => b.ReplacedBy?.Time).Where(t => t is not null).Min();
        for (var i = 0; i < builds.Count; i++)
        {
            var from = i + 1 < builds.Count ? builds[i + 1].ReplacedBy?.Time : null;
            if (from is null) return new BuildAt(builds[i], i, false, knownFrom);
            if (moment >= from) return new BuildAt(builds[i], i, true, knownFrom);
        }
        return new BuildAt(null, -1, false, knownFrom);
    }

    /// <summary>The page that lists a depot's manifests with the date SteamDB first saw each one.</summary>
    public static string SteamDbManifests(uint depot) => $"https://steamdb.info/depot/{depot}/manifests/";
}
