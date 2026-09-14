using System.Globalization;
using CODDowngrader.Builds;

namespace CODDowngrader.App;

public static class Format
{
    public static string Size(ulong bytes) => bytes switch
    {
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.0} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0} MB",
        >= 1UL << 10 => $"{bytes / (double)(1UL << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static string Date(DateTimeOffset when) =>
        when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset when) =>
        when.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string BuildTitle(GameEntry game, Build build)
    {
        switch (build.Kind)
        {
            case BuildKind.Installed:
                return game.Installed is { BuildId: > 0 } app ? $"Installed now (build {app.BuildId})" : "Installed now";

            case BuildKind.Latest:
                var id = game.Info?.PublicBuildId is { } b ? $" (build {b})" : "";
                return game.Installed?.UpdatePending == true ? $"Latest on Steam{id}, queued but not installed" : $"Latest on Steam{id}";

            default:
                return build.ReplacedBy switch
                {
                    { Source: UpdateSource.Log or UpdateSource.List, Time: { } time } => $"Before the update of {Date(time)}",
                    { Time: { } built } => $"Before the build of {Date(built)}",
                    null => build.ListOnly ? "Newest build on the built-in list" : "Newest build Steam fetched on this PC",
                    _ => "Earlier build",
                };
        }
    }

    /// <summary>Every build's title, with the time added where two updates fall on the same day.</summary>
    public static IReadOnlyList<string> BuildTitles(GameEntry game, IReadOnlyList<Build> builds)
    {
        var titles = builds.Select(b => BuildTitle(game, b)).ToList();
        var repeated = titles.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (var i = 0; i < titles.Count; i++)
        {
            if (repeated.Contains(titles[i]) && builds[i].ReplacedBy?.Time is { } time)
                titles[i] += time.ToLocalTime().ToString(" HH:mm", CultureInfo.InvariantCulture);
        }
        return titles;
    }

    public static string BuildDetail(GameLibrary library, GameEntry game, Build build)
    {
        var parts = new List<string>();
        if (build.Built is { } built) parts.Add($"built {Date(built)}");

        if (build.Kind != BuildKind.Installed && game.Installed is not null)
        {
            var count = build.DifferentFromInstalled.Count;
            if (count > 0)
            {
                var sizes = build.DifferentFromInstalled.Select(d => library.DepotSize(game, d, build.Manifests[d])).ToList();
                var size = sizes.All(s => s is not null) ? $", {Size((ulong)sizes.Sum(s => (long)s!.Value))}" : "";
                parts.Add(count == 1 ? $"1 depot differs{size}" : $"{count} depots differ{size}");
            }
        }

        if (build.Unknown.Count > 0) parts.Add($"{build.Unknown.Count} depots unknown");
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }
}
